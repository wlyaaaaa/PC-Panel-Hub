using System.Globalization;
using System.Text.RegularExpressions;

namespace HS2.CrystalOverlay.Core;

public enum PhoneBatteryProvider
{
    XiaomiHyperConnect,
    AndroidAdb,
}

public sealed record PhoneBatteryReading(
    PhoneBatteryProvider Provider,
    int Percentage,
    bool? IsCharging,
    bool IsConnected,
    DateTimeOffset ObservedAt,
    string? Evidence = null,
    DateTimeOffset? ConfirmedAt = null);

public sealed record XiaomiBatteryLogSnapshot(
    int Percentage,
    bool? IsCharging,
    DateTimeOffset ObservedAt);

public sealed record XiaomiConnectionLogSnapshot(
    bool IsConnected,
    DateTimeOffset ObservedAt);

public static class PhoneBatteryProbeEvidence
{
    public static PhoneBatteryReading CreateXiaomiLiveUiReading(
        int percentage,
        DateTimeOffset uiObservedAt,
        XiaomiBatteryLogSnapshot? chargingTrend,
        DateTimeOffset now,
        TimeSpan maximumChargingTrendAge)
    {
        bool? charging = null;
        if (chargingTrend is not null)
        {
            var age = now - chargingTrend.ObservedAt;
            if (age >= TimeSpan.FromMinutes(-1) &&
                age <= maximumChargingTrendAge)
            {
                charging = chargingTrend.IsCharging;
            }
        }

        return new PhoneBatteryReading(
            PhoneBatteryProvider.XiaomiHyperConnect,
            percentage,
            charging,
            true,
            uiObservedAt,
            "xiaomi-ui");
    }

}

public static class PhoneBatterySelection
{
    public static PhoneBatteryReading? Select(
        PhoneBatteryReading? reading,
        DateTimeOffset now,
        TimeSpan maximumAge) =>
        IsUsable(reading, now, maximumAge) ? reading : null;

    private static bool IsUsable(
        PhoneBatteryReading? reading,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        if (reading is null ||
            !reading.IsConnected ||
            reading.Percentage is < 0 or > 100)
        {
            return false;
        }

        var freshnessAt = reading.ConfirmedAt ?? reading.ObservedAt;
        var age = now - freshnessAt;
        return age >= TimeSpan.FromSeconds(-5) && age <= maximumAge;
    }
}

public static partial class XiaomiBatteryLogParser
{
    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd HH:mm:ss,fff",
        "yyyy-MM-dd HH:mm:ss",
    ];

    public static XiaomiBatteryLogSnapshot? Parse(
        string text,
        DateTimeOffset now,
        TimeSpan maximumTrendAge)
    {
        var samples = BatteryLine()
            .Matches(text)
            .Select(match => ParseSample(match, now.Offset))
            .Where(sample => sample is not null)
            .Select(sample => sample!.Value)
            .OrderBy(sample => sample.ObservedAt)
            .ToArray();
        if (samples.Length == 0)
        {
            return null;
        }

        var latest = samples[^1];
        var recent = samples
            .Where(sample =>
            {
                var age = now - sample.ObservedAt;
                return age >= TimeSpan.FromMinutes(-1) &&
                       age <= maximumTrendAge;
            })
            .TakeLast(6)
            .ToArray();
        bool? charging = null;
        for (var index = recent.Length - 1; index > 0; index--)
        {
            var delta =
                recent[index].Percentage - recent[index - 1].Percentage;
            if (delta == 0)
            {
                continue;
            }

            charging = delta > 0;
            break;
        }

        return new XiaomiBatteryLogSnapshot(
            latest.Percentage,
            charging,
            latest.ObservedAt);
    }

    private static BatterySample? ParseSample(
        Match match,
        TimeSpan offset)
    {
        if (!int.TryParse(
                match.Groups["battery"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var percentage) ||
            percentage is < 0 or > 100 ||
            !DateTime.TryParseExact(
                match.Groups["timestamp"].Value,
                TimestampFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var timestamp))
        {
            return null;
        }

        return new BatterySample(
            percentage,
            new DateTimeOffset(
                DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified),
                offset));
    }

    private readonly record struct BatterySample(
        int Percentage,
        DateTimeOffset ObservedAt);

    [GeneratedRegex(
        @"(?m)^(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:,\d{1,3})?).{0,300}?(?:battery_level:\s*|Battery=)(?<battery>\d{1,3})%?",
        RegexOptions.CultureInvariant)]
    private static partial Regex BatteryLine();
}

public static partial class XiaomiConnectionLogParser
{
    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd HH:mm:ss,fff",
        "yyyy-MM-dd HH:mm:ss",
    ];

    public static XiaomiConnectionLogSnapshot? Parse(
        string text,
        DateTimeOffset now,
        TimeSpan maximumActivityAge)
    {
        XiaomiConnectionLogSnapshot? latest = null;
        foreach (Match match in ConnectionLine().Matches(text))
        {
            if (!DateTime.TryParseExact(
                    match.Groups["timestamp"].Value,
                    TimestampFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var timestamp))
            {
                continue;
            }

            var connected = !match.Groups["disconnected"].Success;
            if (match.Groups["connected"].Success)
            {
                connected =
                    match.Groups["connected"].Value == "1" &&
                    match.Groups["active"].Value == "1";
            }

            var observedAt = new DateTimeOffset(
                DateTime.SpecifyKind(
                    timestamp,
                    DateTimeKind.Unspecified),
                now.Offset);
            if (latest is null ||
                observedAt >= latest.ObservedAt)
            {
                latest = new XiaomiConnectionLogSnapshot(
                    connected,
                    observedAt);
            }
        }

        if (latest is null)
        {
            return null;
        }

        var age = now - latest.ObservedAt;
        return age >= TimeSpan.FromMinutes(-1) &&
               age <= maximumActivityAge
            ? latest
            : null;
    }

    [GeneratedRegex(
        @"(?mi)^(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:,\d{1,3})?).{0,500}?(?:(?:business connected (?<connected>[01]), active (?<active>[01]))|(?:\[IDM\] OnEvent from epX)|(?<disconnected>device disconnected|connection closed))",
        RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionLine();
}
