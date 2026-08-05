using System.Security.Cryptography;
using System.Text;
using HS2.CrystalOverlay.Core;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace HS2_CrystalOverlay;

internal sealed record ActivePhoneNotification(
    OverlayKind Kind,
    OverlaySource Source,
    string DedupKey);

internal sealed class PhoneNotificationSourceCoordinator : IDisposable
{
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AccessRetryInterval =
        TimeSpan.FromMinutes(1);

    private readonly IOverlayPublisher publisher;
    private readonly UserNotificationListener listener =
        UserNotificationListener.Current;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Dictionary<uint, ActivePhoneNotification> active = [];
    private readonly Dictionary<uint, string> fingerprints = [];
    private readonly HashSet<uint> timedPublished = [];
    private readonly Timer timer;
    private bool baselineCaptured;
    private bool accessRequested;
    private bool accessAllowed;
    private string? lastInventoryState;
    private DateTimeOffset nextAccessAttemptAt = DateTimeOffset.MinValue;
    private int polling;
    private bool disposed;

    internal PhoneNotificationSourceCoordinator(
        IOverlayPublisher publisher)
    {
        this.publisher = publisher;
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
            var now = DateTimeOffset.UtcNow;
            if (!accessAllowed && now < nextAccessAttemptAt)
            {
                return;
            }

            if (!accessRequested || !accessAllowed)
            {
                accessRequested = true;
                var access = await listener.RequestAccessAsync();
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
            var currentIds = new HashSet<uint>();
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

                currentIds.Add(notification.Id);
                var category =
                    PhoneNotificationClassifier.Classify(title, body);
                var isTimed = category is
                    PhoneNotificationCategory.Ordinary or
                    PhoneNotificationCategory.Dynamic;
                var fingerprint =
                    PhoneNotificationClassifier.DedupKey(
                        title,
                        body);
                var known = fingerprints.TryGetValue(
                    notification.Id,
                    out var previousFingerprint);
                var changed =
                    !string.Equals(
                        previousFingerprint,
                        fingerprint,
                        StringComparison.Ordinal);
                if (!baselineCaptured)
                {
                    fingerprints[notification.Id] = fingerprint;
                    continue;
                }

                if (isTimed)
                {
                    EndActive(notification.Id);
                    if ((!known || changed) &&
                        timedPublished.Add(notification.Id))
                    {
                        PublishTimed(
                            notification.Id,
                            category,
                            source,
                            appName,
                            title,
                            body);
                    }
                }
                else
                {
                    if (!known || changed)
                    {
                        PublishPersistent(
                            notification.Id,
                            category,
                            source,
                            appName,
                            title,
                            body);
                    }
                }

                fingerprints[notification.Id] = fingerprint;
            }

            LogInventory(xiaomiCount, phoneLinkCount);
            baselineCaptured = true;
            foreach (var id in fingerprints.Keys
                         .Where(id => !currentIds.Contains(id))
                         .ToArray())
            {
                EndActive(id);
                fingerprints.Remove(id);
                timedPublished.Remove(id);
            }
        }
        catch (OperationCanceledException)
        {
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
        finally
        {
            Interlocked.Exchange(ref polling, 0);
        }
    }

    private void PublishTimed(
        uint id,
        PhoneNotificationCategory category,
        OverlaySource source,
        string appName,
        string title,
        string? body)
    {
        var accepted = publisher.Publish(OverlayRequest.Timed(
            EventId(id),
            category == PhoneNotificationCategory.Dynamic
                ? OverlayKind.PhoneDynamic
                : OverlayKind.PhoneNotification,
            source,
            title,
            body,
            dedupKey: PhoneNotificationClassifier.DedupKey(
                title,
                body),
            visual: new OverlayVisualData(
                Eyebrow: category == PhoneNotificationCategory.Dynamic
                    ? "手机动态 / LIVE"
                    : "手机通知 / PHONE",
                Subtitle: appName,
                AccentHex: "#70F0B2")));
        RuntimeLog.Write(
            $"Phone notification event: source={SourceLabel(source)}, " +
            $"result={(accepted ? "published" : "deduplicated")}.");
    }

    private void PublishPersistent(
        uint id,
        PhoneNotificationCategory category,
        OverlaySource source,
        string appName,
        string title,
        string? body)
    {
        var dedupKey = PhoneNotificationClassifier.DedupKey(
            title,
            body);
        if (active.TryGetValue(id, out var previous) &&
            !string.Equals(
                previous.DedupKey,
                dedupKey,
                StringComparison.Ordinal))
        {
            EndActive(id);
        }

        var kind = category switch
        {
            PhoneNotificationCategory.Call => OverlayKind.PhoneCall,
            PhoneNotificationCategory.Transfer => OverlayKind.PhoneTransfer,
            _ => throw new ArgumentOutOfRangeException(nameof(category)),
        };
        _ = publisher.Publish(OverlayRequest.End(
            EventId(id),
            OverlayKind.PhoneNotification,
            source));
        _ = publisher.Publish(OverlayRequest.Active(
            ActiveEventId(dedupKey),
            kind,
            source,
            title,
            body,
            visual: new OverlayVisualData(
                Eyebrow: category switch
                {
                    PhoneNotificationCategory.Call =>
                        "手机来电 / CALL",
                    _ => "跨设备传输 / TRANSFER",
                },
                Subtitle: appName,
                AccentHex: category ==
                           PhoneNotificationCategory.Call
                    ? "#FF9EAE"
                    : "#70F0B2")));
        active[id] = new ActivePhoneNotification(
            kind,
            source,
            dedupKey);
    }

    private void EndActive(uint id)
    {
        if (!active.Remove(id, out var existing))
        {
            return;
        }

        if (active.Values.Any(candidate =>
                string.Equals(
                    candidate.DedupKey,
                    existing.DedupKey,
                    StringComparison.Ordinal)))
        {
            return;
        }

        _ = publisher.Publish(OverlayRequest.End(
            ActiveEventId(existing.DedupKey),
            existing.Kind,
            existing.Source));
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

    private static string EventId(uint id) =>
        $"phone-notification:{id}";

    private static string ActiveEventId(string dedupKey)
    {
        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(dedupKey));
        return $"phone-active:{Convert.ToHexStringLower(hash)[..20]}";
    }

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
