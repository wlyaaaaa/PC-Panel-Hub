using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class NotificationMarqueeSpeedTests
{
    [Fact]
    public void NotificationTravel_UsesTheConfiguredSpeedMultiplier()
    {
        var progress = MarqueeMotion.SpeedUpHeldProgress(0.50);

        Assert.Equal(
            0.16 + (0.50 - 0.16) *
            MarqueeMotion.NotificationScrollSpeedMultiplier,
            progress,
            10);
    }

    [Theory]
    [InlineData(0.00, 0.00)]
    [InlineData(0.10, 0.10)]
    [InlineData(0.16, 0.16)]
    [InlineData(0.84, 0.84)]
    [InlineData(1.00, 0.84)]
    public void NotificationMarquee_KeepsTheLeadingAndTrailingHolds(
        double progress,
        double expected)
    {
        Assert.Equal(
            expected,
            MarqueeMotion.SpeedUpHeldProgress(progress),
            10);
    }

    [Fact]
    public void NotificationMarquee_RejectsInvalidSpeedWithoutMovingTheTimeline()
    {
        Assert.Equal(
            0.50,
            MarqueeMotion.SpeedUpHeldProgress(0.50, double.NaN),
            10);
        Assert.Equal(
            0.50,
            MarqueeMotion.SpeedUpHeldProgress(0.50, 0),
            10);
    }
}
