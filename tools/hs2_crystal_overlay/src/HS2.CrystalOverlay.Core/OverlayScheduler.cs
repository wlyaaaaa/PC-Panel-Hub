namespace HS2.CrystalOverlay.Core;

public sealed class OverlayScheduler
{
    private static readonly TimeSpan GenericDeduplicationWindow =
        TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PhoneDeduplicationWindow =
        TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MaximumQueuedNotificationAge =
        TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MinimumDismissalSuppressionWindow =
        TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, OverlayItem> items =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, VisibleTimerState> visibleTimers =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeduplicationState> recentDedupKeys =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PhoneDeduplicationState> recentPhoneNotifications = [];
    private readonly List<DismissalSuppressionState> dismissalSuppressions = [];
    private long nextPublishSequence;

    public bool Publish(OverlayRequest request, DateTimeOffset now)
    {
        UpdateVisibleTimers(now);
        RemoveExpired(now);

        if (!request.IsActive)
        {
            RemoveItem(request.EventId);
            return true;
        }

        var policy = OverlayPolicies.For(request.Kind);
        if (IsDismissalSuppressed(request, policy))
        {
            return false;
        }

        if (IsDuplicate(request, policy, now))
        {
            return false;
        }

        var usesVisibleTimer = UsesVisibleTimer(request, policy);
        var existingItem = items.GetValueOrDefault(request.EventId);
        var existingTimer = visibleTimers.GetValueOrDefault(request.EventId);
        var preservesVisibleTimer =
            usesVisibleTimer &&
            existingItem is not null &&
            UsesVisibleTimer(existingItem.Request, existingItem.Policy) &&
            existingTimer is not null &&
            IsSameVisibleOccurrence(existingItem.Request, request);
        var preservesOrderingIdentity =
            existingItem is not null &&
            existingItem.Request.Kind == request.Kind &&
            existingItem.Request.Source == request.Source &&
            (!usesVisibleTimer || preservesVisibleTimer);
        DateTimeOffset? expiresAt =
            policy.Lifetime == OverlayLifetime.Timed &&
            !usesVisibleTimer
                ? now + policy.Duration!.Value
                : null;
        var publishSequence = preservesOrderingIdentity
            ? existingItem!.PublishSequence
            : ++nextPublishSequence;

        items[request.EventId] = new OverlayItem(
            request,
            policy,
            preservesOrderingIdentity
                ? existingItem!.PublishedAt
                : now,
            expiresAt,
            publishSequence);

        if (usesVisibleTimer)
        {
            if (preservesVisibleTimer)
            {
                if (existingTimer!.Remaining is { } remaining &&
                    policy.Duration is { } duration &&
                    remaining > duration)
                {
                    existingTimer.Remaining = duration;
                }
            }
            else
            {
                visibleTimers[request.EventId] =
                    new VisibleTimerState(policy.Duration, now);
            }
        }
        else
        {
            visibleTimers.Remove(request.EventId);
        }

        RecordDeduplication(request, policy, now);
        if (IsStackedNotification(policy))
        {
            RetainLatestNotifications();
        }

        return true;
    }

    public int ClearDismissible(DateTimeOffset now)
    {
        UpdateVisibleTimers(now);
        RemoveExpired(now);

        var dismissible = items.Values
            .Where(item => IsDismissible(item.Request.Kind))
            .ToArray();
        foreach (var item in dismissible)
        {
            dismissalSuppressions.Add(new(
                item.Request,
                DismissalSuppressionExpiry(item, now)));
            RemoveItem(item.Request.EventId);
        }

        return dismissible.Length;
    }

    public OverlayItem? GetPrimaryCard(DateTimeOffset now)
    {
        UpdateVisibleTimers(now);
        RemoveExpired(now);
        return OrderedItems()
            .FirstOrDefault(item =>
                item.Policy.VisualTier is
                    OverlayVisualTier.Crystal or
                    OverlayVisualTier.Emphasis);
    }

    public OverlayFrame GetFrame(
        DateTimeOffset now,
        int maxVisibleNotifications =
            VisibleCardSelector.MaximumRetainedPhoneNotifications) =>
        GetFrame(
            now,
            VisibleCardSelector.MaximumVisibleCards,
            maxVisibleNotifications);

