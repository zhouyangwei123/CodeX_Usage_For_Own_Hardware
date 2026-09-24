using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexToolsHost.Quota
{
    /// <summary>
    /// 通用 API 余额查询：DeepSeek / 硅基流动 / OpenRouter / 自定义。
    /// 各厂商按预设端点与字段解析；自定义按用户填写的完整地址请求，
    /// 并从常见余额字段（total_balance / totalBalance / limit_remaining / balance 等）中识别。
    /// </summary>
    public sealed class ApiBalanceProvider : IDeepSeekSource, IDisposable
    {
        private const string ProviderDeepSeek = "deepseek";
        private const string ProviderSiliconFlow = "siliconflow";
        private const string ProviderOpenRouter = "openrouter";
        private const string ProviderCustom = "custom";

        public static readonly string[] KnownProviders = new string[]
        {
            ProviderDeepSeek, ProviderSiliconFlow, ProviderOpenRouter, ProviderCustom
        };

        public static string NormalizeProvider(string provider)
        {
            string p = (provider ?? ProviderDeepSeek).Trim().ToLowerInvariant();
            foreach (string k in KnownProviders)
                if (k == p) return k;
            return ProviderCustom;
        }

        public static string DisplayName(string provider)
        {
            switch (NormalizeProvider(provider))
            {
                case ProviderSiliconFlow: return "硅基流动";
                case ProviderOpenRouter: return "OpenRouter";
                case ProviderCustom: return "自定义";
                default: return "DeepSeek";
            }
        }

        public static string DefaultBaseUrl(string provider)
        {
            switch (NormalizeProvider(provider))
            {
                case ProviderSiliconFlow: return "https://api.siliconflow.cn";
                case ProviderOpenRouter: return "https://openrouter.ai/api";
                default: return "https://api.deepseek.com";
            }
        }

        private readonly object _sync = new object();
        private Timer _timer;
        private bool _disposed;
        private long _balanceCents;
        private string _currency = "CNY";
        private bool _available;
        private bool _unlimited;
        private bool _isStale = true;
        private string _apiKey;
        private string _baseUrl;
        private string _provider = ProviderDeepSeek;
        private int _refreshSeconds;
        private string _lastError = "";

        public ApiBalanceProvider(string apiKey, string baseUrl, int refreshSeconds)
            : this(apiKey, baseUrl, refreshSeconds, ProviderDeepSeek)
        {
        }

        public ApiBalanceProvider(string apiKey, string baseUrl, int refreshSeconds, string provider)
        {
            _apiKey = apiKey ?? "";
            _provider = NormalizeProvider(provider);
            _currency = DefaultCurrency(_provider);
            _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl(_provider) : baseUrl.TrimEnd('/');
            _refreshSeconds = refreshSeconds < 30 ? 300 : refreshSeconds;
        }

        public event Action Changed;

        public long BalanceCents { get { lock (_sync) { return _balanceCents; } } }
        public string Currency { get { lock (_sync) { return _currency; } } }
        public bool Available { get { lock (_sync) { return _available; } } }
        public bool IsStale { get { lock (_sync) { return _isStale; } } }
        public bool Unlimited { get { lock (_sync) { return _unlimited; } } }
        public string ProviderName { get { lock (_sync) { return DisplayName(_provider); } } }
        public string LastError { get { lock (_sync) { return _lastError; } } }

        public void UpdateCredentials(string apiKey, string baseUrl, int refreshSeconds)
        {
            UpdateCredentials(apiKey, baseUrl, refreshSeconds, _provider);
        }

        public void UpdateCredentials(string apiKey, string baseUrl, int refreshSeconds, string provider)
        {
            lock (_sync)
            {
                _apiKey = apiKey ?? "";
                _provider = NormalizeProvider(provider);
                _currency = DefaultCurrency(_provider);
                _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl(_provider) : baseUrl.TrimEnd('/');
                _refreshSeconds = refreshSeconds < 30 ? 300 : refreshSeconds;
            }
            if (_timer != null)
            {
                _timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(_refreshSeconds));
            }
        }

        public void Start()
        {
            lock (_sync)
            {
                if (_disposed || _timer != null) return;
                _timer = new Timer(delegate
                {
                    try { Fetch(); }
                    catch (Exception) { MarkStale(); }
                }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(_refreshSeconds));
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

        public string Fetch()
        {
            string key;
            string url;
            string provider;
            lock (_sync)
            {
                key = _apiKey;
                url = _baseUrl;
                provider = _provider;
            }
            if (string.IsNullOrWhiteSpace(key))
            {
                lock (_sync)
                {
                    _lastError = "未配置 API Key";
                    _isStale = true;
                    _available = false;
                    _unlimited = false;
                }
                RaiseChanged();
                return "未配置 API Key";
            }

            try
            {
                /* .NET Framework 默认可能走 TLS1.0，多数厂商要求 TLS1.2+ */
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                string endpoint = BuildEndpoint(provider, url);
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endpoint);
                request.Method = "GET";
                request.Accept = "application/json";
                request.Headers["Authorization"] = "Bearer " + key;
                request.Timeout = 10000;
                request.UserAgent = "CodexToolsHost/0.3";

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream))
                {
                    string json = reader.ReadToEnd();
                    bool available;
                    long cents;
                    string currency;
                    bool unlimited;
                    ParseBalanceJson(provider, json, out available, out cents, out currency, out unlimited);
                    lock (_sync)
                    {
                        _available = available;
                        _balanceCents = cents;
                        _currency = currency;
                        _unlimited = unlimited;
                        _isStale = false;
                        _lastError = "";
                    }
                    RaiseChanged();
                    return "OK " + (unlimited ? "UNLIMITED" :
                        (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture) + " " + currency);
                }
            }
            catch (WebException ex)
            {
                string detail = DescribeWebException(ex);
                lock (_sync) { _lastError = detail; _isStale = true; _available = false; _unlimited = false; }
                RaiseChanged();
                throw new InvalidOperationException(DisplayName(provider) + " 查询失败: " + detail, ex);
            }
            catch (Exception ex)
            {
                lock (_sync) { _lastError = ex.Message; _isStale = true; _available = false; _unlimited = false; }
                RaiseChanged();
                throw;
            }
        }

        private static string BuildEndpoint(string provider, string baseUrl)
        {
            string normalized = NormalizeProvider(provider);
            string b = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl(normalized) : baseUrl.TrimEnd('/');
            switch (normalized)
            {
                case ProviderSiliconFlow:
                    return EndWithCheck(b, "/v1/user/info");
                case ProviderOpenRouter:
                    return EndWithCheck(b, "/v1/key");
                case ProviderCustom:
                    return b; /* 自定义：用户填什么就请求什么 */
                default:
                    return EndWithCheck(b, "/user/balance");
            }
        }

        private static string EndWithCheck(string baseUrl, string suffix)
        {
            if (baseUrl.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return baseUrl;
            return baseUrl + suffix;
        }

        /// <summary>解析厂商余额响应（internal 供自检桩使用）。</summary>
        internal static void ParseBalanceJson(string provider, string json,
            out bool available, out long cents, out string currency, out bool unlimited)
        {
            string normalized = NormalizeProvider(provider);
            available = false;
            cents = 0;
            currency = DefaultCurrency(normalized);
            unlimited = false;

            IDictionary root = new JavaScriptSerializer().DeserializeObject(json) as IDictionary;
            if (root == null) throw new InvalidDataException("响应格式错误");

            if (normalized == ProviderSiliconFlow)
            {
                IDictionary data = GetDict(root, "data");
                string total = data == null ? null : GetString(data, "totalBalance");
                if (string.IsNullOrEmpty(total) && data != null) total = GetString(data, "balance");
                if (string.IsNullOrEmpty(total) && data != null) total = GetString(data, "chargeBalance");
                if (!TryParseMoney(total, out cents))
                    throw new InvalidDataException("响应中未找到 data.totalBalance");
                available = true;
                string c = data == null ? null : GetString(data, "currency");
                if (!string.IsNullOrWhiteSpace(c)) currency = c;
                return;
            }

            if (normalized == ProviderOpenRouter)
            {
                IDictionary data = GetDict(root, "data");
                if (data == null || !data.Contains("limit_remaining"))
                    throw new InvalidDataException("响应中未找到 data.limit_remaining");
                object remaining = data["limit_remaining"];
                if (remaining == null)
                {
                    available = true;
                    unlimited = true;
                    return;
                }
                if (!TryParseMoney(Convert.ToString(remaining, CultureInfo.InvariantCulture), out cents))
                    throw new InvalidDataException("响应中未找到 data.limit_remaining");
                available = true;
                currency = "USD";
                return;
            }

            /* DeepSeek / 自定义：先试 balance_infos，再试常见字段 */
            if (root.Contains("is_available"))
            {
                try { available = Convert.ToBoolean(root["is_available"], CultureInfo.InvariantCulture); }
                catch (Exception) { available = false; }
            }

            bool found = false;
            IList infos = root["balance_infos"] as IList;
            if (infos != null)
            {
                bool fallbackFound = false;
                long fallbackCents = 0;
                string fallbackCurrency = currency;
                foreach (object item in infos)
                {
                    IDictionary info = item as IDictionary;
                    if (info == null) continue;
                    string total = GetString(info, "total_balance");
                    long parsedCents;
                    if (TryParseMoney(total, out parsedCents))
                    {
                        string c = GetString(info, "currency");
                        if (normalized == ProviderDeepSeek
                            && string.Equals(c, "CNY", StringComparison.OrdinalIgnoreCase))
                        {
                            cents = parsedCents;
                            currency = "CNY";
                            found = true;
                            break;
                        }
                        if (!fallbackFound)
                        {
                            fallbackFound = true;
                            fallbackCents = parsedCents;
                            if (!string.IsNullOrWhiteSpace(c)) fallbackCurrency = c;
                        }
                    }
                }
                if (!found && fallbackFound)
                {
                    cents = fallbackCents;
                    currency = fallbackCurrency;
                    found = true;
                }
            }

            if (!found)
            {
                IDictionary data = GetDict(root, "data");
                bool limitKeyPresent = (data != null && data.Contains("limit_remaining"))
                    || root.Contains("limit_remaining");
                string[] keys = { "totalBalance", "total_balance", "limit_remaining",
                                  "available_balance", "balance", "chargeBalance" };
                foreach (string key in keys)
                {
                    object value = null;
                    if (data != null && data.Contains(key)) value = data[key];
                    else if (root.Contains(key)) value = root[key];
                    if (value == null)
                    {
                        if (key == "limit_remaining" && limitKeyPresent) { unlimited = true; found = true; }
                        continue;
                    }
                    if (TryParseMoney(Convert.ToString(value, CultureInfo.InvariantCulture), out cents))
                    {
                        found = true;
                        break;
                    }
                }
                if (found)
                {
                    available = true;
                    string c = data == null ? null : (GetString(data, "currency") ?? GetString(data, "currency_code"));
                    if (string.IsNullOrWhiteSpace(c)) c = GetString(root, "currency") ?? GetString(root, "currency_code");
                    if (!string.IsNullOrWhiteSpace(c)) currency = c;
                }
            }

            if (!found)
                throw new InvalidDataException("响应中未找到可识别的余额字段");
        }

        private static IDictionary GetDict(IDictionary root, string key)
        {
            if (root == null || !root.Contains(key)) return null;
            return root[key] as IDictionary;
        }

        private static string DefaultCurrency(string provider)
        {
            string normalized = NormalizeProvider(provider);
            return normalized == ProviderDeepSeek || normalized == ProviderSiliconFlow
                ? "CNY" : "USD";
        }

        private static string GetString(IDictionary dict, string key)
        {
            if (dict == null || !dict.Contains(key)) return null;
            object value = dict[key];
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static bool TryParseMoney(string text, out long cents)
        {
            cents = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            decimal value;
            if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
            if (value < 0m) value = 0m;
            long raw = (long)Math.Round(value * 100m);
            cents = Math.Max(0, Math.Min(raw, (long)uint.MaxValue));
            return true;
        }

        private static string DescribeWebException(WebException ex)
        {
            HttpWebResponse resp = ex.Response as HttpWebResponse;
            if (resp != null)
                return "HTTP " + (int)resp.StatusCode + " " + resp.StatusDescription;
            if (ex.Status == WebExceptionStatus.Timeout) return "连接超时";
            if (ex.Status == WebExceptionStatus.SecureChannelFailure) return "TLS 握手失败";
            if (ex.Status == WebExceptionStatus.NameResolutionFailure) return "DNS 解析失败";
            if (ex.Status == WebExceptionStatus.ConnectFailure) return "无法建立连接";
            return ex.Message;
        }

        private void MarkStale()
        {
            lock (_sync) { _isStale = true; if (string.IsNullOrEmpty(_lastError)) _lastError = "刷新超时/异常"; }
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null) handler();
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
            }
            Stop();
        }
    }
}
