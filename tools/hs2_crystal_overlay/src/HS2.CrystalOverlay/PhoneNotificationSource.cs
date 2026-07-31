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

    private readonly IOverlayPublisher publisher;
    private readonly UserNotificationListener listener =
        UserNotificationListener.Current;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Dictionary<uint, ActivePhoneNotification> active = [];
    private readonly Dictionary<uint, string> fingerprints = [];
    private readonly Timer timer;
    private bool baselineCaptured;
    private bool accessRequested;
    private bool accessAllowed;
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
            if (!accessRequested)
            {
                accessRequested = true;
                var access = await listener.RequestAccessAsync();
                accessAllowed =
                    access == UserNotificationListenerAccessStatus.Allowed;
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
            foreach (var notification in notifications)
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

                currentIds.Add(notification.Id);
                var category =
                    PhoneNotificationClassifier.Classify(title, body);
                var isPersistent =
                    category != PhoneNotificationCategory.Ordinary;
                var fingerprint =
                    PhoneNotificationClassifier.DedupKey(
                        appName,
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

                if (isPersistent)
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
                else
                {
                    EndActive(notification.Id);
                    if (!known || changed)
                    {
                        PublishOrdinary(
                            notification.Id,
                            source,
                            appName,
                            title,
                            body);
                    }
                }

                fingerprints[notification.Id] = fingerprint;
            }

            baselineCaptured = true;
            foreach (var id in fingerprints.Keys
                         .Where(id => !currentIds.Contains(id))
                         .ToArray())
            {
                EndActive(id);
                fingerprints.Remove(id);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (UnauthorizedAccessException)
        {
            accessAllowed = false;
            RuntimeLog.Write(
                "Phone notification access was revoked.");
        }
        catch (Exception exception)
        {
            RuntimeLog.Write(
                $"Phone notification probe failed: {exception.GetType().Name}");
        }
        finally
        {
            Interlocked.Exchange(ref polling, 0);
        }
    }

    private void PublishOrdinary(
        uint id,
        OverlaySource source,
        string appName,
        string title,
        string? body)
    {
        _ = publisher.Publish(OverlayRequest.Timed(
            EventId(id),
            OverlayKind.PhoneNotification,
            source,
            title,
            body,
            dedupKey: PhoneNotificationClassifier.DedupKey(
                appName,
                title,
                body),
            visual: new OverlayVisualData(
                Eyebrow: "手机通知 / PHONE",
                Subtitle: appName,
                AccentHex: "#9FE8FF")));
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
            appName,
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
            _ => OverlayKind.PhoneDynamic,
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
                    PhoneNotificationCategory.Transfer =>
                        "跨设备传输 / TRANSFER",
                    _ => "手机动态 / LIVE",
                },
                Subtitle: appName,
                AccentHex: category ==
                           PhoneNotificationCategory.Call
                    ? "#FF9EAE"
                    : "#9FE8FF")));
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
        source = SourceFor(appName);
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

    private static OverlaySource SourceFor(string appName)
    {
        if (appName.Contains(
                "Phone Link",
                StringComparison.OrdinalIgnoreCase) ||
            appName.Contains(
                "手机连接",
                StringComparison.OrdinalIgnoreCase) ||
            appName.Contains(
                "Link to Windows",
                StringComparison.OrdinalIgnoreCase))
        {
            return OverlaySource.PhoneLink;
        }

        if (appName.Contains(
                "Xiaomi",
                StringComparison.OrdinalIgnoreCase) ||
            appName.Contains(
                "小米",
                StringComparison.OrdinalIgnoreCase) ||
            appName.Contains(
                "妙享",
                StringComparison.OrdinalIgnoreCase) ||
            appName.Contains(
                "MiSmartShare",
                StringComparison.OrdinalIgnoreCase))
        {
            return OverlaySource.XiaomiHyperConnect;
        }

        return OverlaySource.System;
    }

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
