using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CodexToolsHost.Updates;

internal static class Runner
{
    private static int _passed;
    private static int _failed;
    private static string _directory;
    private const string ReleaseUrl = "https://github.com/zhouyangwei123/CodeX_Usage_For_Own_Hardware/releases/tag/";
    [STAThread]
    private static int Main(string[] args)
    {
        _directory = args[0];
        Directory.CreateDirectory(_directory);
        try
        {
            Test("numeric semantic precedence", Versions);
            Test("unsafe and foreign release links", Links);
            Test("newer, equal and older stable releases", StableReleases);
            Test("draft and prerelease exclusion", ExcludedReleases);
            Test("404 differs from up-to-date", NoRelease);
            Test("304 cache survives a new service instance", Cache304);
            Test("304 without a cache fails", Uncached304);
            Test("failures retain last successful version and timestamp", ErrorRetention);
            Test("manual checks coalesce and complete once", Coalescing);
            Test("automatic delay, interval and backoff", Scheduling);
            Test("disable cancels in-flight request; manual works disabled", DisableCancellation);
            Test("reenable during cancellation retries after30s then returns to12h", ReenableDuringCancellation);
            Test("dispose cancels without publishing late results", Disposal);
            Test("disposal during notification prevents later callbacks", NotificationDisposal);
            Test("malformed response and unsafe URLs are rejected", InvalidResponses);
            Test("unwritable cache does not discard valid remote result", UnwritableCache);
            Test("anonymous fixed endpoint, timeout and redirect restrictions", RequestContract);
            Test("response byte bound, invalid UTF8 and cancelled reads", ResponseBoundaries);
            Test("automatic timer dispatch and disabled stale callback", TimerDispatch);
            Test("native panel preference, manual check, explicit safe page click", Panel);
            if (args.Length > 1 && args[1] == "--live") Test("one real anonymous GitHub check", Live);
            Console.WriteLine("RESULT " + _passed + " passed, " + _failed + " failed");
            return _failed == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.Message); return 1; }
    }
    private static void Test(string name, Action action)
    {
        try { action(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static ReleaseVersion Parse(string value)
    {
        ReleaseVersion result;
        Assert(ReleaseVersion.TryParse(value, out result), "Cannot parse " + value);
        return result;
    }
    private static void Versions()
    {
        Assert(Parse("v0.10.0").CompareTo(Parse("0.9.9")) > 0, "10 must sort after 9 numerically");
        Assert(Parse("1.0.0-alpha.9").CompareTo(Parse("1.0.0-alpha.10")) < 0, "prerelease numeric order");
        Assert(Parse("1.0.0-rc.1").CompareTo(Parse("1.0.0")) < 0, "stable after prerelease");
        Assert(Parse("1.0.0+build.2").CompareTo(Parse("1.0.0+other")) == 0, "metadata ignored");
        Assert(Parse("1.0.0-99999999999999999999").CompareTo(Parse("1.0.0-100000000000000000000")) < 0, "large prerelease numbers");
        foreach (string value in new[] { "", "1.2", "1.2.3.4", "01.2.3", "1.2.3-01", "1.2.3-", "1.2.3+", "1.2.-1", "2147483648.0.0", "release1.2.3", "1.2.3\n" })
        { ReleaseVersion ignored; Assert(!ReleaseVersion.TryParse(value, out ignored), "Invalid version accepted: " + value); }
    }
    private static void Links()
    {
        Uri parsed;
        Assert(ReleaseLink.TryGet(ReleaseUrl + "v0.3.0", "v0.3.0", out parsed), "valid same-repo link rejected");
        foreach (string value in new[] {
            "http://github.com/zhouyangwei123/CodeX_Usage_For_Own_Hardware/releases/tag/v0.3.0",
            "https://evil.example/zhouyangwei123/CodeX_Usage_For_Own_Hardware/releases/tag/v0.3.0",
            "https://github.com/other/repo/releases/tag/v0.3.0",
            "https://github.com@evil.example/zhouyangwei123/CodeX_Usage_For_Own_Hardware/releases/tag/v0.3.0",
            "https://user@github.com/zhouyangwei123/CodeX_Usage_For_Own_Hardware/releases/tag/v0.3.0",
            "https://github.com:444/zhouyangwei123/CodeX_Usage_For_Own_Hardware/releases/tag/v0.3.0",
            ReleaseUrl + "v0.3.0?redirect=evil", ReleaseUrl + "v0.3.0#fragment", ReleaseUrl + "v0.4.0",
            ReleaseUrl + "..%2F..%2Fevil", "file:///C:/evil.exe", "javascript:alert(1)" })
            Assert(!ReleaseLink.TryGet(value, "v0.3.0", out parsed), "Unsafe release URL accepted: " + value);
    }
    private static string Json(string version, bool draft, bool prerelease)
    {
        return "{\"tag_name\":\"" + version + "\",\"html_url\":\"" + ReleaseUrl + version + "\",\"draft\":" + draft.ToString().ToLowerInvariant() + ",\"prerelease\":" + prerelease.ToString().ToLowerInvariant() + "}";
    }
    private static ReleaseHttpResponse Ok(string version)
    { return new ReleaseHttpResponse(200, Json(version, false, false), "\"fixture-etag\"", null); }
    private static void Wait(Task task)
    { Assert(task.Wait(4000), "Check did not finish within 4s"); }
    private static Fixture NewFixture(string name)
    { return new Fixture(Path.Combine(_directory, name + "-" + Guid.NewGuid().ToString("N"))); }
    private static void StableReleases()
    {
        using (Fixture f = NewFixture("stable"))
        {
            f.Transport.Enqueue(Ok("v0.10.0")); Wait(f.Service.CheckAsync());
            Assert(f.Service.Snapshot.State == UpdateCheckState.UpdateAvailable, "newer release missing");
            f.Transport.Enqueue(Ok("v0.2.0")); Wait(f.Service.CheckAsync());
            Assert(f.Service.Snapshot.State == UpdateCheckState.UpToDate, "equal release not up-to-date");
            f.Transport.Enqueue(Ok("v0.1.0")); Wait(f.Service.CheckAsync());
            Assert(f.Service.Snapshot.State == UpdateCheckState.UpToDate, "older must not offer downgrade");
        }
    }
    private static void ExcludedReleases()
    {
        foreach (string body in new[] { Json("v9.0.0", true, false), Json("v9.0.0", false, true), Json("v9.0.0-rc.1", false, false) })
        using (Fixture f = NewFixture("excluded"))
        {
            f.Transport.Enqueue(new ReleaseHttpResponse(200, body, null, null)); Wait(f.Service.CheckAsync());
            Assert(f.Service.Snapshot.State == UpdateCheckState.NoRelease && f.Service.Snapshot.LatestTag == null, "unstable release promoted");
        }
    }
    private static void NoRelease()
    {
        using (Fixture f = NewFixture("404"))
        {
            f.Transport.Enqueue(new ReleaseHttpResponse(404, null, null, null)); Wait(f.Service.CheckAsync());
            Assert(f.Service.Snapshot.State == UpdateCheckState.NoRelease, "404 presented as latest");
            Assert(!f.Service.Snapshot.LastSuccessUtc.HasValue, "404 counted as valid release success");
        }
    }
    private static void Cache304()
    {
        string directory;
        using (Fixture f = NewFixture("cache"))
        {
            directory = f.Directory;
            f.Transport.Enqueue(Ok("v0.3.0")); Wait(f.Service.CheckAsync());
        }
        using (Fixture f = new Fixture(directory))
        {
            Assert(f.Service.Snapshot.LatestTag == "v0.3.0", "cache not loaded");
            Assert(f.Service.Snapshot.State == UpdateCheckState.NeverChecked, "cache must not claim fresh check");
            f.Transport.Enqueue(new ReleaseHttpResponse(304, null, null, null)); Wait(f.Service.CheckAsync());
            Assert(f.Transport.LastEtag == "\"fixture-etag\"", "conditional ETag omitted");
            Assert(f.Service.Snapshot.State == UpdateCheckState.UpdateAvailable, "304 lost cached release");
            Assert(f.Service.Snapshot.LastSuccessUtc == f.Now, "304 should refresh success time");
        }
    }
    private static void Uncached304()
    {
        using (Fixture f = NewFixture("304"))
        {
            f.Transport.Enqueue(new ReleaseHttpResponse(304, null, null, null)); Wait(f.Service.CheckAsync());
            Assert(f.Service.Snapshot.State == UpdateCheckState.Failed, "uncached304 must fail");
        }
    }
    private static void ErrorRetention()
    {
        using (Fixture f = NewFixture("errors"))
        {
            f.Transport.Enqueue(Ok("v0.3.0")); Wait(f.Service.CheckAsync());
            DateTimeOffset? successful = f.Service.Snapshot.LastSuccessUtc;
            foreach (object failure in new object[] { new ReleaseHttpResponse(500, null, null, null), new TimeoutException(), new IOException("private fixture error text") })
            {
                f.Now = f.Now.AddMinutes(1); f.Transport.Enqueue(failure); Wait(f.Service.CheckAsync());
                Assert(f.Service.Snapshot.State == UpdateCheckState.Failed, "error presented as latest");
                Assert(f.Service.Snapshot.LatestTag == "v0.3.0" && f.Service.Snapshot.LastSuccessUtc == successful, "error erased last success");
                Assert(f.Service.Snapshot.LastAttemptUtc == f.Now, "attempt time not updated");
                Assert(!f.Service.Snapshot.StatusMessage.Contains("private"), "raw error leaked to UI");
            }
        }
    }
    private static void Coalescing()
    {
        using (Fixture f = NewFixture("coalesce"))
        {
            var pending = new TaskCompletionSource<ReleaseHttpResponse>(); f.Transport.Enqueue(pending);
            Task first = f.Service.CheckAsync(); Task second = f.Service.CheckAsync();
            Assert(Object.ReferenceEquals(first, second), "in-flight callers did not share completion");
            pending.SetResult(Ok("v0.3.0")); Wait(first);
            Assert(f.Transport.Calls == 1, "concurrent duplicate HTTP requests");
        }
    }
    private static void Scheduling()
    {
        using (Fixture f = NewFixture("schedule"))
        {
            f.Service.Start(true); Assert(f.Timer.Delay == TimeSpan.FromSeconds(30), "startup must wait30s");
            f.Service.Start(true); Assert(f.Transport.Calls == 0, "Start performed immediate network I/O");
            f.Transport.Enqueue(Ok("v0.2.0")); Wait(f.Service.CheckAsync());
            Assert(f.Timer.Delay == TimeSpan.FromHours(12), "success interval must be12h");
            f.Transport.Enqueue(new ReleaseHttpResponse(500, null, null, null)); Wait(f.Service.CheckAsync());
            Assert(f.Timer.Delay == TimeSpan.FromMinutes(15), "first backoff15m");
            f.Transport.Enqueue(new ReleaseHttpResponse(500, null, null, null)); Wait(f.Service.CheckAsync());
            Assert(f.Timer.Delay == TimeSpan.FromMinutes(30), "second backoff30m");
            f.Transport.Enqueue(new ReleaseHttpResponse(429, null, null, TimeSpan.FromHours(3))); Wait(f.Service.CheckAsync());
            Assert(f.Timer.Delay == TimeSpan.FromHours(3), "rate limit Retry-After ignored");
            f.Service.SetEnabled(false); Assert(f.Timer.Delay == null, "disabled timer still scheduled");
            f.Service.SetEnabled(true); Assert(f.Timer.Delay == TimeSpan.FromSeconds(30), "reenable delay");
        }
    }
    private static void DisableCancellation()
    {
        using (Fixture f = NewFixture("cancel"))
        {
            f.Service.Start(true); f.Transport.Enqueue(new TaskCompletionSource<ReleaseHttpResponse>());
            Task task = f.Service.CheckAsync(); Assert(f.Transport.Entered.WaitOne(2000), "transport not entered");
            f.Service.SetEnabled(false); Wait(task);
            Assert(f.Transport.LastToken.IsCancellationRequested, "disable did not cancel");
            Assert(f.Service.Snapshot.State == UpdateCheckState.Cancelled && !f.Service.Snapshot.AutoChecksEnabled, "cancellation state missing");
            f.Transport.Enqueue(Ok("v0.3.0")); Wait(f.Service.CheckAsync());
            Assert(f.Service.Snapshot.State == UpdateCheckState.UpdateAvailable && f.Timer.Delay == null, "manual disabled check must work without scheduling");
        }
    }
    private static void Disposal()
    {
        using (Fixture f = NewFixture("dispose"))
        {
            int changed = 0; f.Service.Changed += delegate { Interlocked.Increment(ref changed); };
            f.Transport.Enqueue(new TaskCompletionSource<ReleaseHttpResponse>());
            Task task = f.Service.CheckAsync(); Assert(f.Transport.Entered.WaitOne(2000), "transport not entered");
            f.Service.Dispose(); int before = changed; Wait(task);
            Assert(f.Transport.LastToken.IsCancellationRequested && f.Timer.Disposed, "dispose did not cancel timer/request");
            Assert(changed == before, "late notification after dispose");
            int calls = f.Transport.Calls; Wait(f.Service.CheckAsync());
            Assert(calls == f.Transport.Calls, "disposed service made a new request");
        }
    }
    private static void ReenableDuringCancellation()
    {
        using (Fixture f = NewFixture("reenable-cancelling"))
        {
            f.Service.Start(true);
            var pending = new DelayedCancellationReply();
            f.Transport.Enqueue(pending);
            Task first = f.Service.CheckAsync();
            Assert(f.Transport.Entered.WaitOne(2000), "transport not entered");
            f.Service.SetEnabled(false);
            f.Service.SetEnabled(true);
            Assert(f.Transport.LastToken.IsCancellationRequested, "old request was not cancelled");
            Assert(Object.ReferenceEquals(first, f.Service.CheckAsync()) && f.Transport.Calls == 1,
                "reenable started a duplicate request before cancellation completed");
            pending.Completion.SetResult(new ReleaseHttpResponse(404, null, null, null));
            Wait(first);
            Assert(f.Service.Snapshot.State == UpdateCheckState.Cancelled && f.Service.Snapshot.AutoChecksEnabled,
                "cancelled request lost enabled preference");
            Assert(f.Timer.Delay == TimeSpan.FromSeconds(30) && f.Service.Snapshot.NextCheckUtc == f.Now.AddSeconds(30),
                "reenabled cancellation must retry after30s, actual=" + f.Timer.Delay);

            var retry = new TaskCompletionSource<ReleaseHttpResponse>();
            f.Transport.Enqueue(retry); f.Timer.Callback();
            Task restarted = f.Service.CheckAsync();
            retry.SetResult(Ok("v0.3.0")); Wait(restarted);
            Assert(f.Transport.Calls == 2 && f.Service.Snapshot.State == UpdateCheckState.UpdateAvailable,
                "scheduled retry was missing or duplicated");
            Assert(f.Timer.Delay == TimeSpan.FromHours(12), "successful retry did not restore12h interval");
        }
    }
    private static void InvalidResponses()
    {
        foreach (string body in new[] { "{", "{}", Json("v0.3.0", false, false).Replace("github.com", "evil.example"), Json("v0.3.0", false, false).Replace("\"draft\":false", "\"draft\":\"false\""), new string('x', 300000) })
        using (Fixture f = NewFixture("invalid"))
        {
            f.Transport.Enqueue(new ReleaseHttpResponse(200, body, null, null)); Wait(f.Service.CheckAsync());
            Assert(f.Service.Snapshot.State == UpdateCheckState.Failed && f.Service.Snapshot.ReleaseUrl == null, "invalid data accepted");
        }
    }
    private static void NotificationDisposal()
    {
        using (Fixture f = NewFixture("notify-dispose"))
        {
            int later = 0;
            f.Service.Changed += delegate { f.Service.Dispose(); };
            f.Service.Changed += delegate { later++; };
            f.Service.Start(true);
            Assert(later == 0, "notification continued after a subscriber disposed service");
        }
    }
    private static void UnwritableCache()
    {
        string path = Path.Combine(_directory, "not-a-directory"); File.WriteAllText(path, "fixture");
        using (Fixture f = new Fixture(path))
        {
            f.Transport.Enqueue(Ok("v0.3.0")); Wait(f.Service.CheckAsync());
            Assert(f.Service.Snapshot.State == UpdateCheckState.UpdateAvailable, "cache error discarded remote data");
        }
    }
    private static void Live()
    {
        using (var service = new ReleaseUpdateService(new Version(0, 2, 0), Path.Combine(_directory, "live")))
        {
            Assert(service.CheckAsync().Wait(20000), "live check exceeded deadline");
            UpdateSnapshot s = service.Snapshot;
            Console.WriteLine("LIVE state=" + s.State + " tag=" + s.LatestTag + " successUtc=" + s.LastSuccessUtc);
            Assert(s.State == UpdateCheckState.UpToDate || s.State == UpdateCheckState.UpdateAvailable, "real anonymous check failed: " + s.StatusMessage);
        }
    }
    private static void RequestContract()
    {
        var transport = new GitHubReleaseTransport(new Version(0, 2, 0));
        HttpWebRequest request = transport.CreateRequest("\"fixture\"");
        Assert(request.RequestUri.AbsoluteUri == GitHubReleaseTransport.Endpoint && request.Method == "GET", "wrong endpoint");
        Assert(request.Headers[HttpRequestHeader.Authorization] == null && request.Credentials == null && !request.UseDefaultCredentials, "authentication enabled");
        Assert(!request.AllowAutoRedirect && request.Timeout == 15000 && request.ReadWriteTimeout == 15000, "timeout/redirect boundary missing");
        Console.WriteLine("TRANSPORT defaultTLS=" + ServicePointManager.SecurityProtocol);
        Assert(((int)ServicePointManager.SecurityProtocol & 3072) != 0 || (int)ServicePointManager.SecurityProtocol == 0,
            "legacy .NET startup does not enable TLS1.2");
        Assert(request.Headers[HttpRequestHeader.IfNoneMatch] == "\"fixture\"", "ETag missing");
        request = transport.CreateRequest("\"a\"\r\nAuthorization: secret");
        Assert(request.Headers[HttpRequestHeader.IfNoneMatch] == null, "malformed ETag accepted");
    }
    private static void ResponseBoundaries()
    {
        using (var stream = new MemoryStream(new byte[GitHubReleaseTransport.MaximumResponseBytes + 1]))
        {
            try { GitHubReleaseTransport.ReadBoundedAsync(stream, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("oversized stream accepted"); }
            catch (InvalidDataException) { }
        }
        using (var stream = new MemoryStream(new byte[] { 0xff }))
        {
            try { GitHubReleaseTransport.ReadBoundedAsync(stream, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("invalid UTF8 accepted"); }
            catch (System.Text.DecoderFallbackException) { }
        }
        using (var stream = new MemoryStream(new byte[64]))
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try { GitHubReleaseTransport.ReadBoundedAsync(stream, cancellation.Token).GetAwaiter().GetResult(); throw new Exception("cancelled read succeeded"); }
            catch (OperationCanceledException) { }
        }
    }
    private static void TimerDispatch()
    {
        using (Fixture f = NewFixture("timer"))
        {
            f.Service.Start(true);
            var pending = new TaskCompletionSource<ReleaseHttpResponse>();
            f.Transport.Enqueue(pending); f.Timer.Callback();
            Assert(f.Transport.Entered.WaitOne(2000), "timer never started HTTP check");
            Task running = f.Service.CheckAsync(); pending.SetResult(Ok("v0.3.0")); Wait(running);
            Assert(f.Service.Snapshot.State == UpdateCheckState.UpdateAvailable && f.Transport.Calls == 1, "automatic result missing or duplicated");
            Action stale = f.Timer.Callback; f.Service.SetEnabled(false); int calls = f.Transport.Calls; stale();
            Assert(f.Transport.Calls == calls, "stale timer callback ran while disabled");
        }
    }
    private static void Panel()
    {
        Type type = Assembly.GetExecutingAssembly().GetType("CodexToolsHost.UI.UpdateSettingsPanel");
        Assert(type != null, "settings panel not implemented");
        using (Fixture f = NewFixture("panel"))
        {
            int saved = 0, opened = 0; bool preference = false;
            Action<bool> save = delegate(bool enabled) { saved++; preference = enabled; };
            Action<Uri> open = delegate(Uri uri) { opened++; Assert(uri.Host == "github.com", "foreign browser target"); };
            ConstructorInfo constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(ReleaseUpdateService), typeof(bool), typeof(Action<bool>), typeof(Action<Uri>) }, null);
            Assert(constructor != null, "panel test boundary unavailable");
            using (var panel = (UserControl)constructor.Invoke(new object[] { f.Service, false, save, open }))
            {
                panel.CreateControl();
                var checkbox = (CheckBox)Find(panel, "updates-enabled");
                var check = (Button)Find(panel, "updates-check");
                var release = (Button)Find(panel, "updates-release");
                Assert(!checkbox.Checked && saved == 0 && !release.Enabled, "initial panel changed settings or opened page");
                checkbox.Checked = true;
                Assert(saved == 1 && preference && f.Service.Snapshot.AutoChecksEnabled, "preference callback missing");
                f.Transport.Enqueue(Ok("v0.3.0")); Click(check);
                Assert(SpinWait.SpinUntil(delegate { Application.DoEvents(); return release.Enabled; }, 2000), "manual check result not visible");
                Assert(opened == 0, "browser opened on discovery");
                Click(release); Assert(opened == 1, "explicit release click did not open page");
            }
            f.Transport.Enqueue(Ok("v0.4.0")); Wait(f.Service.CheckAsync());
            Assert(f.Service.Snapshot.LatestTag == "v0.4.0", "panel disposed tray-owned service");
        }
    }
    private static Control Find(Control parent, string tag)
    {
        if (Object.Equals(parent.Tag, tag)) return parent;
        foreach (Control child in parent.Controls)
        { Control found = Find(child, tag); if (found != null) return found; }
        return null;
    }
    private static void Click(Button button)
    {
        Assert(button != null && button.Enabled, "button missing or disabled");
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(button, new object[] { EventArgs.Empty });
    }
    private sealed class Fixture : IDisposable
    {
        public readonly string Directory;
        public readonly FakeTransport Transport = new FakeTransport();
        public readonly FakeTimer Timer = new FakeTimer();
        public DateTimeOffset Now = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);
        public readonly ReleaseUpdateService Service;
        public Fixture(string directory)
        { Directory = directory; Service = new ReleaseUpdateService(new Version(0, 2, 0), directory, Transport, Timer, delegate { return Now; }); }
        public void Dispose() { Service.Dispose(); Transport.Entered.Dispose(); }
    }
    private sealed class FakeTransport : IReleaseTransport
    {
        private readonly Queue<object> _replies = new Queue<object>();
        public int Calls; public string LastEtag; public CancellationToken LastToken;
        public readonly ManualResetEvent Entered = new ManualResetEvent(false);
        public void Enqueue(object reply) { _replies.Enqueue(reply); }
        public async Task<ReleaseHttpResponse> GetAsync(string etag, CancellationToken token)
        {
            Calls++; LastEtag = etag; LastToken = token; object result = _replies.Dequeue(); Entered.Set();
            Exception failure = result as Exception; if (failure != null) throw failure;
            var delayed = result as DelayedCancellationReply;
            if (delayed != null) return await delayed.Completion.Task.ConfigureAwait(false);
            var pending = result as TaskCompletionSource<ReleaseHttpResponse>;
            if (pending == null) return (ReleaseHttpResponse)result;
            using (token.Register(delegate { pending.TrySetCanceled(); })) return await pending.Task.ConfigureAwait(false);
        }
    }
    private sealed class DelayedCancellationReply
    {
        // The transport takes time to finish after Abort, as real network cleanup can do.
        public readonly TaskCompletionSource<ReleaseHttpResponse> Completion = new TaskCompletionSource<ReleaseHttpResponse>();
    }
    private sealed class FakeTimer : IUpdateTimer
    {
        public TimeSpan? Delay; public bool Disposed;
        public Action Callback;
        public void Schedule(TimeSpan dueTime, Action callback) { Delay = dueTime; Callback = callback; }
        public void Stop() { Delay = null; }
        public void Dispose() { Stop(); Disposed = true; }
    }
}
