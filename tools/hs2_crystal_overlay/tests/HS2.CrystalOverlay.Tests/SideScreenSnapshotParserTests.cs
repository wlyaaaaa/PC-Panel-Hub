using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class SideScreenSnapshotParserTests
{
    private const string Snapshot = """
        {
          "weather": {
            "city": "示例城市",
            "condition": "多云",
            "temperature_celsius": 31,
            "high_temperature_celsius": 34,
            "low_temperature_celsius": 26,
            "rain_probability_percent": 45
          },
          "cpu": { "temperature_celsius": 67.3 },
          "gpu": {
            "temperature_celsius": 58.1,
            "hotspot_temperature_celsius": 68
          },
          "memory": {
            "module_temperatures_celsius": [67, 67.5]
          },
          "physical_disks": [
            {
              "device_id": "1",
              "model": "Predator SSD",
              "bus_type": "NVMe",
              "capacity_gb": 3815.44,
              "free_gb": 1357.9,
              "temperature_celsius": 58,
              "health_status": "Healthy"
            },
            {
              "device_id": "5",
              "model": "Lenovo thinkplus 1TB",
              "bus_type": "USB",
              "volume_drives": ["H:\\"],
              "capacity_gb": 953.86,
              "free_gb": 629.68
            }
          ],
          "network": { "latency_status": "live" }
        }
        """;

    [Fact]
    public void ParsesHardwareAndWeatherFromTheExistingSnapshot()
    {
        var hardware = SideScreenSnapshotParser.ParseHardware(
            Snapshot,
            [1726, 1098],
            TimeSpan.FromSeconds(31));
        var weather = SideScreenSnapshotParser.ParseWeather(Snapshot);

        Assert.Equal(67.3, hardware.CpuTemperatureCelsius);
        Assert.Equal(58.1, hardware.GpuTemperatureCelsius);
        Assert.Equal(68, hardware.GpuHotspotTemperatureCelsius);
        Assert.Equal([67, 67.5], hardware.MemoryModuleTemperaturesCelsius);
        Assert.Equal([1726, 1098], hardware.PumpRpms);
        Assert.Equal(TimeSpan.FromSeconds(31), hardware.NetworkDownFor);
        Assert.Equal(2, hardware.Disks.Count);
        Assert.Equal("Healthy", hardware.Disks[0].HealthStatus);
        Assert.Equal("示例城市", weather?.City);
        Assert.Equal(34, weather?.HighTemperatureCelsius);
        Assert.Equal(45, weather?.RainProbabilityPercent);
        Assert.Equal(
            "live",
            SideScreenSnapshotParser.ParseNetworkLatencyStatus(Snapshot));
        var usb = SideScreenSnapshotParser.ParseUsbDevices(Snapshot);
        Assert.Single(usb);
        Assert.Equal("Lenovo thinkplus 1TB", usb[0].DisplayName);
        Assert.Equal(["H:\\"], usb[0].VolumeDrives);
    }

    [Fact]
    public void ParsesOnlyPumpFanRpmSensors()
    {
        const string lhm = """
            {
              "Children": [
                {
                  "Text": "System Fan #5 / Pump",
                  "Type": "Fan",
                  "SensorId": "/lpc/0/fan/0",
                  "RawValue": "1726 RPM",
                  "Children": ""
                },
                {
                  "Text": "System Fan #5 / Pump",
                  "Type": "Control",
                  "SensorId": "/lpc/0/control/0",
                  "RawValue": "41.0 %",
                  "Children": ""
                }
              ]
            }
            """;

        Assert.Equal(
            [1726],
            SideScreenSnapshotParser.ParsePumpRpms(lhm));
    }

    [Fact]
    public void HardwareParserTreatsNullOrTextSensorValuesAsUnavailable()
    {
        const string snapshot = """
            {
              "cpu": { "temperature_celsius": null },
              "gpu": {
                "temperature_celsius": "unavailable",
                "hotspot_temperature_celsius": null
              },
              "memory": {
                "module_temperatures_celsius": [null, "offline", 63.5]
              },
              "physical_disks": [
                {
                  "model": "Example SSD",
                  "temperature_celsius": null,
                  "free_gb": "unknown",
                  "capacity_gb": 1000
                }
              ]
            }
            """;

        var hardware = SideScreenSnapshotParser.ParseHardware(snapshot);

        Assert.Null(hardware.CpuTemperatureCelsius);
        Assert.Null(hardware.GpuTemperatureCelsius);
        Assert.Null(hardware.GpuHotspotTemperatureCelsius);
        Assert.Equal([63.5], hardware.MemoryModuleTemperaturesCelsius);
        Assert.Single(hardware.Disks);
        Assert.Null(hardware.Disks[0].TemperatureCelsius);
        Assert.Null(hardware.Disks[0].FreeGigabytes);
        Assert.Equal(1000, hardware.Disks[0].CapacityGigabytes);
    }
}
