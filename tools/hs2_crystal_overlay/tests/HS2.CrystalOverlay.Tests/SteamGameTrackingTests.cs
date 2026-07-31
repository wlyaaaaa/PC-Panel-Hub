using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class SteamGameTrackingTests
{
    [Fact]
    public void TracksTheFirstProcessUntilSteamRemovesTheApp()
    {
        const string log = """
            [2026-07-30 10:00:00] Client version: 1
            [2026-07-30 10:01:00] AppID 1449850 adding PID 100 as a tracked process
            [2026-07-30 10:01:01] AppID 1449850 adding PID 101 as a tracked process
            [2026-07-30 10:02:00] AppID 431960 adding PID 200 as a tracked process
            [2026-07-30 10:03:00] Remove 431960 from running list
            """;

        var running = SteamGameProcessLogParser.Parse(
            log,
            TimeSpan.FromHours(8));

        Assert.Single(running);
        Assert.Equal((uint)1449850, running[0].AppId);
        Assert.Equal(10, running[0].StartedAt.Hour);
        Assert.Equal(1, running[0].StartedAt.Minute);
    }

    [Fact]
    public void ANewSteamSessionDropsStaleRunningState()
    {
        const string log = """
            [2026-07-30 10:01:00] AppID 10 adding PID 100 as a tracked process
            [2026-07-30 11:00:00] Client version: 2
            [2026-07-30 11:01:00] AppID 20 adding PID 200 as a tracked process
            """;

        var running = SteamGameProcessLogParser.Parse(
            log,
            TimeSpan.Zero);

        Assert.Single(running);
        Assert.Equal((uint)20, running[0].AppId);
    }

    [Fact]
    public void ParsesSteamManifestMetadata()
    {
        const string manifest = """
            "AppState"
            {
                "appid"        "1449850"
                "name"         "Yu-Gi-Oh!  Master Duel"
                "installdir"   "Yu-Gi-Oh!  Master Duel"
            }
            """;

        var metadata = SteamManifestParser.Parse(manifest);

        Assert.Equal((uint)1449850, metadata?.AppId);
        Assert.Equal("Yu-Gi-Oh!  Master Duel", metadata?.Name);
        Assert.Equal(
            "Yu-Gi-Oh!  Master Duel",
            metadata?.InstallDirectory);
    }
}
