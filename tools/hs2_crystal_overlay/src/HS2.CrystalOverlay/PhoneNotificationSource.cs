using HS2.CrystalOverlay.Core;

namespace HS2_CrystalOverlay;

internal sealed class PhoneNotificationSourceCoordinator : IDisposable
{
    private readonly LifetimePublicationGate publicationGate = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task loop;

    internal PhoneNotificationSourceCoordinator(IOverlayPublisher publisher)
    {
        var client = new PhoneBridgeNotificationClient(request =>
            publicationGate.TryPublish(() => publisher.Publish(request)));
        loop = Task.Run(() => client.RunAsync(cancellation.Token));
    }

    public void Dispose()
    {
        if (!publicationGate.Close()) return;
        cancellation.Cancel();
        _ = loop.ContinueWith(completed =>
        {
            _ = completed.Exception;
            cancellation.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
