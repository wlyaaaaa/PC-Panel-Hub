using HS2.CrystalOverlay.Core;
using Microsoft.Win32;
using Windows.Storage;

namespace HS2_CrystalOverlay;

internal sealed class GlanceSourceCoordinator : IDisposable
{
    private const string VisibilitySetting = "GlanceVisible";

    private readonly IOverlayPublisher publisher;
    private readonly GlanceHotkeyWindow hotkey;
    private readonly Timer refreshTimer;
    private volatile bool visible;
    private bool disposed;

    internal GlanceSourceCoordinator(IOverlayPublisher publisher)
    {
        this.publisher = publisher;
        hotkey = new GlanceHotkeyWindow(
            Toggle);
        refreshTimer = new Timer(
            _ => Refresh(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        SystemEvents.SessionSwitch += OnSessionSwitch;
        visible = ReadVisibilitySetting();
        if (visible)
        {
            Refresh();
        }
    }

    internal void Toggle()
    {
        if (disposed)
        {
            return;
        }

        visible = !visible;
        WriteVisibilitySetting(visible);
        if (visible)
        {
            Refresh();
            return;
        }

        refreshTimer.Change(
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        _ = publisher.Publish(OverlayRequest.End(
            "glance",
            OverlayKind.Glance,
            OverlaySource.System));
    }

    internal void Refresh()
    {
        if (disposed || !visible)
        {
            return;
        }

        try
        {
            if (!visible)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            _ = publisher.Publish(OverlayRequest.Active(
                "glance",
                OverlayKind.Glance,
                OverlaySource.System,
                GlanceClock.FormatChinaTime(now)));
        }
        catch (Exception exception)
        {
            RuntimeLog.Write(
                $"Glance source failed: {exception.GetType().Name}");
        }
        finally
        {
            ScheduleNextRefresh();
        }
    }

    private void ScheduleNextRefresh()
    {
        if (disposed || !visible)
        {
            return;
        }

        try
        {
            refreshTimer.Change(
                GlanceClock.DelayUntilNextMinute(
                    DateTimeOffset.UtcNow),
                Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnSessionSwitch(
        object sender,
        SessionSwitchEventArgs args)
    {
        if (args.Reason == SessionSwitchReason.SessionUnlock)
        {
            Refresh();
        }
    }

    private static bool ReadVisibilitySetting()
    {
        try
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            return !values.TryGetValue(VisibilitySetting, out var value) ||
                   value is true;
        }
        catch (Exception exception)
        {
            RuntimeLog.Write(
                $"Glance setting read failed: {exception.GetType().Name}");
            return false;
        }
    }

    private static void WriteVisibilitySetting(bool value)
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[
                VisibilitySetting] = value;
        }
        catch (Exception exception)
        {
            RuntimeLog.Write(
                $"Glance setting write failed: {exception.GetType().Name}");
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
        refreshTimer.Dispose();
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

    private readonly Action toggle;
    private bool disposed;

    internal GlanceHotkeyWindow(Action toggle)
    {
        this.toggle = toggle;
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
            toggle();
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
