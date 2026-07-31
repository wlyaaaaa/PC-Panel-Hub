using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class PhoneBatteryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 16, 30, 0, TimeSpan.FromHours(8));

    [Fact]
    public void XiaomiReading_WinsWhenBothProvidersAreCurrent()
    {
        var xiaomi = new PhoneBatteryReading(
            PhoneBatteryProvider.XiaomiHyperConnect,
            97,
            true,
            true,
            Now);
        var phoneLink = new PhoneBatteryReading(
            PhoneBatteryProvider.PhoneLink,
            93,
            false,
            true,
            Now);

        var selected = PhoneBatteryArbitration.Select(
            xiaomi,
            phoneLink,
            Now,
            TimeSpan.FromSeconds(15));

        Assert.Same(xiaomi, selected);
    }

    [Fact]
    public void PhoneLink_IsUsedOnlyAsConnectedCurrentFallback()
    {
        var disconnectedXiaomi = new PhoneBatteryReading(
            PhoneBatteryProvider.XiaomiHyperConnect,
            97,
            true,
            false,
            Now);
        var phoneLink = new PhoneBatteryReading(
            PhoneBatteryProvider.PhoneLink,
            93,
            false,
            true,
            Now);

        var selected = PhoneBatteryArbitration.Select(
            disconnectedXiaomi,
            phoneLink,
            Now,
            TimeSpan.FromSeconds(15));

        Assert.Same(phoneLink, selected);
    }

    [Fact]
    public void MissingDisconnectedOrStaleProviders_HideBattery()
    {
        var stale = new PhoneBatteryReading(
            PhoneBatteryProvider.XiaomiHyperConnect,
            97,
            true,
            true,
            Now - TimeSpan.FromMinutes(2));
        var disconnected = new PhoneBatteryReading(
            PhoneBatteryProvider.PhoneLink,
            93,
            false,
            false,
            Now);

        var selected = PhoneBatteryArbitration.Select(
            stale,
            disconnected,
            Now,
            TimeSpan.FromSeconds(15));

        Assert.Null(selected);
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
