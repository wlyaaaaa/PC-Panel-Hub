using System.Globalization;
using HS2.CrystalOverlay.Core;
using Microsoft.Win32;
using Windows.ApplicationModel.Appointments;

namespace HS2_CrystalOverlay;

internal sealed class GlanceSourceCoordinator : IDisposable
{
    private static readonly Uri SnapshotUri =
        new("http://127.0.0.1:18765/snapshot");
    private static readonly TimeZoneInfo ChinaTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    private static readonly string[] Weekdays =
    [
        "周日",
        "周一",
        "周二",
        "周三",
        "周四",
        "周五",
        "周六",
    ];

    private readonly IOverlayPublisher publisher;
    private readonly HttpClient client = new()
    {
        Timeout = TimeSpan.FromSeconds(3),
    };
    private readonly CancellationTokenSource cancellation = new();
    private readonly AppointmentProbe appointments = new();
    private readonly GlanceHotkeyWindow hotkey;
    private int showing;
    private bool disposed;

    internal GlanceSourceCoordinator(IOverlayPublisher publisher)
    {
        this.publisher = publisher;
        hotkey = new GlanceHotkeyWindow(
            () => Show("快捷总览"));
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _ = ShowAfterLaunchAsync();
    }

    internal void Show(string reason)
    {
        if (disposed || Interlocked.Exchange(ref showing, 1) != 0)
        {
            return;
        }

        _ = ShowAsync(reason);
    }

