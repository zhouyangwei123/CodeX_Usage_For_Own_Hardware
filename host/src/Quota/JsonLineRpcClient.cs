using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexToolsHost.Quota
{
    public sealed class RpcNotificationEventArgs : EventArgs
    {
        public RpcNotificationEventArgs(string method, IDictionary<string, object> parameters)
        {
            Method = method;
            Parameters = parameters;
        }
        public string Method { get; private set; }
        public IDictionary<string, object> Parameters { get; private set; }
    }

    /// <summary>基于换行分隔 JSON 的轻量 JSON-RPC 客户端（与参考 CodexQuotaHUD 一致）</summary>
    public sealed class JsonLineRpcClient : IDisposable
    {
        private readonly IJsonLineTransport _transport;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly object _sync = new object();
        private readonly Dictionary<string, TaskCompletionSource<IDictionary<string, object>>> _pending =
            new Dictionary<string, TaskCompletionSource<IDictionary<string, object>>>();
        private int _requestId;
        private bool _disposed;

        public JsonLineRpcClient(IJsonLineTransport transport)
        {
            _transport = transport;
            _transport.MessageReceived += OnMessage;
            _transport.Faulted += OnFaulted;
        }

        public event EventHandler<RpcNotificationEventArgs> NotificationReceived;
        public event EventHandler<Exception> TransportFaulted;

        public async Task<IDictionary<string, object>> RequestAsync(
            string method, IDictionary<string, object> parameters, TimeSpan timeout)
        {
            int id = Interlocked.Increment(ref _requestId);
            string idText = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var tcs = new TaskCompletionSource<IDictionary<string, object>>();
            lock (_sync) _pending[idText] = tcs;

            var message = new Dictionary<string, object>();
            message["jsonrpc"] = "2.0";
            message["id"] = idText;
            message["method"] = method;
            message["params"] = parameters ?? new Dictionary<string, object>();
            SendLine(message);

            Task completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
            lock (_sync) _pending.Remove(idText);
            if (completed != tcs.Task) throw new TimeoutException("RPC 请求超时: " + method);
            return await tcs.Task.ConfigureAwait(false);
        }

        public async Task SendNotificationAsync(string method, IDictionary<string, object> parameters)
        {
            var message = new Dictionary<string, object>();
            message["jsonrpc"] = "2.0";
            message["method"] = method;
            message["params"] = parameters ?? new Dictionary<string, object>();
            SendLine(message);
            await Task.FromResult(0).ConfigureAwait(false);
        }

        private void SendLine(IDictionary<string, object> message)
        {
            string json = _json.Serialize(message) + "\n";
            _transport.Send(Encoding.UTF8.GetBytes(json));
        }

        private void OnMessage(byte[] data)
        {
            string text = Encoding.UTF8.GetString(data).Trim();
            if (text.Length == 0) return;
            IDictionary<string, object> message;
            try { message = _json.DeserializeObject(text) as IDictionary<string, object>; }
            catch (Exception) { return; }
            if (message == null) return;

            object idValue;
            if (message.TryGetValue("id", out idValue) && idValue != null)
            {
                string idText = Convert.ToString(idValue);
                TaskCompletionSource<IDictionary<string, object>> tcs;
                lock (_sync) { _pending.TryGetValue(idText, out tcs); }
                if (tcs != null)
                {
                    object error;
                    if (message.TryGetValue("error", out error) && error != null)
                    {
                        tcs.TrySetException(new InvalidOperationException("RPC error: " + _json.Serialize(error)));
                    }
                    else
                    {
                        object result;
                        tcs.TrySetResult(message.TryGetValue("result", out result)
                            ? result as IDictionary<string, object>
                            : new Dictionary<string, object>());
                    }
                    return;
                }
            }

            object methodValue;
            if (message.TryGetValue("method", out methodValue))
            {
                IDictionary<string, object> parameters = null;
                object paramsValue;
                if (message.TryGetValue("params", out paramsValue))
                    parameters = paramsValue as IDictionary<string, object>;
                var handler = NotificationReceived;
                if (handler != null)
                    handler(this, new RpcNotificationEventArgs(Convert.ToString(methodValue), parameters ?? new Dictionary<string, object>()));
            }
        }

        private void OnFaulted(Exception exception)
        {
            var handler = TransportFaulted;
            if (handler != null) handler(this, exception);
            List<TaskCompletionSource<IDictionary<string, object>>> pending;
            lock (_sync)
            {
                pending = new List<TaskCompletionSource<IDictionary<string, object>>>(_pending.Values);
                _pending.Clear();
            }
            foreach (var tcs in pending)
                tcs.TrySetException(exception);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _transport.MessageReceived -= OnMessage;
            _transport.Faulted -= OnFaulted;
            _transport.Dispose();
        }
    }
}
