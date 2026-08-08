namespace HS2.CrystalOverlay.Core;

public static class MarqueeMotion
{
    private const double StartHold = 0.03;
    private const double EndHold = 0.97;
    private const double NotificationStartHold = 0.16;
    private const double NotificationEndHold = 0.16;

    // Body scrolling is a short, repeatable visual affordance. It must not
    // take the full 60-second notification lifetime to reveal the final line.
    public const double NotificationScrollPeriodSeconds = 12;
    public const double NotificationScrollSpeedMultiplier = 2.4;

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

    public static double NotificationProgress(
        double elapsedSeconds,
        double periodSeconds = NotificationScrollPeriodSeconds)
    {
        if (!double.IsFinite(elapsedSeconds))
        {
            return 0;
        }

        if (!double.IsFinite(periodSeconds) || periodSeconds <= 0)
        {
            periodSeconds = NotificationScrollPeriodSeconds;
        }

        var elapsed = Math.Max(0, elapsedSeconds);
        var phase = elapsed % periodSeconds / periodSeconds;
        // A triangular wave keeps the offset continuous at cycle boundaries:
        // the body reaches its tail, holds, and then returns to the leading
        // line instead of jumping from the tail back to the top.
        return phase <= 0.5
            ? phase * 2
            : (1 - phase) * 2;
    }
}
