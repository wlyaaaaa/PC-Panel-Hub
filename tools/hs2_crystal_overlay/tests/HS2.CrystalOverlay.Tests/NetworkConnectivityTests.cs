using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class NetworkConnectivityTests
{
    [Theory]
    [InlineData(true, true, NetworkConnectivityState.Online)]
    [InlineData(true, false, NetworkConnectivityState.Online)]
    [InlineData(false, true, NetworkConnectivityState.Offline)]
    [InlineData(false, false, NetworkConnectivityState.Offline)]
    [InlineData(null, false, NetworkConnectivityState.Offline)]
    [InlineData(null, true, NetworkConnectivityState.Unknown)]
    [InlineData(null, null, NetworkConnectivityState.Unknown)]
    public void ClassifiesWindowsConnectivity(bool? internet, bool? adapter,
        NetworkConnectivityState expected)
    {
        Assert.Equal(expected, NetworkConnectivityClassifier.Classify(internet, adapter));
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


}