    private async Task ShowAfterLaunchAsync()
    {
        try
        {
            await Task.Delay(
                TimeSpan.FromSeconds(2),
                cancellation.Token);
            Show("欢迎回来");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ShowAsync(string reason)
    {
        try
        {
            GlanceWeather? weather = null;
            try
            {
                var json = await client.GetStringAsync(
                    SnapshotUri,
                    cancellation.Token);
                weather =
                    SideScreenSnapshotParser.ParseWeather(json);
            }
            catch (HttpRequestException)
            {
                RuntimeLog.Write(
                    "Glance weather snapshot is unavailable.");
            }
            catch (TaskCanceledException)
            {
                RuntimeLog.Write(
                    "Glance weather snapshot timed out.");
            }

            var now = TimeZoneInfo.ConvertTime(
                DateTimeOffset.UtcNow,
                ChinaTimeZone);
            var nextAppointment = await appointments.ReadNextAsync(
                now,
                cancellation.Token);
            var dateLine =
                $"{now.Month}月{now.Day}日 " +
                $"{Weekdays[(int)now.DayOfWeek]}";
            var weatherLine = FormatWeather(weather);
            var body = string.IsNullOrWhiteSpace(weatherLine)
                ? dateLine
                : $"{dateLine}  ·  {weatherLine}";
            if (!string.IsNullOrWhiteSpace(nextAppointment))
            {
                body += $"{Environment.NewLine}下一项 · {nextAppointment}";
            }
            _ = publisher.Publish(OverlayRequest.Timed(
                "glance",
                OverlayKind.Glance,
                OverlaySource.System,
                now.ToString("HH:mm", CultureInfo.InvariantCulture),
                body,
                dedupKey: $"glance:{now:yyyyMMddHHmm}",
                visual: new OverlayVisualData(
                    Eyebrow: reason,
                    Meta: FormatForecast(weather),
                    AccentHex: "#A7F3FF")));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            RuntimeLog.Write(
                $"Glance source failed: {exception.GetType().Name}");
        }
        finally
        {
            Interlocked.Exchange(ref showing, 0);
        }
    }

    private static string? FormatWeather(GlanceWeather? weather)
    {
        if (weather is null)
        {
            return null;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(weather.City))
        {
            parts.Add(weather.City!);
        }

        if (weather.TemperatureCelsius is { } temperature)
        {
            parts.Add($"{temperature:0}°C");
        }

        if (!string.IsNullOrWhiteSpace(weather.Condition))
        {
            parts.Add(weather.Condition!);
        }

        return parts.Count == 0
            ? null
            : string.Join(' ', parts);
    }

    private static string? FormatForecast(GlanceWeather? weather)
    {
        if (weather is null)
        {
            return null;
        }

        var parts = new List<string>();
        if (weather.LowTemperatureCelsius is { } low &&
            weather.HighTemperatureCelsius is { } high)
        {
            parts.Add($"今日 {low:0}–{high:0}°C");
        }

        if (weather.RainProbabilityPercent is { } rain)
        {
            parts.Add($"降雨 {rain:0}%");
        }

        return parts.Count == 0
            ? null
            : string.Join("  ·  ", parts);
    }

    private void OnSessionSwitch(
        object sender,
        SessionSwitchEventArgs args)
    {
        if (args.Reason == SessionSwitchReason.SessionUnlock)
        {
            Show("已解锁");
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        hotkey.Dispose();
        cancellation.Cancel();
        client.Dispose();
        cancellation.Dispose();
    }
}

internal sealed class AppointmentProbe
{
    private static readonly TimeSpan OperationTimeout =
        TimeSpan.FromSeconds(2);

    private AppointmentStore? store;
    private bool accessAttempted;

    internal async Task<string?> ReadNextAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!accessAttempted)
            {
                accessAttempted = true;
                store = await AppointmentManager.RequestStoreAsync(
                        AppointmentStoreAccessType.AllCalendarsReadOnly)
                    .AsTask()
                    .WaitAsync(
                        OperationTimeout,
                        cancellationToken);
            }

            if (store is null)
            {
                return null;
            }

            var appointments =
                await store.FindAppointmentsAsync(
                        now,
                        TimeSpan.FromDays(14))
                    .AsTask()
                    .WaitAsync(
                        OperationTimeout,
                        cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var next = appointments
                .Where(item =>
                    item.StartTime + item.Duration >= now)
                .OrderBy(item => item.StartTime)
                .FirstOrDefault();
            if (next is null)
            {
                return null;
            }

            var localStart = TimeZoneInfo.ConvertTime(
                next.StartTime,
                TimeZoneInfo.FindSystemTimeZoneById(
                    "China Standard Time"));
            var subject = string.IsNullOrWhiteSpace(next.Subject)
                ? "日程"
                : next.Subject.Trim();
            if (next.AllDay)
            {
                return $"{FormatDay(localStart, now)} 全天  {subject}";
            }

            return
                $"{FormatDay(localStart, now)} " +
                $"{localStart:HH:mm}  {subject}";
        }
        catch (UnauthorizedAccessException)
        {
            store = null;
            return null;
        }
        catch (TimeoutException)
        {
            RuntimeLog.Write(
                "Calendar probe timed out; glance will omit appointments.");
            return null;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RuntimeLog.Write(
                $"Calendar probe failed: {exception.GetType().Name}");
            return null;
        }
    }

    private static string FormatDay(
        DateTimeOffset value,
        DateTimeOffset now)
    {
        if (value.Date == now.Date)
        {
            return "今天";
        }

        if (value.Date == now.Date.AddDays(1))
        {
            return "明天";
        }

        return $"{value.Month}月{value.Day}日";
    }
}

internal sealed class GlanceHotkeyWindow :
    System.Windows.Forms.NativeWindow,
    IDisposable
{
    private const int HotkeyId = 1;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint KeyG = 0x47;

    private readonly Action show;
    private bool disposed;

    internal GlanceHotkeyWindow(Action show)
    {
        this.show = show;
        CreateHandle(new System.Windows.Forms.CreateParams
        {
            Caption = "HS2 glance hotkey",
        });
        if (!NativeMethods.RegisterHotKey(
                Handle,
                HotkeyId,
                ModControl | ModAlt | ModNoRepeat,
                KeyG))
        {
            RuntimeLog.Write(
                "Global glance hotkey Ctrl+Alt+G is unavailable.");
        }
    }

    protected override void WndProc(
        ref System.Windows.Forms.Message message)
    {
        if (message.Msg == WmHotkey &&
            message.WParam.ToInt32() == HotkeyId)
        {
            show();
            return;
        }

        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        _ = NativeMethods.UnregisterHotKey(Handle, HotkeyId);
        DestroyHandle();
    }
}
