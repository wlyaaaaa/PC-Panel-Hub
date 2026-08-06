using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class SteamGameTrackingTests
{
    private static readonly TimeZoneInfo PacificTime =
        TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

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
            PacificTime);

        Assert.Single(running);
        Assert.Equal((uint)1449850, running[0].AppId);
        Assert.Equal(10, running[0].StartedAt.Hour);
        Assert.Equal(1, running[0].StartedAt.Minute);
        Assert.Equal(TimeSpan.FromHours(-7), running[0].StartedAt.Offset);
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
            TimeZoneInfo.Utc);

        Assert.Single(running);
        Assert.Equal((uint)20, running[0].AppId);
    }

    [Theory]
    [InlineData("2026-07-30 10:01:00", -7)]
    [InlineData("2026-01-30 10:01:00", -8)]
    public void UsesTheOffsetThatAppliedWhenEachSteamLineWasWritten(
        string timestamp,
        int expectedOffsetHours)
    {
        var running = SteamGameProcessLogParser.Parse(
            $"[{timestamp}] AppID 1449850 adding PID 100 as a tracked process",
            PacificTime);

        Assert.Single(running);
        Assert.Equal(
            TimeSpan.FromHours(expectedOffsetHours),
            running[0].StartedAt.Offset);
    }

    [Fact]
    public void RepeatedDaylightSavingHour_RemainsChronological()
    {
        const string log = """
            [2026-11-01 00:30:00] Client version: 1
            [2026-11-01 01:50:00] AppID 10 adding PID 100 as a tracked process
            [2026-11-01 01:55:00] Remove 10 from running list
            [2026-11-01 01:10:00] AppID 20 adding PID 200 as a tracked process
            """;

        var running = SteamGameProcessLogParser.Parse(log, PacificTime);

        Assert.Single(running);
        Assert.Equal((uint)20, running[0].AppId);
        Assert.Equal(TimeSpan.FromHours(-8), running[0].StartedAt.Offset);
        Assert.Equal(9, running[0].StartedAt.UtcDateTime.Hour);
        Assert.Equal(10, running[0].StartedAt.UtcDateTime.Minute);
    }

    [Fact]
    public void NonexistentDaylightSavingTimestamp_IsRejected()
    {
        const string log = """
            [2026-03-08 02:30:00] AppID 1449850 adding PID 100 as a tracked process
            """;

        var running = SteamGameProcessLogParser.Parse(log, PacificTime);

        Assert.Empty(running);
    }

    [Fact]
    public void SteamStartMeta_AlwaysUsesChinaStandardTime()
    {
        var running = SteamGameProcessLogParser.Parse(
            "[2026-08-06 00:04:03] AppID 1449850 adding PID 100 as a tracked process",
            PacificTime);

        Assert.Equal(
            "启动于 15:04 · UTC+8",
            SteamGameDisplay.FormatStartMeta(Assert.Single(running).StartedAt));
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
