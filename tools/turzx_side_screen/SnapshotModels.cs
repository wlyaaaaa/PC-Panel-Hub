using System;
using System.Runtime.Serialization;

namespace TURZX.SideScreen
{
    [DataContract]
    public sealed class Snapshot
    {
        [DataMember(Name = "schema_version")]
        public int? SchemaVersion { get; set; }

        [DataMember(Name = "timestamp_unix_ms")]
        public long? TimestampUnixMs { get; set; }

        [DataMember(Name = "sequence")]
        public long? Sequence { get; set; }

        [DataMember(Name = "time")]
        public object Time { get; set; }

        [DataMember(Name = "weather")]
        public WeatherSnapshot Weather { get; set; }

        [DataMember(Name = "alert")]
        public AlertSnapshot Alert { get; set; }

        [DataMember(Name = "foreground_app")]
        public ForegroundAppSnapshot ForegroundApp { get; set; }

        [DataMember(Name = "cpu")]
        public CpuSnapshot Cpu { get; set; }

        [DataMember(Name = "gpu")]
        public GpuSnapshot Gpu { get; set; }

        [DataMember(Name = "fps")]
        public FpsSnapshot Fps { get; set; }

        [DataMember(Name = "memory")]
        public MemorySnapshot Memory { get; set; }

        [DataMember(Name = "disks")]
        public DiskSnapshot[] Disks { get; set; }

        [DataMember(Name = "physical_disks")]
        public PhysicalDiskSnapshot[] PhysicalDisks { get; set; }

        [DataMember(Name = "network")]
        public NetworkSnapshot Network { get; set; }

        [DataMember(Name = "top_processes")]
        public ProcessSnapshot[] TopProcesses { get; set; }

        [DataMember(Name = "health")]
        public HealthSnapshot Health { get; set; }

        [DataMember(Name = "trust")]
        public TrustSnapshot Trust { get; set; }
    }

    [DataContract]
    public sealed class TimeSnapshot
    {
        [DataMember(Name = "date")]
        public string Date { get; set; }

        [DataMember(Name = "weekday")]
        public string Weekday { get; set; }

        [DataMember(Name = "time")]
        public string Time { get; set; }

        [DataMember(Name = "timestamp")]
        public string Timestamp { get; set; }

        [DataMember(Name = "update_interval_seconds")]
        public double? UpdateIntervalSeconds { get; set; }
    }

    [DataContract]
    public sealed class WeatherSnapshot
    {
        [DataMember(Name = "city")]
        public string City { get; set; }

        [DataMember(Name = "temperature_celsius")]
        public double? TemperatureCelsius { get; set; }

        [DataMember(Name = "temperature_c")]
        public double? TemperatureC { get; set; }

        [DataMember(Name = "temperature_text")]
        public string TemperatureText { get; set; }

        [DataMember(Name = "high_temperature_celsius")]
        public double? HighTemperatureCelsius { get; set; }

        [DataMember(Name = "low_temperature_celsius")]
        public double? LowTemperatureCelsius { get; set; }

        [DataMember(Name = "rain_probability_percent")]
        public double? RainProbabilityPercent { get; set; }

        [DataMember(Name = "condition")]
        public string Condition { get; set; }

        [DataMember(Name = "summary")]
        public string Summary { get; set; }

        [DataMember(Name = "aqi")]
        public int? Aqi { get; set; }

        [DataMember(Name = "humidity_percent")]
        public double? HumidityPercent { get; set; }

        [DataMember(Name = "source")]
        public string Source { get; set; }

        [DataMember(Name = "updated_at")]
        public string UpdatedAt { get; set; }
    }

    [DataContract]
    public sealed class AlertSnapshot
    {
        [DataMember(Name = "level")]
        public string Level { get; set; }

        [DataMember(Name = "message")]
        public string Message { get; set; }

        [DataMember(Name = "items")]
        public string[] Items { get; set; }
    }

