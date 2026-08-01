namespace HS2.CrystalOverlay.Core;

public static class OverlayPolicies
{
    private static readonly TypographyScale Ambient =
        new(48, 0, 30, 0);
    private static readonly TypographyScale Normal =
        new(60, 44, 34, 2);
    private static readonly TypographyScale Phone =
        new(64, 48, 36, 2);
    private static readonly TypographyScale PhoneMessage =
        new(64, 48, 36, 0);
    private static readonly TypographyScale Critical =
        new(76, 48, 36, 2);

    private static readonly IReadOnlyDictionary<OverlayKind, OverlayPresentationPolicy>
        Policies = new Dictionary<OverlayKind, OverlayPresentationPolicy>
        {
            [OverlayKind.Glance] = Active(
                OverlayVisualTier.Direct, 300, Normal),
            [OverlayKind.MediaActive] = Active(
                OverlayVisualTier.Crystal, 200, Normal),
            [OverlayKind.MediaTrackChange] = Timed(
                OverlayVisualTier.Crystal, 8, 610, Normal),
            [OverlayKind.GameActive] = Active(
                OverlayVisualTier.Crystal, 180, Normal),
            [OverlayKind.GameAchievement] = Timed(
                OverlayVisualTier.Crystal, 12, 620, Normal),
            [OverlayKind.GameSummary] = Timed(
                OverlayVisualTier.Crystal, 20, 600, Normal),
            [OverlayKind.SystemOperation] = Timed(
                OverlayVisualTier.Crystal, 6, 640, Normal),
            [OverlayKind.DeviceOrNetwork] = Timed(
                OverlayVisualTier.Crystal, 12, 650, Normal),
            [OverlayKind.ImportantTask] = Active(
                OverlayVisualTier.Crystal, 700, Normal),
            [OverlayKind.ImportantTaskComplete] = Timed(
                OverlayVisualTier.Crystal, 15, 710, Normal),
            [OverlayKind.HardwareAlert] = Active(
                OverlayVisualTier.Emphasis, 1000, Critical),
            [OverlayKind.HardwareResolved] = Timed(
                OverlayVisualTier.Crystal, 10, 990, Normal),
            [OverlayKind.PhoneBattery] = Active(
                OverlayVisualTier.Direct, 50, Ambient),
            [OverlayKind.PhoneConnection] = Timed(
                OverlayVisualTier.StackedNotification, 5, 750, Phone),
            [OverlayKind.PhoneNotification] = Timed(
                OverlayVisualTier.StackedNotification,
                60,
                800,
                PhoneMessage),
            [OverlayKind.PhoneDynamic] = Timed(
                OverlayVisualTier.StackedNotification, 60, 850, Phone),
            [OverlayKind.PhoneCall] = Active(
                OverlayVisualTier.Emphasis, 950, Critical),
            [OverlayKind.PhoneTransfer] = Active(
                OverlayVisualTier.Emphasis, 900, Critical),
        };

    public static OverlayPresentationPolicy For(OverlayKind kind) =>
        Policies.TryGetValue(kind, out var policy)
            ? policy
            : throw new ArgumentOutOfRangeException(nameof(kind), kind, null);

    private static OverlayPresentationPolicy Timed(
        OverlayVisualTier tier,
        int seconds,
        int priority,
        TypographyScale typography,
        bool canPin = false) =>
        new(
            tier,
            OverlayLifetime.Timed,
            TimeSpan.FromSeconds(seconds),
            priority,
            typography,
            canPin);

    private static OverlayPresentationPolicy Active(
        OverlayVisualTier tier,
        int priority,
        TypographyScale typography) =>
        new(tier, OverlayLifetime.WhileActive, null, priority, typography);
}
