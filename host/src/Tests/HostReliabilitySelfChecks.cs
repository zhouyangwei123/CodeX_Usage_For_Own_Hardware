using System;
using System.Collections.Generic;
using System.Threading;
using CodexToolsHost.Monitor;
using CodexToolsHost.Protocol;
using CodexToolsHost.Quota;

namespace CodexToolsHost.Tests
{
    internal static class HostReliabilitySelfChecks
    {
        public static Dictionary<string, object> Run()
        {
            var result = new Dictionary<string, object>();
            var heartbeat = new SerialHeartbeatTracker(2);
            byte[] first = { 0x00, 0x00, 0x00, 0x01 };
            byte[] second = { 0x00, 0x00, 0x00, 0x02 };
            byte[] third = { 0x00, 0x00, 0x00, 0x03 };

            bool firstPing = heartbeat.RegisterPing(first);
            bool wrongPongRejected = !heartbeat.AcceptPong(second);
            bool firstMissTolerated = heartbeat.RegisterPing(second);
            bool secondMissDisconnects = !heartbeat.RegisterPing(third);
            heartbeat.Reset();
            bool resetRestores = heartbeat.RegisterPing(first)
                && heartbeat.AcceptPong(first)
                && heartbeat.RegisterPing(second);

            result["serialHeartbeatPolicyOk"] = firstPing && wrongPongRejected
                && firstMissTolerated && secondMissDisconnects && resetRestores;
            result["sqliteTransientBindingOk"] =
                DesktopLogStatusMonitor.SqliteTransient == new IntPtr(-1);
            result["pcMonitorSerializationOk"] = CheckPcMonitorSerialization();
            Dictionary<string, object> network = NetworkSpeedSelfChecks.Run();
            foreach (KeyValuePair<string, object> item in network)
                result[item.Key] = item.Value;
            Dictionary<string, object> pcMetricsV3 = PcMetricsProtocolV3SelfChecks.Run();
            foreach (KeyValuePair<string, object> item in pcMetricsV3)
                result[item.Key] = item.Value;
            return result;
        }

        private static bool CheckPcMonitorSerialization()
        {
            var provider = new BlockingPcMetricsProvider();
            var service = new PcMonitorService(provider, 500);
            Exception firstError = null;
            Exception secondError = null;
            Exception disposeError = null;
            var disposeDone = new ManualResetEvent(false);
            Thread first = new Thread(new ThreadStart(delegate
            {
                try { service.RefreshNow(); }
                catch (Exception ex) { firstError = ex; }
            }));
            Thread second = new Thread(new ThreadStart(delegate
            {
                try { service.RefreshNow(); }
                catch (Exception ex) { secondError = ex; }
            }));
            Thread disposer = new Thread(new ThreadStart(delegate
            {
                try { service.Dispose(); }
                catch (Exception ex) { disposeError = ex; }
                finally { disposeDone.Set(); }
            }));

            try
            {
                first.Start();
                if (!provider.Entered.WaitOne(1500)) return false;
                second.Start();
                Thread.Sleep(120);
                disposer.Start();
                Thread.Sleep(120);
                bool disposeWaitedForRead = !disposeDone.WaitOne(0);
                provider.Release.Set();
                bool threadsStopped = first.Join(2000) && second.Join(2000)
                    && disposer.Join(2000);
                return threadsStopped && disposeWaitedForRead
                    && provider.ReadCalls == 1 && provider.MaxConcurrentReads == 1
                    && !provider.DisposedWhileReading
                    && firstError == null && secondError == null && disposeError == null;
            }
            finally
            {
                provider.Release.Set();
                if (first.IsAlive) first.Join(1000);
                if (second.IsAlive) second.Join(1000);
                if (disposer.IsAlive) disposer.Join(1000);
                disposeDone.Dispose();
                provider.DisposeSignals();
            }
        }

        private sealed class BlockingPcMetricsProvider : IPcMetricsProvider, IDisposable
        {
            private int _activeReads;
            private int _readCalls;
            private int _maxConcurrentReads;
            private int _disposedWhileReading;

            public readonly ManualResetEvent Entered = new ManualResetEvent(false);
            public readonly ManualResetEvent Release = new ManualResetEvent(false);

            public int ReadCalls { get { return Volatile.Read(ref _readCalls); } }
            public int MaxConcurrentReads { get { return Volatile.Read(ref _maxConcurrentReads); } }
            public bool DisposedWhileReading
            {
                get { return Volatile.Read(ref _disposedWhileReading) != 0; }
            }

            public PcMetricsSnapshot Read()
            {
                Interlocked.Increment(ref _readCalls);
                int active = Interlocked.Increment(ref _activeReads);
                UpdateMaximum(active);
                Entered.Set();
                try
                {
                    Release.WaitOne(3000);
                    return PcMetricsSnapshot.CreateUnavailable("test", DateTimeOffset.UtcNow);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeReads);
                }
            }

            public void Dispose()
            {
                if (Volatile.Read(ref _activeReads) != 0)
                    Interlocked.Exchange(ref _disposedWhileReading, 1);
            }

            public void DisposeSignals()
            {
                Entered.Dispose();
                Release.Dispose();
            }

            private void UpdateMaximum(int active)
            {
                while (true)
                {
                    int current = Volatile.Read(ref _maxConcurrentReads);
                    if (active <= current) return;
                    if (Interlocked.CompareExchange(ref _maxConcurrentReads,
                        active, current) == current) return;
                }
            }
        }
    }
}
