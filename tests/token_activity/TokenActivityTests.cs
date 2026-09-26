using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using CodexToolsHost.Usage;

internal static class TokenActivityTests
{
    private static int failures, checks;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "CodexTokenActivity-" + Guid.NewGuid().ToString("N"));
    private static string Home;
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 26, 12, 0, 5, TimeSpan.Zero);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Test(string name, Action action)
    {
        Home = Path.Combine(Root, (++checks).ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Path.Combine(Home, "sessions"));
        try { action(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static UsageActivityEvent Event(DateTimeOffset time, long input, long output)
    { return new UsageActivityEvent { Time = time, InputTokens = input, OutputTokens = output }; }
    private static UsageReport Healthy(DateTimeOffset now, params UsageActivityEvent[] events)
    { return new UsageReport { UpdatedAt = now, ActivitySourceAvailable = true, IsScanComplete = true, RecentActivity = events.ToList().AsReadOnly() }; }
    private static string Meta(string id, string parent, DateTimeOffset time)
    {
        return "{\"timestamp\":\"" + time.ToString("o") + "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + id + "\"" +
            (parent == null ? "" : ",\"forked_from_id\":\"" + parent + "\"") + "}}\n" +
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"synthetic-model\"}}\n";
    }
    private static string Count(DateTimeOffset time, int input, int output, bool cumulative, int cache, int reasoning)
    {
        return "{\"timestamp\":\"" + time.ToString("o") + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"" +
            (cumulative ? "total_token_usage" : "last_token_usage") + "\":{\"input_tokens\":" + input + ",\"cached_input_tokens\":" + cache + ",\"output_tokens\":" + output +
            ",\"reasoning_output_tokens\":" + reasoning + ",\"total_tokens\":" + (input + output) + "}}}}\n";
    }
    private static string Count(DateTimeOffset time, int input, int output)
    { return Count(time, input, output, true, 0, 0); }
    private static string Write(string name, string content)
    { string path = Path.Combine(Home, "sessions", name + ".jsonl"); File.WriteAllText(path, content, new UTF8Encoding(false)); return path; }
    private static UsageReport Scan() { return new UsageScanner(Home).Scan(CancellationToken.None); }
    public static int Main()
    {
        Test("preceding minute excludes boundary, includes actual time, ignores future", delegate {
            UsageActivitySnapshot a = UsageActivity.Create(Healthy(Now, Event(Now.AddSeconds(-30),120,30), Event(Now.AddSeconds(-60),500,100), Event(Now.AddHours(-2),700,200), Event(Now.AddTicks(1),900,100)), Now);
            Check(a.InputTokensPerMinute == 120 && a.OutputTokensPerMinute == 30, "rolling interval must be (now-60s, now]");
            Check(a.HourTokens == 750, "hour excludes future and old history, includes minute boundary");
            Check(a.LastEventAt == Now.AddSeconds(-30), "future must not become last observed event");
        });
        Test("historical import never becomes current spike", delegate {
            UsageActivitySnapshot a = UsageActivity.Create(Healthy(Now, Event(Now.AddHours(-2),500,200)), Now);
            Check(a.IsReady && a.InputTokensPerMinute == 0 && a.HourTokens == 0, "history retains original event time");
        });
        Test("current instant included and exact hour boundary excluded", delegate {
            UsageActivitySnapshot a = UsageActivity.Create(Healthy(Now, Event(Now,12,3), Event(Now.AddHours(-1),80,20)),Now);
            Check(a.InputTokensPerMinute == 12 && a.OutputTokensPerMinute == 3 && a.HourTokens == 15,"exact interval endpoints");
        });
        Test("projection sorts numeric records and keeps same-time sessions", delegate {
            UsageActivitySnapshot a = UsageActivity.Create(Healthy(Now, Event(Now.AddSeconds(-1),4,2), Event(Now.AddSeconds(-30),120,30), Event(Now.AddSeconds(-30),10,5)), Now);
            Check(a.InputTokensPerMinute == 134 && a.OutputTokensPerMinute == 37, "sort without deduplicating independent same-time records");
        });
        Test("10-second chart endpoints have exact preceding minute windows", delegate {
            UsageActivitySnapshot a = UsageActivity.Create(Healthy(Now, Event(Now.AddSeconds(-5),120,30), Event(Now.AddSeconds(-65),999,999), Event(Now.AddSeconds(-3595),7,3)), Now);
            Check(a.Points.Count == 361, "one hour has 361 endpoint samples");
            Check(a.Points.Last().Time == Now.AddSeconds(-5) && a.Points.First().Time == Now.AddSeconds(-3605), "UTC bucket alignment");
            Check(a.Points.Last().InputTokens == 120 && a.Points.Last().OutputTokens == 30, "each sample uses its own minute boundary");
            Check(a.Points.Zip(a.Points.Skip(1), (x,y) => (y.Time-x.Time).TotalSeconds).All(x => x == 10), "sample interval");
        });
        Test("idle healthy scan is observable zero, never source-stale", delegate {
            UsageReport r = Healthy(Now); r.LastObservedEventAt = Now.AddDays(-2);
            UsageActivitySnapshot a = UsageActivity.Create(r, Now);
            Check(a.IsReady && !a.IsStale && !a.IsPartial && a.Points.All(x => x.HasData), "scan freshness and coverage are independent of event age");
            Check(a.InputTokensPerMinute == 0 && a.OutputTokensPerMinute == 0, "zero observed records");
        });
        Test("initial and unavailable have no fabricated zero coverage", delegate {
            UsageActivitySnapshot initial = UsageActivity.Create(new UsageReport(), Now);
            UsageActivitySnapshot unavailable = UsageActivity.Create(new UsageReport { UpdatedAt = Now }, Now);
            Check(!initial.IsReady && !unavailable.IsReady && initial.Status != unavailable.Status, "initial and unavailable statuses distinct");
            Check(initial.Points.All(x => !x.HasData) && unavailable.Points.All(x => !x.HasData), "unknown source is not observed zero");
            Check(!UsageActivity.Create(null, Now).IsReady, "null report is initial");
        });
        Test("stale threshold derives from successful refresh timestamp", delegate {
            UsageReport r = Healthy(Now);
            Check(!UsageActivity.Create(r, Now.AddSeconds(30)).IsStale, "30s threshold inclusive");
            UsageActivitySnapshot stale = UsageActivity.Create(r, Now.AddSeconds(31));
            Check(stale.IsStale && stale.Points.Last().HasData == false, "after30s do not claim current coverage");
        });
        Test("partial and pending scans remain visibly incomplete", delegate {
            UsageReport r = Healthy(Now, Event(Now.AddSeconds(-20),12,3)); r.IsScanComplete = false;
            UsageActivitySnapshot partial = UsageActivity.Create(r, Now);
            Check(partial.IsReady && partial.IsPartial && partial.Points.All(x => x.HasData) && partial.InputTokensPerMinute == 12, "preserve observed partial curve while flagging incomplete totals");
            r.IsScanComplete = true; r.FilesPending = 1;
            UsageActivitySnapshot pending = UsageActivity.Create(r,Now);
            Check(pending.IsPartial && pending.Status == "扫描中" && pending.Points.All(x => !x.HasData), "pending scan metadata is incomplete and has no continuous curve");
        });
        Test("failed refresh preserves numbers but is stale immediately", delegate {
            UsageReport r = Healthy(Now, Event(Now.AddSeconds(-20),12,3)); r.ScanFailed = true;
            UsageActivitySnapshot a = UsageActivity.Create(r, Now);
            Check(a.IsStale && a.IsPartial && a.InputTokensPerMinute == 12 && a.Points.All(x => !x.HasData), "failed snapshot cannot imply healthy coverage");
        });
        Test("first refresh failure is distinct from still scanning", delegate {
            UsageActivitySnapshot a = UsageActivity.Create(new UsageReport { ScanFailed = true },Now);
            Check(!a.IsReady && a.IsStale && a.IsPartial && a.Status == "读取失败","initial failure must not remain labelled scanning");
        });
        Test("daily ccusage import is unavailable for minute activity", delegate {
            UsageReport r = Healthy(Now, Event(Now,12,3)); r.IsImported = true;
            UsageActivitySnapshot a = UsageActivity.Create(r, Now);
            Check(!a.IsReady && a.InputTokensPerMinute == 0 && a.Points.All(x => !x.HasData), "daily import provides no precise event timestamps");
        });
        Test("scanner exposes deduplicated actual event timestamps and subset totals", delegate {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Write("one", Meta("a",null,now.AddMinutes(-5)) + Count(now.AddSeconds(-30),120,30,true,80,20) + Count(now.AddSeconds(-20),120,30,true,80,20));
            UsageReport r = Scan(); UsageActivitySnapshot a = UsageActivity.Create(r,now);
            Check(r.RecentActivity.Count == 1 && r.RecentActivity[0].Time == now.AddSeconds(-30), "one cumulative delta at logged time");
            Check(a.InputTokensPerMinute == 120 && a.OutputTokensPerMinute == 30 && r.Totals.TotalTokens == 150, "cache and reasoning remain subsets");
        });
        Test("archive and fork copies contribute once", delegate {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string parent = Write("parent", Meta("a",null,now.AddMinutes(-2)) + Count(now.AddSeconds(-40),100,20));
            Directory.CreateDirectory(Path.Combine(Home,"archived_sessions")); File.Copy(parent,Path.Combine(Home,"archived_sessions","archive.jsonl"));
            Write("child",Meta("b","a",now.AddSeconds(-35)) + Count(now.AddSeconds(-30),100,20) + Count(now.AddSeconds(-20),150,30));
            UsageReport r = Scan(); UsageActivitySnapshot a = UsageActivity.Create(r,now);
            Check(r.RecentActivity.Count == 2 && a.InputTokensPerMinute == 150 && a.OutputTokensPerMinute == 30, "archive/fork inherited history excluded");
        });
        Test("same timestamp independent sessions both count", delegate {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Write("a",Meta("a",null,now.AddMinutes(-2)) + Count(now.AddSeconds(-10),120,30));
            Write("b",Meta("b",null,now.AddMinutes(-2)) + Count(now.AddSeconds(-10),120,30));
            Check(UsageActivity.Create(Scan(),now).InputTokensPerMinute == 240, "separate sessions are not duplicates");
        });
        Test("missing model label does not invalidate numeric coverage", delegate {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Write("a", "{\"type\":\"session_meta\",\"payload\":{\"id\":\"a\"}}\n" + Count(now.AddSeconds(-20),120,30));
            UsageReport r = Scan(); UsageActivitySnapshot a = UsageActivity.Create(r,now);
            Check(!a.IsPartial && a.InputTokensPerMinute == 120 && r.Warnings.Count > 1,"only the model label is missing; precise numeric events remain covered");
        });
        Test("unchanged scan retains event times, no reads, rolling expiry", delegate {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Write("a",Meta("a",null,now.AddHours(-2)) + Count(now.AddSeconds(-59),120,30));
            UsageScanner scanner = new UsageScanner(Home); UsageReport first = scanner.Scan(CancellationToken.None); UsageReport second = scanner.Scan(CancellationToken.None);
            Check(second.BytesRead == 0 && second.RecentActivity.Count == 1 && second.RecentActivity[0].Time == first.RecentActivity[0].Time, "unchanged cache retains event time");
            Check(UsageActivity.Create(second,now).InputTokensPerMinute == 120 && UsageActivity.Create(second,now.AddSeconds(1)).InputTokensPerMinute == 0, "time alone expires window");
        });
        Test("scanner excludes old rows but retains preceding minute for first point", delegate {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Write("a",Meta("a",null,now.AddHours(-3)) + Count(now.AddHours(-2),100,20) + Count(now.AddSeconds(-3655),120,25) + Count(now.AddSeconds(-20),130,30));
            UsageReport r = Scan(); Check(r.RecentActivity.Count == 2 && r.RecentActivity[0].InputTokens == 20 && r.Totals.TotalTokens == 160, "retention includes hour plus preceding minute without old rows");
        });
        Test("future logged counts stay outside current sums", delegate {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Write("a",Meta("a",null,now.AddHours(-2)) + Count(now.AddSeconds(-30),100,20) + Count(now.AddHours(1),200,40));
            UsageActivitySnapshot a = UsageActivity.Create(Scan(),now);
            Check(a.InputTokensPerMinute == 100 && a.HourTokens == 120 && a.LastEventAt == now.AddSeconds(-30), "future excluded from rate/hour/last-event");
        });
        Test("truncation and growing rewrite replace recent activity", delegate {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string path = Write("a",Meta("a",null,now.AddHours(-2)) + Count(now.AddSeconds(-30),100,20) + Count(now.AddSeconds(-20),200,40));
            UsageScanner scanner = new UsageScanner(Home); scanner.Scan(CancellationToken.None);
            File.WriteAllText(path,Meta("a",null,now.AddHours(-2)) + Count(now.AddSeconds(-10),10,2));
            Check(UsageActivity.Create(scanner.Scan(CancellationToken.None),now).InputTokensPerMinute == 10,"truncated activity replaced");
            File.WriteAllText(path,Meta("a",null,now.AddHours(-2)) + Count(now.AddSeconds(-9),20,4) + Count(now.AddSeconds(-8),30,6));
            Check(UsageActivity.Create(scanner.Scan(CancellationToken.None),now).InputTokensPerMinute == 30,"growing rewrite replaces prior events");
        });
        Test("empty readable source is ready, missing source is unavailable", delegate {
            UsageReport empty = Scan(); Check(UsageActivity.Create(empty,DateTimeOffset.UtcNow).IsReady,"empty readable sessions directory is healthy zero");
            Directory.Delete(Path.Combine(Home,"sessions")); UsageReport absent = Scan();
            Check(!UsageActivity.Create(absent,DateTimeOffset.UtcNow).IsReady,"no source directories is unavailable");
        });
        Test("incomplete final line cannot masquerade as zero", delegate {
            DateTimeOffset now = DateTimeOffset.UtcNow; string line = Count(now.AddSeconds(-20),120,30);
            string path = Write("a",Meta("a",null,now.AddHours(-2)) + line.Substring(0,line.Length/2));
            UsageScanner scanner = new UsageScanner(Home); UsageReport r = scanner.Scan(CancellationToken.None);
            Check(UsageActivity.Create(r,now).IsPartial,"partial line marks incomplete coverage");
            Check(UsageActivity.Create(scanner.Scan(CancellationToken.None),now).IsPartial,"unchanged partial stays partial");
            File.AppendAllText(path,line.Substring(line.Length/2));
            Check(!UsageActivity.Create(scanner.Scan(CancellationToken.None),now).IsPartial,"completed line restores coverage");
        });
        Test("missing cached file preserves counts with incomplete coverage", delegate {
            DateTimeOffset now = DateTimeOffset.UtcNow; string path = Write("a",Meta("a",null,now.AddHours(-2)) + Count(now.AddSeconds(-20),120,30));
            UsageScanner scanner = new UsageScanner(Home); scanner.Scan(CancellationToken.None); File.Delete(path);
            UsageActivitySnapshot a = UsageActivity.Create(scanner.Scan(CancellationToken.None),now);
            Check(a.IsPartial && a.InputTokensPerMinute == 120,"known missing file before discovery is uncertain, not healthy cached data");
        });
        Test("unreadable file is partial and recovery clears transient health", delegate {
            DateTimeOffset now = DateTimeOffset.UtcNow; string path = Write("a",Meta("a",null,now.AddHours(-2)) + Count(now.AddSeconds(-20),120,30));
            UsageScanner scanner = new UsageScanner(Home);
            using (FileStream held = new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                Check(UsageActivity.Create(scanner.Scan(CancellationToken.None),now).IsPartial,"unreadable source visibly incomplete");
            Check(!UsageActivity.Create(scanner.Scan(CancellationToken.None),now).IsPartial,"fresh readable scan recovers");
        });
        Test("service failure retains recent snapshot and marks failed", delegate {
            DateTimeOffset now = DateTimeOffset.UtcNow; Write("a",Meta("a",null,now.AddHours(-2)) + Count(now.AddSeconds(-20),120,30));
            using (LocalUsageService service = new LocalUsageService(Home)) {
                service.RefreshAsync().Wait(); UsageReport before = service.Report;
                object scanner = typeof(LocalUsageService).GetField("scanner",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(service);
                scanner.GetType().GetField("knownPaths",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(scanner,new List<string>{"\0"});
                scanner.GetType().GetField("nextDiscovery",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(scanner,DateTime.MaxValue);
                service.RefreshAsync().Wait(); UsageReport after = service.Report;
                Check(after.ScanFailed && after.UpdatedAt == before.UpdatedAt && after.ActivitySourceAvailable == before.ActivitySourceAvailable && object.ReferenceEquals(after.RecentActivity,before.RecentActivity),"failure retains last good metadata and exact numeric event collection");
            }
        });
        Test("pending byte-budget scan is explicitly partial", delegate {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Write("a",Meta("a",null,now.AddHours(-2)) + Count(now.AddSeconds(-20),120,30) + new string(' ',(int)UsageScanner.RefreshByteBudget + 1) + "\n");
            UsageReport r = Scan(); UsageActivitySnapshot a = UsageActivity.Create(r,now);
            Check(r.FilesPending > 0 && a.IsPartial && a.Points.All(x => !x.HasData),"partial initial scan is never a complete flat zero curve");
        });
        Test("recent event cap is bounded and truncation visibly partial", delegate {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            StringBuilder content = new StringBuilder(Meta("a",null,now.AddHours(-2)));
            for (int i=1;i<=50010;i++) content.Append(Count(now.AddSeconds(-20).AddTicks(i),i,i));
            Write("a",content.ToString()); UsageScanner scanner = new UsageScanner(Home); UsageReport r = scanner.Scan(CancellationToken.None);
            Check(r.RecentActivity.Count == 50000 && r.Totals.TotalTokens == 100020,"recent extra memory bounded; cumulative totals remain complete");
            Check(UsageActivity.Create(r,now).IsPartial && UsageActivity.Create(scanner.Scan(CancellationToken.None),now).IsPartial,"cap is visible across unchanged scans");
        });
        Test("projection bound and repeated million-event-equivalent work", delegate {
            List<UsageActivityEvent> events = new List<UsageActivityEvent>();
            for (int i=0;i<50000;i++) events.Add(Event(Now.AddSeconds(-3660 + (i%3660)),1,1));
            UsageReport r = Healthy(Now,events.ToArray()); Stopwatch sw = Stopwatch.StartNew();
            for (int i=0;i<20;i++) Check(UsageActivity.Create(r,Now.AddSeconds(i)).Points.Count == 361,"fixed chart memory");
            sw.Stop(); Console.WriteLine("METRIC projection_50000_events_x20_ms="+sw.ElapsedMilliseconds);
            Check(sw.Elapsed < TimeSpan.FromSeconds(10),"bounded sorted sliding projection should stay responsive");
        });
        try { Directory.Delete(Root,true); } catch { }
        Console.WriteLine("Token activity: " + checks + " checks, " + failures + " failures.");
        return failures == 0 ? 0 : 1;
    }
}
