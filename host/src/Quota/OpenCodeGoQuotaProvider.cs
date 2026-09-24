using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Web.Script.Serialization;
using CodexToolsHost.Model;

namespace CodexToolsHost.Quota
{
    public sealed class OpenCodeGoQuotaProvider : IOpenCodeGoQuotaSource
    {
        public const string UsageEndpoint = "https://opencode.ai/zen/go/v1/usage";

        private readonly object _sync = new object();
        private Timer _timer;
        private int _fetching;
        private bool _disposed;
        private string _apiKey;
        private int _refreshSeconds;
        private bool _isConfigured;
        private OpenCodeGoQuotaSnapshot _quota = OpenCodeGoQuotaSnapshot.EmptyStale();
        private string _lastError = "";

        public OpenCodeGoQuotaProvider(string apiKey, int refreshSeconds)
        {
            _apiKey = apiKey ?? "";
            _refreshSeconds = NormalizeRefreshSeconds(refreshSeconds);
            _isConfigured = !string.IsNullOrWhiteSpace(_apiKey);
        }

        public event Action Changed;

        public OpenCodeGoQuotaSnapshot Quota
        {
            get { lock (_sync) { return _quota; } }
        }

        public bool IsConfigured
        {
            get { lock (_sync) { return _isConfigured; } }
        }

        public bool IsStale
        {
            get { lock (_sync) { return _quota.IsStale; } }
        }

        public string LastError
        {
            get { lock (_sync) { return _lastError; } }
        }

        public void Start()
        {
            lock (_sync)
            {
                if (_disposed || _timer != null) return;
                _timer = new Timer(delegate
                {
                    try { Fetch(); }
                    catch (Exception) { /* Fetch 已保留旧快照并标记 stale。 */ }
                }, null, TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(_refreshSeconds));
            }
        }

        public void Stop()
        {
            Timer timer;
            lock (_sync)
            {
                timer = _timer;
                _timer = null;
            }
            if (timer != null) timer.Dispose();
        }

        public void UpdateCredentials(string apiKey, int refreshSeconds)
        {
            Timer timer;
            bool configured;
            lock (_sync)
            {
                _apiKey = apiKey ?? "";
                _refreshSeconds = NormalizeRefreshSeconds(refreshSeconds);
                configured = !string.IsNullOrWhiteSpace(_apiKey);
                _isConfigured = configured;
                if (!configured)
                {
                    _lastError = "未配置 API Key";
                    _quota = _quota.AsStale();
                }
                timer = _timer;
            }
            if (timer != null)
                timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(_refreshSeconds));
            RaiseChanged();
        }

        public string Fetch()
        {
            if (Interlocked.CompareExchange(ref _fetching, 1, 0) != 0)
                return "刷新进行中";
            try
            {
                return FetchCore();
            }
            finally
            {
                Volatile.Write(ref _fetching, 0);
            }
        }

        internal static OpenCodeGoQuotaSnapshot ParseUsageJson(string json,
            DateTimeOffset observedAt)
        {
            IDictionary root = new JavaScriptSerializer().DeserializeObject(json) as IDictionary;
            if (root == null) throw new InvalidDataException("响应格式错误");
            IDictionary usage = GetDict(root, "usage");
            if (usage == null) throw new InvalidDataException("响应中未找到 usage");
            return new OpenCodeGoQuotaSnapshot(
                ParseWindow(usage, "rolling"),
                ParseWindow(usage, "weekly"),
                ParseWindow(usage, "monthly"),
                observedAt, false);
        }

        private string FetchCore()
        {
            string key;
            lock (_sync) key = _apiKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                lock (_sync)
                {
                    _isConfigured = false;
                    _lastError = "未配置 API Key";
                    _quota = _quota.AsStale();
                }
                RaiseChanged();
                return "未配置 API Key";
            }

            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(UsageEndpoint);
                request.Method = "GET";
                request.Accept = "application/json";
                request.Headers["Authorization"] = "Bearer " + key;
                request.Timeout = 10000;
                request.UserAgent = "CodexToolsHost/0.3";

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream))
                {
                    OpenCodeGoQuotaSnapshot snapshot = ParseUsageJson(
                        reader.ReadToEnd(), DateTimeOffset.UtcNow);
                    lock (_sync)
                    {
                        _quota = snapshot;
                        _isConfigured = true;
                        _lastError = "";
                    }
                    RaiseChanged();
                    return "OK";
                }
            }
            catch (WebException ex)
            {
                string detail = DescribeWebException(ex);
                MarkStale(detail);
                throw new InvalidOperationException("OpenCode Go 查询失败: " + detail, ex);
            }
            catch (Exception ex)
            {
                MarkStale(ex.Message);
                throw;
            }
        }

        private static OpenCodeGoQuotaWindow ParseWindow(IDictionary usage, string key)
        {
            IDictionary value = GetDict(usage, key);
            if (value == null) return OpenCodeGoQuotaWindow.Empty();
            string status = GetString(value, "status") ?? "";
            int? percent = TryReadPercent(value, "percent");
            DateTimeOffset? resetsAt = TryReadDate(value, "resetsAt");
            return new OpenCodeGoQuotaWindow(status, percent, resetsAt);
        }

        private static int? TryReadPercent(IDictionary dict, string key)
        {
            if (dict == null || !dict.Contains(key) || dict[key] == null) return null;
            decimal number;
            if (!decimal.TryParse(Convert.ToString(dict[key], CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return null;
            if (number < 0m) number = 0m;
            if (number > 100m) number = 100m;
            return (int)Math.Round(number, MidpointRounding.AwayFromZero);
        }

        private static DateTimeOffset? TryReadDate(IDictionary dict, string key)
        {
            string text = GetString(dict, key);
            DateTimeOffset value;
            if (string.IsNullOrWhiteSpace(text)
                || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out value)) return null;
            return value;
        }

        private static IDictionary GetDict(IDictionary dict, string key)
        {
            if (dict == null || !dict.Contains(key)) return null;
            return dict[key] as IDictionary;
        }

        private static string GetString(IDictionary dict, string key)
        {
            if (dict == null || !dict.Contains(key) || dict[key] == null) return null;
            return Convert.ToString(dict[key], CultureInfo.InvariantCulture);
        }

        private static int NormalizeRefreshSeconds(int seconds)
        {
            return seconds < 30 ? 300 : Math.Min(86400, seconds);
        }

        private void MarkStale(string error)
        {
            lock (_sync)
            {
                _quota = _quota.AsStale();
                if (!string.IsNullOrWhiteSpace(error)) _lastError = error;
            }
            RaiseChanged();
        }

        private static string DescribeWebException(WebException ex)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            if (response != null)
            {
                if ((int)response.StatusCode == 429) return "HTTP 429 请求过于频繁";
                switch (response.StatusCode)
                {
                    case HttpStatusCode.Unauthorized: return "HTTP 401 Key 无效或已过期";
                    case HttpStatusCode.Forbidden: return "HTTP 403 未检测到 OpenCode Go 订阅";
                    default: return "HTTP " + (int)response.StatusCode + " " + response.StatusDescription;
                }
            }
            if (ex.Status == WebExceptionStatus.Timeout) return "连接超时";
            if (ex.Status == WebExceptionStatus.SecureChannelFailure) return "TLS 握手失败";
            if (ex.Status == WebExceptionStatus.NameResolutionFailure) return "DNS 解析失败";
            if (ex.Status == WebExceptionStatus.ConnectFailure) return "无法建立连接";
            return ex.Message;
        }

        private void RaiseChanged()
        {
            Action handler = Changed;
            try { if (handler != null) handler(); }
            catch (Exception) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
