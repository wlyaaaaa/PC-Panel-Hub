namespace HS2.CrystalOverlay.Core;

public sealed class OverlayScheduler
{
    private static readonly TimeSpan DeduplicationWindow = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, OverlayItem> items =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> recentDedupKeys =
        new(StringComparer.OrdinalIgnoreCase);

    public bool Publish(OverlayRequest request, DateTimeOffset now)
    {
        RemoveExpired(now);

        if (!request.IsActive)
        {
            items.Remove(request.EventId);
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
        DateTimeOffset? expiresAt = policy.Lifetime == OverlayLifetime.Timed
            ? now + policy.Duration!.Value
            : null;

        items[request.EventId] = new OverlayItem(
            request,
            policy,
            now,
            expiresAt);

        if (normalizedDedupKey is not null)
        {
            recentDedupKeys[normalizedDedupKey] = now;
        }

        return true;
    }

    public OverlayFrame GetFrame(DateTimeOffset now)
    {
        RemoveExpired(now);

        var ordered = items.Values
            .OrderByDescending(item => item.Policy.Priority)
            .ThenByDescending(item => item.PublishedAt)
            .ToArray();

        var direct = ordered
            .Where(item => item.Policy.VisualTier == OverlayVisualTier.Direct)
            .ToArray();
        var cards = ordered
            .Where(item => item.Policy.VisualTier != OverlayVisualTier.Direct)
            .ToArray();

        return new OverlayFrame(direct, cards);
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
}
