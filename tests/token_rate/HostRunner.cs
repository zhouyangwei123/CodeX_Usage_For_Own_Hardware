using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using CodexToolsHost.Usage;
using CodexToolsHost.Protocol;
using CodexToolsHost.UI;

internal static class HostTokenSpeedTests
{
    static int checks;
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
    static void Check(bool value, string label) { checks++; if (!value) throw new Exception(label); }
    static Dictionary<string, object> Obj(object value) { return (Dictionary<string, object>)value; }
    static long Number(Dictionary<string, object> value, string key) { return Convert.ToInt64(value[key]); }
    static void Fixtures(string file)
    {
        foreach (var raw in (object[])Json.DeserializeObject(File.ReadAllText(file)))
        {
            var test = Obj(raw); var tracker = new TokenSpeedTracker();
            var records = (object[])test["records"]; int consumed = 0;
            foreach (var item in (object[])test["checks"])
            {
                var expected = Obj(item);
                while (consumed < Number(expected, "after")) tracker.Observe(Obj(records[consumed++]), null);
                var actual = tracker.Snapshot(DateTimeOffset.FromUnixTimeMilliseconds(Number(expected, "now")), true);
                object rate = expected["rate"];
                Check(rate == null ? !actual.TokensPerSecond.HasValue : actual.TokensPerSecond.HasValue &&
                    Math.Abs(actual.TokensPerSecond.Value - Convert.ToDouble(rate)) < .000001, test["name"] + " rate");
                Check(actual.ActiveSessions == Number(expected, "active") && actual.MeasuredSessions == Number(expected, "measured") &&
                    actual.SessionCount == Number(expected, "sources"), test["name"] + " source lifecycle");
            }
        }
    }
    static string Row(string type, object payload, DateTimeOffset at)
    { return Json.Serialize(new { type = type, payload = payload, timestamp = at.ToString("o") }) + "\n"; }
    static void Service(string output)
    {
        string home = Path.Combine(output, "中文 service " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "sessions"));
        string path = Path.Combine(home, "sessions", "page.jsonl");
        var now = DateTimeOffset.UtcNow;
        string text = Row("session_meta", new { id = "one", history_mode = "paginated" }, now.AddMinutes(-1));
        text += Row("event_msg", new { type = "task_started", turn_id = "turn" }, now.AddSeconds(-12));
        text += Row("event_msg", new { type = "item_completed", thread_id = "one", turn_id = "turn",
            started_at_ms = now.AddSeconds(-11).ToUnixTimeMilliseconds(), completed_at_ms = now.AddSeconds(-1).ToUnixTimeMilliseconds(),
            item = new { type = "Reasoning", id = "reason" } }, now.AddSeconds(-1));
        text += Row("token_usage_record", new { thread_id = "one", turn_id = "turn", response_id = "response",
            usage = new { input_tokens = 1000, output_tokens = 1000, total_tokens = 2000 } }, now.AddMilliseconds(-500));
        File.WriteAllText(path, text);
        using (var service = new LocalUsageService(home))
        {
            service.Start();
            var end = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < end && (service.Report.TokenSpeed == null || service.Report.TokenSpeed.TokensPerSecond != 100)) Thread.Sleep(20);
            Check(service.Report.TokenSpeed != null && service.Report.TokenSpeed.TokensPerSecond == 100, "actual service reads model timing");
            File.AppendAllText(path, Row("event_msg", new { type = "task_complete", turn_id = "turn" }, DateTimeOffset.UtcNow));
            end = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < end && service.Report.TokenSpeed.TokensPerSecond != 0) Thread.Sleep(20);
            Check(service.Report.TokenSpeed.TokensPerSecond == 0, "service completion reaches zero within refresh interval");
            string archived = Path.Combine(home, "archived_sessions"); Directory.CreateDirectory(archived);
            File.Copy(path, Path.Combine(archived, "copy.jsonl"));
            end = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < end && service.Report.FilesDiscovered < 2) Thread.Sleep(20);
            Check(service.Report.FilesDiscovered == 2 && service.Report.TokenSpeed.TokensPerSecond == 0 &&
                service.Report.TokenSpeed.SessionCount == 1, "discovered archive copy does not restart finished source");
        }
        var empty = new UsageScanner(Path.Combine(home, "missing")).Scan(CancellationToken.None);
        Check(!empty.TokenSpeed.IsReady && empty.TokenSpeed.CompactText() == "--/s", "unavailable directory is not fabricated idle zero");
    }
    static void Pixels(string output)
    {
        var frame = new OledUsageFrame { Flags = 165, Remaining = 78, ResetSeconds = 600000,
            Upper = 20000, Current = 7300, Input = 5300, Output = 2000, BalanceCents = 499,
            Points = Enumerable.Range(0, 76).Select(i => (byte)(4+i%29)).ToArray() };
        byte[] wire = frame.Encode(), legacy = OledPixelRenderer.RenderBits(frame, "U80K", "D1.2M");
        foreach (double? value in new double?[] { null, 0, 12.3, 300, 999.99, 999900, 2000000 })
        {
            var speed = new TokenSpeedSnapshot { IsReady = true, TokensPerSecond = value };
            Check(speed.CompactText().Length * 6 - 1 <= 41, "speed footer fits 41 pixels");
            byte[] bits = OledPixelRenderer.RenderHostBits(frame, "U80K", "D1.2M", speed);
            for (int y = 0; y < 64; y++) for (int x = 0; x < 128; x++)
            {
                if (x >= 2 && x <= 42 && y >= 56 && y <= 62) continue;
                int i = x + y / 8 * 128, mask = 1 << (y % 8);
                if ((legacy[i] & mask) != (bits[i] & mask)) throw new Exception("speed changed graph, quota, bezel or details");
            }
            Check(frame.Encode().SequenceEqual(wire), "host-only rate never mutates serial usage fields");
            string name = value.HasValue ? value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "waiting";
            using (Bitmap image = OledPixelRenderer.RenderLive(new Size(408, 216), frame, null, true, speed))
                image.Save(Path.Combine(output, "host-speed-" + name + ".png"), ImageFormat.Png);
        }
        frame.View = 1;
        Check(OledPixelRenderer.RenderBits(frame,"U80K","D1.2M").SequenceEqual(OledPixelRenderer.RenderHostBits(frame,"U80K","D1.2M",new TokenSpeedSnapshot())),
            "account daily mode is not presented as account live TPS");
    }
    static void Live(string output, string home)
    {
        var scanner = new UsageScanner(LocalUsageService.ResolveHome(home));
        UsageReport report = null; var start = DateTime.UtcNow;
        do { report = scanner.Scan(CancellationToken.None); } while (report.FilesPending > 0 && DateTime.UtcNow - start < TimeSpan.FromSeconds(30));
        var speed = report.TokenSpeed;
        Check(speed != null && speed.IsReady && speed.SessionCount > 0, "real native tracker initialized all local sources");
        File.WriteAllText(Path.Combine(output,"live-native.json"), Json.Serialize(new { passed=true,
            hostVersion=typeof(LocalUsageService).Assembly.GetName().Version.ToString(), files=report.FilesDiscovered,
            sources=speed.SessionCount, active=speed.ActiveSessions, measured=speed.MeasuredSessions,
            ready=speed.IsReady, rate=speed.TokensPerSecond, personalContentOrIdentityRecorded=false }));
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            string output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
            Fixtures(args[0]); Service(output); Pixels(output);
            if (args.Contains("--live"))
            { string home = args.FirstOrDefault(a => a.StartsWith("--probe-home=", StringComparison.Ordinal));
              Live(output, home == null ? null : home.Substring("--probe-home=".Length)); }
            Console.WriteLine("PASS " + checks + " native host TPS checks"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); return 1; }
    }
}
