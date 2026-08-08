using System.Security.Cryptography;
using System.Text;

namespace HS2.CrystalOverlay.Core;

public sealed record PhoneNotificationSnapshotItem(
    uint Id,
    DateTimeOffset CreationTime,
    string AppName,
    string Title,
    string? Body,
    OverlaySource Source);

internal sealed record TrackedPhoneNotification(
    OverlayKind Kind,
    OverlaySource Source,
    string EventId,
    string Title,
    string? Body,
    DateTimeOffset LastObservedAt);

public sealed class PhoneNotificationSnapshotReconciler
{
    private readonly TimeSpan activeSafetyLease;
    private readonly Dictionary<uint, TrackedPhoneNotification> active = [];
    private readonly Dictionary<uint, string> fingerprints = [];
    private readonly Dictionary<uint, PhoneNotificationCategory> categories = [];
    private bool baselineCaptured;

    public PhoneNotificationSnapshotReconciler(TimeSpan activeSafetyLease)
    {
        if (activeSafetyLease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(activeSafetyLease));
        }

        this.activeSafetyLease = activeSafetyLease;
    }

    public IReadOnlyList<OverlayRequest> Reconcile(
        IEnumerable<PhoneNotificationSnapshotItem> snapshot,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var requests = new List<OverlayRequest>();
        var currentIds = new HashSet<uint>();
        var ordered = snapshot
            .Select(item => new
            {
                Item = item,
                Category = PhoneNotificationClassifier.Classify(
                    item.Title,
                    item.Body),
                IsPlaceholder = PhoneNotificationClassifier.
                    IsPlaceholderNotification(item.Title, item.Body),
            })
            .OrderBy(candidate => candidate.Item.CreationTime)
            .ThenBy(candidate => candidate.Item.Id)
            .ToArray();
        var newestVerification = ordered.LastOrDefault(candidate =>
            !candidate.IsPlaceholder &&
            candidate.Category ==
            PhoneNotificationCategory.VerificationCode);
        foreach (var candidate in ordered)
        {
            var item = candidate.Item;
            if (candidate.IsPlaceholder)
            {
                if (fingerprints.ContainsKey(item.Id))
                {
                    var previousCategory = categories[item.Id];
                    EndActive(item.Id, requests);
                    EndTimed(
                        item.Id,
                        previousCategory,
                        item.Source,
                        requests);
                    fingerprints.Remove(item.Id);
                    categories.Remove(item.Id);
                }

                continue;
            }

            currentIds.Add(item.Id);
            var category = candidate.Category;
            var isTimed = category is
                PhoneNotificationCategory.Ordinary or
                PhoneNotificationCategory.Dynamic or
                PhoneNotificationCategory.VerificationCode;
            var fingerprint = PhoneNotificationClassifier.DedupKey(
                item.Title,
                item.Body);
            var known = fingerprints.TryGetValue(
                item.Id,
                out var previousFingerprint);
            var changed = !string.Equals(
                previousFingerprint,
                fingerprint,
                StringComparison.Ordinal);

            if (!baselineCaptured)
            {
                fingerprints[item.Id] = fingerprint;
                categories[item.Id] = category;
                if (!isTimed)
                {
                    PublishPersistent(item, category, now, requests);
                }

                continue;
            }

            if (isTimed)
            {
                EndActive(item.Id, requests);
                var isNewestVerification =
                    category !=
                        PhoneNotificationCategory.VerificationCode ||
                    newestVerification?.Item.Id == item.Id;
                if (isNewestVerification && (!known || changed))
                {
                    requests.Add(TimedRequest(item, category));
                }
            }
            else if (PhoneNotificationClassifier.ShouldPublishPersistent(
                         known,
                         changed,
                         active.ContainsKey(item.Id)))
            {
                if (categories.TryGetValue(item.Id, out var previous) &&
                    previous is PhoneNotificationCategory.Ordinary or
                        PhoneNotificationCategory.Dynamic)
                {
                    requests.Add(OverlayRequest.End(
                        TimedEventId(item.Id),
                        OverlayKind.PhoneNotification,
                        item.Source));
                }

                PublishPersistent(item, category, now, requests);
            }
            else if (active.TryGetValue(item.Id, out var existing))
            {
                active[item.Id] = existing with
                {
                    LastObservedAt = now,
                };
            }

            fingerprints[item.Id] = fingerprint;
            categories[item.Id] = category;
        }

        baselineCaptured = true;
        foreach (var id in fingerprints.Keys
                     .Where(id => !currentIds.Contains(id))
                     .ToArray())
        {
            EndActive(id, requests);
            fingerprints.Remove(id);
            categories.Remove(id);
        }

        return requests;
    }

    private static void EndTimed(
        uint id,
        PhoneNotificationCategory category,
        OverlaySource source,
        ICollection<OverlayRequest> requests)
    {
        var kind = category switch
        {
            PhoneNotificationCategory.Ordinary =>
                OverlayKind.PhoneNotification,
            PhoneNotificationCategory.Dynamic =>
                OverlayKind.PhoneDynamic,
            _ => (OverlayKind?)null,
        };
        if (kind is null)
        {
            return;
        }

        requests.Add(OverlayRequest.End(
            TimedEventId(id),
            kind.Value,
            source));
    }

    public IReadOnlyList<OverlayRequest> ExpireStale(DateTimeOffset now)
    {
        var requests = new List<OverlayRequest>();
        foreach (var id in active
                     .Where(pair =>
                         now - pair.Value.LastObservedAt >=
                         activeSafetyLease)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            EndActive(id, requests);
        }

        return requests;
    }

    private void PublishPersistent(
        PhoneNotificationSnapshotItem item,
        PhoneNotificationCategory category,
        DateTimeOffset now,
        ICollection<OverlayRequest> requests)
    {
        EndActive(item.Id, requests);
        var kind = category switch
        {
            PhoneNotificationCategory.Call => OverlayKind.PhoneCall,
            PhoneNotificationCategory.Transfer => OverlayKind.PhoneTransfer,
            _ => throw new ArgumentOutOfRangeException(nameof(category)),
        };
        var eventId = PhoneNotificationClassifier.ResolveActiveEventId(
            active.Values.Select(candidate =>
                new PhoneActiveNotificationIdentity(
                    candidate.EventId,
                    candidate.Kind,
                    candidate.Title,
                    candidate.Body)),
            kind,
            item.Title,
            item.Body,
            ActiveEventId(PhoneNotificationClassifier.DedupKey(
                item.Title,
                item.Body)));
        requests.Add(OverlayRequest.Active(
            eventId,
            kind,
            item.Source,
            item.Title,
            item.Body,
            visual: new OverlayVisualData(
                Eyebrow: category switch
                {
                    PhoneNotificationCategory.Call =>
                        "手机来电 / CALL",
                    _ => "跨设备传输 / TRANSFER",
                },
                Subtitle: item.AppName,
                AccentHex: category == PhoneNotificationCategory.Call
                    ? "#FF9EAE"
                    : "#70F0B2")));
        active[item.Id] = new TrackedPhoneNotification(
            kind,
            item.Source,
            eventId,
            item.Title,
            item.Body,
            now);
    }

    private void EndActive(
        uint id,
        ICollection<OverlayRequest> requests)
    {
        if (!active.Remove(id, out var existing) ||
            active.Values.Any(candidate => string.Equals(
                candidate.EventId,
                existing.EventId,
                StringComparison.Ordinal)))
        {
            return;
        }

        requests.Add(OverlayRequest.End(
            existing.EventId,
            existing.Kind,
            existing.Source));
    }

    private static OverlayRequest TimedRequest(
        PhoneNotificationSnapshotItem item,
        PhoneNotificationCategory category)
    {
        if (category == PhoneNotificationCategory.VerificationCode)
        {
            if (!PhoneVerificationCodeDetector.TryExtract(
                    item.Title,
                    item.Body,
                    out var code))
            {
                throw new InvalidOperationException(
                    "Verification notification did not contain a code.");
            }

            var codeIsInTitle = ContainsCode(item.Title, code);

            return OverlayRequest.Timed(
                VerificationCodeEventId,
                OverlayKind.PhoneVerificationCode,
                item.Source,
                code,
                body: null,
                dedupKey: $"verification-code:{code}",
                visual: new OverlayVisualData(
                    Eyebrow: "验证码 / CODE",
                    Subtitle: codeIsInTitle
                        ? item.AppName
                        : item.Title,
                    Meta: codeIsInTitle ? null : item.AppName,
                    AccentHex: "#70F0B2",
                    VerificationCode: code));
        }

        return OverlayRequest.Timed(
            TimedEventId(item.Id),
            category == PhoneNotificationCategory.Dynamic
                ? OverlayKind.PhoneDynamic
                : OverlayKind.PhoneNotification,
            item.Source,
            item.Title,
            item.Body,
            dedupKey: PhoneNotificationClassifier.DedupKey(
                item.Title,
                item.Body),
            visual: new OverlayVisualData(
                Eyebrow: category == PhoneNotificationCategory.Dynamic
                    ? "手机动态 / LIVE"
                    : "手机通知 / PHONE",
                Subtitle: item.AppName,
                AccentHex: "#70F0B2"));
    }

    private const string VerificationCodeEventId =
        "phone-verification-code";

    private static bool ContainsCode(string value, string code) =>
        string.Concat(value
                .Normalize(NormalizationForm.FormKC)
                .Where(char.IsAsciiDigit))
            .Contains(code, StringComparison.Ordinal);

    private static string TimedEventId(uint id) =>
        $"phone-notification:{id}";

    private static string ActiveEventId(string dedupKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(dedupKey));
        return $"phone-active:{Convert.ToHexStringLower(hash)[..20]}";
    }
}
