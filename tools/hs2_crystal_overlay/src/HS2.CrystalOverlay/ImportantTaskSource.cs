using HS2.CrystalOverlay.Core;

namespace HS2_CrystalOverlay;

internal sealed class ImportantTaskSourceCoordinator : IDisposable
{
    internal const string PipeName = "HS2.CrystalOverlay.Tasks";
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LeaseSweepInterval = TimeSpan.FromSeconds(5);

    private readonly IOverlayPublisher publisher;
    private readonly LifetimePublicationGate publicationGate = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly ImportantTaskLeaseRegistry leases = new(DefaultLease);
    private readonly Task server;
    private readonly Timer leaseTimer;

    internal ImportantTaskSourceCoordinator(IOverlayPublisher publisher)
    {
        this.publisher = publisher;
        server = ImportantTaskPipeServer.RunAsync(
            PipeName,
            ReadTimeout,
            update =>
            {
                Publish(update);
                return ValueTask.CompletedTask;
            },
            cancellation.Token,
            exception => RuntimeLog.Write(
                $"Important task pipe failed: {exception.GetType().Name}"));
        leaseTimer = new Timer(
            SweepExpiredLeases,
            null,
            LeaseSweepInterval,
            LeaseSweepInterval);
    }

    private void Publish(ImportantTaskUpdate update)
    {
        if (update.State == ImportantTaskState.Active)
        {
            leases.Refresh(
                update.Id,
                DateTimeOffset.UtcNow,
                update.Lease);
        }
        else
        {
            leases.Remove(update.Id);
        }

        foreach (var request in ImportantTaskProjection.Project(update))
        {
            _ = publicationGate.TryPublish(
                () => publisher.Publish(request));
        }
    }

    private void SweepExpiredLeases(object? state)
    {
        if (publicationGate.IsClosed)
        {
            return;
        }

        leases.Expire(DateTimeOffset.UtcNow, id =>
        {
            _ = publicationGate.TryPublish(() => publisher.Publish(
                OverlayRequest.End(
                    ImportantTaskProjection.ActiveEventId(id),
                    OverlayKind.ImportantTask,
                    OverlaySource.Task)));
            RuntimeLog.Write($"Important task lease expired: id={id}.");
        });
    }

    public void Dispose()
    {
        if (!publicationGate.Close())
        {
            return;
        }

        leaseTimer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        cancellation.Cancel();
        var completed = false;
        try
        {
            completed = server.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }

        if (completed)
        {
            cancellation.Dispose();
            return;
        }

        _ = server.ContinueWith(
            completedServer =>
            {
                _ = completedServer.Exception;
                cancellation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
