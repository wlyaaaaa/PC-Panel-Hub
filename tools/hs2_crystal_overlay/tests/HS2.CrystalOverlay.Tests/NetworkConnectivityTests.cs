using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class NetworkConnectivityTests
{
    [Theory]
    [InlineData("connecting")]
    [InlineData("stale")]
    [InlineData("unavailable")]
    [InlineData(null)]
    public void TransientLatencyStatesAreUnknown(string? status)
    {
        Assert.Equal(
            NetworkConnectivityState.Unknown,
            NetworkConnectivityClassifier.Classify(
                status,
                hasInternetAccess: null,
                hasNetworkInterface: true));
    }

    [Fact]
    public void WindowsInternetEvidenceOverridesAStalePingSample()
    {
        Assert.Equal(
            NetworkConnectivityState.Online,
            NetworkConnectivityClassifier.Classify(
                "stale",
                hasInternetAccess: true,
                hasNetworkInterface: true));
    }

    [Fact]
    public void FailedPingAndNoWindowsInternetEvidenceIsOffline()
    {
        Assert.Equal(
            NetworkConnectivityState.Offline,
            NetworkConnectivityClassifier.Classify(
                "unavailable",
                hasInternetAccess: false,
                hasNetworkInterface: true));
    }

    [Fact]
    public void TrackerIgnoresRefreshGapsAndDebouncesDisconnect()
    {
        var tracker = new NetworkConnectivityTracker(
            offlineConfirmationCount: 3);

        Assert.Equal(
            NetworkConnectivityTransition.None,
            tracker.Observe(NetworkConnectivityState.Online));
        Assert.Equal(
            NetworkConnectivityTransition.None,
            tracker.Observe(NetworkConnectivityState.Unknown));
        Assert.Equal(
            NetworkConnectivityTransition.None,
            tracker.Observe(NetworkConnectivityState.Offline));
        Assert.Equal(
            NetworkConnectivityTransition.None,
            tracker.Observe(NetworkConnectivityState.Online));

        Assert.Equal(
            NetworkConnectivityTransition.None,
            tracker.Observe(NetworkConnectivityState.Offline));
        Assert.Equal(
            NetworkConnectivityTransition.None,
            tracker.Observe(NetworkConnectivityState.Offline));
        Assert.Equal(
            NetworkConnectivityTransition.Disconnected,
            tracker.Observe(NetworkConnectivityState.Offline));
        Assert.Equal(
            NetworkConnectivityTransition.Restored,
            tracker.Observe(NetworkConnectivityState.Online));
    }

    [Fact]
    public void StartingOfflineDoesNotCreateAStartupPopup()
    {
        var tracker = new NetworkConnectivityTracker();

        Assert.Equal(
            NetworkConnectivityTransition.None,
            tracker.Observe(NetworkConnectivityState.Offline));
        Assert.Equal(
            NetworkConnectivityTransition.Restored,
            tracker.Observe(NetworkConnectivityState.Online));
    }

    [Fact]
    public void TrackerCanConfirmAnOutagePresentAtStartup()
    {
        var tracker = new NetworkConnectivityTracker(
            offlineConfirmationCount: 3,
            reportInitialOffline: true);

        Assert.Equal(
            NetworkConnectivityTransition.None,
            tracker.Observe(NetworkConnectivityState.Offline));
        Assert.Equal(
            NetworkConnectivityTransition.None,
            tracker.Observe(NetworkConnectivityState.Offline));
        Assert.Equal(
            NetworkConnectivityTransition.Disconnected,
            tracker.Observe(NetworkConnectivityState.Offline));
        Assert.Equal(
            NetworkConnectivityTransition.Restored,
            tracker.Observe(NetworkConnectivityState.Online));
    }
}
