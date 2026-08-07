namespace HS2.CrystalOverlay.Core;

public sealed record HardwareDiskTelemetry(
    string Name,
    double? TemperatureCelsius,
    double? FreeGigabytes,
    double? CapacityGigabytes,
    string? HealthStatus);

public sealed record HardwareTelemetry(
    double? CpuTemperatureCelsius,
    double? GpuTemperatureCelsius,
    double? GpuHotspotTemperatureCelsius,
    IReadOnlyList<double> MemoryModuleTemperaturesCelsius,
    IReadOnlyList<HardwareDiskTelemetry> Disks,
    IReadOnlyList<double> PumpRpms,
    TimeSpan? NetworkDownFor);

public sealed record HardwareAlertFinding(
    string Key,
    string Title,
    string Body,
    string SuggestedAction,
    int Severity,
    string? EvidenceKey = null);

public static class HardwareAlertContinuity
{
    public const int PumpStoppedSeverity = 110;

    public static bool ShouldRetainActive(
        string? activeFindingKey,
        bool pumpTelemetryAvailable,
        HardwareAlertFinding? nextFinding) =>
        !pumpTelemetryAvailable &&
        string.Equals(
            activeFindingKey,
            "pump-stopped",
            StringComparison.Ordinal) &&
        (nextFinding?.Severity ?? int.MinValue) <= PumpStoppedSeverity;

    public static bool ShouldRetainActive(
        HardwareAlertFinding? activeFinding,
        bool recoveryEvidenceAvailable,
        HardwareAlertFinding? nextFinding) =>
        activeFinding is not null &&
        !recoveryEvidenceAvailable &&
        (nextFinding?.Severity ?? int.MinValue) <= activeFinding.Severity;
}

public sealed class ConsecutiveEvidenceGate
{
    private readonly int requiredSamples;
    private int samples;

    public ConsecutiveEvidenceGate(int requiredSamples)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requiredSamples, 1);
        this.requiredSamples = requiredSamples;
    }

    public bool Observe(bool hasEvidence)
    {
        if (!hasEvidence)
        {
            samples = 0;
            return false;
        }

        samples++;
        if (samples < requiredSamples)
        {
            return false;
        }

        samples = 0;
        return true;
    }

    public void Reset() => samples = 0;
}

public static class HardwareAlertRecovery
{
    public static bool HasRecoveryEvidence(
        HardwareAlertFinding activeFinding,
        HardwareTelemetry telemetry,
        bool pumpTelemetryAvailable)
    {
        ArgumentNullException.ThrowIfNull(activeFinding);
        ArgumentNullException.ThrowIfNull(telemetry);
        return activeFinding.Key switch
        {
            "cpu-overheat" => telemetry.CpuTemperatureCelsius.HasValue,
            "gpu-overheat" => telemetry.GpuTemperatureCelsius.HasValue,
            "gpu-hotspot" =>
                telemetry.GpuHotspotTemperatureCelsius.HasValue,
            "memory-overheat" =>
                HasExpectedSensorCount(
                    activeFinding.EvidenceKey,
                    "memory-count:",
                    telemetry.MemoryModuleTemperaturesCelsius.Count),
            "pump-stopped" =>
                pumpTelemetryAvailable &&
                HasExpectedSensorCount(
                    activeFinding.EvidenceKey,
                    "pump-count:",
                    telemetry.PumpRpms.Count(value =>
                        value >= 0 && value < 20000)),
            "network-down" => true,
            _ when activeFinding.Key.StartsWith(
                "disk-",
                StringComparison.Ordinal) =>
                HasDiskRecoveryEvidence(activeFinding, telemetry.Disks),
            _ => false,
        };
    }

    private static bool HasDiskRecoveryEvidence(
        HardwareAlertFinding activeFinding,
        IReadOnlyList<HardwareDiskTelemetry> disks)
    {
        if (string.IsNullOrWhiteSpace(activeFinding.EvidenceKey))
        {
            return false;
        }

        var disk = disks.FirstOrDefault(candidate => string.Equals(
            candidate.Name,
            activeFinding.EvidenceKey,
            StringComparison.OrdinalIgnoreCase));
        return activeFinding.Key switch
        {
            "disk-overheat" => disk?.TemperatureCelsius.HasValue == true,
            "disk-health" =>
                !string.IsNullOrWhiteSpace(disk?.HealthStatus),
            "disk-space" =>
                disk?.FreeGigabytes.HasValue == true &&
                disk.CapacityGigabytes.HasValue,
            _ => false,
        };
    }

    private static bool HasExpectedSensorCount(
        string? evidenceKey,
        string prefix,
        int actualCount)
    {
        if (evidenceKey?.StartsWith(
                prefix,
                StringComparison.Ordinal) == true &&
            int.TryParse(evidenceKey[prefix.Length..], out var expectedCount))
        {
            return actualCount >= expectedCount;
        }

        return actualCount > 0;
    }
}

