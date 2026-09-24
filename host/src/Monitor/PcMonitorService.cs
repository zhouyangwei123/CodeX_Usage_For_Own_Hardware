using System;
using System.Threading;

namespace CodexToolsHost.Monitor
{
    public sealed class PcMonitorService : IDisposable
    {
        private readonly IPcMetricsProvider _provider;
        private readonly int _intervalMilliseconds;
        private readonly object _sync = new object();
        private Timer _timer;
        private PcMetricsSnapshot _current;
        private DateTimeOffset _lastGoodUtc;
        private bool _disposed;
        private bool _refreshInProgress;

        public PcMonitorService(IPcMetricsProvider provider, int intervalMilliseconds)
        {
            if (provider == null) throw new ArgumentNullException("provider");
            if (intervalMilliseconds < 500) throw new ArgumentOutOfRangeException("intervalMilliseconds");
            _provider = provider;
            _intervalMilliseconds = intervalMilliseconds;
            _current = PcMetricsSnapshot.CreateUnavailable("not sampled", DateTimeOffset.UtcNow);
        }

        public event Action<PcMetricsSnapshot> Changed;

        public PcMetricsSnapshot Current
        {
            get
            {
                lock (_sync) return Clone(_current);
            }
        }

        public void Start()
        {
            lock (_sync)
            {
                if (_disposed || _timer != null) return;
                _timer = new Timer(delegate { RefreshNow(); }, null, 0, _intervalMilliseconds);
            }
        }

        public void Stop()
        {
            Timer timer;
            lock (_sync)
            {
                timer = _timer;
                _timer = null;
            }
            if (timer != null) timer.Dispose();
        }

        public void RefreshNow()
        {
            lock (_sync)
            {
                if (_disposed || _refreshInProgress) return;
                _refreshInProgress = true;
            }
            try
            {
                PcMetricsSnapshot next;
                try
                {
                    next = _provider.Read() ?? PcMetricsSnapshot.CreateUnavailable(
                        "provider returned no snapshot", DateTimeOffset.UtcNow);
                    if (next.SampledAtUtc == default(DateTimeOffset))
                        next.SampledAtUtc = DateTimeOffset.UtcNow;
                    _lastGoodUtc = next.SampledAtUtc;
                }
                catch (Exception ex)
                {
                    PcMetricsSnapshot previous = Current;
                    if (previous != null && previous.CpuLoadPercent >= 0)
                    {
                        next = Clone(previous);
                        next.ErrorText = ex.GetType().Name + ": " + ex.Message;
                        next.IsStale = (DateTimeOffset.UtcNow - _lastGoodUtc).TotalSeconds >= 6.0;
                        next.SampledAtUtc = DateTimeOffset.UtcNow;
                    }
                    else
                    {
                        next = PcMetricsSnapshot.CreateUnavailable(
                            ex.GetType().Name + ": " + ex.Message, DateTimeOffset.UtcNow);
                    }
                }

                lock (_sync)
                {
                    if (_disposed) return;
                    _current = Clone(next);
                }
                RaiseChangedSafely(Clone(next));
            }
            finally
            {
                lock (_sync)
                {
                    _refreshInProgress = false;
                    System.Threading.Monitor.PulseAll(_sync);
                }
            }
        }

        private void RaiseChangedSafely(PcMetricsSnapshot snapshot)
        {
            Action<PcMetricsSnapshot> handler = Changed;
            if (handler == null) return;
            foreach (Action<PcMetricsSnapshot> subscriber in handler.GetInvocationList())
            {
                try { subscriber(Clone(snapshot)); }
                catch (Exception) { }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
            }
            Stop();
            lock (_sync)
            {
                while (_refreshInProgress) System.Threading.Monitor.Wait(_sync);
            }
            IDisposable disposable = _provider as IDisposable;
            if (disposable != null) disposable.Dispose();
        }

        private static PcMetricsSnapshot Clone(PcMetricsSnapshot source)
        {
            if (source == null) return null;
            return new PcMetricsSnapshot
            {
                CpuLoadPercent = source.CpuLoadPercent,
                GpuLoadPercent = source.GpuLoadPercent,
                MemoryUsedBytes = source.MemoryUsedBytes,
                MemoryTotalBytes = source.MemoryTotalBytes,
                CpuTemperatureC = source.CpuTemperatureC,
                GpuTemperatureC = source.GpuTemperatureC,
                MotherboardTemperatureC = source.MotherboardTemperatureC,
                TemperatureSource = source.TemperatureSource,
                GpuSource = source.GpuSource,
                MotherboardTemperatureSource = source.MotherboardTemperatureSource,
                NetworkSpeedAvailable = source.NetworkSpeedAvailable,
                NetworkDownloadKiBPerSecond = source.NetworkDownloadKiBPerSecond,
                NetworkUploadKiBPerSecond = source.NetworkUploadKiBPerSecond,
                SampledAtUtc = source.SampledAtUtc,
                IsStale = source.IsStale,
                ErrorText = source.ErrorText
            };
        }
    }
}
