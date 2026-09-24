using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using CodexToolsHost.Quota;

internal sealed class TestTransport : IJsonLineTransport
{
    public event Action<byte[]> MessageReceived;
    public event Action<Exception> Faulted;
    public string QuotaResult = "{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":43,\"windowDurationMins\":10080},\"secondary\":{\"usedPercent\":30,\"windowDurationMins\":300}}}";
    public string Account = "{\"account\":{\"type\":\"chatgpt\",\"email\":\"fixture@example.invalid\"}}";
    public bool HoldQuota;
    public bool HoldAll;
    public bool ThrowOnSend;
    public bool BlockSend;
    public bool EmitInitialAccountNotification;
    public readonly ManualResetEventSlim WriteRelease = new ManualResetEventSlim();
    public int QuotaRequests;
    public int Disposes;
    public string HeldId;
    public void Start() { }
    public void Send(byte[] bytes)
    {
        if (BlockSend) WriteRelease.Wait();
        if (ThrowOnSend) throw new IOException("synthetic pipe closed");
        var request = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(bytes));
        if (!request.ContainsKey("id")) return;
        string method = Convert.ToString(request["method"]), id = Convert.ToString(request["id"]);
        if (HoldAll) { HeldId = id; return; }
        string result = "{}";
        if (method == "account/read")
        {
            if (EmitInitialAccountNotification)
            {
                EmitInitialAccountNotification = false;
                Emit("{\"method\":\"account/updated\",\"params\":{\"authMode\":\"chatgpt\"}}");
            }
            result = Account;
        }
        if (method == "account/rateLimits/read")
        {
            Interlocked.Increment(ref QuotaRequests);
            if (HoldQuota) { HeldId = id; return; }
            result = QuotaResult;
        }
        Reply(id, result);
    }
    public void Reply(string id, string result) { Emit("{\"id\":\"" + id + "\",\"result\":" + result + "}"); }
    public void Emit(string json)
    {
        var handler = MessageReceived;
        if (handler != null) handler(Encoding.UTF8.GetBytes(json));
    }
    public void Break() { var handler = Faulted; if (handler != null) handler(new IOException("synthetic disconnect")); }
    public void Dispose() { Interlocked.Increment(ref Disposes); WriteRelease.Set(); }
}