    public OverlayFrame GetFrame(
        DateTimeOffset now,
        int maxVisibleCards,
        int maxVisibleNotifications)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxVisibleCards);
        ArgumentOutOfRangeException.ThrowIfNegative(
            maxVisibleNotifications);
        UpdateVisibleTimers(now);
        RemoveExpired(now);

        var ordered = OrderedItems();
        var direct = ordered
            .Where(item => item.Policy.VisualTier == OverlayVisualTier.Direct)
            .ToArray();
        var cardCandidates = ordered
            .Where(item => item.Policy.VisualTier != OverlayVisualTier.Direct)
            .ToArray();
        var selected = VisibleCardSelector.Select(
            cardCandidates,
            maxVisibleCards,
            maxVisibleNotifications);
        SetVisibleTimerSelection(
            selected.Select(item => item.Request.EventId),
            now);
        var visibleCards = selected
            .Select(item => ProjectVisibleExpiry(item, now))
            .ToArray();
        var cards = visibleCards
            .Where(item => !IsStackedNotification(item.Policy))
            .ToArray();
        var notifications = visibleCards
            .Where(item => IsStackedNotification(item.Policy))
            .ToArray();

        return new OverlayFrame(
            direct,
            cards,
            notifications,
            visibleCards);
    }

    private OverlayItem[] OrderedItems() => items.Values
        .OrderByDescending(item => item.Policy.Priority)
        .ThenByDescending(item => item.PublishedAt)
        .ThenByDescending(item => item.PublishSequence)
        .ToArray();

    private void RetainLatestNotifications()
    {
        foreach (var eventId in items.Values
                     .Where(item => IsStackedNotification(item.Policy))
                     .OrderByDescending(item => item.PublishedAt)
                     .ThenByDescending(item => item.PublishSequence)
                     .Skip(
                         VisibleCardSelector.
                             MaximumRetainedPhoneNotifications)
                     .Select(item => item.Request.EventId)
                     .ToArray())
        {
            RemoveItem(eventId);
        }
    }

    private void UpdateVisibleTimers(DateTimeOffset now)
    {
        foreach (var state in visibleTimers.Values)
        {
            if (state.IsVisible &&
                state.Remaining is { } remaining &&
                now > state.LastUpdated)
            {
                state.Remaining = remaining - (now - state.LastUpdated);
            }

            state.LastUpdated = now;
        }
    }

    private void SetVisibleTimerSelection(
        IEnumerable<string> selectedEventIds,
        DateTimeOffset now)
    {
        var selected = selectedEventIds.ToHashSet(StringComparer.Ordinal);
        foreach (var pair in visibleTimers)
        {
            pair.Value.IsVisible = selected.Contains(pair.Key);
            pair.Value.LastUpdated = now;
        }
    }

    private OverlayItem ProjectVisibleExpiry(
        OverlayItem item,
        DateTimeOffset now)
    {
        if (!visibleTimers.TryGetValue(
                item.Request.EventId,
                out var state))
        {
            return item;
        }

        return item with
        {
            ExpiresAt = state.Remaining is null
                ? null
                : now + state.Remaining.Value,
        };
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var eventId in items
                     .Where(pair =>
                         pair.Value.ExpiresAt is not null &&
                         pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            RemoveItem(eventId);
        }

        foreach (var eventId in visibleTimers
                     .Where(pair =>
                         pair.Value.Remaining is { } remaining &&
                         remaining <= TimeSpan.Zero)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            RemoveItem(eventId);
        }

        foreach (var eventId in items
                     .Where(pair =>
                         IsStackedNotification(pair.Value.Policy) &&
                         now - pair.Value.PublishedAt >=
                         MaximumQueuedNotificationAge)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            RemoveItem(eventId);
        }

        foreach (var key in recentDedupKeys
                     .Where(pair =>
                         now - pair.Value.ObservedAt >=
                         PhoneDeduplicationWindow)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            recentDedupKeys.Remove(key);
        }

        recentPhoneNotifications.RemoveAll(state =>
            now - state.ObservedAt >= PhoneDeduplicationWindow);
        dismissalSuppressions.RemoveAll(state =>
            state.ExpiresAt <= now);
    }

    private void RemoveItem(string eventId)
    {
        items.Remove(eventId);
        visibleTimers.Remove(eventId);
    }

    private bool IsDuplicate(
        OverlayRequest request,
        OverlayPresentationPolicy policy,
        DateTimeOffset now)
    {
        if (IsStackedNotification(policy))
        {
            return recentPhoneNotifications.Any(previous =>
            {
                if (string.Equals(
                        previous.Request.EventId,
                        request.EventId,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                return now - previous.ObservedAt <
                           PhoneDeduplicationWindow &&
                       PhoneNotificationClassifier.
                           AreApproximatelyEquivalent(
                               previous.Request.Title,
                               previous.Request.Body,
                               request.Title,
                               request.Body);
            });
        }

        var normalizedDedupKey = NormalizeDedupKey(request.DedupKey);
        return normalizedDedupKey is not null &&
               recentDedupKeys.TryGetValue(
                   normalizedDedupKey,
                   out var previous) &&
               !string.Equals(
                   previous.EventId,
                   request.EventId,
                   StringComparison.Ordinal) &&
               now - previous.ObservedAt < GenericDeduplicationWindow;
    }

    private void RecordDeduplication(
        OverlayRequest request,
        OverlayPresentationPolicy policy,
        DateTimeOffset now)
    {
        if (IsStackedNotification(policy))
        {
            recentPhoneNotifications.RemoveAll(previous =>
                string.Equals(
                    previous.Request.EventId,
                    request.EventId,
                    StringComparison.Ordinal));
            recentPhoneNotifications.Add(new(request, now));
            return;
        }

        var normalizedDedupKey = NormalizeDedupKey(request.DedupKey);
        if (normalizedDedupKey is not null)
        {
            recentDedupKeys[normalizedDedupKey] = new(
                now,
                request.Source,
                request.EventId);
        }
    }

    private bool IsDismissalSuppressed(
        OverlayRequest request,
        OverlayPresentationPolicy policy)
    {
        foreach (var suppression in dismissalSuppressions)
        {
            var previous = suppression.Request;
            if (previous.Kind != request.Kind)
            {
                continue;
            }

            if (IsStackedNotification(policy))
            {
                var compatibleSources =
                    previous.Source == request.Source ||
                    IsPhoneRelaySource(previous.Source) &&
                    IsPhoneRelaySource(request.Source);
                if (compatibleSources &&
                    PhoneNotificationClassifier.
                        AreApproximatelyEquivalent(
                            previous.Title,
                            previous.Body,
                            request.Title,
                            request.Body))
                {
                    return true;
                }

                continue;
            }

            if (previous.Source == request.Source &&
                string.Equals(
                    SuppressionFingerprint(previous),
                    SuppressionFingerprint(request),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static DateTimeOffset DismissalSuppressionExpiry(
        OverlayItem item,
        DateTimeOffset now)
    {
        if (IsStackedNotification(item.Policy))
        {
            return now + PhoneDeduplicationWindow;
        }

        var duration = item.Policy.Duration ??
                       MinimumDismissalSuppressionWindow;
        return now + (duration > MinimumDismissalSuppressionWindow
            ? duration
            : MinimumDismissalSuppressionWindow);
    }

    private static string SuppressionFingerprint(OverlayRequest request) =>
        NormalizeDedupKey(request.DedupKey) ??
        PhoneNotificationClassifier.DedupKey(
            request.Title,
            request.Body);

    private static bool IsDismissible(OverlayKind kind) =>
        kind is
            OverlayKind.MediaTrackChange or
            OverlayKind.GameAchievement or
            OverlayKind.GameSummary or
            OverlayKind.SystemOperation or
            OverlayKind.DeviceOrNetwork or
            OverlayKind.ImportantTaskComplete or
            OverlayKind.HardwareResolved or
            OverlayKind.PhoneConnection or
            OverlayKind.PhoneNotification or
            OverlayKind.PhoneDynamic;

    private static bool UsesVisibleTimer(
        OverlayRequest request,
        OverlayPresentationPolicy policy) =>
        policy.Lifetime == OverlayLifetime.Timed &&
        (IsStackedNotification(policy) ||
         request.Kind == OverlayKind.GameSummary);

    private static bool IsSameVisibleOccurrence(
        OverlayRequest previous,
        OverlayRequest current)
    {
        if (current.Kind != OverlayKind.GameSummary)
        {
            return true;
        }

        return string.Equals(
            SuppressionFingerprint(previous),
            SuppressionFingerprint(current),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStackedNotification(
        OverlayPresentationPolicy policy) =>
        policy.VisualTier == OverlayVisualTier.StackedNotification;

    private static bool IsPhoneRelaySource(OverlaySource source) =>
        source is
            OverlaySource.XiaomiHyperConnect or
            OverlaySource.PhoneLink;

    private static string? NormalizeDedupKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(
            ' ',
            value.Trim().Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed class VisibleTimerState(
        TimeSpan? remaining,
        DateTimeOffset lastUpdated)
    {
        internal TimeSpan? Remaining { get; set; } = remaining;

        internal DateTimeOffset LastUpdated { get; set; } = lastUpdated;

        internal bool IsVisible { get; set; }
    }

    private sealed record DeduplicationState(
        DateTimeOffset ObservedAt,
        OverlaySource Source,
        string EventId);

    private sealed record PhoneDeduplicationState(
        OverlayRequest Request,
        DateTimeOffset ObservedAt);

    private sealed record DismissalSuppressionState(
        OverlayRequest Request,
        DateTimeOffset ExpiresAt);
}
