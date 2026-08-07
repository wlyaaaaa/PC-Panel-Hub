using HS2.CrystalOverlay.Core;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace HS2_CrystalOverlay;

internal sealed class PhoneNotificationSourceCoordinator : IDisposable
{
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AccessRetryInterval =
        TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ActiveSafetyLease =
        TimeSpan.FromMinutes(5);

    private readonly IOverlayPublisher publisher;
    private readonly LifetimePublicationGate publicationGate = new();
    private readonly UserNotificationListener listener =
        UserNotificationListener.Current;
    private readonly CancellationTokenSource cancellation = new();
    private readonly PhoneNotificationSnapshotReconciler reconciler =
        new(ActiveSafetyLease);
    private readonly PeriodicTimer timer;
    private readonly Task loop;
    private bool accessRequested;
    private bool accessAllowed;
    private string? lastInventoryState;
    private DateTimeOffset nextAccessAttemptAt = DateTimeOffset.MinValue;

    internal PhoneNotificationSourceCoordinator(
        IOverlayPublisher publisher)
    {
        this.publisher = publisher;
        timer = new PeriodicTimer(PollInterval);
        loop = Task.Run(PollLoopAsync);
    }

    private async Task PollLoopAsync()
    {
        try
        {
            await PollOnceAsync();
            while (await timer.WaitForNextTickAsync(cancellation.Token))
            {
                await PollOnceAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PollOnceAsync()
    {
        if (publicationGate.IsClosed)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var expired = reconciler.ExpireStale(now);
            PublishRequests(expired);
            if (expired.Count > 0)
            {
                RuntimeLog.Write(
                    $"Phone active notification leases expired: " +
                    $"count={expired.Count}.");
            }

            if (!accessAllowed && now < nextAccessAttemptAt)
            {
                return;
            }

            if (!accessRequested || !accessAllowed)
            {
                accessRequested = true;
                var access = await listener.RequestAccessAsync();
                if (publicationGate.IsClosed)
                {
                    return;
                }

                accessAllowed =
                    access == UserNotificationListenerAccessStatus.Allowed;
                nextAccessAttemptAt = accessAllowed
                    ? DateTimeOffset.MinValue
                    : now + AccessRetryInterval;
                RuntimeLog.Write(
                    accessAllowed
                        ? "Phone notification access is allowed."
                        : $"Phone notification access is {access}.");
            }

            if (!accessAllowed)
            {
                return;
            }

            var notifications = await listener.GetNotificationsAsync(
                NotificationKinds.Toast);
            if (publicationGate.IsClosed)
            {
                return;
            }

            var snapshot = new List<PhoneNotificationSnapshotItem>();
            var xiaomiCount = 0;
            var phoneLinkCount = 0;
            foreach (var notification in notifications
                         .OrderBy(notification => notification.CreationTime)
                         .ThenBy(notification => notification.Id))
            {
                if (!TryRead(
                        notification,
                        out var appName,
                        out var title,
                        out var body,
                        out var source))
                {
                    continue;
                }

                if (source == OverlaySource.XiaomiHyperConnect)
                {
                    xiaomiCount++;
                }
                else if (source == OverlaySource.PhoneLink)
                {
                    phoneLinkCount++;
                }

                snapshot.Add(new PhoneNotificationSnapshotItem(
                    notification.Id,
                    notification.CreationTime,
                    appName,
                    title,
                    body,
                    source));
            }

            LogInventory(xiaomiCount, phoneLinkCount);
            PublishRequests(reconciler.Reconcile(snapshot, now));
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            accessAllowed = false;
            accessRequested = false;
            nextAccessAttemptAt =
                DateTimeOffset.UtcNow + AccessRetryInterval;
            RuntimeLog.Write(
                "Phone notification access was revoked.");
        }
        catch (Exception exception)
        {
            accessAllowed = false;
            accessRequested = false;
            nextAccessAttemptAt =
                DateTimeOffset.UtcNow + AccessRetryInterval;
            RuntimeLog.Write(
                $"Phone notification probe failed: {exception.GetType().Name}");
        }
    }

    private void PublishRequests(
        IReadOnlyList<OverlayRequest> requests)
    {
        foreach (var request in requests)
        {
            var accepted = publicationGate.TryPublish(
                () => publisher.Publish(request));
            if (request.IsActive &&
                request.Kind is OverlayKind.PhoneNotification or
                    OverlayKind.PhoneDynamic)
            {
                RuntimeLog.Write(
                    $"Phone notification event: " +
                    $"source={SourceLabel(request.Source)}, " +
                    $"result={(accepted ? "published" : "deduplicated")}.");
            }
        }
    }

    private static bool TryRead(
        UserNotification notification,
        out string appName,
        out string title,
        out string? body,
        out OverlaySource source)
    {
        appName =
            notification.AppInfo.DisplayInfo.DisplayName?.Trim() ??
            string.Empty;
        source = PhoneNotificationClassifier.SourceForRelayApp(appName);
        if (source == OverlaySource.System)
        {
            title = string.Empty;
            body = null;
            return false;
        }

        var binding = notification.Notification.Visual.GetBinding(
            KnownNotificationBindings.ToastGeneric);
        if (binding is null)
        {
            title = string.Empty;
            body = null;
            return false;
        }

        var texts = binding
            .GetTextElements()
            .Select(element => element.Text?.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Cast<string>()
            .ToArray();
        if (texts.Length == 0)
        {
            title = appName;
            body = null;
            return true;
        }

        title = texts[0];
        body = texts.Length <= 1
            ? null
            : string.Join("  ·  ", texts.Skip(1));
        return true;
    }

    private void LogInventory(int xiaomiCount, int phoneLinkCount)
    {
        var state = $"xiaomi={xiaomiCount}; phone-link={phoneLinkCount}";
        if (string.Equals(
                state,
                lastInventoryState,
                StringComparison.Ordinal))
        {
            return;
        }

        lastInventoryState = state;
        RuntimeLog.Write($"Phone notification inventory: {state}.");
    }

    private static string SourceLabel(OverlaySource source) => source switch
    {
        OverlaySource.XiaomiHyperConnect => "xiaomi",
        OverlaySource.PhoneLink => "phone-link",
        _ => "unknown",
    };

    public void Dispose()
    {
        if (!publicationGate.Close())
        {
            return;
        }

        cancellation.Cancel();
        timer.Dispose();
        var completed = false;
        try
        {
            completed = loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }

        if (completed)
        {
            cancellation.Dispose();
            return;
        }

        _ = loop.ContinueWith(
            completedLoop =>
            {
                _ = completedLoop.Exception;
                cancellation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
