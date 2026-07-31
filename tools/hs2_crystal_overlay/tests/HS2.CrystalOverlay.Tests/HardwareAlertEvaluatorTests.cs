using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class HardwareAlertEvaluatorTests
{
    [Fact]
    public void HealthyTelemetry_ProducesNoAlert()
    {
        var telemetry = new HardwareTelemetry(
            CpuTemperatureCelsius: 72,
            GpuTemperatureCelsius: 67,
            GpuHotspotTemperatureCelsius: 79,
            MemoryModuleTemperaturesCelsius: [64, 65],
            Disks:
            [
                new HardwareDiskTelemetry(
                    "System NVMe",
                    57,
                    225,
                    599,
                    "Healthy"),
            ],
            PumpRpms: [1726, 1096],
            NetworkDownFor: null);

        Assert.Empty(HardwareAlertEvaluator.Evaluate(telemetry));
    }

    [Fact]
    public void OverheatAndStorageHealth_ProduceActionableFindings()
    {
        var telemetry = new HardwareTelemetry(
            CpuTemperatureCelsius: 94,
            GpuTemperatureCelsius: 86,
            GpuHotspotTemperatureCelsius: 106,
            MemoryModuleTemperaturesCelsius: [64, 88],
            Disks:
            [
                new HardwareDiskTelemetry(
                    "Archive HDD",
                    72,
                    8,
                    4000,
                    "Warning"),
            ],
            PumpRpms: [1700],
            NetworkDownFor: null);

        var findings = HardwareAlertEvaluator.Evaluate(telemetry);

        Assert.Contains(findings, item => item.Key == "cpu-overheat");
        Assert.Contains(findings, item => item.Key == "gpu-overheat");
        Assert.Contains(findings, item => item.Key == "gpu-hotspot");
        Assert.Contains(findings, item => item.Key == "memory-overheat");
        Assert.Contains(findings, item => item.Key == "disk-overheat");
        Assert.Contains(findings, item => item.Key == "disk-health");
        Assert.Contains(findings, item => item.Key == "disk-space");
    }

    [Fact]
    public void PumpAlertRequiresAllAvailablePumpSensorsToBeStopped()
    {
        var onePumpStillRunning = new HardwareTelemetry(
            null,
            null,
            null,
            [],
            [],
            [0, 1100],
            null);
        var allStopped = onePumpStillRunning with
        {
            PumpRpms = [0, 120],
        };

        Assert.DoesNotContain(
            HardwareAlertEvaluator.Evaluate(onePumpStillRunning),
            item => item.Key == "pump-stopped");
        Assert.Contains(
            HardwareAlertEvaluator.Evaluate(allStopped),
            item => item.Key == "pump-stopped");
    }

    [Fact]
    public void LongNetworkOutage_BecomesAnActiveFinding()
    {
        var telemetry = new HardwareTelemetry(
            null,
            null,
            null,
            [],
            [],
            [],
            TimeSpan.FromSeconds(31));

        Assert.Contains(
            HardwareAlertEvaluator.Evaluate(telemetry),
            item => item.Key == "network-down");
    }
}
