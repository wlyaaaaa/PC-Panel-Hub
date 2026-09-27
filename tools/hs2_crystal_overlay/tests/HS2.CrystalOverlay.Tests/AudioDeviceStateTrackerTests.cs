using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class AudioDeviceStateTrackerTests
{
    [Fact]
    public void DeviceSwitchReportsNameEvenWhenVolumeDoesNotChange()
    {
        var tracker = new AudioDeviceStateTracker();
        Assert.Null(tracker.Observe("speakers", "音箱"));
        Assert.Null(tracker.Observe("speakers", "音箱"));
        var request = tracker.Observe("headphones", "耳机");
        Assert.NotNull(request);
        Assert.Equal(OverlayKind.DeviceOrNetwork, request.Kind);
        Assert.Equal("耳机", request.Body);
        Assert.Null(tracker.Observe("headphones", "耳机"));
        Assert.Equal("音箱", tracker.Observe("speakers", "音箱")?.Body);
    }
}
