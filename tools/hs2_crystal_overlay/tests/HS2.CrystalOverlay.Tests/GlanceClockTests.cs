using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class GlanceClockTests
{
    [Fact]
    public void Format_ShowsOnlyHourAndMinute()
    {
        var value = new DateTimeOffset(
            2026,
            7,
            31,
            17,
            45,
            36,
            TimeSpan.FromHours(8));

        Assert.Equal("17:45", GlanceClock.Format(value));
    }

    [Fact]
    public void FormatChinaTime_DoesNotFollowTheWindowsDisplayTimeZone()
    {
        var utc = new DateTimeOffset(
            2026,
            7,
            31,
            9,
            45,
            36,
            TimeSpan.Zero);

        Assert.Equal("17:45", GlanceClock.FormatChinaTime(utc));
    }
}
