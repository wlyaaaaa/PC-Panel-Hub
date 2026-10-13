using System.Globalization;

namespace HS2.CrystalOverlay.Core;

public static class AndroidAdbBatteryParser
{
    public static string? SelectTransport(string devices, string serial)
    {
        var lines = devices.Split('\n', StringSplitOptions.TrimEntries |
            StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || lines[0] != "List of devices attached")
            throw new FormatException("Invalid adb devices output.");

        var wireless = new List<string>();
        var usb = new List<string>();
        foreach (var line in lines.Skip(1))
        {
            var fields = line.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2)
                throw new FormatException("Invalid adb device row.");
            if (fields[1] != "device")
                continue;
            var transport = fields[0];
            var prefix = $"adb-{serial}-";
            const string suffix = "._adb-tls-connect._tcp";
            if (transport.StartsWith(prefix, StringComparison.Ordinal) &&
                transport.EndsWith(suffix, StringComparison.Ordinal) &&
                transport.Length > prefix.Length + suffix.Length)
                wireless.Add(transport);
            else if (transport == serial)
                usb.Add(transport);
        }

        var candidates = wireless.Count > 0 ? wireless : usb;
        if (candidates.Count > 1)
            throw new InvalidOperationException("Ambiguous adb phone transport.");
        return candidates.SingleOrDefault();
    }

    public static PhoneBatteryReading Parse(
        string output, DateTimeOffset observedAt)
    {
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0)
                continue;
            var key = line[..colon].Trim();
            if (key is not ("level" or "scale" or "status"))
                continue;
            if (!int.TryParse(line[(colon + 1)..].Trim(),
                    NumberStyles.None, CultureInfo.InvariantCulture,
                    out var value) || !values.TryAdd(key, value))
                throw new FormatException("Invalid adb battery field.");
        }

        if (!values.TryGetValue("level", out var level) ||
            !values.TryGetValue("scale", out var scale) ||
            scale <= 0 || level < 0 || level > scale)
            throw new FormatException("Invalid adb battery level or scale.");
        bool? charging = values.GetValueOrDefault("status", 1) switch
        {
            1 => null,
            2 or 5 => true,
            3 or 4 => false,
            _ => throw new FormatException("Invalid adb battery status."),
        };
        return new PhoneBatteryReading(
            PhoneBatteryProvider.AndroidAdb,
            (int)Math.Round(level * 100d / scale, MidpointRounding.AwayFromZero),
            charging, true, observedAt, "android-adb-dumpsys");
    }
}
