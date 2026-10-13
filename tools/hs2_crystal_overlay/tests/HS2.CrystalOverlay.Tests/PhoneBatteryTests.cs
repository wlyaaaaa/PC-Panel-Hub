using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class PhoneBatteryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 16, 30, 0, TimeSpan.FromHours(8));

    [Fact]
    public void DisconnectedOrStaleReading_HidesBattery()
    {
        var stale = new PhoneBatteryReading(
            PhoneBatteryProvider.XiaomiHyperConnect,
            97,
            true,
            true,
            Now - TimeSpan.FromMinutes(2));
        var disconnected = new PhoneBatteryReading(
            PhoneBatteryProvider.XiaomiHyperConnect,
            93,
            false,
            false,
            Now);

        Assert.Null(PhoneBatterySelection.Select(
            stale,
            Now,
            TimeSpan.FromSeconds(15)));
        Assert.Null(PhoneBatterySelection.Select(
            disconnected,
            Now,
            TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void XiaomiLogParser_UsesLatestBatteryAndDetectsIncrease()
    {
        const string log = """
            2026-07-30 15:30:33,100 INFO HandleGetPhoneElectricQuantity,battery_level: 97
            2026-07-30 15:36:00,200 INFO OnGetPhoneElectricQuantityResult Battery=98%
            2026-07-30 15:42:01,300 INFO HandleGetPhoneElectricQuantity,battery_level: 99
            """;

        var snapshot = XiaomiBatteryLogParser.Parse(
            log,
            Now,
            TimeSpan.FromHours(2));

        Assert.NotNull(snapshot);
        Assert.Equal(99, snapshot.Percentage);
        Assert.True(snapshot.IsCharging);
    }

    [Fact]
    public void XiaomiLogParser_DoesNotInventChargingFromOneOrOldSample()
    {
        const string log = """
            2026-07-30 12:00:00,100 INFO HandleGetPhoneElectricQuantity,battery_level: 97
            """;

        var snapshot = XiaomiBatteryLogParser.Parse(
            log,
            Now,
            TimeSpan.FromMinutes(30));

        Assert.NotNull(snapshot);
        Assert.Equal(97, snapshot.Percentage);
        Assert.Null(snapshot.IsCharging);
    }

    [Fact]
    public void XiaomiLogParser_DoesNotKeepChargingAfterOnlyRecentPlateauSamples()
    {
        const string log = """
            2026-07-30 16:20:00,100 INFO HandleGetPhoneElectricQuantity,battery_level: 50
            2026-07-30 16:21:00,100 INFO HandleGetPhoneElectricQuantity,battery_level: 51
            2026-07-30 16:29:00,100 INFO HandleGetPhoneElectricQuantity,battery_level: 51
            2026-07-30 16:30:00,100 INFO HandleGetPhoneElectricQuantity,battery_level: 51
            """;

        var snapshot = XiaomiBatteryLogParser.Parse(
            log,
            Now,
            TimeSpan.FromMinutes(5));

        Assert.NotNull(snapshot);
        Assert.Equal(51, snapshot.Percentage);
        Assert.Null(snapshot.IsCharging);
    }

    [Fact]
    public void XiaomiConnectionLogParser_UsesRecentLiveTransportTraffic()
    {
        const string log = """
            2026-07-30 16:25:00,100 DEBUG [screen_share][ui] business connected 1, active 1
            2026-07-30 16:29:30,200 DEBUG [IDM] OnEvent from epX, type 10, length 6659
            """;

        var snapshot = XiaomiConnectionLogParser.Parse(
            log,
            Now,
            TimeSpan.FromMinutes(5));

        Assert.NotNull(snapshot);
        Assert.True(snapshot.IsConnected);
        Assert.Equal(
            new DateTimeOffset(
                2026,
                7,
                30,
                16,
                29,
                30,
                200,
                TimeSpan.FromHours(8)),
            snapshot.ObservedAt);
    }

    [Fact]
    public void XiaomiConnectionLogParser_RejectsStaleOrDisconnectedState()
    {
        const string stale = """
            2026-07-30 16:20:00,100 DEBUG [IDM] OnEvent from epX, type 10, length 6659
            """;
        const string disconnected = """
            2026-07-30 16:29:00,100 DEBUG [IDM] OnEvent from epX, type 10, length 6659
            2026-07-30 16:29:40,100 DEBUG business connected 0, active 0
            """;

        Assert.Null(XiaomiConnectionLogParser.Parse(
            stale,
            Now,
            TimeSpan.FromMinutes(5)));
        Assert.False(XiaomiConnectionLogParser.Parse(
            disconnected,
            Now,
            TimeSpan.FromMinutes(5))?.IsConnected);
    }
}
