using System.Globalization;

namespace HS2.CrystalOverlay.Core;

public static class GlanceClock
{
    private static readonly TimeZoneInfo ChinaTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");

    public static string Format(DateTimeOffset value) =>
        value.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string FormatChinaTime(DateTimeOffset value) =>
        Format(TimeZoneInfo.ConvertTime(value, ChinaTimeZone));
}