public static class HardwareAlertEvaluator
{
    public static IReadOnlyList<HardwareAlertFinding> Evaluate(
        HardwareTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        var findings = new List<HardwareAlertFinding>();
        AddTemperatureFinding(
            findings,
            "cpu-overheat",
            "CPU 温度过高",
            telemetry.CpuTemperatureCelsius,
            90,
            "检查水泵、冷排风扇与冷头接触",
            95);
        AddTemperatureFinding(
            findings,
            "gpu-overheat",
            "GPU 温度过高",
            telemetry.GpuTemperatureCelsius,
            85,
            "检查显卡风扇、进风和负载",
            85);
        AddTemperatureFinding(
            findings,
            "gpu-hotspot",
            "GPU 热点温度过高",
            telemetry.GpuHotspotTemperatureCelsius,
            100,
            "降低负载并检查显卡散热",
            90);

        var hottestMemory = telemetry
            .MemoryModuleTemperaturesCelsius
            .DefaultIfEmpty(double.NaN)
            .Max();
        if (!double.IsNaN(hottestMemory))
        {
            AddTemperatureFinding(
                findings,
                "memory-overheat",
                "内存温度过高",
                hottestMemory,
                85,
                "检查内存区域风道",
                75,
                $"memory-count:{telemetry.MemoryModuleTemperaturesCelsius.Count}");
        }

        foreach (var disk in telemetry.Disks)
        {
            if (disk.TemperatureCelsius is >= 70)
            {
                findings.Add(new HardwareAlertFinding(
                    "disk-overheat",
                    $"{disk.Name} 温度过高",
                    $"{disk.TemperatureCelsius:0.#}°C",
                    "降低持续读写并检查硬盘散热",
                    80,
                    disk.Name));
            }

            if (IsUnhealthy(disk.HealthStatus))
            {
                findings.Add(new HardwareAlertFinding(
                    "disk-health",
                    $"{disk.Name} 健康异常",
                    disk.HealthStatus!,
                    "立即备份重要数据并检查 SMART",
                    100,
                    disk.Name));
            }

            if (IsCriticallyLowOnSpace(disk))
            {
                findings.Add(new HardwareAlertFinding(
                    "disk-space",
                    $"{disk.Name} 空间严重不足",
                    $"仅剩 {disk.FreeGigabytes:0.#} GB",
                    "清理或迁移数据，至少保留 5% 空间",
                    70,
                    disk.Name));
            }
        }

        var validPumpRpms = telemetry.PumpRpms
            .Where(value => value >= 0 && value < 20000)
            .ToArray();
        if (validPumpRpms.Length > 0 &&
            validPumpRpms.All(value => value < 300))
        {
            findings.Add(new HardwareAlertFinding(
                "pump-stopped",
                "水泵转速异常",
                $"泵速 {string.Join(" / ", validPumpRpms.Select(value => $"{value:0} RPM"))}",
                "立即检查水泵供电、接线与 L-Connect",
                HardwareAlertContinuity.PumpStoppedSeverity,
                $"pump-count:{validPumpRpms.Length}"));
        }

        if (telemetry.NetworkDownFor is { } networkDown &&
            networkDown >= TimeSpan.FromSeconds(30))
        {
            findings.Add(new HardwareAlertFinding(
                "network-down",
                "网络长时间断开",
                $"已中断 {FormatDuration(networkDown)}",
                "检查路由器、网线或网络适配器",
                65));
        }

        return findings
            .OrderByDescending(finding => finding.Severity)
            .ThenBy(finding => finding.Key, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddTemperatureFinding(
        ICollection<HardwareAlertFinding> findings,
        string key,
        string title,
        double? temperature,
        double threshold,
        string action,
        int severity,
        string? evidenceKey = null)
    {
        if (temperature is null ||
            temperature.Value < threshold ||
            temperature.Value > 150)
        {
            return;
        }

        findings.Add(new HardwareAlertFinding(
            key,
            title,
            $"{temperature.Value:0.#}°C",
            action,
            severity,
            evidenceKey));
    }

    private static bool IsUnhealthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return !value.Equals(
                   "Healthy",
                   StringComparison.OrdinalIgnoreCase) &&
               !value.Equals(
                   "Unknown",
                   StringComparison.OrdinalIgnoreCase) &&
               !value.Equals(
                   "OK",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCriticallyLowOnSpace(
        HardwareDiskTelemetry disk)
    {
        if (disk.FreeGigabytes is null ||
            disk.CapacityGigabytes is null ||
            disk.CapacityGigabytes <= 0)
        {
            return false;
        }

        var ratio =
            disk.FreeGigabytes.Value / disk.CapacityGigabytes.Value;
        return disk.FreeGigabytes.Value <= 10 || ratio <= 0.03;
    }

    private static string FormatDuration(TimeSpan value) =>
        value.TotalMinutes >= 1
            ? $"{(int)value.TotalMinutes} 分钟"
            : $"{Math.Max(1, (int)value.TotalSeconds)} 秒";
}
