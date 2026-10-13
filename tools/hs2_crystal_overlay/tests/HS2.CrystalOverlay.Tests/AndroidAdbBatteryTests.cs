using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class AndroidAdbBatteryTests
{
    [Theory]
    [InlineData(1, null)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public void ParsesNativeLevelScaleAndStatus(int status, bool? charging)
    {
        var now = DateTimeOffset.Now;
        var reading = AndroidAdbBatteryParser.Parse(
            $"Current Battery Service state:\n level: 3\n scale: 4\n status: {status}", now);
        Assert.Equal(75, reading.Percentage);
        Assert.Equal(charging, reading.IsCharging);
        Assert.Equal(PhoneBatteryProvider.AndroidAdb, reading.Provider);
        Assert.Equal(now, reading.ObservedAt);
    }

    [Theory]
    [InlineData("level: 50")]
    [InlineData("scale: 100")]
    [InlineData("level: 1\nscale: 0")]
    [InlineData("level: -1\nscale: 100")]
    [InlineData("level: 101\nscale: 100")]
    [InlineData("level: 50\nscale: 100\nstatus: 6")]
    [InlineData("level: 50\nlevel: 51\nscale: 100")]
    [InlineData("level: abc\nscale: 100")]
    public void RejectsInvalidBatteryOutput(string output) =>
        Assert.Throws<FormatException>(() =>
            AndroidAdbBatteryParser.Parse(output, DateTimeOffset.Now));

    [Fact]
    public void PrefersConnectedWirelessForExactConfiguredPhone()
    {
        const string devices = "List of devices attached\n" +
            "other\tdevice\nphone\tdevice\n" +
            "adb-phone-extra-x._adb-tls-connect._tcp\toffline\n" +
            "adb-phone-key._adb-tls-connect._tcp\tdevice\n";
        Assert.Equal("adb-phone-key._adb-tls-connect._tcp",
            AndroidAdbBatteryParser.SelectTransport(devices, "phone"));
        Assert.Equal("other",
            AndroidAdbBatteryParser.SelectTransport(devices, "other"));
        Assert.Null(AndroidAdbBatteryParser.SelectTransport(devices, "phon"));
    }

    [Theory]
    [InlineData("phone\tdevice\nphone\tdevice")]
    [InlineData("adb-phone-a._adb-tls-connect._tcp\tdevice\n" +
                "adb-phone-b._adb-tls-connect._tcp\tdevice\nphone\tdevice")]
    public void RejectsAmbiguousTransport(string rows) =>
        Assert.Throws<InvalidOperationException>(() =>
            AndroidAdbBatteryParser.SelectTransport(
                "List of devices attached\n" + rows, "phone"));

    [Fact]
    public void DisconnectedTargetReturnsNullAndBadListingFails()
    {
        Assert.Null(AndroidAdbBatteryParser.SelectTransport(
            "List of devices attached\nphone\tunauthorized\nother\tdevice", "phone"));
        Assert.Equal("phone", AndroidAdbBatteryParser.SelectTransport(
            "List of devices attached\nphone\tdevice", "phone"));
        Assert.Throws<FormatException>(() =>
            AndroidAdbBatteryParser.SelectTransport("invalid", "phone"));
    }
}
