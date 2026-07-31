namespace HS2.CrystalOverlay.Core;

public sealed class OverlayScheduler
{
    private static readonly TimeSpan DeduplicationWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaximumQueuedNotificationAge =
        TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, OverlayItem> items =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, NotificationTimerState>
        notificationTimers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> recentDedupKeys =
        new(StringComparer.OrdinalIgnoreCase);

    public bool Publish(OverlayRequest request, DateTimeOffset now)
    {
        UpdateNotificationTimers(now);
        RemoveExpired(now);

        if (!request.IsActive)
        {
            items.Remove(request.EventId);
            notificationTimers.Remove(request.EventId);
            return true;
        }

        var normalizedDedupKey = NormalizeDedupKey(request.DedupKey);
        if (normalizedDedupKey is not null &&
            recentDedupKeys.TryGetValue(normalizedDedupKey, out var previous) &&
            now - previous < DeduplicationWindow &&
            (!items.TryGetValue(request.EventId, out var sameItem) ||
             !string.Equals(
                 NormalizeDedupKey(sameItem.Request.DedupKey),
                 normalizedDedupKey,
                 StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var policy = OverlayPolicies.For(request.Kind);
        var isStackedNotification =
            policy.VisualTier == OverlayVisualTier.StackedNotification;
        var existingItem = items.GetValueOrDefault(request.EventId);
        var existingNotificationTimer =
            notificationTimers.GetValueOrDefault(request.EventId);
        var preservesNotificationTimer =
            isStackedNotification &&
            existingItem is not null &&
            existingItem.Policy.VisualTier ==
            OverlayVisualTier.StackedNotification &&
            existingNotificationTimer is not null;
        DateTimeOffset? expiresAt =
            policy.Lifetime == OverlayLifetime.Timed &&
            !isStackedNotification
            ? now + policy.Duration!.Value
            : null;

        items[request.EventId] = new OverlayItem(
            request,
            policy,
            preservesNotificationTimer
                ? existingItem!.PublishedAt
                : now,
            expiresAt);

        if (isStackedNotification)
        {
            if (preservesNotificationTimer)
            {
                if (existingNotificationTimer!.Remaining is { } remaining &&
                    policy.Duration is { } duration &&
                    remaining > duration)
                {
                    existingNotificationTimer.Remaining = duration;
                }
            }
            else
            {
                notificationTimers[request.EventId] =
                    new NotificationTimerState(policy.Duration, now);
            }
        }
        else
        {
            notificationTimers.Remove(request.EventId);
        }

        if (normalizedDedupKey is not null)
        {
            recentDedupKeys[normalizedDedupKey] = now;
        }

        return true;
    }

    public OverlayItem? GetPrimaryCard(DateTimeOffset now)
    {
        UpdateNotificationTimers(now);
        RemoveExpired(now);
        return OrderedItems()
            .FirstOrDefault(item =>
                item.Policy.VisualTier is
                    OverlayVisualTier.Crystal or
                    OverlayVisualTier.Emphasis);
    }

    public OverlayFrame GetFrame(
        DateTimeOffset now,
        int maxVisibleNotifications = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(
            maxVisibleNotifications);
        UpdateNotificationTimers(now);
        RemoveExpired(now);

        var ordered = OrderedItems();

        var direct = ordered
            .Where(item => item.Policy.VisualTier == OverlayVisualTier.Direct)
            .ToArray();
        var cards = ordered
            .Where(item => item.Policy.VisualTier != OverlayVisualTier.Direct)
            .Where(item =>
                item.Policy.VisualTier !=
                OverlayVisualTier.StackedNotification)
            .ToArray();
        var notifications = SelectVisibleNotifications(
            now,
            maxVisibleNotifications);

        return new OverlayFrame(direct, cards, notifications);
    }

    private OverlayItem[] OrderedItems() => items.Values
        .OrderByDescending(item => item.Policy.Priority)
        .ThenByDescending(item => item.PublishedAt)
        .ToArray();

    private OverlayItem[] SelectVisibleNotifications(
        DateTimeOffset now,
        int capacity)
    {
        var candidates = items.Values
            .Where(item =>
                item.Policy.VisualTier ==
                OverlayVisualTier.StackedNotification)
            .OrderBy(item => item.PublishedAt)
            .ToArray();
        var selected = candidates
            .Where(item =>
                notificationTimers[item.Request.EventId].IsVisible)
            .Concat(candidates.Where(item =>
                !notificationTimers[item.Request.EventId].IsVisible))
            .Take(capacity)
            .ToArray();
        var selectedIds = selected
            .Select(item => item.Request.EventId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            var state = notificationTimers[candidate.Request.EventId];
            state.IsVisible = selectedIds.Contains(
                candidate.Request.EventId);
            state.HasBeenVisible |= state.IsVisible;
            state.LastUpdated = now;
        }

        return selected
            .Select(item =>
            {
                var remaining =
                    notificationTimers[item.Request.EventId].Remaining;
                return item with
                {
                    ExpiresAt = remaining is null
                        ? null
                        : now + remaining.Value,
                };
            })
            .ToArray();
    }

    private void UpdateNotificationTimers(DateTimeOffset now)
    {
        foreach (var state in notificationTimers.Values)
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

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var eventId in items
                     .Where(pair =>
                         pair.Value.ExpiresAt is not null &&
                         pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            items.Remove(eventId);
        }

        foreach (var eventId in notificationTimers
                     .Where(pair =>
                         pair.Value.Remaining is { } remaining &&
                         remaining <= TimeSpan.Zero)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            notificationTimers.Remove(eventId);
            items.Remove(eventId);
        }

        foreach (var eventId in items
                     .Where(pair =>
                         pair.Value.Policy.VisualTier ==
                         OverlayVisualTier.StackedNotification &&
                         notificationTimers.TryGetValue(
                             pair.Key,
                             out var state) &&
                         !state.HasBeenVisible &&
                         now - pair.Value.PublishedAt >=
                         MaximumQueuedNotificationAge)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            notificationTimers.Remove(eventId);
            items.Remove(eventId);
        }

        foreach (var key in recentDedupKeys
                     .Where(pair => now - pair.Value >= DeduplicationWindow)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            recentDedupKeys.Remove(key);
        }
    }

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

    private sealed class NotificationTimerState(
        TimeSpan? remaining,
        DateTimeOffset lastUpdated)
    {
        internal TimeSpan? Remaining { get; set; } = remaining;

        internal DateTimeOffset LastUpdated { get; set; } = lastUpdated;

        internal bool IsVisible { get; set; }

        internal bool HasBeenVisible { get; set; }
    }
}
