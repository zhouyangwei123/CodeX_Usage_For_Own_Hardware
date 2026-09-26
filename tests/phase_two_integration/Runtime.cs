using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using CodexToolsHost.Core;
using CodexToolsHost.Model;
using CodexToolsHost.Monitor;
using CodexToolsHost.Protocol;
using CodexToolsHost.Quota;
using CodexToolsHost.Tests;
using CodexToolsHost.UI;
using CodexToolsHost.Usage;
using CodexToolsHost.Updates;

internal static class Runtime
{
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
    [STAThread] static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        string directory = args[0]; int seconds = int.Parse(args[1]); Directory.CreateDirectory(directory);
        var json = new JavaScriptSerializer(); var rows = new List<object>();
        var config = AppConfig.Load(directory, directory); config.Mijia.AutoRefresh = false; config.QuotaHudStyle = "glass";
        if(args.Length>2&&args[2]=="unpinned")config.QuotaHudTopMost=false;
        var source = CodexStatusProvider.CreateDefault(30); var device = new MockDeviceLink();
        var pc = new PcMonitorService(new WindowsPcMetricsProvider(), 2000);
        ConstructorInfo constructor = null;
        foreach (var candidate in typeof(BridgeService).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance))
            if (candidate.GetParameters().Length == 8) constructor = candidate;
        if (constructor == null) throw new Exception("Missing isolated BridgeService constructor.");
        bool passed; int updates = 0, staleAfterWarmup = 0; DateTimeOffset previous = DateTimeOffset.MinValue;
        int gdiBefore = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
        using (var bridge = (BridgeService)constructor.Invoke(new object[] { config, device, source, null,
            new FakeDeepSeekSource(), pc, new FakeChatGptRestartService(), new FakeOpenCodeGoSource() }))
        using (var usage = new LocalUsageService(null))
        using (var release = new ReleaseUpdateService(typeof(BridgeService).Assembly.GetName().Version, Path.Combine(directory, "cache")))
        using (var hud = new QuotaHudForm(config, source, bridge.DeepSeek, delegate { bridge.RefreshQuota(); }, bridge, usage))
        using (var log = new StreamWriter(Path.Combine(directory, "samples.jsonl")))
        {
            hud.Location = new Point(-30000, -30000); hud.Show();
            bridge.Start(); usage.Start(); release.Start(true);
            var watch = Stopwatch.StartNew(); double next = 0; int toggles = 0, activityUpdates = 0;
            DateTimeOffset lastActivityUpdate = default(DateTimeOffset);
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                Application.DoEvents(); Thread.Sleep(25);
                if (watch.Elapsed.TotalSeconds < next) continue;
                next += 10;
                if(toggles<2 && watch.Elapsed.TotalSeconds>=40+toggles*20)
                {
                    hud.Hide();
                    if(!hud.ToggleChart())throw new Exception("Runtime chart toggle failed");
                    hud.Location=new Point(-30000,-30000);hud.Show();toggles++;
                }
                var activity=(UsageActivitySnapshot)typeof(QuotaHudForm).GetField("_activitySnapshot",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(hud);
                if(activity.AsOf!=lastActivityUpdate){lastActivityUpdate=activity.AsOf;activityUpdates++;}
                var quota = source.Quota;
                if (quota.ObservedAt != DateTimeOffset.MinValue && quota.ObservedAt != previous) { updates++; previous = quota.ObservedAt; }
                if (watch.Elapsed.TotalSeconds > 30 && quota.IsStale) staleAfterWarmup++;
                using (var process = Process.GetCurrentProcess())
                {
                    process.Refresh();
                    var row = new { elapsedSeconds = Math.Round(watch.Elapsed.TotalSeconds, 1), quotaFresh = !quota.IsStale,
                        updates = updates, reconnects = source.ReconnectCount, usageFiles = usage.Report.FilesDiscovered,
                        usagePending = usage.Report.FilesPending, usageBytes = usage.Report.BytesRead,
                        updateState = release.Snapshot.State.ToString(), privateBytes = process.PrivateMemorySize64,
                        handles = process.HandleCount, gdi = GetGuiResources(process.Handle, 0), cpuSeconds = process.TotalProcessorTime.TotalSeconds,
                        activityStatus=activity.Status,activityReady=activity.IsReady,activityStale=activity.IsStale,activityPartial=activity.IsPartial,
                        chartPoints=activity.Points.Count,activityUpdates=activityUpdates,chartExpanded=config.QuotaHudChartExpanded,
                        pcFrameLength = device.LastPcMetrics == null ? 0 : device.LastPcMetrics.Length };
                    log.WriteLine(json.Serialize(row)); log.Flush(); rows.Add(row);
                }
            }
            bool nativeGlass = !(bool)typeof(QuotaHudForm).GetProperty("UsesOpaqueFallback", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(hud, null);
            var finalActivity=UsageActivity.Create(usage.Report,DateTimeOffset.UtcNow);
            passed = !source.Quota.IsStale && updates >= 3 && staleAfterWarmup == 0 && usage.Report.FilesPending == 0
                && usage.Report.FilesDiscovered > 0 && release.Snapshot.LastSuccessUtc.HasValue && nativeGlass
                && device.LastPcMetrics != null && device.LastPcMetrics.Length == 28
                && finalActivity.IsReady&&!finalActivity.IsStale&&finalActivity.Points.Count==361&&activityUpdates>=6&&toggles==2;
            hud.BeginShutdown(); hud.Close();
            var summary = new { passed = passed, seconds = watch.Elapsed.TotalSeconds, quotaUpdates = updates,
                staleAfterWarmup = staleAfterWarmup, automaticReconnects = source.ReconnectCount,
                usageFiles = usage.Report.FilesDiscovered, usagePending = usage.Report.FilesPending,
                updateState = release.Snapshot.State.ToString(), nativeGlass = nativeGlass,
                activityUpdates=activityUpdates,activityStatus=finalActivity.Status,chartPoints=finalActivity.Points.Count,chartToggles=toggles,topMost=config.QuotaHudTopMost,
                hardwareUsed = false, visibleDesktopWindows = false, assembly = typeof(BridgeService).Assembly.Location };
            File.WriteAllText(Path.Combine(directory, "summary.json"), json.Serialize(summary)); Console.WriteLine(json.Serialize(summary));
        }
        Application.DoEvents(); GC.Collect(); GC.WaitForPendingFinalizers();
        Console.WriteLine("GDI before=" + gdiBefore + " afterDispose=" + GetGuiResources(Process.GetCurrentProcess().Handle, 0));
        return passed ? 0 : 1;
    }
}
