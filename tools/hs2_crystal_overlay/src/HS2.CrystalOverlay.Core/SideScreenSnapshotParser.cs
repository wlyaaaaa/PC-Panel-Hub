using System.Globalization;
using System.Text.Json;

namespace HS2.CrystalOverlay.Core;

public sealed record GlanceWeather(
    string? City,
    string? Condition,
    double? TemperatureCelsius,
    double? HighTemperatureCelsius,
    double? LowTemperatureCelsius,
    double? RainProbabilityPercent);

public sealed record UsbStorageDevice(
    string Key,
    string DisplayName,
    IReadOnlyList<string> VolumeDrives);

public static class SideScreenSnapshotParser
{
    public static HardwareTelemetry ParseHardware(
        string snapshotJson,
        IReadOnlyList<double>? pumpRpms = null,
        TimeSpan? networkDownFor = null)
    {
        using var document = JsonDocument.Parse(snapshotJson);
        var root = document.RootElement;
        var disks = new List<HardwareDiskTelemetry>();
        if (TryProperty(root, "physical_disks", out var diskArray) &&
            diskArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var disk in diskArray.EnumerateArray())
            {
                var name = Text(disk, "model") ??
                           Text(disk, "device_id") ??
                           "物理磁盘";
                disks.Add(new HardwareDiskTelemetry(
                    name,
                    Number(disk, "temperature_celsius"),
                    Number(disk, "free_gb"),
                    Number(disk, "capacity_gb"),
                    Text(disk, "health_status")));
            }
        }

        var memoryTemperatures = new List<double>();
        if (TryPath(
                root,
                out var modules,
                "memory",
                "module_temperatures_celsius") &&
            modules.ValueKind == JsonValueKind.Array)
        {
            foreach (var temperature in modules.EnumerateArray())
            {
                if (temperature.ValueKind == JsonValueKind.Number &&
                    temperature.TryGetDouble(out var value) &&
                    value is >= 0 and <= 150)
                {
                    memoryTemperatures.Add(value);
                }
            }
        }

        return new HardwareTelemetry(
            Number(root, "cpu", "temperature_celsius"),
            Number(root, "gpu", "temperature_celsius"),
            Number(root, "gpu", "hotspot_temperature_celsius"),
            memoryTemperatures,
            disks,
            pumpRpms ?? [],
            networkDownFor);
    }

    public static string? ParseNetworkLatencyStatus(string snapshotJson)
    {
        using var document = JsonDocument.Parse(snapshotJson);
        return Text(document.RootElement, "network", "latency_status");
    }

    public static IReadOnlyList<UsbStorageDevice> ParseUsbDevices(
        string snapshotJson)
    {
        using var document = JsonDocument.Parse(snapshotJson);
        var root = document.RootElement;
        var result = new List<UsbStorageDevice>();
        if (!TryProperty(root, "physical_disks", out var disks) ||
            disks.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var disk in disks.EnumerateArray())
        {
            if (!string.Equals(
                    Text(disk, "bus_type"),
                    "USB",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = Text(disk, "device_id") ??
                      Text(disk, "model");
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var volumes = new List<string>();
            if (TryProperty(
                    disk,
                    "volume_drives",
                    out var volumeArray) &&
                volumeArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var volume in volumeArray.EnumerateArray())
                {
                    if (volume.ValueKind == JsonValueKind.String &&
                        volume.GetString() is { } text &&
                        !string.IsNullOrWhiteSpace(text))
                    {
                        volumes.Add(text.Trim());
                    }
                }
            }

            result.Add(new UsbStorageDevice(
                key,
                Text(disk, "model") ?? "USB 存储",
                volumes));
        }

        return result;
    }

    public static GlanceWeather? ParseWeather(string snapshotJson)
    {
        using var document = JsonDocument.Parse(snapshotJson);
        if (!TryProperty(
                document.RootElement,
                "weather",
                out var weather) ||
            weather.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var city = Text(weather, "city");
        var condition = Text(weather, "condition") ??
                        Text(weather, "summary");
        var current = Number(weather, "temperature_celsius") ??
                      Number(weather, "temperature_c");
        if (city is null && condition is null && current is null)
        {
            return null;
        }

        return new GlanceWeather(
            city,
            condition,
            current,
            Number(weather, "high_temperature_celsius"),
            Number(weather, "low_temperature_celsius"),
            Number(weather, "rain_probability_percent"));
    }

    public static IReadOnlyList<double> ParsePumpRpms(string lhmJson)
    {
        using var document = JsonDocument.Parse(lhmJson);
        var values = new List<double>();
        Visit(document.RootElement, values);
        return values;
    }

    private static void Visit(
        JsonElement node,
        ICollection<double> values)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var text = Text(node, "Text");
            var sensorId = Text(node, "SensorId");
            var type = Text(node, "Type");
            if (!string.IsNullOrWhiteSpace(text) &&
                text.Contains("pump", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(
                     type,
                     "Fan",
                     StringComparison.OrdinalIgnoreCase) ||
                 sensorId?.Contains(
                     "/fan/",
                     StringComparison.OrdinalIgnoreCase) == true))
            {
                var raw = Text(node, "RawValue") ??
                          Text(node, "Value");
                if (TryLeadingNumber(raw, out var rpm) &&
                    rpm is >= 0 and < 20000)
                {
                    values.Add(rpm);
                }
            }

            if (TryProperty(node, "Children", out var children))
            {
                Visit(children, values);
            }

            return;
        }

        if (node.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var child in node.EnumerateArray())
        {
            Visit(child, values);
        }
    }

    private static bool TryLeadingNumber(
        string? text,
        out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var token = text.Trim().Split(
            [' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries)[0];
        return double.TryParse(
            token,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static string? Text(
        JsonElement root,
        params string[] path)
    {
        if (!TryPath(root, out var value, path) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static double? Number(
        JsonElement root,
        params string[] path)
    {
        return TryPath(root, out var value, path) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out var number)
            ? number
            : null;
    }

    private static bool TryPath(
        JsonElement root,
        out JsonElement value,
        params string[] path)
    {
        value = root;
        foreach (var segment in path)
        {
            if (!TryProperty(value, segment, out value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryProperty(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }
}
