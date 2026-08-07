namespace HS2.CrystalOverlay.Core;

public static class MarqueeMotion
{
    private const double StartHold = 0.03;
    private const double EndHold = 0.97;
    private const double NotificationStartHold = 0.16;
    private const double NotificationEndHold = 0.16;

    // Long phone notifications should remain readable without taking the
    // entire 60-second lifetime to reveal their final lines.
    public const double NotificationScrollSpeedMultiplier = 1.45;

    public static double OffsetForLine(
        double overflow,
        double lineProgress)
    {
        if (!double.IsFinite(overflow) || overflow <= 0)
        {
            return 0;
        }

        var normalized = Math.Clamp(
            (lineProgress - StartHold) /
            (EndHold - StartHold),
            0,
            1);
        return overflow * normalized;
    }

    /// <summary>
    /// Speeds up the travel segment of a held progress curve while keeping
    /// the leading and trailing holds visible. The caller can then pass the
    /// returned normalized progress through its existing easing function.
    /// </summary>
    public static double SpeedUpHeldProgress(
        double progress,
        double speedMultiplier = NotificationScrollSpeedMultiplier)
    {
        var normalized = Math.Clamp(
            double.IsFinite(progress) ? progress : 0,
            0,
            1);
        if (!double.IsFinite(speedMultiplier) || speedMultiplier <= 0)
        {
            return normalized;
        }

        if (normalized <= NotificationStartHold)
        {
            return normalized;
        }

        var travel = 1 -
            NotificationStartHold -
            NotificationEndHold;
        var accelerated = Math.Min(
            travel,
            (normalized - NotificationStartHold) * speedMultiplier);
        return NotificationStartHold + accelerated;
    }
}
