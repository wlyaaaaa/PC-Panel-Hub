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

    [Fact]
    public void MissingPumpProbeCannotResolveAnExistingPumpAlert()
    {
        Assert.True(HardwareAlertContinuity.ShouldRetainActive(
            "pump-stopped",
            pumpTelemetryAvailable: false,
            nextFinding: null));
        Assert.False(HardwareAlertContinuity.ShouldRetainActive(
            "pump-stopped",
            pumpTelemetryAvailable: true,
            nextFinding: null));
        Assert.False(HardwareAlertContinuity.ShouldRetainActive(
            "cpu-overheat",
            pumpTelemetryAvailable: false,
            nextFinding: null));
        Assert.True(HardwareAlertContinuity.ShouldRetainActive(
            "pump-stopped",
            pumpTelemetryAvailable: false,
            nextFinding: new HardwareAlertFinding(
                "cpu-overheat",
                "CPU 温度过高",
                "95°C",
                "检查散热",
                95)));
        Assert.False(HardwareAlertContinuity.ShouldRetainActive(
            "pump-stopped",
            pumpTelemetryAvailable: false,
            nextFinding: new HardwareAlertFinding(
                "future-critical",
                "未来严重故障",
                "需要立即处理",
                "关闭设备",
                HardwareAlertContinuity.PumpStoppedSeverity + 1)));
    }

    [Fact]
    public void RecoveryNeedsThreeConsecutiveValidHealthySamples()
    {
        var gate = new ConsecutiveEvidenceGate(requiredSamples: 3);

        Assert.False(gate.Observe(hasEvidence: true));
        Assert.False(gate.Observe(hasEvidence: false));
        Assert.False(gate.Observe(hasEvidence: true));
        Assert.False(gate.Observe(hasEvidence: true));
        Assert.True(gate.Observe(hasEvidence: true));
    }

    [Fact]
    public void MissingCpuTelemetryCannotConfirmCpuRecovery()
    {
        var missingCpu = new HardwareTelemetry(
            null,
            45,
            55,
            [50],
            [],
            [2500],
            null);
        var healthyCpu = missingCpu with
        {
            CpuTemperatureCelsius = 55,
        };

        var cpuFinding = new HardwareAlertFinding(
            "cpu-overheat",
            "CPU 温度过高",
            "95°C",
            "检查散热",
            95);
        Assert.False(HardwareAlertRecovery.HasRecoveryEvidence(
            cpuFinding,
            missingCpu,
            pumpTelemetryAvailable: true));
        Assert.True(HardwareAlertRecovery.HasRecoveryEvidence(
            cpuFinding,
            healthyCpu,
            pumpTelemetryAvailable: true));
    }

    [Fact]
    public void AnotherHealthyDiskCannotConfirmAlertingDiskRecovery()
    {
        var alertingDisk = new HardwareAlertFinding(
            "disk-health",
            "Disk A 健康异常",
            "Bad",
            "立即备份",
            100,
            EvidenceKey: "Disk A");
        var onlyOtherDiskRemains = new HardwareTelemetry(
            50,
            45,
            55,
            [45, 46],
            [new HardwareDiskTelemetry(
                "Disk B",
                35,
                500,
                1000,
                "Good")],
            [2500],
            null);
        var alertingDiskReturnsHealthy = onlyOtherDiskRemains with
        {
            Disks =
            [
                .. onlyOtherDiskRemains.Disks,
                new HardwareDiskTelemetry(
                    "Disk A",
                    35,
                    500,
                    1000,
                    "Good"),
            ],
        };

        Assert.False(HardwareAlertRecovery.HasRecoveryEvidence(
            alertingDisk,
            onlyOtherDiskRemains,
            pumpTelemetryAvailable: true));
        Assert.True(HardwareAlertContinuity.ShouldRetainActive(
            alertingDisk,
            recoveryEvidenceAvailable: false,
            nextFinding: new HardwareAlertFinding(
                "cpu-overheat",
                "CPU 温度过高",
                "95°C",
                "检查散热",
                95)));
        Assert.True(HardwareAlertRecovery.HasRecoveryEvidence(
            alertingDisk,
            alertingDiskReturnsHealthy,
            pumpTelemetryAvailable: true));
    }
}
