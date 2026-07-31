using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class PlaybackProgressTests
{
    [Fact]
    public void UsesTheSameExactClockAsTheDisplayedPlaybackTime()
    {
        var ratio = PlaybackProgress.Calculate(
            TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(51),
            TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(3));

        Assert.Equal(171d / 243d, ratio, 6);
    }

    [Theory]
    [InlineData(-1, 60, 0)]
    [InlineData(90, 60, 1)]
    [InlineData(30, 0, 0)]
    public void ClampsInvalidOrOutOfRangePositions(
        int positionSeconds,
        int durationSeconds,
        double expected)
    {
        Assert.Equal(
            expected,
            PlaybackProgress.Calculate(
                TimeSpan.FromSeconds(positionSeconds),
                TimeSpan.FromSeconds(durationSeconds)));
    }

    [Fact]
    public void SeekingBackward_ImmediatelyMovesTheProgressBackward()
    {
        var beforeSeek = PlaybackProgress.Calculate(
            TimeSpan.FromSeconds(90),
            TimeSpan.FromSeconds(180));
        var afterSeek = PlaybackProgress.Calculate(
            TimeSpan.FromSeconds(24),
            TimeSpan.FromSeconds(180));

        Assert.Equal(0.5, beforeSeek, 6);
        Assert.Equal(24d / 180d, afterSeek, 6);
        Assert.True(afterSeek < beforeSeek);
    }
}
