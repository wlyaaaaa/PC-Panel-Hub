using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using HS2.CrystalOverlay.Core;

namespace HS2_CrystalOverlay;

internal interface IPhoneBatteryProbe
{
    Task<PhoneBatteryReading?> ReadAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

internal sealed partial class XiaomiHyperConnectBatteryProbe :
    IPhoneBatteryProbe
{
    private const int TailBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan ChargingTrendAge =
        TimeSpan.FromHours(6);
    private static readonly TimeSpan LogBatteryMaximumAge =
        TimeSpan.FromHours(12);
    private static readonly TimeSpan LogConnectionMaximumAge =
        TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LogReadInterval =
        TimeSpan.FromSeconds(10);

    private readonly string logPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData),
        "MI",
        "AIoT",
        "Log",
        "smart_share_log.txt");
    private XiaomiDeviceLogState? cachedLogState;
    private DateTimeOffset lastLogRead = DateTimeOffset.MinValue;

    public Task<PhoneBatteryReading?> ReadAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        Task.Run(() => Read(now, cancellationToken), cancellationToken);

    private PhoneBatteryReading? Read(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process
            .GetProcessesByName("MiSmartShare")
            .OrderByDescending(candidate => candidate.StartTime)
            .FirstOrDefault();
        if (process is null)
        {
            return null;
        }

        var currentPercentage =
            TryReadConnectedPercentage(process.Id);
        var logState = TryReadLog(now);
        var connected = currentPercentage is not null ||
                        logState?.Connection?.IsConnected == true;
        if (!connected)
        {
            return null;
        }

        var logBattery = logState?.Battery;
        var percentage = currentPercentage ??
                         (logBattery is not null &&
                          now - logBattery.ObservedAt <=
                          LogBatteryMaximumAge
                             ? logBattery.Percentage
                             : null);
        if (percentage is null)
        {
            return null;
        }

        var charging = logBattery is not null &&
                       now - logBattery.ObservedAt <= ChargingTrendAge
            ? logBattery.IsCharging
            : null;
        return new PhoneBatteryReading(
            PhoneBatteryProvider.XiaomiHyperConnect,
            percentage.Value,
            charging,
            true,
            now,
            currentPercentage is not null
                ? "xiaomi-ui"
                : "xiaomi-log");
    }

    private static int? TryReadConnectedPercentage(int processId)
    {
        try
        {
            var windows = AutomationElement.RootElement.FindAll(
                TreeScope.Children,
                new PropertyCondition(
                    AutomationElement.ProcessIdProperty,
                    processId));
            foreach (AutomationElement window in windows)
            {
                var disconnect = window.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(
                        AutomationElement.AutomationIdProperty,
                        "DisconnectButton"));
                if (disconnect is null)
                {
                    continue;
                }

                var textElements = window.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(
                        AutomationElement.ControlTypeProperty,
                        ControlType.Text));
                foreach (AutomationElement element in textElements)
                {
                    var match = PercentText().Match(element.Current.Name);
                    if (match.Success &&
                        int.TryParse(
                            match.Groups["value"].Value,
                            out var percentage) &&
                        percentage is >= 0 and <= 100)
                    {
                        return percentage;
                    }
                }
            }
        }
        catch (ElementNotAvailableException)
        {
            // Xiaomi rebuilt its visual tree while it was being sampled.
        }
        catch (InvalidOperationException)
        {
            // Treat an inaccessible or incomplete tree as no current data.
        }

        return null;
    }

    private XiaomiDeviceLogState? TryReadLog(DateTimeOffset now)
    {
        if (now - lastLogRead < LogReadInterval)
        {
            return cachedLogState;
        }

        lastLogRead = now;
        try
        {
            using var stream = new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var length = (int)Math.Min(TailBytes, stream.Length);
            if (length <= 0)
            {
                return null;
            }

            stream.Seek(-length, SeekOrigin.End);
            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, buffer.Length);
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            var battery = XiaomiBatteryLogParser.Parse(
                text,
                now,
                ChargingTrendAge);
            var connection = XiaomiConnectionLogParser.Parse(
                text,
                now,
                LogConnectionMaximumAge);
            cachedLogState = new XiaomiDeviceLogState(
                battery,
                connection);
            return cachedLogState;
        }
        catch (IOException)
        {
            cachedLogState = null;
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            cachedLogState = null;
            return null;
        }
    }

    [GeneratedRegex(@"^\s*(?<value>\d{1,3})%\s*$")]
    private static partial Regex PercentText();

    private sealed record XiaomiDeviceLogState(
        XiaomiBatteryLogSnapshot? Battery,
        XiaomiConnectionLogSnapshot? Connection);
}

