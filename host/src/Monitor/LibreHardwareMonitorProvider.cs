using System;
using System.Collections;
using System.IO;
using System.Reflection;

namespace CodexToolsHost.Monitor
{
    /// <summary>
    /// 从内嵌资源缓存加载可选的 LibreHardwareMonitorLib。即使释放失败、权限不足或
    /// 某台电脑不支持传感器，主程序仍可继续提供 CPU/内存等系统原生指标。
    /// </summary>
    internal sealed class LibreHardwareMonitorProvider : IDisposable
    {
        private readonly object _sync = new object();
        private object _computer;
        private MethodInfo _closeMethod;
        private bool _available;
        private int _cpuTemperaturePriority;
        private int _gpuLoadPriority;
        private int _gpuTemperaturePriority;
        private int _motherboardTemperaturePriority;

        public LibreHardwareMonitorProvider() : this(null)
        {
        }

        internal LibreHardwareMonitorProvider(string dependencyCacheRoot)
        {
            try
            {
                EmbeddedDependencyPreparation dependencies =
                    EmbeddedDependencyStore.Prepare(dependencyCacheRoot);
                string assemblyPath = Path.Combine(dependencies.DirectoryPath,
                    "LibreHardwareMonitorLib.dll");
                if (!File.Exists(assemblyPath)) return;

                Assembly assembly = Assembly.LoadFrom(assemblyPath);
                Type computerType = assembly.GetType(
                    "LibreHardwareMonitor.Hardware.Computer", true);
                _computer = Activator.CreateInstance(computerType);
                SetBoolean(computerType, _computer, "IsCpuEnabled", true);
                SetBoolean(computerType, _computer, "IsGpuEnabled", true);
                SetBoolean(computerType, _computer, "IsMotherboardEnabled", true);
                computerType.GetMethod("Open", Type.EmptyTypes).Invoke(_computer, null);
                _closeMethod = computerType.GetMethod("Close", Type.EmptyTypes);
                _available = true;
            }
            catch (Exception)
            {
                _computer = null;
                _available = false;
            }
        }

        public bool Available { get { return _available; } }

