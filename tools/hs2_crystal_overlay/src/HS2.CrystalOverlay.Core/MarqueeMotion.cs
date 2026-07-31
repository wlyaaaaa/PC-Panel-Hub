namespace HS2.CrystalOverlay.Core;

public static class MarqueeMotion
{
    private const double StartHold = 0.03;
    private const double EndHold = 0.97;

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
}
