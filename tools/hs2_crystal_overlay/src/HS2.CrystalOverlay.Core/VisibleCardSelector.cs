namespace HS2.CrystalOverlay.Core;

public static class VisibleCardSelector
{
    public const int MaximumVisibleCards = 6;
    public const int MaximumRetainedPhoneNotifications = 3;

    public static IReadOnlyList<OverlayItem> Select(
        IReadOnlyList<OverlayItem> orderedCandidates,
        int capacity = MaximumVisibleCards,
        int notificationCapacity = MaximumRetainedPhoneNotifications)
    {
        ArgumentNullException.ThrowIfNull(orderedCandidates);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentOutOfRangeException.ThrowIfNegative(notificationCapacity);

        var effectiveCapacity = Math.Min(capacity, MaximumVisibleCards);
        if (effectiveCapacity == 0 || orderedCandidates.Count == 0)
        {
            return [];
        }

        var effectiveNotificationCapacity = Math.Min(
            notificationCapacity,
            MaximumRetainedPhoneNotifications);
        var indexed = orderedCandidates
            .Select((item, index) => new IndexedItem(item, index))
            .ToArray();
        var notifications = indexed
            .Where(candidate => IsStackedNotification(candidate.Item))
            .OrderByDescending(candidate => candidate.Item.PublishedAt)
            .ThenByDescending(candidate => candidate.Item.PublishSequence)
            .Take(effectiveNotificationCapacity)
            .ToArray();
        var latestNotification = notifications.FirstOrDefault();
        var selected = new HashSet<string>(StringComparer.Ordinal);

        if (latestNotification is not null)
        {
            selected.Add(latestNotification.Item.Request.EventId);
        }

        var protectedCards = indexed
            .Where(candidate => IsProtected(candidate.Item))
            .DistinctBy(candidate => candidate.Item.Request.EventId)
            .OrderByDescending(candidate => ProtectionPriority(candidate.Item))
            .ThenBy(candidate => candidate.Index);
        AddUntilFull(protectedCards, selected, effectiveCapacity);

        AddUntilFull(
            indexed.Where(candidate =>
                !IsStackedNotification(candidate.Item)),
            selected,
            effectiveCapacity);
        AddUntilFull(
            notifications.Skip(latestNotification is null ? 0 : 1),
            selected,
            effectiveCapacity);

        return indexed
            .Where(candidate => selected.Contains(
                candidate.Item.Request.EventId))
            .Select(candidate => candidate.Item)
            .ToArray();
    }

    private static void AddUntilFull(
        IEnumerable<IndexedItem> candidates,
        ISet<string> selected,
        int capacity)
    {
        foreach (var candidate in candidates)
        {
            if (selected.Count >= capacity)
            {
                return;
            }

            selected.Add(candidate.Item.Request.EventId);
        }
    }

    private static bool IsStackedNotification(OverlayItem item) =>
        item.Policy.VisualTier == OverlayVisualTier.StackedNotification;

    private static bool IsProtected(OverlayItem item) =>
        item.Request.Kind is
            OverlayKind.HardwareAlert or
            OverlayKind.PhoneCall or
            OverlayKind.PhoneVerificationCode or
            OverlayKind.PhoneTransfer or
            OverlayKind.ImportantTask or
            OverlayKind.ImportantTaskComplete or
            OverlayKind.MediaActive or
            OverlayKind.GameActive or
            OverlayKind.GameSummary;

    private static int ProtectionPriority(OverlayItem item) =>
        item.Request.Kind switch
        {
            OverlayKind.HardwareAlert => 700,
            OverlayKind.PhoneCall => 600,
            OverlayKind.PhoneVerificationCode => 590,
            OverlayKind.PhoneTransfer => 570,
            OverlayKind.ImportantTaskComplete => 550,
            OverlayKind.ImportantTask => 530,
            OverlayKind.GameActive => 400,
            OverlayKind.MediaActive => 300,
            OverlayKind.GameSummary => 200,
            _ => 100,
        };

    private sealed record IndexedItem(OverlayItem Item, int Index);
}
