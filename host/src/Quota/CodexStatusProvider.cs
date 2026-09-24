using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodexToolsHost.Model;
using CodexToolsHost.Protocol;

namespace CodexToolsHost.Quota
{
    /// <summary>Codex 运行状态 + 额度：通过本机 codex app-server JSON-RPC 获取</summary>
    public sealed class CodexStatusProvider : ICodexStatusSource, IDisposable
    {
        public const int RequestTimeoutSeconds = 60;
        public const int CodexStateOffline = 0;
        public const int CodexStateIdle = 1;
        public const int CodexStateRunning = 2;
        public const int CodexStateWaiting = 3;
        public const int CodexStateError = 4;
        public const int CodexStateComplete = 5;

        private readonly Func<IJsonLineTransport> _transportFactory;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);
        private readonly object _sync = new object();
        private IJsonLineTransport _transport;
        private JsonLineRpcClient _client;
        private Timer _reconnectTimer;
        private Timer _refreshTimer;
        private int _reconnectIndex;
        private bool _stopped;
        private bool _disposed;
        private QuotaSnapshot _quota = QuotaSnapshot.EmptyStale();
        private int _state = CodexStateOffline;
        private string _statusText = "connecting...";
        private int _refreshSeconds;

        public CodexStatusProvider(Func<IJsonLineTransport> transportFactory, int refreshSeconds)
        {
            if (transportFactory == null) throw new ArgumentNullException("transportFactory");
            _transportFactory = transportFactory;
            _refreshSeconds = refreshSeconds < 10 ? 30 : refreshSeconds;
        }

        public static CodexStatusProvider CreateDefault(int refreshSeconds)
        {
            return new CodexStatusProvider(delegate
            {
                string path = CodexLocator.Find();
                if (path == null) throw new FileNotFoundException("未找到 Codex 可执行文件");
                return new ProcessJsonLineTransport(path, "app-server --listen stdio://");
            }, refreshSeconds);
        }

        public event Action Changed;
        public event Action<string> StatusChanged;

        public QuotaSnapshot Quota { get { lock (_sync) { return _quota; } } }
        public int State { get { lock (_sync) { return _state; } } }
        public string StatusText { get { lock (_sync) { return _statusText; } } }