    [DataContract]
    public sealed class ForegroundAppSnapshot
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "process_name")]
        public string ProcessName { get; set; }

        [DataMember(Name = "title")]
        public string Title { get; set; }

        [DataMember(Name = "process_id")]
        public int? ProcessId { get; set; }

        [DataMember(Name = "exe_path")]
        public string ExePath { get; set; }

        [DataMember(Name = "source")]
        public string Source { get; set; }
    }

    [DataContract]
    public sealed class CpuSnapshot
    {
        [DataMember(Name = "model")]
        public string Model { get; set; }

        [DataMember(Name = "usage_percent")]
        public double? UsagePercent { get; set; }

        [DataMember(Name = "temperature_celsius")]
        public double? TemperatureCelsius { get; set; }

        [DataMember(Name = "power_watts")]
        public double? PowerWatts { get; set; }

        [DataMember(Name = "clock_ghz")]
        public double? ClockGhz { get; set; }

        [DataMember(Name = "clock_mhz")]
        public double? ClockMhz { get; set; }

        [DataMember(Name = "core_voltage")]
        public double? CoreVoltage { get; set; }

        [DataMember(Name = "load_history_percent")]
        public double[] LoadHistoryPercent { get; set; }

        [DataMember(Name = "logical_count")]
        public int? LogicalCount { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "source")]
        public string Source { get; set; }
    }

    [DataContract]
    public sealed class GpuSnapshot
    {
        [DataMember(Name = "model")]
        public string Model { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "usage_percent")]
        public double? UsagePercent { get; set; }

        [DataMember(Name = "temperature_celsius")]
        public double? TemperatureCelsius { get; set; }

        [DataMember(Name = "temperature_c")]
        public double? TemperatureC { get; set; }

        [DataMember(Name = "power_watts")]
        public double? PowerWatts { get; set; }

        [DataMember(Name = "core_clock_ghz")]
        public double? CoreClockGhz { get; set; }

        [DataMember(Name = "core_clock_mhz")]
        public double? CoreClockMhz { get; set; }

        [DataMember(Name = "core_voltage")]
        public double? CoreVoltage { get; set; }

        [DataMember(Name = "memory_clock_ghz")]
        public double? MemoryClockGhz { get; set; }

        [DataMember(Name = "memory_clock_mhz")]
        public double? MemoryClockMhz { get; set; }

        [DataMember(Name = "vram_used_gb")]
        public double? VramUsedGb { get; set; }

        [DataMember(Name = "vram_total_gb")]
        public double? VramTotalGb { get; set; }

        [DataMember(Name = "load_history_percent")]
        public double[] LoadHistoryPercent { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "source")]
        public string Source { get; set; }
    }

    [DataContract]
    public sealed class FpsSnapshot
    {
        [DataMember(Name = "current")]
        public double? Current { get; set; }

        [DataMember(Name = "average")]
        public double? Average { get; set; }

        [DataMember(Name = "low_1_percent")]
        public double? Low1Percent { get; set; }

        [DataMember(Name = "frame_time_ms")]
        public double? FrameTimeMs { get; set; }

        [DataMember(Name = "source")]
        public string Source { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "sample_age_seconds")]
        public double? SampleAgeSeconds { get; set; }

        [DataMember(Name = "detail")]
        public string Detail { get; set; }
    }

    [DataContract]
    public sealed class MemorySnapshot
    {
        [DataMember(Name = "ram_usage_percent")]
        public double? RamUsagePercent { get; set; }

        [DataMember(Name = "used_percent")]
        public double? UsedPercent { get; set; }

        [DataMember(Name = "ram_used_gb")]
        public double? RamUsedGb { get; set; }

        [DataMember(Name = "used_gb")]
        public double? UsedGb { get; set; }

        [DataMember(Name = "available_gb")]
        public double? AvailableGb { get; set; }

        [DataMember(Name = "ram_total_gb")]
        public double? RamTotalGb { get; set; }

        [DataMember(Name = "vram_usage_percent")]
        public double? VramUsagePercent { get; set; }

        [DataMember(Name = "vram_used_gb")]
        public double? VramUsedGb { get; set; }

        [DataMember(Name = "vram_total_gb")]
        public double? VramTotalGb { get; set; }

        [DataMember(Name = "total_gb")]
        public double? TotalGb { get; set; }

        [DataMember(Name = "motherboard_temperature_celsius")]
        public double? MotherboardTemperatureCelsius { get; set; }

        [DataMember(Name = "module_temperatures_celsius")]
        public double[] ModuleTemperaturesCelsius { get; set; }

        [DataMember(Name = "source")]
        public string Source { get; set; }
    }

    [DataContract]
    public sealed class DiskSnapshot
    {
        [DataMember(Name = "drive")]
        public string Drive { get; set; }

        [DataMember(Name = "label")]
        public string Label { get; set; }

        [DataMember(Name = "usage_percent")]
        public double? UsagePercent { get; set; }

        [DataMember(Name = "used_percent")]
        public double? UsedPercent { get; set; }

        [DataMember(Name = "free_text")]
        public string FreeText { get; set; }

        [DataMember(Name = "free_gb")]
        public double? FreeGb { get; set; }

        [DataMember(Name = "total_gb")]
        public double? TotalGb { get; set; }

        [DataMember(Name = "drive_type")]
        public string DriveType { get; set; }
    }

    [DataContract]
    public sealed class PhysicalDiskSnapshot
    {
        [DataMember(Name = "model")]
        public string Model { get; set; }

        [DataMember(Name = "volume_drives")]
        public string[] VolumeDrives { get; set; }

        [DataMember(Name = "volumes")]
        public string[] Volumes { get; set; }

        [DataMember(Name = "bus_type")]
        public string BusType { get; set; }

        [DataMember(Name = "media_type")]
        public string MediaType { get; set; }

        [DataMember(Name = "capacity_gb")]
        public double? CapacityGb { get; set; }

        [DataMember(Name = "total_gb")]
        public double? TotalGb { get; set; }

        [DataMember(Name = "used_percent")]
        public double? UsedPercent { get; set; }

        [DataMember(Name = "free_gb")]
        public double? FreeGb { get; set; }

        [DataMember(Name = "read_bytes_per_second")]
        public double? ReadBytesPerSecond { get; set; }

        [DataMember(Name = "write_bytes_per_second")]
        public double? WriteBytesPerSecond { get; set; }

        [DataMember(Name = "activity_percent")]
        public double? ActivityPercent { get; set; }

        [DataMember(Name = "temperature_celsius")]
        public double? TemperatureCelsius { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "source")]
        public string Source { get; set; }
    }

    [DataContract]
    public sealed class NetworkSnapshot
    {
        [DataMember(Name = "download_bytes_per_second")]
        public double? DownloadBytesPerSecond { get; set; }

        [DataMember(Name = "rx_bytes_per_sec")]
        public double? RxBytesPerSecond { get; set; }

        [DataMember(Name = "upload_bytes_per_second")]
        public double? UploadBytesPerSecond { get; set; }

        [DataMember(Name = "tx_bytes_per_sec")]
        public double? TxBytesPerSecond { get; set; }

        [DataMember(Name = "ping_ms")]
        public double? PingMs { get; set; }

        [DataMember(Name = "jitter_ms")]
        public double? JitterMs { get; set; }

        [DataMember(Name = "packet_loss_percent")]
        public double? PacketLossPercent { get; set; }

        [DataMember(Name = "dpc_latency_microseconds")]
        public double? DpcLatencyMicroseconds { get; set; }

        [DataMember(Name = "dpc_percent")]
        public double? DpcPercent { get; set; }

        [DataMember(Name = "addresses")]
        public string[] Addresses { get; set; }

        [DataMember(Name = "source")]
        public string Source { get; set; }
    }

    [DataContract]
    public sealed class ProcessSnapshot
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "description")]
        public string Description { get; set; }

        [DataMember(Name = "cpu_percent")]
        public double? CpuPercent { get; set; }

        [DataMember(Name = "gpu_percent")]
        public double? GpuPercent { get; set; }

        [DataMember(Name = "memory_gb")]
        public double? MemoryGb { get; set; }

        [DataMember(Name = "memory_mb")]
        public double? MemoryMb { get; set; }

        [DataMember(Name = "pid")]
        public int? Pid { get; set; }
    }

    [DataContract]
    public sealed class HealthSnapshot
    {
        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "detail")]
        public string Detail { get; set; }

        [DataMember(Name = "dpc_latency_us")]
        public double? DpcLatencyUs { get; set; }

        [DataMember(Name = "hard_page_faults_per_second")]
        public double? HardPageFaultsPerSecond { get; set; }

        [DataMember(Name = "refresh_interval_seconds")]
        public double? RefreshIntervalSeconds { get; set; }

        [DataMember(Name = "generated_at")]
        public string GeneratedAt { get; set; }

        [DataMember(Name = "errors")]
        public HealthErrorSnapshot[] Errors { get; set; }
    }

    [DataContract]
    public sealed class HealthErrorSnapshot
    {
        [DataMember(Name = "component")]
        public string Component { get; set; }

        [DataMember(Name = "error")]
        public string Error { get; set; }
    }

    [DataContract]
    public sealed class TrustSnapshot
    {
        [DataMember(Name = "score")]
        public int? Score { get; set; }

        [DataMember(Name = "level")]
        public string Level { get; set; }

        [DataMember(Name = "summary")]
        public string Summary { get; set; }

        [DataMember(Name = "worst_component")]
        public string WorstComponent { get; set; }

        [DataMember(Name = "worst_label")]
        public string WorstLabel { get; set; }

        [DataMember(Name = "missing_count")]
        public int? MissingCount { get; set; }

        [DataMember(Name = "missing_field_count")]
        public int? MissingFieldCount { get; set; }

        [DataMember(Name = "fallback_count")]
        public int? FallbackCount { get; set; }

        [DataMember(Name = "stale_count")]
        public int? StaleCount { get; set; }

        [DataMember(Name = "log_path")]
        public string LogPath { get; set; }

        [DataMember(Name = "items")]
        public TrustItemSnapshot[] Items { get; set; }
    }

    [DataContract]
    public sealed class TrustItemSnapshot
    {
        [DataMember(Name = "component")]
        public string Component { get; set; }

        [DataMember(Name = "label")]
        public string Label { get; set; }

        [DataMember(Name = "score")]
        public int? Score { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "source")]
        public string Source { get; set; }

        [DataMember(Name = "missing_count")]
        public int? MissingCount { get; set; }

        [DataMember(Name = "fallback")]
        public bool? Fallback { get; set; }

        [DataMember(Name = "detail")]
        public string Detail { get; set; }
    }
}
