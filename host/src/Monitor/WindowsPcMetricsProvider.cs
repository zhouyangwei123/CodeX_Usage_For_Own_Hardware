using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Management;

namespace CodexToolsHost.Monitor
{
    public sealed class WindowsPcMetricsProvider : IPcMetricsProvider, IDisposable
    {
        private readonly PerformanceCounter _cpuCounter;
        private readonly LibreHardwareMonitorProvider _hardwareSensors;
        private readonly NetworkSpeedSampler _networkSpeedSampler;

        public WindowsPcMetricsProvider()
        {
            try
            {
                _cpuCounter = new PerformanceCounter(
                    "Processor", "% Processor Time", "_Total", true);
                _cpuCounter.NextValue();
            }
            catch (Exception)
            {
                _cpuCounter = null;
            }
            _hardwareSensors = new LibreHardwareMonitorProvider();
            _networkSpeedSampler = new NetworkSpeedSampler();
        }

        public PcMetricsSnapshot Read()
        {
            var errors = new List<string>();
            var snapshot = new PcMetricsSnapshot
            {
                CpuLoadPercent = -1,
                GpuLoadPercent = -1,
                MemoryUsedBytes = 0,
                MemoryTotalBytes = 0,
                CpuTemperatureC = null,
                GpuTemperatureC = null,
                MotherboardTemperatureC = null,
                TemperatureSource = PcMetricsSnapshot.SourceUnavailable,
                GpuSource = PcMetricsSnapshot.SourceUnavailable,
                MotherboardTemperatureSource = PcMetricsSnapshot.SourceUnavailable,
                NetworkSpeedAvailable = false,
                NetworkDownloadKiBPerSecond = 0u,
                NetworkUploadKiBPerSecond = 0u,
                SampledAtUtc = DateTimeOffset.UtcNow,
                IsStale = false,
                ErrorText = ""
            };

            ReadCpuLoad(snapshot, errors);
            ReadMemory(snapshot, errors);
            ApplyNetworkSample(snapshot, errors, _networkSpeedSampler.Read);

            _hardwareSensors.Read(snapshot);

            if (snapshot.GpuLoadPercent < 0 || !snapshot.GpuTemperatureC.HasValue)
                TryReadNvidiaSmi(snapshot);
            if (snapshot.GpuLoadPercent < 0)
                TryReadWindowsGpuEngine(snapshot);
            if (!snapshot.MotherboardTemperatureC.HasValue)
                ApplyAcpiMotherboardFallback(snapshot, TryReadAcpiTemperature());

            snapshot.ErrorText = string.Join("; ", errors.ToArray());
            return snapshot;
        }

        internal static void ApplyNetworkSample(PcMetricsSnapshot snapshot,
                                                List<string> errors,
                                                Func<NetworkSpeedSample> reader)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            if (errors == null) throw new ArgumentNullException("errors");
            if (reader == null) throw new ArgumentNullException("reader");

            snapshot.NetworkSpeedAvailable = false;
            snapshot.NetworkDownloadKiBPerSecond = 0u;
            snapshot.NetworkUploadKiBPerSecond = 0u;
            try
            {
                NetworkSpeedSample network = reader();
                if (network == null)
                    throw new InvalidOperationException("network sampler returned no value");
                snapshot.NetworkSpeedAvailable = network.Available;
                snapshot.NetworkDownloadKiBPerSecond = network.DownloadKiBPerSecond;
                snapshot.NetworkUploadKiBPerSecond = network.UploadKiBPerSecond;
            }
            catch (Exception ex)
            {
                errors.Add("network: " + ex.GetType().Name);
            }
        }

        internal static void ApplyAcpiMotherboardFallback(PcMetricsSnapshot snapshot,
                                                           double? acpiTemperature)
        {
            if (snapshot == null || snapshot.MotherboardTemperatureC.HasValue
                || !acpiTemperature.HasValue
                || !IsPlausibleTemperature(acpiTemperature.Value))
                return;

            snapshot.MotherboardTemperatureC = Math.Round(acpiTemperature.Value, 1);
            snapshot.MotherboardTemperatureSource =
                PcMetricsSnapshot.SourceAcpiThermalZone;
        }

