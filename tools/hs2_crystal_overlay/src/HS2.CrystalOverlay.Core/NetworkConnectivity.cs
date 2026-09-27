namespace HS2.CrystalOverlay.Core;

public enum NetworkConnectivityState
{
    Unknown,
    Online,
    Offline,
}

public enum NetworkConnectivityTransition
{
    None,
    Disconnected,
    Restored,
}

public static class NetworkConnectivityClassifier
{
    public static NetworkConnectivityState Classify(
        bool? hasInternetAccess,
        bool? hasNetworkInterface) => hasInternetAccess is true
            ? NetworkConnectivityState.Online
            : hasInternetAccess is false || hasNetworkInterface is false
                ? NetworkConnectivityState.Offline
                : NetworkConnectivityState.Unknown;

}

public sealed class NetworkConnectivityTracker
{
    private readonly int offlineConfirmationCount;
    private bool? stableOnline;
    private int consecutiveOffline;

    public NetworkConnectivityTracker(
        int offlineConfirmationCount = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            offlineConfirmationCount,
            1);
        this.offlineConfirmationCount = offlineConfirmationCount;
    }

    public NetworkConnectivityTransition Observe(
        NetworkConnectivityState state)
    {
        if (state == NetworkConnectivityState.Unknown)
        {
            return NetworkConnectivityTransition.None;
        }

        if (state == NetworkConnectivityState.Online)
        {
            consecutiveOffline = 0;
            if (stableOnline is null)
            {
                stableOnline = true;
                return NetworkConnectivityTransition.None;
            }

            if (stableOnline is false)
            {
                stableOnline = true;
                return NetworkConnectivityTransition.Restored;
            }

            return NetworkConnectivityTransition.None;
        }

        if (stableOnline is null)
        {
            stableOnline = false;
            consecutiveOffline = 0;
            return NetworkConnectivityTransition.None;
        }

        if (stableOnline is false)
        {
            return NetworkConnectivityTransition.None;
        }

        consecutiveOffline++;
        if (consecutiveOffline < offlineConfirmationCount)
        {
            return NetworkConnectivityTransition.None;
        }

        stableOnline = false;
        consecutiveOffline = 0;
        return NetworkConnectivityTransition.Disconnected;
    }
}