internal static class Runner
{
    private static int failures;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Run(string name, Action action)
    {
        try { action(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); }
    }
    private static CodexStatusProvider Provider(TestTransport transport)
    {
        var source = new CodexStatusProvider(delegate { return transport; }, 30);
        Check(source.StartAsync().Wait(3000), "initial connection timed out");
        return source;
    }
    private static void Refresh(CodexStatusProvider source)
    {
        try { Check(source.RefreshQuotaAsync().Wait(3000), "refresh timed out"); }
        catch (AggregateException) { }
    }
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--cycles") return RunCycles(int.Parse(args[1]));
        if (args.Length > 0 && args[0] == "--stderr-child")
        {
            for (int i = 0; i < 16000; i++) Console.Error.WriteLine(new string('x', 160));
            Console.Out.WriteLine("ready"); Console.Out.Flush(); Thread.Sleep(500); return 0;
        }
        Run("empty response preserves last good value and marks stale", delegate {
            var t = new TestTransport(); using (var p = Provider(t)) {
                var observed = p.Quota.ObservedAt; t.QuotaResult = "{}"; Refresh(p);
                Check(p.Quota.PrimaryRemainingPercent == 57, "last good value erased");
                Check(p.Quota.IsStale && p.Quota.ObservedAt == observed, "invalid response marked fresh");
            }
        });
        Run("initial account notification retries without reconnect", delegate {
            var t = new TestTransport { EmitInitialAccountNotification = true };
            using (var p = Provider(t)) {
                Check(t.Disposes == 0 && p.ReconnectCount == 0 && p.Quota.PrimaryRemainingPercent == 57,
                    "normal initial account notification restarted the connection");
            }
        });
        Run("foreign limit notification does not overwrite codex", delegate {
            var t = new TestTransport(); using (var p = Provider(t)) {
                t.Emit("{\"method\":\"account/rateLimits/updated\",\"params\":{\"rateLimits\":{\"limitId\":\"other\",\"primary\":{\"usedPercent\":99}}}}");
                Check(p.Quota.PrimaryRemainingPercent == 57, "foreign bucket overwrote codex");
            }
        });
        Run("explicit null removes second window", delegate {
            var t = new TestTransport(); using (var p = Provider(t)) {
                t.Emit("{\"method\":\"account/rateLimits/updated\",\"params\":{\"rateLimits\":{\"limitId\":\"codex\",\"secondary\":null}}}");
                Check(p.Quota.SecondaryRemainingPercent == null, "removed window retained");
                Check(p.Quota.PrimaryRemainingPercent == 57, "unmentioned primary lost");
            }
        });
        Run("partial window fields preserve percentage", delegate {
            var t = new TestTransport(); using (var p = Provider(t)) {
                t.Emit("{\"method\":\"account/rateLimits/updated\",\"params\":{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"resetsAt\":1900000000}}}}");
                Check(p.Quota.PrimaryRemainingPercent == 57, "missing usedPercent erased window");
            }
        });
        Run("unrelated metadata is not a freshness heartbeat", delegate {
            var t = new TestTransport(); using (var p = Provider(t)) {
                var when = p.Quota.ObservedAt; Thread.Sleep(15);
                t.Emit("{\"method\":\"account/rateLimits/updated\",\"params\":{\"rateLimits\":{\"limitId\":\"codex\",\"credits\":null}}}");
                Check(p.Quota.ObservedAt == when, "non-quota metadata refreshed freshness");
            }
        });
        Run("simultaneous refreshes are coalesced", delegate {
            var t = new TestTransport(); using (var p = Provider(t)) {
                t.HoldQuota = true; var tasks = new Task[30];
                for (int i = 0; i < tasks.Length; i++) tasks[i] = p.RefreshQuotaAsync();
                Check(SpinWait.SpinUntil(delegate { return t.HeldId != null; }, 1000), "request never sent");
                t.HoldQuota = false; t.Reply(t.HeldId, t.QuotaResult);
                Check(Task.WaitAll(tasks, 3000), "refresh queue stuck");
                Check(t.QuotaRequests == 2, "queued duplicate refresh count=" + t.QuotaRequests);
            }
        });
        Run("account change discards previous account quota", delegate {
            var t = new TestTransport(); using (var p = Provider(t)) {
                t.Account = "{\"account\":{\"type\":\"chatgpt\",\"email\":\"other@example.invalid\"}}";
                t.QuotaResult = "{}"; Refresh(p);
                Check(!p.Quota.PrimaryRemainingPercent.HasValue && p.Quota.IsStale, "quota leaked across accounts");
            }
        });
        Run("logout discards quota immediately", delegate {
            var t = new TestTransport(); using (var p = Provider(t)) {
                t.Emit("{\"method\":\"account/updated\",\"params\":{\"authMode\":null}}");
                Check(!p.Quota.PrimaryRemainingPercent.HasValue, "logged out quota retained");
            }
        });
        Run("malformed account response preserves last good value", delegate {
            var t = new TestTransport(); using (var p = Provider(t)) {
                t.Account = "{}"; Refresh(p);
                Check(p.Quota.PrimaryRemainingPercent == 57 && p.Quota.IsStale, "malformed account envelope cleared quota");
            }
        });
        Run("late quota push cannot repopulate signed-out account", delegate {
            var t = new TestTransport(); using (var p = Provider(t)) {
                t.Account = "{\"account\":null}"; Refresh(p);
                t.Emit("{\"method\":\"account/rateLimits/updated\",\"params\":{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":43}}}}");
                Check(!p.Quota.PrimaryRemainingPercent.HasValue && p.Quota.IsStale, "late push revived signed-out quota");
            }
        });
        Run("account without stable identity is invalid", delegate {
            var t = new TestTransport(); using (var p = Provider(t)) {
                t.Account = "{\"account\":{\"type\":\"chatgpt\"}}"; t.QuotaResult = "{}"; Refresh(p);
                Check(p.Quota.PrimaryRemainingPercent == 57 && p.Quota.IsStale, "missing identity erased last good quota");
            }
        });
        Run("stopping interrupts initialization notification", delegate {
            var t = new TestTransport { BlockSend = true }; using (var client = new JsonLineRpcClient(t)) {
                var operation = Task.Run(delegate { return client.SendNotificationAsync("initialized", null); });
                Thread.Sleep(30); client.Dispose();
                Check(SpinWait.SpinUntil(delegate { return operation.IsCompleted; }, 1000), "notification remained blocked after disposal");
            }
        });
        Run("initialization notification has a deadline", delegate {
            var t = new TestTransport { BlockSend = true }; using (var client = new JsonLineRpcClient(t)) {
                var operation = Task.Run(delegate { return client.SendNotificationAsync("initialized", null); });
                bool completed = SpinWait.SpinUntil(delegate { return operation.IsCompleted; }, 11000);
                t.WriteRelease.Set(); Check(completed && operation.IsFaulted, "notification has no deadline");
                GC.KeepAlive(operation.Exception);
            }
        });
        Run("blocked write observes request deadline", delegate {
            var t = new TestTransport { BlockSend = true }; using (var client = new JsonLineRpcClient(t)) {
                var operation = Task.Run(delegate { return client.RequestAsync("test", null, TimeSpan.FromMilliseconds(100)); });
                bool completed = SpinWait.SpinUntil(delegate { return operation.IsCompleted; }, 1000);
                t.WriteRelease.Set();
                Check(completed && operation.IsFaulted, "write prevented request timeout");
                GC.KeepAlive(operation.Exception);
            }
        });
        Run("rpc disposal completes pending requests promptly", delegate {
            var t = new TestTransport { HoldAll = true }; var client = new JsonLineRpcClient(t);
            var task = client.RequestAsync("test", null, TimeSpan.FromMinutes(1)); client.Dispose();
            Check(SpinWait.SpinUntil(delegate { return task.IsCompleted; }, 1000), "pending request stranded on disposal");
        });
        Run("rpc send failure removes pending entry", delegate {
            var t = new TestTransport { ThrowOnSend = true }; using (var client = new JsonLineRpcClient(t)) {
                try { client.RequestAsync("test", null, TimeSpan.FromSeconds(1)).Wait(); } catch (AggregateException) { }
                var pending = (System.Collections.IDictionary)typeof(JsonLineRpcClient).GetField("_pending", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(client);
                Check(pending.Count == 0, "failed send left pending entries");
            }
        });
        Run("stderr pressure does not block stdout", delegate {
            using (var t = new ProcessJsonLineTransport(typeof(Runner).Assembly.Location, "--stderr-child")) {
                var ready = new ManualResetEventSlim();
                t.MessageReceived += delegate(byte[] data) { if (Encoding.UTF8.GetString(data).Contains("ready")) ready.Set(); };
                t.Start(); Check(ready.Wait(5000), "child blocked before stdout under stderr pressure");
            }
        });
        Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1;
    }

    private static int RunCycles(int cycles)
    {
        var created = new List<TestTransport>();
        var guard = new object();
        using (var source = new CodexStatusProvider(delegate {
            var transport = new TestTransport(); lock (guard) created.Add(transport); return transport;
        }, 10))
        {
            source.StartAsync().Wait();
            for (int i = 0; i < cycles; i++)
            {
                TestTransport previous; int before;
                lock (guard) { before = created.Count; previous = created[before - 1]; }
                previous.Break();
                bool recovered = SpinWait.SpinUntil(delegate {
                    lock (guard) return created.Count == before + 1 && created[before].QuotaRequests == 1 && !source.Quota.IsStale;
                }, 4000);
                if (!recovered || previous.Disposes != 1) throw new Exception("Reconnect failed at cycle " + i);
                if ((i + 1) % 10 == 0) Console.WriteLine("Reconnect cycles passed: " + (i + 1));
            }
            source.Stop();
            Check(SpinWait.SpinUntil(delegate { lock (guard) return created.TrueForAll(t => t.Disposes == 1); }, 2000), "transport cleanup incomplete");
            for (int i = 0; i < 100; i++) { source.StartAsync().Wait(); source.Stop(); }
            Check(SpinWait.SpinUntil(delegate { lock (guard) return created.TrueForAll(t => t.Disposes == 1); }, 2000), "restart cleanup incomplete");
            Console.WriteLine("PASS " + cycles + " reconnects; 100 start/stop cycles; all transports disposed exactly once");
        }
        return 0;
    }
}
