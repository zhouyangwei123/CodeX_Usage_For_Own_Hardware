using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CodexToolsHost.Model;

namespace CodexToolsHost.Quota
{
    /// <summary>A single cancellable supervisor owns the Codex child and quota polling.</summary>
    public sealed class CodexStatusProvider : ICodexStatusSource, IDisposable
    {
        public const int RequestTimeoutSeconds = 20;
        public const int CodexStateOffline = 0, CodexStateIdle = 1, CodexStateRunning = 2,
            CodexStateWaiting = 3, CodexStateError = 4, CodexStateComplete = 5;
        private readonly Func<IJsonLineTransport> _transportFactory;
        private readonly object _sync = new object();
        private readonly int _refreshSeconds;
        private CancellationTokenSource _lifetime;
        private CancellationTokenSource _connectionLifetime;
        private JsonLineRpcClient _client;
        private Task _runner;
        private Task _refreshTask;
        private TaskCompletionSource<bool> _firstAttempt;
        private bool _stopped = true, _disposed, _ready, _accountConfirmed;
        private long _accountRevision;
        private string _identity;
        private QuotaSnapshot _quota = QuotaSnapshot.EmptyStale();
        private int _state = CodexStateOffline;
        private string _statusText = "connecting...";
        private int _reconnectCount;
        private int _consecutiveFailures;
        private sealed class AccountChangedException : Exception { }

        public CodexStatusProvider(Func<IJsonLineTransport> transportFactory, int refreshSeconds)
        {
            if (transportFactory == null) throw new ArgumentNullException("transportFactory");
            _transportFactory = transportFactory;
            _refreshSeconds = Math.Max(10, refreshSeconds);
        }
        public static CodexStatusProvider CreateDefault(int refreshSeconds)
        {
            return new CodexStatusProvider(delegate {
                string path = CodexLocator.Find();
                if (path == null) throw new FileNotFoundException("Codex executable was not found.");
                return new ProcessJsonLineTransport(path, "app-server --listen stdio://");
            }, refreshSeconds);
        }
        public event Action Changed;
        public event Action<string> StatusChanged;
        public QuotaSnapshot Quota { get { lock (_sync) return _quota; } }
        public int State { get { lock (_sync) return _state; } }
        public string StatusText { get { lock (_sync) return _statusText; } }
        public string LastError { get { return Quota.LastError; } }
        public int ReconnectCount { get { lock (_sync) return _reconnectCount; } }
        public int ConsecutiveFailures { get { lock (_sync) return _consecutiveFailures; } }
        public bool IsRequestInFlight { get { lock (_sync) return _refreshTask != null && !_refreshTask.IsCompleted; } }

