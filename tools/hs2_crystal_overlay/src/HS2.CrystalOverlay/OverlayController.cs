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
        lock (sync)
        {
            frame = scheduler.GetFrame(now);
        }

        var card = frame.PrimaryCard;
        if (!Equals(card?.Request, lastCardRequest))
        {
            lastCardRequest = card?.Request;
            cardWindow.Render(card, placement);
        }

        var directSignature = string.Join(
            '\u001f',
            frame.DirectItems.Select(item =>
                $"{item.Request.EventId}\u001e{item.Request.Title}\u001e{item.Request.Body}"));
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

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        timer.Stop();
        cardWindow.Dispose();
        directWindow.Dispose();
    }
}
