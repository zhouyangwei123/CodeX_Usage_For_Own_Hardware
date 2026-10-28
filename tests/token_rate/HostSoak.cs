using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;
using CodexToolsHost.Usage;

// Run against the actual single-EXE release, using its normal refresh service.
internal static class HostTokenSpeedSoak
{
    static int Main(string[] args)
    {
        string output = args[0]; double seconds = double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        var clock = Stopwatch.StartNew(); var process = Process.GetCurrentProcess();
        TimeSpan cpu = process.TotalProcessorTime;
        int snapshots = 0, updates = 0, activePeak = 0, measuredPeak = 0, idle = 0, waiting = 0;
        long memoryPeak = 0; bool passed = true; string error = null;
        DateTimeOffset previousAt = DateTimeOffset.MinValue; double? previousRate = null;
        try
        {
            using (var service = new LocalUsageService(null))
            {
                service.Start();
                while (clock.Elapsed.TotalSeconds < seconds)
                {
                    Thread.Sleep(100); var report = service.Report; var speed = report.TokenSpeed;
                    if (report.ScanFailed) throw new Exception("scan failed");
                    if (speed == null || !speed.IsReady || speed.AsOf == previousAt) continue;
                    if (speed.AsOf < previousAt || speed.SessionCount < 1 || speed.MeasuredSessions > speed.ActiveSessions)
                        throw new Exception("source or timestamp invariant");
                    double? rate = speed.TokensPerSecond;
                    if (rate.HasValue && (double.IsNaN(rate.Value) || double.IsInfinity(rate.Value) || rate < 0))
                        throw new Exception("invalid rate");
                    if (speed.ActiveSessions == 0 && rate != 0) throw new Exception("idle must be zero");
                    if (speed.ActiveSessions > 0 && speed.MeasuredSessions == 0 && rate.HasValue)
                        throw new Exception("waiting must be unknown");
                    snapshots++; if (snapshots > 1 && rate != previousRate) updates++;
                    activePeak = Math.Max(activePeak, speed.ActiveSessions); measuredPeak = Math.Max(measuredPeak, speed.MeasuredSessions);
                    if (speed.ActiveSessions == 0) idle++; if (!rate.HasValue) waiting++;
                    previousAt = speed.AsOf; previousRate = rate;
                    process.Refresh(); memoryPeak = Math.Max(memoryPeak, process.PrivateMemorySize64);
                }
            }
            if (snapshots < seconds || clock.Elapsed.TotalSeconds < seconds) throw new Exception("insufficient live refreshes");
        }
        catch (Exception ex) { passed = false; error = ex.GetType().Name; }
        process.Refresh();
        var result = new { passed, hostVersion = typeof(LocalUsageService).Assembly.GetName().Version.ToString(),
            elapsedSeconds = Math.Round(clock.Elapsed.TotalSeconds, 3), snapshots, rateUpdates = updates,
            peakActiveSources = activePeak, peakMeasuredSources = measuredPeak, idleSnapshots = idle, waitingSnapshots = waiting,
            singleCoreCpuPercent = Math.Round((process.TotalProcessorTime - cpu).TotalSeconds / clock.Elapsed.TotalSeconds * 100, 3),
            privateMemoryPeakMiB = Math.Round(memoryPeak / 1048576d, 2),
            personalContentOrIdentityRecorded = false, errorType = error };
        File.WriteAllText(output, new JavaScriptSerializer().Serialize(result));
        Console.WriteLine(passed ? "PASS native release live-service soak" : "FAIL native release live-service soak");
        return passed ? 0 : 1;
    }
}
