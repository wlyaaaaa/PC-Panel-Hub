namespace HS2.CrystalOverlay.Core;

/// <summary>
/// Distinguishes a confirmed (including empty) Steam catalog from a failed read.
/// </summary>
public sealed record SteamCatalogReadResult(
    IReadOnlyDictionary<uint, SteamGameMetadata>? Catalog,
    string? FailureReason = null)
{
    public bool IsSuccess => Catalog is not null;

    public static SteamCatalogReadResult Success(
        IReadOnlyDictionary<uint, SteamGameMetadata> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new SteamCatalogReadResult(catalog);
    }

    public static SteamCatalogReadResult Failure(string failureReason) =>
        new(null, failureReason);
}

public sealed class SteamCatalogRefreshState
{
    private readonly TimeSpan successfulRefreshInterval;
    private readonly TimeSpan failedRefreshInterval;
    private IReadOnlyDictionary<uint, SteamGameMetadata> catalog =
        new Dictionary<uint, SteamGameMetadata>();

    public SteamCatalogRefreshState(
        TimeSpan successfulRefreshInterval,
        TimeSpan failedRefreshInterval)
    {
        if (successfulRefreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(successfulRefreshInterval));
        }

        if (failedRefreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failedRefreshInterval));
        }

        this.successfulRefreshInterval = successfulRefreshInterval;
        this.failedRefreshInterval = failedRefreshInterval;
    }

    public IReadOnlyDictionary<uint, SteamGameMetadata> Catalog => catalog;

    public bool HasConfirmedCatalog { get; private set; }

    public DateTimeOffset NextRefreshAt { get; private set; } =
        DateTimeOffset.MinValue;

    public bool ShouldRefresh(DateTimeOffset now) => now >= NextRefreshAt;

    /// <summary>
    /// Returns true only when a complete catalog was accepted. A successful empty
    /// catalog deliberately clears older entries; a failed read deliberately does not.
    /// </summary>
    public bool Apply(SteamCatalogReadResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsSuccess)
        {
            NextRefreshAt = now + failedRefreshInterval;
            return false;
        }

        catalog = new Dictionary<uint, SteamGameMetadata>(result.Catalog!);
        HasConfirmedCatalog = true;
        NextRefreshAt = now + successfulRefreshInterval;
        return true;
    }
}