        public async Task StartAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_client != null) return;
                _stopped = false;
                IJsonLineTransport transport = _transportFactory();
                transport.Start();
                JsonLineRpcClient client = new JsonLineRpcClient(transport);
                client.NotificationReceived += OnNotification;
                client.TransportFaulted += OnTransportFaulted;
                lock (_sync)
                {
                    _transport = transport;
                    _client = client;
                }

                var clientInfo = new Dictionary<string, object>();
                clientInfo["name"] = "codex-tools-host";
                clientInfo["title"] = "CodeX Tools Host";
                clientInfo["version"] = "0.1.0";
                var capabilities = new Dictionary<string, object>();
                capabilities["experimentalApi"] = true;
                var initialize = new Dictionary<string, object>();
                initialize["clientInfo"] = clientInfo;
                initialize["capabilities"] = capabilities;

                await client.RequestAsync("initialize", initialize, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                await client.SendNotificationAsync("initialized", null).ConfigureAwait(false);
                SetState(CodexStateIdle, "connected");
                _reconnectIndex = 0;
                await RefreshQuotaAsync().ConfigureAwait(false);

                _refreshTimer = new Timer(delegate
                {
                    try { RefreshQuotaAsync().Wait(TimeSpan.FromSeconds(20)); }
                    catch (Exception) { }
                }, null, TimeSpan.FromSeconds(_refreshSeconds), TimeSpan.FromSeconds(_refreshSeconds));
            }
            catch
            {
                CloseConnection();
                SetState(CodexStateOffline, "connect failed");
                ScheduleReconnect();
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task RefreshQuotaAsync()
        {
            await _refreshGate.WaitAsync().ConfigureAwait(false);
            try
            {
                JsonLineRpcClient client;
                lock (_sync) { client = _client; }
                if (client == null) throw new InvalidOperationException("未连接 Codex");
                IDictionary<string, object> result = await client.RequestAsync(
                    "account/rateLimits/read", null, TimeSpan.FromSeconds(RequestTimeoutSeconds)).ConfigureAwait(false);
                PublishQuota(ParseFull(result));
            }
            catch
            {
                lock (_sync) { _quota = _quota.AsStale(); }
                RaiseChanged();
                throw;
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        public void Stop()
        {
            _stopped = true;
            Timer t;
            lock (_sync) { t = _reconnectTimer; _reconnectTimer = null; }
            if (t != null) t.Dispose();
            lock (_sync) { t = _refreshTimer; _refreshTimer = null; }
            if (t != null) t.Dispose();
            CloseConnection();
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
            _gate.Dispose();
            _refreshGate.Dispose();
        }

        private void OnNotification(object sender, RpcNotificationEventArgs args)
        {
            string method = args.Method;
            if (string.Equals(method, "account/rateLimits/updated", StringComparison.Ordinal))
            {
                object snapshotValue;
                IDictionary<string, object> snapshot = args.Parameters.TryGetValue("rateLimits", out snapshotValue)
                    ? snapshotValue as IDictionary<string, object>
                    : null;
                if (snapshot != null) PublishQuota(ParseSparse(snapshot));
            }
            else if (string.Equals(method, "item/started", StringComparison.Ordinal))
            {
                string thread = ReadString(args.Parameters, "threadId");
                string text = "RUN " + ShortId(thread);
                SetState(CodexStateRunning, text);
            }
            else if (string.Equals(method, "item/completed", StringComparison.Ordinal))
            {
                string thread = ReadString(args.Parameters, "threadId");
                SetState(CodexStateComplete, "DONE " + ShortId(thread));
            }
            else if (string.Equals(method, "guardian/warning", StringComparison.Ordinal))
            {
                string message = ReadString(args.Parameters, "message") ?? "需要确认";
                SetState(CodexStateWaiting, "WAIT " + Trim(AsciiClean(message), 26));
            }
            else if (string.Equals(method, "agent/message/delta", StringComparison.Ordinal))
            {
                if (State == CodexStateRunning)
                {
                    string delta = ReadString(args.Parameters, "delta") ?? "";
                    SetState(CodexStateRunning, "RUN " + Trim(AsciiClean(delta), 24));
                }
            }
        }

        private void OnTransportFaulted(object sender, Exception exception)
        {
            if (_stopped) return;
            CloseConnection();
            SetState(CodexStateOffline, "disconnected");
            ScheduleReconnect();
        }

        private void ScheduleReconnect()
        {
            if (_stopped || _disposed) return;
            int[] delays = { 1, 2, 5, 15, 30, 60, 120, 300 };
            int index = Math.Min(_reconnectIndex, delays.Length - 1);
            if (_reconnectIndex < delays.Length - 1) _reconnectIndex++;
            lock (_sync)
            {
                if (_reconnectTimer != null) return;
                _reconnectTimer = new Timer(delegate
                {
                    lock (_sync) { if (_reconnectTimer != null) _reconnectTimer.Dispose(); _reconnectTimer = null; }
                    try { StartAsync().Wait(TimeSpan.FromSeconds(80)); }
                    catch { ScheduleReconnect(); }
                }, null, TimeSpan.FromSeconds(delays[index]), Timeout.InfiniteTimeSpan);
            }
        }

        private void CloseConnection()
        {
            JsonLineRpcClient client;
            IJsonLineTransport transport;
            lock (_sync)
            {
                client = _client;
                transport = _transport;
                _client = null;
                _transport = null;
            }
            if (client != null)
            {
                client.NotificationReceived -= OnNotification;
                client.TransportFaulted -= OnTransportFaulted;
                client.Dispose();
            }
            if (transport != null) transport.Dispose();
        }

        private void SetState(int state, string text)
        {
            lock (_sync)
            {
                _state = state;
                _statusText = text;
            }
            var handler = StatusChanged;
            if (handler != null) handler(text);
            RaiseChanged();
        }

        private void PublishQuota(QuotaSnapshot snapshot)
        {
            lock (_sync) { _quota = snapshot; }
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null) handler();
        }

        private QuotaSnapshot ParseFull(IDictionary<string, object> result)
        {
            var buckets = ReadDict(result, "rateLimitsByLimitId");
            IDictionary<string, object> codex = null;
            if (buckets != null) codex = ReadDict(buckets, "codex");
            IDictionary<string, object> snapshot = codex ?? ReadDict(result, "rateLimits")
                ?? new Dictionary<string, object>();
            var primary = ReadDict(snapshot, "primary");
            var secondary = ReadDict(snapshot, "secondary");
            return QuotaSnapshot.FromUsedPercent(
                ReadInt(primary, "usedPercent"),
                ReadInt(secondary, "usedPercent"),
                ReadLong(primary, "resetsAt"),
                ReadLong(secondary, "resetsAt"),
                DateTimeOffset.UtcNow);
        }

        private QuotaSnapshot ParseSparse(IDictionary<string, object> snapshot)
        {
            QuotaSnapshot current = Quota;
            var primary = ReadDict(snapshot, "primary");
            var secondary = ReadDict(snapshot, "secondary");
            int? prim = primary == null ? current.PrimaryRemainingPercent : ToRemaining(ReadInt(primary, "usedPercent"));
            int? sec = secondary == null ? current.SecondaryRemainingPercent : ToRemaining(ReadInt(secondary, "usedPercent"));
            DateTimeOffset? primReset = primary == null ? current.PrimaryResetsAt : ReadReset(primary, current.PrimaryResetsAt);
            DateTimeOffset? secReset = secondary == null ? current.SecondaryResetsAt : ReadReset(secondary, current.SecondaryResetsAt);
            return new QuotaSnapshot(prim, sec, primReset, secReset, DateTimeOffset.UtcNow, false);
        }

        private static IDictionary<string, object> ReadDict(IDictionary<string, object> source, string key)
        {
            if (source == null) return null;
            object value;
            return source.TryGetValue(key, out value) ? value as IDictionary<string, object> : null;
        }

        private static string ReadString(IDictionary<string, object> source, string key)
        {
            if (source == null) return null;
            object value;
            if (!source.TryGetValue(key, out value) || value == null) return null;
            return Convert.ToString(value);
        }

        private static int? ReadInt(IDictionary<string, object> source, string key)
        {
            if (source == null) return null;
            object value;
            if (!source.TryGetValue(key, out value) || value == null) return null;
            try { return Convert.ToInt32(value); }
            catch (Exception) { return null; }
        }

        private static long? ReadLong(IDictionary<string, object> source, string key)
        {
            if (source == null) return null;
            object value;
            if (!source.TryGetValue(key, out value) || value == null) return null;
            try { return Convert.ToInt64(value); }
            catch (Exception) { return null; }
        }

        private static int? ToRemaining(int? used)
        {
            if (!used.HasValue) return null;
            return 100 - Math.Max(0, Math.Min(100, used.Value));
        }

        private static DateTimeOffset? ReadReset(IDictionary<string, object> source, DateTimeOffset? fallback)
        {
            long? unix = ReadLong(source, "resetsAt");
            if (!unix.HasValue) return fallback;
            try { return DateTimeOffset.FromUnixTimeSeconds(unix.Value); }
            catch (Exception) { return fallback; }
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            return id.Length <= 8 ? id : id.Substring(0, 8);
        }

        private static string Trim(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length <= max) return text;
            return text.Substring(0, max);
        }

        /* OLED 只支持 ASCII：非 ASCII 字符（如中文消息）统一转为空格，避免显示 ??? */
        private static string AsciiClean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder();
            foreach (char ch in text)
                sb.Append(ch >= 0x20 && ch <= 0x7E ? ch : ' ');
            string s = sb.ToString();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Trim();
        }
    }
}