internal sealed partial class PhoneLinkBatteryProbe :
    IPhoneBatteryProbe
{
    private const int MaximumCompanionBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan CompanionMaximumAge =
        TimeSpan.FromMinutes(5);

    private readonly string companionPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "Packages",
        "Microsoft.YourPhone_8wekyb3d8bbwe",
        "LocalState",
        "StartMenu",
        "StartMenuCompanion.json");

    public Task<PhoneBatteryReading?> ReadAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        Task.Run(() => Read(now, cancellationToken), cancellationToken);

    private PhoneBatteryReading? Read(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var companion = TryReadCompanion(now);
        if (companion is not null)
        {
            return companion;
        }

        using var process = Process
            .GetProcessesByName("PhoneExperienceHost")
            .OrderByDescending(candidate => candidate.StartTime)
            .FirstOrDefault();
        if (process is null)
        {
            return null;
        }

        try
        {
            var windows = AutomationElement.RootElement.FindAll(
                TreeScope.Children,
                new PropertyCondition(
                    AutomationElement.ProcessIdProperty,
                    process.Id));
            foreach (AutomationElement window in windows)
            {
                var textElements = window.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(
                        AutomationElement.ControlTypeProperty,
                        ControlType.Text));
                int? percentage = null;
                var charging = false;
                foreach (AutomationElement element in textElements)
                {
                    var name = element.Current.Name;
                    var match = PercentAnywhere().Match(name);
                    if (match.Success &&
                        int.TryParse(
                            match.Groups["value"].Value,
                            out var value) &&
                        value is >= 0 and <= 100)
                    {
                        percentage = value;
                    }

                    charging |= ChargingText().IsMatch(name);
                }

                if (percentage is not null)
                {
                    return new PhoneBatteryReading(
                        PhoneBatteryProvider.PhoneLink,
                        percentage.Value,
                        charging ? true : null,
                        true,
                        now,
                        "phone-link-ui");
                }
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return null;
    }

    private PhoneBatteryReading? TryReadCompanion(DateTimeOffset now)
    {
        try
        {
            var info = new FileInfo(companionPath);
            if (!info.Exists ||
                info.Length <= 0 ||
                info.Length > MaximumCompanionBytes)
            {
                return null;
            }

            var observedAt = new DateTimeOffset(
                info.LastWriteTimeUtc,
                TimeSpan.Zero);
            var age = now.ToUniversalTime() - observedAt;
            if (age < TimeSpan.FromMinutes(-1) ||
                age > CompanionMaximumAge)
            {
                return null;
            }

            using var stream = new FileStream(
                companionPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            var snapshot = PhoneLinkCompanionParser.Parse(
                reader.ReadToEnd());
            if (snapshot is null || !snapshot.IsConnected)
            {
                return null;
            }

            return new PhoneBatteryReading(
                PhoneBatteryProvider.PhoneLink,
                snapshot.Percentage,
                snapshot.IsCharging,
                true,
                now,
                "phone-link-companion");
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\b(?<value>\d{1,3})%\b")]
    private static partial Regex PercentAnywhere();

    [GeneratedRegex(@"charging|充电", RegexOptions.IgnoreCase)]
    private static partial Regex ChargingText();
}

internal sealed class PhoneBatterySourceCoordinator : IDisposable
{
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumReadingAge =
        TimeSpan.FromSeconds(15);

    private readonly IOverlayPublisher publisher;
    private readonly IPhoneBatteryProbe xiaomi;
    private readonly IPhoneBatteryProbe phoneLink;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Timer timer;
    private PhoneBatteryReading? published;
    private string? lastSourceState;
    private bool baselineCaptured;
    private int polling;
    private bool disposed;

    internal PhoneBatterySourceCoordinator(IOverlayPublisher publisher)
        : this(
            publisher,
            new XiaomiHyperConnectBatteryProbe(),
            new PhoneLinkBatteryProbe())
    {
    }

    internal PhoneBatterySourceCoordinator(
        IOverlayPublisher publisher,
        IPhoneBatteryProbe xiaomi,
        IPhoneBatteryProbe phoneLink)
    {
        this.publisher = publisher;
        this.xiaomi = xiaomi;
        this.phoneLink = phoneLink;
        timer = new Timer(
            Poll,
            null,
            TimeSpan.Zero,
            PollInterval);
    }

    private async void Poll(object? state)
    {
        if (disposed || Interlocked.Exchange(ref polling, 1) != 0)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.Now;
            var token = cancellation.Token;
            var xiaomiTask = ReadSourceAsync(
                xiaomi,
                "xiaomi",
                now,
                token);
            var phoneLinkTask = ReadSourceAsync(
                phoneLink,
                "phone-link",
                now,
                token);
            await Task.WhenAll(xiaomiTask, phoneLinkTask);
            var xiaomiReading = await xiaomiTask;
            var phoneLinkReading = await phoneLinkTask;

            var selected = PhoneBatteryArbitration.Select(
                xiaomiReading,
                phoneLinkReading,
                now,
                MaximumReadingAge);
            LogSourceState(
                xiaomiReading,
                phoneLinkReading,
                selected);
            PublishSelection(selected);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            RuntimeLog.Write(
                $"Phone battery probe failed: {exception.GetType().Name}");
            PublishSelection(null);
        }
        finally
        {
            Interlocked.Exchange(ref polling, 0);
        }
    }

    private static async Task<PhoneBatteryReading?> ReadSourceAsync(
        IPhoneBatteryProbe probe,
        string sourceName,
        DateTimeOffset now,
        CancellationToken token)
    {
        try
        {
            return await probe.ReadAsync(now, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            RuntimeLog.Write(
                $"Phone battery source failed: " +
                $"source={sourceName}, error={exception.GetType().Name}.");
            return null;
        }
    }

    private void LogSourceState(
        PhoneBatteryReading? xiaomiReading,
        PhoneBatteryReading? phoneLinkReading,
        PhoneBatteryReading? selected)
    {
        var state =
            $"xiaomi={DescribeReading(xiaomiReading)}; " +
            $"phone-link={DescribeReading(phoneLinkReading)}; " +
            $"selected={selected?.Provider.ToString() ?? "none"}";
        if (string.Equals(
                state,
                lastSourceState,
                StringComparison.Ordinal))
        {
            return;
        }

        lastSourceState = state;
        RuntimeLog.Write($"Phone battery sources: {state}.");
    }

    private static string DescribeReading(PhoneBatteryReading? reading) =>
        reading is null
            ? "none"
            : $"{reading.Percentage}%/" +
              $"{reading.Evidence ?? "unknown"}/" +
              $"charging={reading.IsCharging?.ToString() ?? "unknown"}";

    private void PublishSelection(PhoneBatteryReading? selected)
    {
        if (selected is null)
        {
            if (published is not null)
            {
                var previous = published;
                _ = publisher.Publish(OverlayRequest.End(
                    "phone-battery",
                    OverlayKind.PhoneBattery,
                    SourceFor(previous.Provider)));
                if (baselineCaptured)
                {
                    PublishConnection(previous.Provider, connected: false);
                }

                published = null;
            }

            baselineCaptured = true;
            return;
        }

        if (published is not null &&
            published.Provider == selected.Provider &&
            published.Percentage == selected.Percentage &&
            published.IsCharging == selected.IsCharging)
        {
            baselineCaptured = true;
            return;
        }

        if (published is null && baselineCaptured)
        {
            PublishConnection(selected.Provider, connected: true);
        }

        _ = publisher.Publish(OverlayRequest.Active(
            "phone-battery",
            OverlayKind.PhoneBattery,
            SourceFor(selected.Provider),
            $"{selected.Percentage}%",
            visual: new OverlayVisualData(
                IsCharging: selected.IsCharging)));
        published = selected;
        baselineCaptured = true;
    }

    private void PublishConnection(
        PhoneBatteryProvider provider,
        bool connected)
    {
        var sourceName = provider switch
        {
            PhoneBatteryProvider.XiaomiHyperConnect => "小米妙享",
            PhoneBatteryProvider.PhoneLink => "手机连接",
            _ => "手机",
        };
        _ = publisher.Publish(OverlayRequest.Timed(
            connected ? "phone-connected" : "phone-disconnected",
            OverlayKind.PhoneConnection,
            SourceFor(provider),
            connected ? "手机已连接" : "手机已断开",
            sourceName,
            dedupKey:
                $"{provider}:{(connected ? "connected" : "disconnected")}",
            visual: new OverlayVisualData(
                Eyebrow: connected
                    ? "连接成功 / CONNECTED"
                    : "连接断开 / DISCONNECTED",
                AccentHex: connected ? "#8EF2C8" : "#FFD08A")));
    }

    private static OverlaySource SourceFor(
        PhoneBatteryProvider provider) => provider switch
        {
            PhoneBatteryProvider.XiaomiHyperConnect =>
                OverlaySource.XiaomiHyperConnect,
            PhoneBatteryProvider.PhoneLink => OverlaySource.PhoneLink,
            _ => OverlaySource.System,
        };

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cancellation.Cancel();
        timer.Dispose();
        cancellation.Dispose();
    }
}