        public bool Read(PcMetricsSnapshot snapshot)
        {
            if (!_available || snapshot == null || _computer == null) return false;
            lock (_sync)
            {
                try
                {
                    _cpuTemperaturePriority = 0;
                    _gpuLoadPriority = 0;
                    _gpuTemperaturePriority = 0;
                    _motherboardTemperaturePriority = 0;

                    IEnumerable hardware = GetEnumerable(_computer, "Hardware");
                    if (hardware == null) return false;
                    foreach (object item in hardware)
                        VisitHardware(item, null, snapshot);

                    if (snapshot.CpuTemperatureC.HasValue)
                        snapshot.TemperatureSource = PcMetricsSnapshot.SourceLibreHardwareMonitor;
                    if (snapshot.GpuLoadPercent >= 0 || snapshot.GpuTemperatureC.HasValue)
                        snapshot.GpuSource = PcMetricsSnapshot.SourceLibreHardwareMonitor;
                    if (snapshot.MotherboardTemperatureC.HasValue)
                        snapshot.MotherboardTemperatureSource =
                            PcMetricsSnapshot.SourceLibreHardwareMonitor;
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        private void VisitHardware(object hardware, string rootType, PcMetricsSnapshot snapshot)
        {
            if (hardware == null) return;
            Type type = hardware.GetType();
            MethodInfo update = type.GetMethod("Update", Type.EmptyTypes);
            if (update != null) update.Invoke(hardware, null);

            string hardwareType = Convert.ToString(GetProperty(hardware, "HardwareType"));
            string effectiveRoot = string.IsNullOrEmpty(rootType) ? hardwareType : rootType;
            IEnumerable sensors = GetEnumerable(hardware, "Sensors");
            if (sensors != null)
            {
                foreach (object sensor in sensors)
                    ApplySensor(effectiveRoot, sensor, snapshot);
            }

            IEnumerable children = GetEnumerable(hardware, "SubHardware");
            if (children == null) return;
            foreach (object child in children)
                VisitHardware(child, effectiveRoot, snapshot);
        }

        private void ApplySensor(string hardwareType, object sensor, PcMetricsSnapshot snapshot)
        {
            string sensorType = Convert.ToString(GetProperty(sensor, "SensorType"));
            string name = Convert.ToString(GetProperty(sensor, "Name")) ?? "";
            object raw = GetProperty(sensor, "Value");
            if (raw == null) return;

            double value;
            try { value = Convert.ToDouble(raw); }
            catch (Exception) { return; }

            if (string.Equals(hardwareType, "Cpu", StringComparison.OrdinalIgnoreCase)
                && string.Equals(sensorType, "Temperature", StringComparison.OrdinalIgnoreCase))
            {
                int priority = CpuTemperaturePriority(name);
                if (priority > _cpuTemperaturePriority && IsPlausibleTemperature(value))
                {
                    _cpuTemperaturePriority = priority;
                    snapshot.CpuTemperatureC = Math.Round(value, 1);
                }
                return;
            }

            if (hardwareType != null
                && hardwareType.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(sensorType, "Load", StringComparison.OrdinalIgnoreCase))
                {
                    int priority = GpuLoadPriority(name);
                    if (priority > _gpuLoadPriority && value >= 0.0 && value <= 100.0)
                    {
                        _gpuLoadPriority = priority;
                        snapshot.GpuLoadPercent = ClampPercent(value);
                    }
                }
                else if (string.Equals(sensorType, "Temperature", StringComparison.OrdinalIgnoreCase))
                {
                    int priority = GpuTemperaturePriority(name);
                    if (priority > _gpuTemperaturePriority && IsPlausibleTemperature(value))
                    {
                        _gpuTemperaturePriority = priority;
                        snapshot.GpuTemperatureC = Math.Round(value, 1);
                    }
                }
                return;
            }

            if (string.Equals(hardwareType, "Motherboard", StringComparison.OrdinalIgnoreCase)
                && string.Equals(sensorType, "Temperature", StringComparison.OrdinalIgnoreCase))
            {
                int priority = MotherboardTemperaturePriority(name);
                if (priority > _motherboardTemperaturePriority && IsPlausibleTemperature(value))
                {
                    _motherboardTemperaturePriority = priority;
                    snapshot.MotherboardTemperatureC = Math.Round(value, 1);
                }
            }
        }

        private static int CpuTemperaturePriority(string name)
        {
            if (EqualsName(name, "Core Max")) return 100;
            if (EqualsName(name, "CPU Package")) return 95;
            if (EqualsName(name, "Core Average")) return 90;
            if (name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase)) return 50;
            return 0;
        }

        private static int GpuLoadPriority(string name)
        {
            if (EqualsName(name, "GPU Core")) return 100;
            if (EqualsName(name, "D3D 3D")) return 80;
            if (name.IndexOf("3D", StringComparison.OrdinalIgnoreCase) >= 0) return 60;
            return 0;
        }

        private static int GpuTemperaturePriority(string name)
        {
            if (EqualsName(name, "GPU Core")) return 100;
            if (name.IndexOf("Hot Spot", StringComparison.OrdinalIgnoreCase) >= 0) return 70;
            return 0;
        }

        private static int MotherboardTemperaturePriority(string name)
        {
            if (EqualsName(name, "System") || EqualsName(name, "Motherboard")
                || EqualsName(name, "Mainboard")) return 100;
            if (EqualsName(name, "SYSTIN") || name.StartsWith("System #",
                StringComparison.OrdinalIgnoreCase)) return 80;
            return 0;
        }

        private static bool EqualsName(string actual, string expected)
        {
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
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

        private static object GetProperty(object instance, string name)
        {
            PropertyInfo property = instance.GetType().GetProperty(name);
            return property == null ? null : property.GetValue(instance, null);
        }

        private static IEnumerable GetEnumerable(object instance, string name)
        {
            return GetProperty(instance, name) as IEnumerable;
        }

        private static void SetBoolean(Type type, object instance, string name, bool value)
        {
            PropertyInfo property = type.GetProperty(name);
            if (property != null && property.CanWrite) property.SetValue(instance, value, null);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_computer != null && _closeMethod != null)
                {
                    try { _closeMethod.Invoke(_computer, null); }
                    catch (Exception) { }
                }
                _computer = null;
                _available = false;
            }
        }
    }
}
