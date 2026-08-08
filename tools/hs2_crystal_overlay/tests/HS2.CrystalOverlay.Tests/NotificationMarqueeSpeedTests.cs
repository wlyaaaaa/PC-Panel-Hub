using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class NotificationMarqueeSpeedTests
{
    [Fact]
    public void NotificationProgress_UsesAFixedCycleInsteadOfTheTtl()
    {
        Assert.Equal(
            0.50,
            MarqueeMotion.NotificationProgress(3),
            10);
        Assert.Equal(
            0.5,
            MarqueeMotion.NotificationProgress(
                MarqueeMotion.NotificationScrollPeriodSeconds / 4),
            10);
        Assert.Equal(
            1,
            MarqueeMotion.NotificationProgress(
                MarqueeMotion.NotificationScrollPeriodSeconds / 2),
            10);
        Assert.Equal(
            0.50,
            MarqueeMotion.NotificationProgress(
                8 * MarqueeMotion.NotificationScrollPeriodSeconds +
                MarqueeMotion.NotificationScrollPeriodSeconds / 4),
            10);
        var epsilon = 1e-6;
        Assert.Equal(
            MarqueeMotion.NotificationProgress(
                MarqueeMotion.NotificationScrollPeriodSeconds - epsilon),
            MarqueeMotion.NotificationProgress(epsilon),
            5);
    }

    [Fact]
    public void NotificationScrollSpeed_IsMateriallyFasterThanTheOldBaseline()
    {
        Assert.True(
            MarqueeMotion.NotificationScrollSpeedMultiplier >= 2.0);
    }

    [Fact]
    public void NotificationTravel_UsesTheConfiguredSpeedMultiplier()
    {
        var progress = MarqueeMotion.SpeedUpHeldProgress(0.50);

        Assert.Equal(
            Math.Min(
                0.84,
                0.16 + (0.50 - 0.16) *
                MarqueeMotion.NotificationScrollSpeedMultiplier),
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
