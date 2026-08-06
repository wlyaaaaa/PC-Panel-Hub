using HS2.CrystalOverlay.Core;
using Microsoft.Win32;
using Windows.Storage;

namespace HS2_CrystalOverlay;

internal sealed class GlanceSourceCoordinator : IDisposable
{
    private const string VisibilitySetting = "GlanceVisible";

    private readonly IOverlayPublisher publisher;
    private readonly Timer refreshTimer;
    private volatile bool visible;
    private bool disposed;

    internal GlanceSourceCoordinator(IOverlayPublisher publisher)
    {
        this.publisher = publisher;
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
        refreshTimer.Dispose();
    }
}