        private void ReadCpuLoad(PcMetricsSnapshot snapshot, List<string> errors)
        {
            if (_cpuCounter == null)
            {
                errors.Add("CPU counter unavailable");
                return;
            }

            try
            {
                float value = _cpuCounter.NextValue();
                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    errors.Add("CPU counter returned no value");
                    return;
                }
                snapshot.CpuLoadPercent = ClampPercent(value);
            }
            catch (Exception ex)
            {
                errors.Add("CPU: " + ex.GetType().Name);
            }
        }

        private static void ReadMemory(PcMetricsSnapshot snapshot, List<string> errors)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject item in results)
                    {
                        ulong totalKb = Convert.ToUInt64(item["TotalVisibleMemorySize"],
                            CultureInfo.InvariantCulture);
                        ulong freeKb = Convert.ToUInt64(item["FreePhysicalMemory"],
                            CultureInfo.InvariantCulture);
                        if (totalKb == 0 || freeKb > totalKb)
                        {
                            errors.Add("memory values invalid");
                            return;
                        }
                        snapshot.MemoryTotalBytes = totalKb * 1024UL;
                        snapshot.MemoryUsedBytes = (totalKb - freeKb) * 1024UL;
                        return;
                    }
                }
                errors.Add("memory WMI returned no rows");
            }
            catch (Exception ex)
            {
                errors.Add("memory: " + ex.GetType().Name);
            }
        }

        private static bool TryReadNvidiaSmi(PcMetricsSnapshot snapshot)
        {
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = "nvidia-smi.exe",
                    Arguments = "--query-gpu=utilization.gpu,temperature.gpu --format=csv,noheader,nounits",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process process = Process.Start(start))
                {
                    if (process == null) return false;
                    if (!process.WaitForExit(1500))
                    {
                        try { process.Kill(); }
                        catch (Exception) { }
                        return false;
                    }
                    string output = process.StandardOutput.ReadToEnd();

                    int maxLoad = -1;
                    double? maxTemperature = null;
                    string[] lines = output.Split(new[] { '\r', '\n' },
                        StringSplitOptions.RemoveEmptyEntries);
                    foreach (string line in lines)
                    {
                        string[] values = line.Split(',');
                        if (values.Length < 2) continue;
                        int load;
                        double temperature;
                        if (int.TryParse(values[0].Trim(), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out load))
                            maxLoad = Math.Max(maxLoad, ClampPercent(load));
                        if (double.TryParse(values[1].Trim(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out temperature)
                            && IsPlausibleTemperature(temperature))
                            maxTemperature = !maxTemperature.HasValue
                                ? temperature : Math.Max(maxTemperature.Value, temperature);
                    }

                    if (snapshot.GpuLoadPercent < 0 && maxLoad >= 0)
                        snapshot.GpuLoadPercent = maxLoad;
                    if (!snapshot.GpuTemperatureC.HasValue && maxTemperature.HasValue)
                        snapshot.GpuTemperatureC = Math.Round(maxTemperature.Value, 1);
                    if (maxLoad >= 0 || maxTemperature.HasValue)
                    {
                        snapshot.GpuSource = PcMetricsSnapshot.SourceNvidiaSmi;
                        return true;
                    }
                }
            }
            catch (Exception) { }
            return false;
        }

        private static bool TryReadWindowsGpuEngine(PcMetricsSnapshot snapshot)
        {
            try
            {
                int maximum = -1;
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, UtilizationPercentage FROM " +
                    "Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject item in results)
                    {
                        string name = Convert.ToString(item["Name"], CultureInfo.InvariantCulture) ?? "";
                        if (name.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) < 0
                            && name.IndexOf("engtype_Graphics", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        object raw = item["UtilizationPercentage"];
                        if (raw == null) continue;
                        maximum = Math.Max(maximum, ClampPercent(Convert.ToDouble(raw,
                            CultureInfo.InvariantCulture)));
                    }
                }
                if (maximum < 0) return false;
                snapshot.GpuLoadPercent = maximum;
                snapshot.GpuSource = PcMetricsSnapshot.SourceWindowsGpuEngine;
                return true;
            }
            catch (Exception) { return false; }
        }

        private static double? TryReadAcpiTemperature()
        {
            double? temperature = TryReadMsAcpiThermalZone();
            return temperature.HasValue ? temperature : TryReadPerformanceThermalZone();
        }

        private static double? TryReadMsAcpiThermalZone()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    @"root\WMI",
                    "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject item in results)
                    {
                        object raw = item["CurrentTemperature"];
                        if (raw == null) continue;
                        double value = Convert.ToDouble(raw, CultureInfo.InvariantCulture)
                            / 10.0 - 273.15;
                        if (IsPlausibleTemperature(value)) return value;
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        private static double? TryReadPerformanceThermalZone()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Temperature, HighPrecisionTemperature FROM " +
                    "Win32_PerfFormattedData_Counters_ThermalZoneInformation"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject item in results)
                    {
                        object highPrecision = item["HighPrecisionTemperature"];
                        if (highPrecision != null)
                        {
                            double value = Convert.ToDouble(highPrecision,
                                CultureInfo.InvariantCulture) / 10.0 - 273.15;
                            if (IsPlausibleTemperature(value)) return value;
                        }

                        object normal = item["Temperature"];
                        if (normal != null)
                        {
                            double value = Convert.ToDouble(normal,
                                CultureInfo.InvariantCulture) - 273.15;
                            if (IsPlausibleTemperature(value)) return value;
                        }
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        private static int ClampPercent(double value)
        {
            return Math.Max(0, Math.Min(100,
                (int)Math.Round(value, MidpointRounding.AwayFromZero)));
        }

        private static bool IsPlausibleTemperature(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value)
                && value >= -40.0 && value <= 150.0;
        }

        public void Dispose()
        {
            if (_cpuCounter != null) _cpuCounter.Dispose();
            if (_hardwareSensors != null) _hardwareSensors.Dispose();
        }
    }
}
