using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class SteamCatalogRefreshStateTests
{
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        6,
        12,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public void TransientFailure_RetainsActiveGameAndRetriesSoon()
    {
        var state = new SteamCatalogRefreshState(
            TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(15));
        var game = new SteamGameMetadata(570, "Dota 2", null);
        Assert.True(state.Apply(
            SteamCatalogReadResult.Success(
                new Dictionary<uint, SteamGameMetadata>
                {
                    [game.AppId] = game,
                }),
            Now));

        Assert.False(state.Apply(
            SteamCatalogReadResult.Failure("manifest-read-io"),
            Now + TimeSpan.FromMinutes(5)));

        Assert.True(state.Catalog.TryGetValue(game.AppId, out var retained));
        Assert.Equal(game, retained);
        Assert.False(state.ShouldRefresh(
            Now + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(14)));
        Assert.True(state.ShouldRefresh(
            Now + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void LaterSuccessfulRefresh_ReplacesPreservedCatalog()
    {
        var state = new SteamCatalogRefreshState(
            TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(15));
        var first = new SteamGameMetadata(570, "Dota 2", null);
        var later = new SteamGameMetadata(730, "Counter-Strike 2", null);
        _ = state.Apply(
            SteamCatalogReadResult.Success(
                new Dictionary<uint, SteamGameMetadata>
                {
                    [first.AppId] = first,
                }),
            Now);
        _ = state.Apply(
            SteamCatalogReadResult.Failure("manifest-read-io"),
            Now + TimeSpan.FromMinutes(5));

        Assert.True(state.Apply(
            SteamCatalogReadResult.Success(
                new Dictionary<uint, SteamGameMetadata>
                {
                    [later.AppId] = later,
                }),
            Now + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(15)));

        Assert.False(state.Catalog.ContainsKey(first.AppId));
        Assert.Equal(later, state.Catalog[later.AppId]);
        Assert.False(state.ShouldRefresh(
            Now + TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(14)));
    }

    [Fact]
    public void SuccessfulEmptyCatalog_IsConfirmedAndClearsOlderEntries()
    {
        var state = new SteamCatalogRefreshState(
            TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(15));
        _ = state.Apply(
            SteamCatalogReadResult.Success(
                new Dictionary<uint, SteamGameMetadata>
                {
                    [570] = new SteamGameMetadata(570, "Dota 2", null),
                }),
            Now);

        Assert.True(state.Apply(
            SteamCatalogReadResult.Success(
                new Dictionary<uint, SteamGameMetadata>()),
            Now + TimeSpan.FromMinutes(5)));

        Assert.True(state.HasConfirmedCatalog);
        Assert.Empty(state.Catalog);
    }
}
