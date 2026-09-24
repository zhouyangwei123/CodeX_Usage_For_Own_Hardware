using System;

namespace CodexToolsHost.Monitor
{
    public sealed class PcMetricsSnapshot
    {
        public const string SourceUnavailable = "Unavailable";
        public const string SourceAcpiThermalZone = "ACPI Thermal Zone";
        public const string SourceLibreHardwareMonitor = "LibreHardwareMonitor";
        public const string SourceNvidiaSmi = "NVIDIA SMI";
        public const string SourceWindowsGpuEngine = "Windows GPU Engine";

        public int CpuLoadPercent { get; set; }
        public ulong MemoryUsedBytes { get; set; }
        public ulong MemoryTotalBytes { get; set; }
        public int GpuLoadPercent { get; set; }
        public double? CpuTemperatureC { get; set; }
        public double? GpuTemperatureC { get; set; }
        public double? MotherboardTemperatureC { get; set; }
        public string TemperatureSource { get; set; }
        public string GpuSource { get; set; }
        public string MotherboardTemperatureSource { get; set; }
        public bool NetworkSpeedAvailable { get; set; }
        public uint NetworkDownloadKiBPerSecond { get; set; }
        public uint NetworkUploadKiBPerSecond { get; set; }
        public DateTimeOffset SampledAtUtc { get; set; }
        public bool IsStale { get; set; }
        public string ErrorText { get; set; }

        public int MemoryUsedPercent
        {
            get
            {
                if (MemoryTotalBytes == 0 || MemoryUsedBytes > MemoryTotalBytes) return -1;
                return (int)Math.Round(MemoryUsedBytes * 100.0 / MemoryTotalBytes,
                    MidpointRounding.AwayFromZero);
            }
        }

        public static PcMetricsSnapshot CreateUnavailable(string errorText, DateTimeOffset sampledAtUtc)
        {
            return new PcMetricsSnapshot
            {
                CpuLoadPercent = -1,
                GpuLoadPercent = -1,
                MemoryUsedBytes = 0,
                MemoryTotalBytes = 0,
                CpuTemperatureC = null,
                GpuTemperatureC = null,
                MotherboardTemperatureC = null,
                TemperatureSource = SourceUnavailable,
                GpuSource = SourceUnavailable,
                MotherboardTemperatureSource = SourceUnavailable,
                NetworkSpeedAvailable = false,
                NetworkDownloadKiBPerSecond = 0u,
                NetworkUploadKiBPerSecond = 0u,
                SampledAtUtc = sampledAtUtc,
                IsStale = true,
                ErrorText = errorText ?? ""
            };
        }
    }
}
