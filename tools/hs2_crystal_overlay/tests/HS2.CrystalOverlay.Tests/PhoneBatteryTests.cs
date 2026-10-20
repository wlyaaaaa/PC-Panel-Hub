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
            PhoneBatteryProvider.AndroidAdb,
            97,
            true,
            true,
            Now - TimeSpan.FromMinutes(2));
        var disconnected = new PhoneBatteryReading(
            PhoneBatteryProvider.AndroidAdb,
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
}
