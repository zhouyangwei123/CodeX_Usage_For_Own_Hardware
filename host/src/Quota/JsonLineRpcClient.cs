using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexToolsHost.Quota
{
    public sealed class RpcNotificationEventArgs : EventArgs
    {
        public RpcNotificationEventArgs(string method, IDictionary<string, object> parameters)
        { Method = method; Parameters = parameters; }
        public string Method { get; private set; }
        public IDictionary<string, object> Parameters { get; private set; }
    }

    public sealed class CodexRpcException : Exception
    {
        public int Code { get; private set; }
        public bool IsAuthenticationError { get; private set; }
        internal CodexRpcException(int code, bool authentication)
            : base(authentication ? "Codex authentication is unavailable." : "Codex RPC failed (" + code + ").")
        { Code = code; IsAuthenticationError = authentication; }
    }

    public sealed class JsonLineRpcClient : IDisposable
    {
        private readonly IJsonLineTransport _transport;
        private readonly object _sync = new object();
        private readonly object _sendSync = new object();
        private readonly Dictionary<string, TaskCompletionSource<IDictionary<string, object>>> _pending =
            new Dictionary<string, TaskCompletionSource<IDictionary<string, object>>>();
        private int _requestId;
        private bool _disposed;
        private Exception _fault;

        public JsonLineRpcClient(IJsonLineTransport transport)
        {
            _transport = transport;
            transport.MessageReceived += OnMessage;
            transport.Faulted += OnFaulted;
        }
        public event EventHandler<RpcNotificationEventArgs> NotificationReceived;
        public event EventHandler<Exception> TransportFaulted;

        public Task<IDictionary<string, object>> RequestAsync(string method, IDictionary<string, object> parameters, TimeSpan timeout)
        { return RequestAsync(method, parameters, timeout, CancellationToken.None); }

        public async Task<IDictionary<string, object>> RequestAsync(string method, IDictionary<string, object> parameters,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            string id = Interlocked.Increment(ref _requestId).ToString(CultureInfo.InvariantCulture);
            var completion = new TaskCompletionSource<IDictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException("JsonLineRpcClient");
                if (_fault != null) throw new IOException("Codex connection has closed.", _fault);
                _pending.Add(id, completion);
            }
            try
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    deadline.CancelAfter(timeout);
                    using (deadline.Token.Register(delegate { completion.TrySetCanceled(); }))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var message = new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id },
                            { "method", method }, { "params", parameters ?? new Dictionary<string, object>() } };
                        CancellationToken sendToken = deadline.Token;
                        Task send = Task.Run(delegate {
                            try { SendLine(message, sendToken); }
                            catch (Exception ex) { completion.TrySetException(ex); }
                        });
                        try { return await completion.Task.ConfigureAwait(false); }
                        catch (OperationCanceledException)
                        {
                            bool cancelled = cancellationToken.IsCancellationRequested;
                            if (!send.IsCompleted)
                            {
                                // A blocked pipe write must be interrupted, not left on a worker forever.
                                OnFaulted(new IOException("Codex write did not complete before its deadline."));
                                _transport.Dispose();
                            }
                            if (cancelled) throw new OperationCanceledException(cancellationToken);
                            throw new TimeoutException("Codex request timed out: " + method);
                        }
                    }
                }
            }
            finally { lock (_sync) _pending.Remove(id); }
        }

        public Task SendNotificationAsync(string method, IDictionary<string, object> parameters)
        { return SendNotificationAsync(method, parameters, CancellationToken.None); }

        public async Task SendNotificationAsync(string method, IDictionary<string, object> parameters, CancellationToken token)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var complete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationToken sendToken = deadline.Token;
                using (sendToken.Register(delegate { complete.TrySetCanceled(); }))
                {
                    Task send = Task.Run(delegate {
                        try
                        {
                            SendLine(new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "method", method },
                                { "params", parameters ?? new Dictionary<string, object>() } }, sendToken);
                            complete.TrySetResult(true);
                        }
                        catch (Exception ex) { complete.TrySetException(ex); }
                    });
                    try { await complete.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException)
                    {
                        bool cancelled = token.IsCancellationRequested;
                        if (!send.IsCompleted)
                        {
                            OnFaulted(new IOException("Codex notification write did not complete before its deadline."));
                            _transport.Dispose();
                        }
                        if (cancelled) throw new OperationCanceledException(token);
                        throw new TimeoutException("Codex notification timed out: " + method);
                    }
                }
            }
        }
        private void SendLine(IDictionary<string, object> message, CancellationToken token)
        {
            // A single write prevents concurrent requests from interleaving JSON lines.
            lock (_sendSync)
            {
                token.ThrowIfCancellationRequested();
                lock (_sync) { if (_disposed) throw new ObjectDisposedException("JsonLineRpcClient"); }
                string line = new JavaScriptSerializer().Serialize(message) + "\n";
                _transport.Send(Encoding.UTF8.GetBytes(line));
            }
        }
        private void OnMessage(byte[] bytes)
        {
            IDictionary<string, object> message;
            try { message = new JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(bytes)) as IDictionary<string, object>; }
            catch (ArgumentException) { return; }
            catch (InvalidOperationException) { return; }
            if (message == null) return;
            object id;
            if (message.TryGetValue("id", out id) && id != null)
            {
                TaskCompletionSource<IDictionary<string, object>> completion;
                lock (_sync) _pending.TryGetValue(Convert.ToString(id, CultureInfo.InvariantCulture), out completion);
                if (completion == null) return;
                object error;
                if (message.TryGetValue("error", out error) && error != null)
                {
                    var details = error as IDictionary<string, object>;
                    int code = 0; object raw;
                    if (details != null && details.TryGetValue("code", out raw)) int.TryParse(Convert.ToString(raw), out code);
                    string detail = (QuotaParser.Text(details, "message") ?? "").ToLowerInvariant();
                    bool auth = code == 401 || code == 403 || detail.Contains("unauthorized") || detail.Contains("not logged")
                        || detail.Contains("authentication") || detail.Contains("refresh token") || detail.Contains("401 unauthorized");
                    // Never forward server error text; it can contain account information.
                    completion.TrySetException(new CodexRpcException(code, auth));
                }
                else
                {
                    var result = QuotaParser.Dict(message, "result");
                    if (result == null) completion.TrySetException(new InvalidDataException("Invalid Codex response envelope."));
                    else completion.TrySetResult(result);
                }
                return;
            }
            string method = QuotaParser.Text(message, "method");
            var handler = NotificationReceived;
            if (method != null && handler != null)
                handler(this, new RpcNotificationEventArgs(method, QuotaParser.Dict(message, "params") ?? new Dictionary<string, object>()));
        }
        private void FailPending(Exception error)
        {
            List<TaskCompletionSource<IDictionary<string, object>>> pending;
            lock (_sync) { pending = new List<TaskCompletionSource<IDictionary<string, object>>>(_pending.Values); _pending.Clear(); }
            foreach (var completion in pending) completion.TrySetException(error);
        }
        private void OnFaulted(Exception error)
        {
            lock (_sync) { if (_disposed || _fault != null) return; _fault = error; }
            FailPending(new IOException("Codex connection has closed."));
            var handler = TransportFaulted;
            if (handler != null) handler(this, error);
        }
        public void Dispose()
        {
            lock (_sync) { if (_disposed) return; _disposed = true; }
            _transport.MessageReceived -= OnMessage;
            _transport.Faulted -= OnFaulted;
            FailPending(new ObjectDisposedException("JsonLineRpcClient"));
            _transport.Dispose();
        }
    }
}
