using HS2.CrystalOverlay.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace HS2_CrystalOverlay;

internal interface IOverlayPublisher
{
    bool Publish(OverlayRequest request);
}

internal sealed class OverlayController : IOverlayPublisher, IDisposable
{
    private readonly object sync = new();
    private readonly OverlayScheduler scheduler = new();
    private readonly CrystalCardWindow cardWindow;
    private readonly CrystalCardWindow[] notificationWindows;
    private readonly DirectOverlayWindow directWindow;
    private readonly OverlayPlacement placement;
    private readonly DispatcherQueue dispatcher;
    private readonly DispatcherTimer timer;
    private OverlayRequest? lastCardRequest;
    private string? lastDirectSignature;
    private int lastDriftMinute = -1;
    private bool disposed;

    internal OverlayController(
        MainWindow hostWindow,
        CrystalCardWindow cardWindow,
        DirectOverlayWindow directWindow,
        OverlayPlacement placement)
    {
        this.cardWindow = cardWindow;
        notificationWindows =
        [
            new CrystalCardWindow("HS2 phone notification 1"),
            new CrystalCardWindow("HS2 phone notification 2"),
        ];
        this.directWindow = directWindow;
        this.placement = placement;
        dispatcher = hostWindow.DispatcherQueue;
        timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        timer.Tick += (_, _) => Render();
        timer.Start();
    }

    public bool Publish(OverlayRequest request)
    {
        if (disposed)
        {
            return false;
        }

        bool accepted;
        lock (sync)
        {
            accepted = scheduler.Publish(request, DateTimeOffset.Now);
        }

        if (accepted)
        {
            _ = dispatcher.TryEnqueue(Render);
        }

        return accepted;
    }

    private void Render()
    {
        if (disposed)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        OverlayFrame frame;
        PixelRect notificationRegion;
        lock (sync)
        {
            var primary = scheduler.GetPrimaryCard(now);
            notificationRegion = NotificationRegionFor(primary);
            frame = scheduler.GetFrame(
                now,
                NotificationLayoutPlanner.Capacity(
                    notificationRegion,
                    primary?.Policy.VisualTier ==
                    OverlayVisualTier.Emphasis));
        }

        var card = frame.PrimaryCard;
        if (!Equals(card?.Request, lastCardRequest))
        {
            lastCardRequest = card?.Request;
            cardWindow.Render(card, placement.Card, now);
        }

        RenderNotifications(frame.NotificationCards, notificationRegion, now);

        var directSignature = string.Join(
            '\u001f',
            frame.DirectItems.Select(item =>
                $"{item.Request.EventId}\u001e" +
                $"{item.Request.Title}\u001e" +
                $"{item.Request.Body}\u001e" +
                $"{item.Request.Visual?.Subtitle}\u001e" +
                $"{item.Request.Visual?.Meta}\u001e" +
                $"{item.Request.Visual?.IsCharging}"));
        if (!string.Equals(
                directSignature,
                lastDirectSignature,
                StringComparison.Ordinal) ||
            now.Minute != lastDriftMinute)
        {
            lastDirectSignature = directSignature;
            lastDriftMinute = now.Minute;
            directWindow.Render(frame.DirectItems, placement.Direct, now);
        }
    }

    private PixelRect NotificationRegionFor(OverlayItem? primary)
    {
        var top = placement.Direct.Bottom + 24;
        var bottom = primary is null
            ? placement.Card.Bottom
            : CrystalCardWindow.ResolveCardRect(
                    primary,
                    placement.Card)
                .Top - 24;
        return new PixelRect(
            placement.Card.X,
            top,
            placement.Card.Width,
            Math.Max(0, bottom - top));
    }

    private void RenderNotifications(
        IReadOnlyList<OverlayItem> notifications,
        PixelRect region,
        DateTimeOffset now)
    {
        var visibleCount = Math.Min(
            notifications.Count,
            notificationWindows.Length);
        var slots = NotificationLayoutPlanner.PlanSlots(
            region,
            visibleCount);
        for (var index = 0; index < visibleCount; index++)
        {
            notificationWindows[index].Render(
                notifications[index],
                slots[index],
                now);
        }

        for (var index = visibleCount;
             index < notificationWindows.Length;
             index++)
        {
            notificationWindows[index].Render(null, placement.Card, now);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        timer.Stop();
        cardWindow.Dispose();
        foreach (var notificationWindow in notificationWindows)
        {
            notificationWindow.Dispose();
        }
        directWindow.Dispose();
    }
}
