using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexToolsHost.Updates
{
    internal sealed class GitHubReleaseTransport : IReleaseTransport
    {
        internal const string Endpoint = "https://api.github.com/repos/zhouyangwei123/CodeX_Usage_For_Own_Hardware/releases/latest";
        internal const int MaximumResponseBytes = 256 * 1024;
        internal const int TimeoutMilliseconds = 15000;
        private readonly Version _currentVersion;
        internal GitHubReleaseTransport(Version currentVersion) { _currentVersion = currentVersion; }
        internal HttpWebRequest CreateRequest(string etag)
        {
            // The single-EXE csc build has no target-framework config and can start with only SSL3/TLS1.0.
            // Preserve OS defaults when available; otherwise add TLS1.2 like the existing quota transports.
            if (ServicePointManager.SecurityProtocol != SecurityProtocolType.SystemDefault)
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(Endpoint);
            request.Method = "GET";
            request.UserAgent = "CodeX-Usage/" + _currentVersion;
            request.Accept = "application/vnd.github+json";
            request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            request.AllowAutoRedirect = false;
            request.UseDefaultCredentials = false;
            request.Credentials = null;
            request.PreAuthenticate = false;
            request.Timeout = TimeoutMilliseconds;
            request.ReadWriteTimeout = TimeoutMilliseconds;
            request.MaximumResponseHeadersLength = 16;
            request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            string safeEtag = NormalizeETag(etag);
            if (safeEtag != null) request.Headers[HttpRequestHeader.IfNoneMatch] = safeEtag;
            return request;
        }
        internal static string NormalizeETag(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 256) return null;
            string opaque = value.StartsWith("W/", StringComparison.Ordinal) ? value.Substring(2) : value;
            if (opaque.Length < 2 || opaque[0] != '"' || opaque[opaque.Length - 1] != '"') return null;
            for (int i = 1; i < opaque.Length - 1; i++)
                if (opaque[i] < '!' || opaque[i] > '~' || opaque[i] == '"') return null;
            return value;
        }
        public async Task<ReleaseHttpResponse> GetAsync(string etag, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var request = CreateRequest(etag);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (deadline.Token.Register(request.Abort))
            {
                deadline.CancelAfter(TimeoutMilliseconds);
                try
                {
                    HttpWebResponse response;
                    try { response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false); }
                    catch (WebException ex)
                    {
                        response = ex.Response as HttpWebResponse;
                        if (response == null) throw;
                    }
                    using (response)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        string body = null;
                        if (response.StatusCode == HttpStatusCode.OK)
                        {
                            if (response.ContentLength > MaximumResponseBytes) throw new InvalidDataException("Release response exceeds limit.");
                            using (Stream stream = response.GetResponseStream())
                                body = await ReadBoundedAsync(stream, deadline.Token).ConfigureAwait(false);
                        }
                        return new ReleaseHttpResponse((int)response.StatusCode, body,
                            NormalizeETag(response.Headers[HttpResponseHeader.ETag]), ReadRetryAfter(response));
                    }
                }
                catch (Exception)
                {
                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                    if (deadline.IsCancellationRequested) throw new TimeoutException("Release check timed out.");
                    throw;
                }
            }
        }
        internal static async Task<string> ReadBoundedAsync(Stream stream, CancellationToken token)
        {
            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(chunk, 0, chunk.Length, token).ConfigureAwait(false)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (buffer.Length + count > MaximumResponseBytes) throw new InvalidDataException("Release response exceeds limit.");
                    buffer.Write(chunk, 0, count);
                }
                return new UTF8Encoding(false, true).GetString(buffer.ToArray());
            }
        }
        private static TimeSpan? ReadRetryAfter(HttpWebResponse response)
        {
            string header = response.Headers[HttpResponseHeader.RetryAfter];
            int seconds;
            DateTimeOffset when;
            if (Int32.TryParse(header, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) && seconds >= 0)
                return TimeSpan.FromSeconds(Math.Min(seconds, 43200));
            if (DateTimeOffset.TryParse(header, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out when))
                return when - DateTimeOffset.UtcNow;
            long reset;
            if ((int)response.StatusCode == 403 && Int64.TryParse(response.Headers["X-RateLimit-Reset"], out reset)
                && reset > 0 && reset < 253402300800L)
                return new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(reset) - DateTimeOffset.UtcNow;
            return null;
        }
    }
    internal sealed class UpdateTimer : IUpdateTimer
    {
        private Timer _timer;
        public void Schedule(TimeSpan dueTime, Action callback)
        {
            if (_timer == null) _timer = new Timer(delegate { callback(); }, null, dueTime, Timeout.InfiniteTimeSpan);
            else _timer.Change(dueTime, Timeout.InfiniteTimeSpan);
        }
        public void Stop() { if (_timer != null) _timer.Change(Timeout.Infinite, Timeout.Infinite); }
        public void Dispose() { if (_timer != null) { _timer.Dispose(); _timer = null; } }
    }
}
