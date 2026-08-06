namespace HS2.CrystalOverlay.Core;

public static class AudioHudProjection
{
    public const string EventId = "audio-hud";

    public static int ToPercent(double scalar) => Math.Clamp(
        (int)Math.Round(
            Math.Clamp(scalar, 0, 1) * 100,
            MidpointRounding.AwayFromZero),
        0,
        100);

    public static OverlayRequest Create(
        double scalar,
        bool isMuted) => Create(ToPercent(scalar), isMuted);

    public static OverlayRequest Create(
        int volumePercent,
        bool isMuted)
    {
        var percent = Math.Clamp(volumePercent, 0, 100);
        var icon = isMuted
            ? AudioHudIcon.Muted
            : percent switch
            {
                0 => AudioHudIcon.Silent,
                <= 33 => AudioHudIcon.Low,
                <= 66 => AudioHudIcon.Medium,
                _ => AudioHudIcon.High,
            };
        return OverlayRequest.Timed(
            EventId,
            OverlayKind.SystemOperation,
            OverlaySource.System,
            isMuted ? "静音" : $"{percent}%",
            body: null,
            visual: new OverlayVisualData(
                AccentHex: "#9CE7FF",
                AudioIcon: icon));
    }
}

public sealed class AudioHudStateTracker
{
    private AudioHudState? previous;

    public OverlayRequest? Observe(
        double scalar,
        bool isMuted) => Observe(
            AudioHudProjection.ToPercent(scalar),
            isMuted);

    public OverlayRequest? Observe(
        int volumePercent,
        bool isMuted)
    {
        var current = new AudioHudState(
            Math.Clamp(volumePercent, 0, 100),
            isMuted);
        if (previous is null)
        {
            previous = current;
            return null;
        }

        if (previous == current)
        {
            return null;
        }

        previous = current;
        return AudioHudProjection.Create(
            current.VolumePercent,
            current.IsMuted);
    }

    private sealed record AudioHudState(
        int VolumePercent,
        bool IsMuted);
}
