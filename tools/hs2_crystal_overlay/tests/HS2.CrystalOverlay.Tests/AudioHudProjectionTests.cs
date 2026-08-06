using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class AudioHudProjectionTests
{
    [Theory]
    [InlineData(-0.1, 0)]
    [InlineData(0, 0)]
    [InlineData(0.01, 1)]
    [InlineData(0.4249, 42)]
    [InlineData(0.425, 43)]
    [InlineData(0.995, 100)]
    [InlineData(1, 100)]
    [InlineData(1.2, 100)]
    public void Percent_ClampsAndRoundsLikeTheWindowsHud(
        double scalar,
        int expected)
    {
        Assert.Equal(expected, AudioHudProjection.ToPercent(scalar));
    }

    [Theory]
    [InlineData(0, AudioHudIcon.Silent)]
    [InlineData(1, AudioHudIcon.Low)]
    [InlineData(33, AudioHudIcon.Low)]
    [InlineData(34, AudioHudIcon.Medium)]
    [InlineData(66, AudioHudIcon.Medium)]
    [InlineData(67, AudioHudIcon.High)]
    [InlineData(100, AudioHudIcon.High)]
    public void UnmutedProjection_UsesExactPercentAndAdaptiveIcon(
        int percent,
        AudioHudIcon expectedIcon)
    {
        var request = AudioHudProjection.Create(percent, isMuted: false);

        Assert.Equal(AudioHudProjection.EventId, request.EventId);
        Assert.Equal($"{percent}%", request.Title);
        Assert.Null(request.Body);
        Assert.Equal(expectedIcon, request.Visual?.AudioIcon);
        Assert.Null(request.Visual?.Eyebrow);
        Assert.DoesNotContain("系统音量", request.Title);
    }

    [Fact]
    public void MutedProjection_WinsOverTheRememberedPercent()
    {
        var request = AudioHudProjection.Create(22, isMuted: true);

        Assert.Equal("静音", request.Title);
        Assert.Null(request.Body);
        Assert.Equal(AudioHudIcon.Muted, request.Visual?.AudioIcon);
    }

    [Fact]
    public void Tracker_ProducesOneStableEventWithTheLatestFinalState()
    {
        var tracker = new AudioHudStateTracker();

        Assert.Null(tracker.Observe(22, isMuted: false));
        Assert.Null(tracker.Observe(22, isMuted: false));

        var volume = Assert.IsType<OverlayRequest>(
            tracker.Observe(100, isMuted: false));
        var muted = Assert.IsType<OverlayRequest>(
            tracker.Observe(100, isMuted: true));
        var changedWhileMuted = Assert.IsType<OverlayRequest>(
            tracker.Observe(42, isMuted: true));
        var unmuted = Assert.IsType<OverlayRequest>(
            tracker.Observe(42, isMuted: false));

        Assert.All(
            new[] { volume, muted, changedWhileMuted, unmuted },
            request => Assert.Equal(
                AudioHudProjection.EventId,
                request.EventId));
        Assert.Equal("100%", volume.Title);
        Assert.Equal("静音", muted.Title);
        Assert.Equal("静音", changedWhileMuted.Title);
        Assert.Equal("42%", unmuted.Title);
    }
}
