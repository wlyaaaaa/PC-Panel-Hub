namespace HS2.CrystalOverlay.Core;

public enum OverlayKind
{
    Glance,
    MediaActive,
    MediaTrackChange,
    GameActive,
    GameAchievement,
    GameSummary,
    SystemOperation,
    DeviceOrNetwork,
    ImportantTask,
    ImportantTaskComplete,
    HardwareAlert,
    HardwareResolved,
    PhoneBattery,
    PhoneConnection,
    PhoneNotification,
    PhoneDynamic,
    PhoneCall,
    PhoneTransfer,
}

public enum OverlaySource
{
    System,
    NetEase,
    Steam,
    Game,
    Task,
    Hardware,
    XiaomiHyperConnect,
    PhoneLink,
}

public enum OverlayVisualTier
{
    Direct,
    Crystal,
    Emphasis,
}

public enum OverlayLifetime
{
    Timed,
    WhileActive,
}

public sealed record TypographyScale(
    double TitlePx,
    double BodyPx,
    double MetaPx,
    int MaxBodyLines);

public sealed record OverlayPresentationPolicy(
    OverlayVisualTier VisualTier,
    OverlayLifetime Lifetime,
    TimeSpan? Duration,
    int Priority,
    TypographyScale Typography,
    bool CanPin = false);

public sealed record OverlayVisualData(
    string? Eyebrow = null,
    string? Subtitle = null,
    string? Meta = null,
    double? Progress = null,
    string? ArtworkPath = null,
    string? AccentHex = null,
    bool? IsCharging = null,
    double? MarqueeProgress = null,
    string? TranslatedTitle = null,
    string? SecondaryBody = null);

public sealed record OverlayRequest(
    string EventId,
    OverlayKind Kind,
    OverlaySource Source,
    string Title,
    string? Body,
    string? DedupKey,
    bool IsActive,
    OverlayVisualData? Visual = null)
{
    public static OverlayRequest Active(
        string eventId,
        OverlayKind kind,
        OverlaySource source,
        string title,
        string? body = null,
        string? dedupKey = null,
        OverlayVisualData? visual = null) =>
        new(eventId, kind, source, title, body, dedupKey, true, visual);

    public static OverlayRequest Timed(
        string eventId,
        OverlayKind kind,
        OverlaySource source,
        string title,
        string? body = null,
        string? dedupKey = null,
        OverlayVisualData? visual = null) =>
        new(eventId, kind, source, title, body, dedupKey, true, visual);

    public static OverlayRequest End(
        string eventId,
        OverlayKind kind,
        OverlaySource source) =>
        new(eventId, kind, source, string.Empty, null, null, false);
}

public sealed record OverlayItem(
    OverlayRequest Request,
    OverlayPresentationPolicy Policy,
    DateTimeOffset PublishedAt,
    DateTimeOffset? ExpiresAt);

public sealed record OverlayFrame(
    IReadOnlyList<OverlayItem> DirectItems,
    IReadOnlyList<OverlayItem> Cards)
{
    public OverlayItem? PrimaryCard => Cards.FirstOrDefault();
}
