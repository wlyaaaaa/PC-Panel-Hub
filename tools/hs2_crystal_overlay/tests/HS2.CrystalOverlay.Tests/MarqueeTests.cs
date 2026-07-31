using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class MarqueeTests
{
    [Theory]
    [InlineData(0.00, 0)]
    [InlineData(0.03, 0)]
    [InlineData(0.50, 100)]
    [InlineData(0.97, 200)]
    [InlineData(1.00, 200)]
    public void LineProgress_RevealsTheWholeOverflow(
        double progress,
        double expected)
    {
        Assert.Equal(
            expected,
            MarqueeMotion.OffsetForLine(
                overflow: 200,
                lineProgress: progress),
            3);
    }
}
