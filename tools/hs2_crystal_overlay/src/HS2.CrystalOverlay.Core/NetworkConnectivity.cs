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
        string? latencyStatus,
        bool? hasInternetAccess,
        bool? hasNetworkInterface)
    {
        var status = latencyStatus?.Trim();
        if (string.Equals(
                status,
                "live",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                status,
                "ok",
                StringComparison.OrdinalIgnoreCase) ||
            hasInternetAccess is true)
        {
            return NetworkConnectivityState.Online;
        }

        if (string.Equals(
                status,
                "offline",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                status,
                "disconnected",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                status,
                "down",
                StringComparison.OrdinalIgnoreCase) ||
            hasInternetAccess is false &&
            (hasNetworkInterface is false ||
             string.Equals(
                 status,
                 "unavailable",
                 StringComparison.OrdinalIgnoreCase)))
        {
            return NetworkConnectivityState.Offline;
        }

        // "connecting" and "stale" are normal while the asynchronous
        // latency sampler refreshes. A single unavailable ping is also not
        // proof of an outage when Windows still has a usable interface.
        return NetworkConnectivityState.Unknown;
    }
}

public sealed class NetworkConnectivityTracker
{
    private readonly int offlineConfirmationCount;
    private readonly bool reportInitialOffline;
    private bool? stableOnline;
    private int consecutiveOffline;

    public NetworkConnectivityTracker(
        int offlineConfirmationCount = 3,
        bool reportInitialOffline = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            offlineConfirmationCount,
            1);
        this.offlineConfirmationCount = offlineConfirmationCount;
        this.reportInitialOffline = reportInitialOffline;
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
            if (reportInitialOffline)
            {
                consecutiveOffline++;
                if (consecutiveOffline < offlineConfirmationCount)
                {
                    return NetworkConnectivityTransition.None;
                }
            }

            stableOnline = false;
            consecutiveOffline = 0;
            return reportInitialOffline
                ? NetworkConnectivityTransition.Disconnected
                : NetworkConnectivityTransition.None;
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
