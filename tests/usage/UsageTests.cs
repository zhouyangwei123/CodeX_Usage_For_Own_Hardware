using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using CodexToolsHost.Usage;
using CodexToolsHost.UI;

internal static class UsageTests
{
    private static int failures;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "CodexUsageTests-" + Guid.NewGuid().ToString("N"));
    private static string Home;
    private static int counter;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Test(string name, Action action)
    {
        Home = Path.Combine(Root, (++counter).ToString());
        Directory.CreateDirectory(Path.Combine(Home, "sessions"));
        try { action(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static string Meta(string id, string parent)
    {
        return "{\"timestamp\":\"2026-09-25T02:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + id + "\"" +
            (parent == null ? "" : ",\"forked_from_id\":\"" + parent + "\"") + "}}\n" +
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"synthetic-model\"}}\n";
    }
    private static string Event(int input, int output, bool cumulative, int second)
    {
        return "{\"timestamp\":\"2026-09-25T03:00:" + second.ToString("00") + "Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"" +
            (cumulative ? "total_token_usage" : "last_token_usage") + "\":{\"input_tokens\":" + input + ",\"cached_input_tokens\":0,\"output_tokens\":" + output +
            ",\"reasoning_output_tokens\":0,\"total_tokens\":" + (input + output) + "}}}}\n";
    }
    private static string Write(string name, string content)
    {
        string path = Path.Combine(Home, "sessions", name + ".jsonl"); File.WriteAllText(path, content, new UTF8Encoding(false)); return path;
    }
    private static UsageReport Scan() { return new UsageScanner(Home).Scan(CancellationToken.None); }
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--live")) return Live();
        Test("cumulative duplicates and repeat refresh", delegate {
            string path = Write("one", Meta("a", null) + Event(100, 20, true, 1) + Event(100, 20, true, 2));
            UsageScanner scanner = new UsageScanner(Home); UsageReport a = scanner.Scan(CancellationToken.None);
            Check(a.Totals.TotalTokens == 120, "cumulative snapshots must count once");
            UsageReport b = scanner.Scan(CancellationToken.None); Check(b.Totals.TotalTokens == 120 && b.BytesRead == 0, "unchanged files must not reread or recount");
            File.AppendAllText(path, Event(150, 30, true, 3)); Check(scanner.Scan(CancellationToken.None).Totals.TotalTokens == 180, "append delta");
        });
        Test("duplicate active archive session", delegate {
            string path = Write("one", Meta("a", null) + Event(100, 20, true, 1));
            Directory.CreateDirectory(Path.Combine(Home, "archived_sessions")); File.Copy(path, Path.Combine(Home, "archived_sessions", "renamed.jsonl"));
            Check(Scan().Totals.TotalTokens == 120, "same session ID archive must not count twice");
        });
        Test("duplicate archive verified extension keeps complete history", delegate {
            string prefix = Meta("a", null) + Event(100, 20, true, 1);
            Write("one", prefix);
            Directory.CreateDirectory(Path.Combine(Home, "archived_sessions"));
            File.WriteAllText(Path.Combine(Home, "archived_sessions", "longer.jsonl"), prefix + Event(200, 40, true, 2));
            UsageScanner scanner = new UsageScanner(Home);
            Check(scanner.Scan(CancellationToken.None).Totals.TotalTokens == 240, "choose verified longer archive; never sum copies");
            Check(scanner.Scan(CancellationToken.None).Totals.TotalTokens == 240, "chosen copy remains stable on unchanged refresh");
        });
        Test("divergent duplicate copies report uncertainty", delegate {
            Write("one", Meta("a", null) + Event(100, 20, true, 1));
            Directory.CreateDirectory(Path.Combine(Home, "archived_sessions"));
            File.WriteAllText(Path.Combine(Home, "archived_sessions", "different.jsonl"), Meta("a", null) + Event(90, 18, true, 1) + Event(200, 40, true, 2));
            UsageReport report = Scan();
            Check(report.Totals.TotalTokens == 120 && report.Warnings.Any(x => x.Contains("副本")), "keep one deterministic copy and visibly flag divergence");
        });
        Test("known parent prefix fork with rewritten timestamps", delegate {
            Write("parent", Meta("a", null) + Event(100, 20, true, 1).Replace("03:00", "01:00") + Event(200, 40, true, 2).Replace("03:00", "01:00"));
            Write("child", Meta("b", "a") + Event(100, 20, true, 11) + Event(200, 40, true, 12) + Event(250, 50, true, 13));
            UsageReport r = Scan(); Check(r.Totals.TotalTokens == 300, "fork history must only contribute new 60");
            Check(r.Rows.Single(x => x.SessionId == "b").TotalTokens == 60, "child delta");
        });
        Test("fork cannot inherit parent events after fork time", delegate {
            Write("parent", Meta("a", null) + Event(100, 20, true, 1).Replace("03:00", "01:00") + Event(150, 30, true, 2));
            Write("child", Meta("b", "a") + Event(100, 20, true, 11) + Event(150, 30, true, 12));
            Check(Scan().Totals.TotalTokens == 240, "coincident child count after fork is real new usage");
        });
        Test("last-only parent prefix fork", delegate {
            Write("parent", Meta("a", null) + Event(10, 2, false, 1).Replace("03:00", "01:00") + Event(20, 4, false, 2).Replace("03:00", "01:00"));
            Write("child", Meta("b", "a") + Event(10, 2, false, 11) + Event(20, 4, false, 12) + Event(5, 1, false, 13));
            Check(Scan().Totals.TotalTokens == 42, "last-only inherited prefix must be excluded");
        });
        Test("reasoning and cache subsets plus model split", delegate {
            string first = Event(100, 20, true, 1).Replace("cached_input_tokens\":0", "cached_input_tokens\":50").Replace("reasoning_output_tokens\":0", "reasoning_output_tokens\":10");
            string second = Event(200, 40, true, 2).Replace("cached_input_tokens\":0", "cached_input_tokens\":80").Replace("reasoning_output_tokens\":0", "reasoning_output_tokens\":25");
            Write("one", Meta("a", null) + first + "{\"type\":\"turn_context\",\"payload\":{\"model\":\"other\"}}\n" + second);
            UsageReport r = Scan(); Check(r.Totals.TotalTokens == 240 && r.Totals.CachedInputTokens == 80 && r.Totals.ReasoningOutputTokens == 25 && r.Rows.Count == 2, "subsets never added twice, model switch split");
        });
        Test("counter reset uses last event and warns", delegate {
            string reset = Event(10, 2, true, 2).Replace("\"total_token_usage\":", "\"last_token_usage\":{\"input_tokens\":10,\"output_tokens\":2},\"total_token_usage\":");
            Write("one", Meta("a", null) + Event(100, 20, true, 1) + reset);
            UsageReport r = Scan(); Check(r.Totals.TotalTokens == 132 && r.Warnings.Any(x => x.Contains("回退")), "reset recovery explicitly uncertain");
        });
        Test("stale cumulative snapshot cannot reopen counted tokens", delegate {
            Write("one", Meta("a", null) + Event(100, 20, true, 1) + Event(90, 18, true, 2) + Event(100, 20, true, 3));
            Check(Scan().Totals.TotalTokens == 120, "stale counter replay without request delta must retain high watermark");
        });
        Test("last only duplicates and cumulative transition", delegate {
            Write("one", Meta("a", null) + Event(10, 2, false, 1) + Event(10, 2, false, 1) + Event(5, 1, false, 2) + Event(20, 4, true, 3));
            Check(Scan().Totals.TotalTokens == 24, "last usage dedupe and total transition");
        });
        Test("partial final line", delegate {
            string line = Event(100, 20, true, 1); string path = Write("one", Meta("a", null) + line.Substring(0, line.Length / 2));
            UsageScanner scanner = new UsageScanner(Home); Check(scanner.Scan(CancellationToken.None).Totals.TotalTokens == 0, "partial ignored");
            Check(scanner.Scan(CancellationToken.None).BytesRead == 0, "unchanged partial line must not be polled by rereading its content");
            File.AppendAllText(path, line.Substring(line.Length / 2)); Check(scanner.Scan(CancellationToken.None).Totals.TotalTokens == 120, "partial resumed");
        });
        Test("truncate and rewrite", delegate {
            string path = Write("one", Meta("a", null) + Event(100, 20, true, 1) + Event(200, 40, true, 2)); UsageScanner scanner = new UsageScanner(Home);
            Check(scanner.Scan(CancellationToken.None).Totals.TotalTokens == 240, "initial total");
            File.WriteAllText(path, Meta("a", null) + Event(10, 2, true, 3)); Check(scanner.Scan(CancellationToken.None).Totals.TotalTokens == 12, "truncated cache replaced");
        });
        Test("rewrite grows past previous offset", delegate {
            string path = Write("one", Meta("a", null) + Event(100, 20, true, 1)); UsageScanner scanner = new UsageScanner(Home);
            Check(scanner.Scan(CancellationToken.None).Totals.TotalTokens == 120, "initial total");
            File.WriteAllText(path, Meta("a", null) + Event(10, 2, true, 2) + Event(20, 4, true, 3));
            Check(scanner.Scan(CancellationToken.None).Totals.TotalTokens == 24, "rewritten growing file must invalidate old state");
        });
        Test("invalid counts and malformed JSON", delegate {
            Write("one", Meta("a", null) + Event(-10, 2, true, 1) + "{\"type\":\"event_msg\",\"token_count\": bad}\n" + Event(10, 2, true, 3));
            UsageReport r = Scan(); Check(r.Totals.TotalTokens == 12 && r.Warnings.Count > 0, "reject invalid, retain good rows and warn");
        });
        Test("cancellation never returns fake success", delegate {
            Write("one", Meta("a", null) + Event(10, 2, true, 1)); CancellationTokenSource c = new CancellationTokenSource(); c.Cancel();
            bool cancelled = false; try { new UsageScanner(Home).Scan(c.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "must observe cancellation");
        });
        Test("ccusage focused daily cache normalization", delegate {
            UsageReport r = CcusageImport.Parse("{\"daily\":[{\"date\":\"2026-09-25\",\"models\":{\"test\":{\"inputTokens\":80,\"cacheReadTokens\":20,\"cacheCreationTokens\":5,\"outputTokens\":10,\"totalTokens\":115}}}],\"totals\":{\"totalTokens\":115}}");
            Check(r.IsImported && r.Totals.InputTokens == 105 && r.Totals.CachedInputTokens == 20 && r.Totals.TotalTokens == 115, "import keeps cache a subset and does not add totals twice");
        });
        Test("ccusage rejects unsupported mixed/unified shapes and negatives", delegate {
            bool rejected = false; try { CcusageImport.Parse("{\"daily\":[{\"agent\":\"all\",\"period\":\"2026-09-25\",\"inputTokens\":50,\"outputTokens\":5}]}"); } catch (FormatException) { rejected = true; }
            Check(rejected, "mixed agents must not be attributed to Codex");
            rejected = false; try { CcusageImport.Parse("{\"daily\":[{\"date\":\"2026-09-25\",\"models\":{\"test\":{\"inputTokens\":-5,\"outputTokens\":1}}}]}"); } catch (FormatException) { rejected = true; }
            Check(rejected, "negative import rejected");
        });
        Test("CSV spreadsheet formula escaping", delegate {
            string csv = UsageReport.ToCsv(new[] { new UsageRow { SessionId = "=fake()", Model = "@test", Day = DateTime.Today } }, "本机 Codex");
            Check(csv.Contains("'=fake()") && csv.Contains("'@test"), "labels must not become spreadsheet formulas");
        });
        Test("home precedence and service disposal", delegate {
            Check(LocalUsageService.ResolveHome(Home) == Path.GetFullPath(Home), "explicit home precedence");
            Write("one", Meta("a", null) + Event(10, 2, true, 1));
            LocalUsageService service = new LocalUsageService(Home); int changed = 0; service.Changed += delegate { changed++; };
            service.RefreshAsync().Wait(); Check(service.Report.Totals.TotalTokens == 12 && changed == 1, "service publishes snapshot");
            service.Dispose(); service.RefreshAsync().Wait(); Check(changed == 1, "disposed service must not publish");
        });
        Test("subscriber disposal suppresses later snapshot callbacks", delegate {
            Write("one", Meta("a", null) + Event(10, 2, true, 1));
            LocalUsageService service = new LocalUsageService(Home); int late = 0;
            service.Changed += delegate { service.Dispose(); };
            service.Changed += delegate { late++; };
            service.RefreshAsync().Wait();
            Check(late == 0, "disposing subscriber must suppress later callbacks captured by Notify");
        });
        Test("bounded scan continuation and cancellation resume", delegate {
            Write("one", Meta("a", null) + Event(100, 20, true, 1) + new string('x', 34 * 1024 * 1024) + "\n" + Event(150, 30, true, 2));
            UsageScanner scanner = new UsageScanner(Home);
            UsageReport partial = scanner.Scan(CancellationToken.None);
            Check(partial.FilesPending == 1 && partial.Totals.TotalTokens == 120 && partial.Warnings.Any(x => x.Contains("进行中")), "32 MiB boundary must be visible");
            UsageReport complete = scanner.Scan(CancellationToken.None);
            Check(complete.FilesPending == 0 && complete.Totals.TotalTokens == 180, "continue without recounting");
            scanner = new UsageScanner(Home);
            using (CancellationTokenSource c = new CancellationTokenSource())
            {
                c.CancelAfter(5); bool cancelled = false;
                try { scanner.Scan(c.Token); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "large scan interrupted");
            }
            UsageReport resumed; do { resumed = scanner.Scan(CancellationToken.None); } while (resumed.FilesPending > 0);
            Check(resumed.Totals.TotalTokens == 180, "cancelled scan resumes exactly once");
        });
        Test("concurrent service refresh coalescing and active disposal", delegate {
            Write("one", Meta("a", null) + new string('x', 8 * 1024 * 1024) + "\n" + Event(100, 20, true, 1));
            LocalUsageService service = new LocalUsageService(Home); int changed = 0; service.Changed += delegate { changed++; };
            System.Threading.Tasks.Task first = service.RefreshAsync(); System.Threading.Tasks.Task second = service.RefreshAsync();
            Check(object.ReferenceEquals(first, second), "parallel requests share worker");
            service.Dispose(); first.Wait(); Check(changed == 0, "disposal cancels active publication");
        });
        Test("ccusage monthly and session views remain aggregated", delegate {
            string counts = "{\"test\":{\"inputTokens\":10,\"cacheReadTokens\":5,\"outputTokens\":2,\"totalTokens\":17}}";
            UsageReport month = CcusageImport.Parse("{\"monthly\":[{\"month\":\"2026-09\",\"models\":" + counts + "}]}");
            UsageReport session = CcusageImport.Parse("{\"sessions\":[{\"sessionId\":\"/private/synthetic/session\",\"lastActivity\":\"2026-09-25T02:00:00Z\",\"models\":" + counts + "}]}");
            Check(month.PeriodKind == "monthly" && month.Totals.TotalTokens == 17 && session.PeriodKind == "sessions" && !session.Rows[0].SessionId.Contains("private"), "aggregate imports retain type and exclude local paths");
        });
        Test("imported API estimate never turns missing pricing into free", delegate {
            string input = "{\"daily\":[{\"date\":\"2026-09-25\",\"models\":{\"test\":{\"inputTokens\":10,\"outputTokens\":2}}}],\"totals\":{\"costUSD\":0.25}}";
            Check(CcusageImport.Parse(input).ImportedApiEstimateUsd == 0.25m, "preserve reported API estimate");
            Check(!CcusageImport.Parse(input.Replace("\"inputTokens\":10", "\"missingPricing\":true,\"inputTokens\":10")).ImportedApiEstimateUsd.HasValue, "unknown model pricing remains unknown");
        });
        Test("offscreen native usage panel rendering", delegate {
            Write("one", Meta("synthetic-session-001", null) + Event(10000, 400, true, 1));
            using (LocalUsageService service = new LocalUsageService(Home))
            {
                service.RefreshAsync().Wait();
                using (UsagePanel panel = new UsagePanel(service))
                {
                    panel.Size = new Size(900, 650); panel.CreateControl(); panel.PerformLayout();
                    using (Bitmap bitmap = new Bitmap(panel.Width, panel.Height))
                    { panel.DrawToBitmap(bitmap, panel.ClientRectangle); bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "usage-panel.png")); }
                    DataGridView grid = panel.Controls.Find("usageGrid", true).OfType<DataGridView>().Single();
                    Check(grid.Rows.Count == 1 && grid.Columns.Count == 6, "table renders synthetic stats");
                }
            }
        });
        Console.WriteLine("RESULT tests=" + counter + " failures=" + failures);
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
        return failures == 0 ? 0 : 1;
    }
    private static int Live()
    {
        UsageScanner scanner = new UsageScanner(LocalUsageService.ResolveHome(null));
        Stopwatch all = Stopwatch.StartNew(); Stopwatch tick = Stopwatch.StartNew();
        using (CancellationTokenSource cancel = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
        {
            try
            {
                UsageReport report = null; long bytes = 0; int passes = 0;
                do { report = scanner.Scan(cancel.Token); bytes += report.BytesRead; passes++; } while (report.FilesPending > 0 && all.Elapsed.TotalSeconds < 40);
                Console.WriteLine("LIVE initial_ms=" + tick.ElapsedMilliseconds + " passes=" + passes + " bytes=" + bytes + " files=" + report.FilesDiscovered + " pending=" + report.FilesPending + " rows=" + report.Rows.Count + " warnings=" + report.Warnings.Count);
                long total = report.Totals.TotalTokens;
                tick.Restart(); UsageReport repeat = scanner.Scan(cancel.Token);
                Console.WriteLine("LIVE repeat_ms=" + tick.ElapsedMilliseconds + " bytes=" + repeat.BytesRead + " unchanged_total=" + (total == repeat.Totals.TotalTokens) + " pending=" + repeat.FilesPending);
                return 0;
            }
            catch (OperationCanceledException) { Console.WriteLine("LIVE bounded_read_cancelled_ms=" + all.ElapsedMilliseconds); return 0; }
        }
    }
}
