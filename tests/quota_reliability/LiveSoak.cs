using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using CodexToolsHost.Core;
using CodexToolsHost.Model;
using CodexToolsHost.Monitor;
using CodexToolsHost.Protocol;
using CodexToolsHost.Quota;
using CodexToolsHost.Tests;

internal static class LiveSoak
{
    public static int Main(string[] args)
    {
        int seconds = int.Parse(args[0]); string directory = args[1];
        Directory.CreateDirectory(directory);
        var json = new JavaScriptSerializer();
        var source = CodexStatusProvider.CreateDefault(30);
        var config = AppConfig.CreateDefault(); config.QuotaHudVisible = false; config.Mijia.AutoRefresh = false;
        var device = new MockDeviceLink();
        var pc = new PcMonitorService(new WindowsPcMetricsProvider(), 2000);
        ConstructorInfo constructor = null;
        foreach (var candidate in typeof(BridgeService).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance))
            if (candidate.GetParameters().Length == 8) constructor = candidate;
        if (constructor == null) throw new Exception("Expected isolated BridgeService constructor.");
        int samples = 0, stale = 0, pcValid = 0, updates = 0; bool forcedReconnect = false;
        DateTimeOffset lastObservation = DateTimeOffset.MinValue;
        var watch = Stopwatch.StartNew(); var started = DateTimeOffset.UtcNow;
        using (var bridge = (BridgeService)constructor.Invoke(new object[] {config, device, source, null,
            new FakeDeepSeekSource(), pc, new FakeChatGptRestartService(), new FakeOpenCodeGoSource()}))
        using (var log = new StreamWriter(Path.Combine(directory, "samples.jsonl"), false))
        {
            bridge.Start();
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep(1000);
                if (!forcedReconnect && watch.Elapsed.TotalSeconds > Math.Min(60, seconds / 2))
                {
                    source.Stop(); source.StartAsync().Wait(TimeSpan.FromSeconds(50)); forcedReconnect = true;
                }
                var quota = source.Quota;
                if (quota.ObservedAt != DateTimeOffset.MinValue && quota.ObservedAt != lastObservation)
                { updates++; lastObservation = quota.ObservedAt; }
                if (samples > 0 && watch.Elapsed.TotalSeconds < samples * 10) continue;
                samples++; if (quota.IsStale) stale++;
                var metrics = pc.Current; if (metrics.MemoryTotalBytes > 0) pcValid++;
                using (var process = Process.GetCurrentProcess())
                {
                    process.Refresh();
                    var data = new { utc = DateTimeOffset.UtcNow.ToString("o"), elapsedSeconds = Math.Round(watch.Elapsed.TotalSeconds, 1),
                        quotaFresh = !quota.IsStale, primaryRemaining = quota.PrimaryRemainingPercent,
                        quotaObserved = quota.ObservedAt.ToString("o"), lastError = quota.LastError,
                        reconnects = source.ReconnectCount, updates = updates, pcMemoryValid = metrics.MemoryTotalBytes > 0,
                        statusFrames = device.StatusCount, pcFrames = device.PcMetricsCount,
                        pcFrameLength = device.LastPcMetrics == null ? 0 : device.LastPcMetrics.Length,
                        privateBytes = process.PrivateMemorySize64, handles = process.HandleCount,
                        threads = process.Threads.Count, cpuSeconds = process.TotalProcessorTime.TotalSeconds };
                    log.WriteLine(json.Serialize(data)); log.Flush();
                    if (samples % 30 == 1) Console.WriteLine("Soak elapsed=" + (int)watch.Elapsed.TotalSeconds + "s updates=" + updates + " staleSamples=" + stale);
                }
            }
            bool passed = updates >= Math.Max(1, seconds / 90) && pcValid > 0 && !source.Quota.IsStale
                && device.LastStatusValid && device.LastPcMetrics != null && device.LastPcMetrics.Length == 28;
            var summary = new { startedAt = started.ToString("o"), elapsedSeconds = watch.Elapsed.TotalSeconds,
                requestedSeconds = seconds, assemblyPath = typeof(BridgeService).Assembly.Location, passed = passed,
                samples = samples, staleSamples = stale, successfulQuotaUpdates = updates, pcValidSamples = pcValid,
                forcedOwnedChildReconnect = forcedReconnect, statusFrames = device.StatusCount, pcFrames = device.PcMetricsCount,
                hardwareUsed = false, sampleFile = "samples.jsonl" };
            File.WriteAllText(Path.Combine(directory, "summary.json"), json.Serialize(summary));
            Console.WriteLine(json.Serialize(summary)); return passed ? 0 : 1;
        }
    }
}