        public Task StartAsync()
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException("CodexStatusProvider");
                if (!_stopped) return _firstAttempt.Task;
                _stopped = false;
                var lifetime = new CancellationTokenSource();
                _lifetime = lifetime;
                var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _firstAttempt = first;
                Task prior = _runner;
                _runner = Task.Run(async delegate {
                    if (prior != null) { try { await prior.ConfigureAwait(false); } catch (Exception) { } }
                    try { await RunAsync(lifetime.Token, first).ConfigureAwait(false); }
                    finally { lifetime.Dispose(); }
                });
                return first.Task;
            }
        }

        private async Task RunAsync(CancellationToken token, TaskCompletionSource<bool> first)
        {
            int connectionFailures = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    JsonLineRpcClient client = null;
                    CancellationTokenSource connection = null;
                    IJsonLineTransport transport = null;
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        transport = _transportFactory();
                        client = new JsonLineRpcClient(transport);
                        connection = CancellationTokenSource.CreateLinkedTokenSource(token);
                        client.NotificationReceived += OnNotification;
                        client.TransportFaulted += OnTransportFaulted;
                        lock (_sync)
                        {
                            token.ThrowIfCancellationRequested();
                            _client = client; _connectionLifetime = connection; _ready = false; _accountConfirmed = false; _refreshTask = null;
                        }
                        transport.Start();
                        var info = new Dictionary<string, object> { { "name", "codex-tools-host" }, { "title", "CodeX Tools Host" }, { "version", "0.2.0" } };
                        var initialize = new Dictionary<string, object> { { "clientInfo", info },
                            { "capabilities", new Dictionary<string, object> { { "experimentalApi", true } } } };
                        await client.RequestAsync("initialize", initialize, TimeSpan.FromSeconds(10), connection.Token).ConfigureAwait(false);
                        await client.SendNotificationAsync("initialized", null, connection.Token).ConfigureAwait(false);
                        lock (_sync) { connection.Token.ThrowIfCancellationRequested(); _ready = true; }
                        SetState(CodexStateIdle, "connected");
                        int failures = 0, timeouts = 0;
                        while (!connection.IsCancellationRequested)
                        {
                            bool authenticationFailure = false;
                            try
                            {
                                await RefreshQuotaAsync().ConfigureAwait(false);
                                failures = 0; timeouts = 0; connectionFailures = 0;
                                first.TrySetResult(true);
                            }
                            catch (AccountChangedException) { continue; }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                first.TrySetException(ex);
                                failures++;
                                var rpc = ex as CodexRpcException;
                                authenticationFailure = rpc != null && rpc.IsAuthenticationError;
                                if (ex is TimeoutException) timeouts++; else timeouts = 0;
                                if (timeouts >= 3 || ex is IOException && !(ex is InvalidDataException)) break;
                            }
                            lock (_sync) _consecutiveFailures = failures;
                            int seconds = authenticationFailure ? 300 : (int)Math.Min(300,
                                _refreshSeconds * Math.Pow(2, Math.Min(4, failures)));
                            await Task.Delay(TimeSpan.FromSeconds(seconds), connection.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { if (token.IsCancellationRequested) break; }
                    catch (Exception ex)
                    {
                        first.TrySetException(ex);
                        if (!token.IsCancellationRequested) MarkStale("额度连接失败，正在重连", client);
                    }
                    finally
                    {
                        lock (_sync)
                        {
                            if (ReferenceEquals(_client, client)) { _client = null; _ready = false; _connectionLifetime = null; }
                        }
                        if (connection != null) connection.Cancel();
                        if (client != null)
                        {
                            client.NotificationReceived -= OnNotification;
                            client.TransportFaulted -= OnTransportFaulted;
                            client.Dispose();
                        }
                        else if (transport != null) transport.Dispose();
                        if (connection != null) connection.Dispose();
                    }
                    if (token.IsCancellationRequested) break;
                    MarkStale("额度连接已断开，正在重连", null);
                    SetState(CodexStateOffline, "reconnecting...");
                    lock (_sync) _reconnectCount++;
                    int secondsToReconnect = Math.Min(60, 1 << Math.Min(6, connectionFailures++));
                    await Task.Delay(TimeSpan.FromSeconds(secondsToReconnect), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally { first.TrySetCanceled(); }
        }

        public Task RefreshQuotaAsync()
        {
            lock (_sync)
            {
                if (_disposed || _stopped || !_ready || _client == null)
                {
                    var failed = new TaskCompletionSource<bool>();
                    failed.SetException(new InvalidOperationException("Codex quota connection is not ready."));
                    return failed.Task;
                }
                if (_refreshTask != null && !_refreshTask.IsCompleted) return _refreshTask;
                JsonLineRpcClient client = _client;
                CancellationToken token = _connectionLifetime.Token;
                long revision = _accountRevision;
                _refreshTask = Task.Run(delegate { return RefreshCoreAsync(client, revision, token); });
                return _refreshTask;
            }
        }

        private async Task RefreshCoreAsync(JsonLineRpcClient client, long revision, CancellationToken token)
        {
            try
            {
                lock (_sync) { EnsureCurrent(client, revision, token); _accountConfirmed = false; }
                // Account identity is checked locally before fetching account-scoped values.
                var accountResult = await client.RequestAsync("account/read", new Dictionary<string, object> { { "refreshToken", false } },
                    TimeSpan.FromSeconds(RequestTimeoutSeconds), token).ConfigureAwait(false);
                var account = QuotaParser.Dict(accountResult, "account");
                string type = QuotaParser.Text(account, "type");
                object accountValue;
                if (!accountResult.TryGetValue("account", out accountValue) || accountValue != null &&
                    (account == null || string.IsNullOrEmpty(type)))
                    throw new InvalidDataException("Invalid Codex account response.");
                if (account == null || type == "apiKey" || type == "amazonBedrock")
                {
                    ClearQuota(client, revision, "未登录 Codex 账户或当前认证不提供账户额度");
                    throw new CodexRpcException(401, true);
                }
                string accountKey = IdentityText(account, "accountId") ?? IdentityText(account, "id") ?? IdentityText(account, "email");
                if (accountKey == null) throw new InvalidDataException("Account identity is unavailable.");
                string identity = type + ":" + accountKey;
                bool changed = false;
                lock (_sync)
                {
                    EnsureCurrent(client, revision, token);
                    if (_identity != identity)
                    {
                        _quota = QuotaSnapshot.EmptyStale(); _identity = identity; changed = true;
                    }
                    _accountConfirmed = true;
                }
                if (changed) RaiseChanged();
                var result = await client.RequestAsync("account/rateLimits/read", null,
                    TimeSpan.FromSeconds(RequestTimeoutSeconds), token).ConfigureAwait(false);
                var snapshot = QuotaParser.Full(result);
                lock (_sync)
                {
                    EnsureCurrent(client, revision, token);
                    _quota = snapshot; _consecutiveFailures = 0;
                }
                RaiseChanged();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var rpc = ex as CodexRpcException;
                string error = ex is TimeoutException ? "额度请求超时，保留最近成功值" :
                    ex is InvalidDataException ? "额度响应无效，保留最近成功值" :
                    rpc != null && rpc.IsAuthenticationError ? "Codex 登录已失效，请在 Codex 中重新登录" : "额度请求失败，等待重试";
                bool current;
                lock (_sync) current = ReferenceEquals(_client, client) && revision == _accountRevision && !token.IsCancellationRequested;
                if (current)
                {
                    if (rpc != null && rpc.IsAuthenticationError) ClearQuota(client, revision, error);
                    else MarkStale(error, client);
                }
                throw;
            }
        }

        private void EnsureCurrent(JsonLineRpcClient client, long revision, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(client, _client) || _stopped)
                throw new OperationCanceledException("Quota source changed.");
            // An account notification is normal during initialize. Re-read the account
            // on this connection; reconnecting would receive the same notification again.
            if (revision != _accountRevision) throw new AccountChangedException();
        }
        private static string IdentityText(IDictionary<string, object> account, string key)
        {
            object value;
            string text = account.TryGetValue(key, out value) ? value as string : null;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        private void ClearQuota(JsonLineRpcClient client, long revision, string error)
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_client, client) || revision != _accountRevision) return;
                _quota = QuotaSnapshot.EmptyStale().AsStale(error);
                _identity = null; _accountConfirmed = false;
            }
            RaiseChanged();
        }
        private void MarkStale(string error, JsonLineRpcClient expected)
        {
            lock (_sync)
            {
                if (_stopped || expected != null && !ReferenceEquals(_client, expected)) return;
                _quota = _quota.AsStale(error);
            }
            RaiseChanged();
        }
        private void OnTransportFaulted(object sender, Exception error)
        {
            lock (_sync)
            {
                if (!ReferenceEquals(sender, _client) || _stopped) return;
                if (_connectionLifetime != null) _connectionLifetime.Cancel();
                _quota = _quota.AsStale("额度连接已断开，正在重连");
            }
            RaiseChanged();
        }
        private void OnNotification(object sender, RpcNotificationEventArgs args)
        {
            lock (_sync) { if (!ReferenceEquals(sender, _client) || _stopped) return; }
            if (args.Method == "account/updated")
            {
                lock (_sync)
                {
                    if (!ReferenceEquals(sender, _client) || _stopped) return;
                    _accountRevision++; _identity = null;
                    _accountConfirmed = false;
                    _quota = QuotaSnapshot.EmptyStale().AsStale("账户已变化，等待重新获取额度");
                }
                RaiseChanged();
                return;
            }
            if (args.Method == "account/rateLimits/updated")
            {
                try
                {
                    lock (_sync)
                    {
                        if (!ReferenceEquals(sender, _client) || !_accountConfirmed || _identity == null) return;
                        var next = QuotaParser.Sparse(QuotaParser.Dict(args.Parameters, "rateLimits"), _quota);
                        if (next == null) return;
                        _quota = next;
                    }
                    RaiseChanged();
                }
                catch (InvalidDataException) { MarkStale("额度推送无效，等待重新获取", sender as JsonLineRpcClient); }
            }
            // Desktop activity is observed independently by DesktopLogStatusMonitor.
        }
        private void SetState(int state, string text)
        {
            lock (_sync) { if (_stopped) return; _state = state; _statusText = text; }
            var handler = StatusChanged; if (handler != null) handler(text);
            RaiseChanged();
        }
        private void RaiseChanged() { var handler = Changed; if (handler != null) handler(); }
        public void Stop()
        {
            CancellationTokenSource lifetime;
            JsonLineRpcClient client;
            lock (_sync)
            {
                if (_stopped) return;
                _stopped = true; _ready = false; lifetime = _lifetime; client = _client;
                _quota = _quota.AsStale("额度服务已停止"); _state = CodexStateOffline; _statusText = "stopped";
            }
            try { if (lifetime != null) lifetime.Cancel(); } catch (ObjectDisposedException) { }
            if (client != null) client.Dispose();
        }
        public void Dispose()
        {
            lock (_sync) { if (_disposed) return; _disposed = true; }
            Stop();
        }
    }
}
