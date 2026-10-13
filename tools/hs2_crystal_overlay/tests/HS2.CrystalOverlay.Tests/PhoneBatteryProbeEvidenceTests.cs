using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class PhoneBatteryProbeEvidenceTests
{
    [Fact]
    public void TemporaryFileLockFaultDoesNotClearConfirmedReading()
    {
        var cache = new PhoneBatteryProbeReadingCache();
        var now = DateTimeOffset.UtcNow;
        var confirmed = cache.Apply(new PhoneBatteryProbeAttempt<
            PhoneBatteryReading?>(
            PhoneBatteryProbeAttemptStatus.Succeeded,
            new PhoneBatteryReading(
                PhoneBatteryProvider.XiaomiHyperConnect,
                97,
                true,
                true,
                now,
                "xiaomi-ui"),
            now));

        var retained = cache.Apply(new PhoneBatteryProbeAttempt<
            PhoneBatteryReading?>(
            PhoneBatteryProbeAttemptStatus.Faulted,
            null,
            Error: new IOException("smart share log is locked")));

        Assert.Equal(confirmed, retained);
    }

    [Fact]
    public void StaleXiaomiDisconnectLogIsNotAnExplicitCurrentDisconnect()
    {
        const string log = """
            2026-08-06 11:50:00,100 DEBUG business connected 0, active 0
            """;
        var now = new DateTimeOffset(
            2026,
            8,
            6,
            12,
            0,
            0,
            TimeSpan.FromHours(8));

        var state = XiaomiConnectionLogParser.Parse(
            log,
            now,
            TimeSpan.FromMinutes(5));

        Assert.Null(state);
    }

    [Fact]
    public void LiveXiaomiUiKeepsItsOwnTimestampAndMergesRecentChargingTrend()
    {
        var uiObservedAt = new DateTimeOffset(
            2026,
            8,
            6,
            12,
            0,
            5,
            TimeSpan.Zero);
        var reading = PhoneBatteryProbeEvidence.CreateXiaomiLiveUiReading(
            97,
            uiObservedAt,
            new XiaomiBatteryLogSnapshot(
                96,
                true,
                uiObservedAt.AddSeconds(-30)),
            uiObservedAt,
            TimeSpan.FromHours(6));

        Assert.Equal(97, reading.Percentage);
        Assert.Equal(uiObservedAt, reading.ObservedAt);
        Assert.True(reading.IsCharging);
        Assert.Equal("xiaomi-ui", reading.Evidence);
    }

    [Fact]
    public void OldXiaomiTrendDoesNotAddChargingStateToLiveUi()
    {
        var now = DateTimeOffset.UtcNow;

        var reading = PhoneBatteryProbeEvidence.CreateXiaomiLiveUiReading(
            97,
            now,
            new XiaomiBatteryLogSnapshot(
                96,
                true,
                now.AddHours(-7)),
            now,
            TimeSpan.FromHours(6));

        Assert.Null(reading.IsCharging);
    }

}
