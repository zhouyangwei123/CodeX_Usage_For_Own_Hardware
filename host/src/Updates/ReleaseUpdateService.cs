using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexToolsHost.Updates
{
    public enum UpdateCheckState { NeverChecked, Checking, UpToDate, UpdateAvailable, NoRelease, Failed, Cancelled }
    public sealed class UpdateSnapshot
    {
        public Version CurrentVersion { get; internal set; }
        public string LatestTag { get; internal set; }
        public string ReleaseUrl { get; internal set; }
        public UpdateCheckState State { get; internal set; }
        public string StatusMessage { get; internal set; }
        public DateTimeOffset? LastAttemptUtc { get; internal set; }
        public DateTimeOffset? LastSuccessUtc { get; internal set; }
        public DateTimeOffset? NextCheckUtc { get; internal set; }
        public bool AutoChecksEnabled { get; internal set; }
    }
    internal sealed class ReleaseHttpResponse
    {
        public readonly int StatusCode;
        public readonly string Body;
        public readonly string ETag;
        public readonly TimeSpan? RetryAfter;
        public ReleaseHttpResponse(int statusCode, string body, string etag, TimeSpan? retryAfter)
        { StatusCode = statusCode; Body = body; ETag = etag; RetryAfter = retryAfter; }
    }
    internal interface IReleaseTransport { Task<ReleaseHttpResponse> GetAsync(string etag, CancellationToken token); }
    internal interface IUpdateTimer : IDisposable { void Schedule(TimeSpan dueTime, Action callback); void Stop(); }
    public sealed class ReleaseUpdateService : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Version _currentVersion;
        private readonly IReleaseTransport _transport;
        private readonly IUpdateTimer _timer;
        private readonly Func<DateTimeOffset> _now;
        private readonly ReleaseCache _cache;
        private bool _started, _enabled, _disposed;
        private Task _inFlight;
        private CancellationTokenSource _cancellation;
        private ReleaseRecord _latest;
        private string _etag;
        private DateTimeOffset? _lastAttempt, _lastSuccess, _nextCheck;
        private UpdateCheckState _state;
        private string _message = "尚未检查更新";
        private int _failureCount;
        public event Action Changed;
        public UpdateSnapshot Snapshot
        {
            get
            {
                lock (_gate)
                    return new UpdateSnapshot {
                        CurrentVersion = _currentVersion, LatestTag = _latest == null ? null : _latest.Tag,
                        ReleaseUrl = _latest == null ? null : _latest.Url, State = _state, StatusMessage = _message,
                        LastAttemptUtc = _lastAttempt, LastSuccessUtc = _lastSuccess, NextCheckUtc = _nextCheck,
                        AutoChecksEnabled = _enabled
                    };
            }
        }
        public ReleaseUpdateService(Version currentVersion, string cacheDirectory)
            : this(currentVersion, cacheDirectory, new GitHubReleaseTransport(currentVersion), new UpdateTimer(), delegate { return DateTimeOffset.UtcNow; }) { }
        internal ReleaseUpdateService(Version currentVersion, string cacheDirectory, IReleaseTransport transport, IUpdateTimer timer, Func<DateTimeOffset> now)
        {
            if (currentVersion == null) throw new ArgumentNullException("currentVersion");
            if (transport == null) throw new ArgumentNullException("transport");
            if (timer == null) throw new ArgumentNullException("timer");
            if (now == null) throw new ArgumentNullException("now");
            _currentVersion = currentVersion; _transport = transport; _timer = timer; _now = now;
            _cache = new ReleaseCache(cacheDirectory);
            _cache.Load(out _latest, out _etag, out _lastSuccess);
            if (_latest != null) _message = "尚未检查更新；显示上次成功记录";
        }
        public void Start(bool enabled)
        {
            lock (_gate)
            {
                if (_disposed || _started) return;
                _started = true; _enabled = enabled;
                if (enabled && _inFlight == null) ScheduleLocked(TimeSpan.FromSeconds(30));
            }
            NotifyChanged();
        }
        public void SetEnabled(bool enabled)
        {
            CancellationTokenSource cancellation = null;
            lock (_gate)
            {
                if (_disposed || _enabled == enabled) return;
                _enabled = enabled;
                if (!enabled)
                {
                    _timer.Stop(); _nextCheck = null; cancellation = _cancellation;
                }
                else if (_started && _inFlight == null) ScheduleLocked(TimeSpan.FromSeconds(30));
            }
            Cancel(cancellation);
            NotifyChanged();
        }
        public Task CheckAsync() { return BeginCheck(false); }
        private Task BeginCheck(bool automatic)
        {
            CancellationTokenSource cancellation;
            TaskCompletionSource<bool> completion;
            string etag;
            ReleaseRecord cached;
            lock (_gate)
            {
                if (_disposed || (automatic && (!_started || !_enabled))) return Task.FromResult(false);
                if (_inFlight != null) return _inFlight;
                completion = new TaskCompletionSource<bool>(); _inFlight = completion.Task;
                cancellation = new CancellationTokenSource(); _cancellation = cancellation;
                _timer.Stop(); _nextCheck = null;
                _lastAttempt = _now(); _state = UpdateCheckState.Checking; _message = "正在检查更新…";
                etag = _etag; cached = _latest;
            }
            NotifyChanged();
            Task.Run(delegate { return RunCheckAsync(cancellation, completion, etag, cached); });
            return completion.Task;
        }
        private async Task RunCheckAsync(CancellationTokenSource cancellation, TaskCompletionSource<bool> completion, string etag, ReleaseRecord cached)
        {
            ReleaseRecord release = null;
            string newEtag = null;
            bool success = false, failed = false;
            TimeSpan? retryAfter = null;
            UpdateCheckState state = UpdateCheckState.Failed;
            string message = "检查失败；请稍后重试";
            try
            {
                ReleaseHttpResponse response = await _transport.GetAsync(etag, cancellation.Token).ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
                if (response == null) throw new InvalidDataException("Missing response.");
                retryAfter = response.RetryAfter;
                if (response.StatusCode == 304)
                {
                    if (cached == null || etag == null) throw new InvalidDataException("304 without a cached release.");
                    release = cached; newEtag = etag; success = true;
                }
                else if (response.StatusCode == 200)
                {
                    release = ReleaseRecord.Parse(response.Body);
                    if (release != null) { success = true; newEtag = GitHubReleaseTransport.NormalizeETag(response.ETag); }
                    else { state = UpdateCheckState.NoRelease; message = "暂无可用的正式版本（草稿或预发布版本已排除）"; }
                }
                else if (response.StatusCode == 404)
                {
                    state = UpdateCheckState.NoRelease; message = "未找到公开正式版本（HTTP 404）；无法判断是否最新";
                }
                else
                {
                    failed = true;
                    message = response.StatusCode == 403 || response.StatusCode == 429
                        ? "GitHub 暂时限制请求；将稍后重试"
                        : "检查失败（HTTP " + response.StatusCode.ToString(CultureInfo.InvariantCulture) + "）；保留上次成功记录";
                }
                if (success)
                {
                    bool newer = release.Version.CompareTo(ReleaseVersion.FromVersion(_currentVersion)) > 0;
                    state = newer ? UpdateCheckState.UpdateAvailable : UpdateCheckState.UpToDate;
                    message = newer ? "发现新版本 " + release.Tag : "当前已是最新正式版本";
                }
            }
            catch (OperationCanceledException)
            { state = UpdateCheckState.Cancelled; message = "检查已取消；保留上次成功记录"; }
            catch (TimeoutException)
            { failed = true; message = "检查超时（15 秒）；保留上次成功记录"; }
            catch (Exception)
            { failed = true; message = "检查失败：网络或发布数据无效；保留上次成功记录"; }

            DateTimeOffset? savedAt = null;
            lock (_gate)
            {
                if (!_disposed)
                {
                    if (cancellation.IsCancellationRequested)
                    { state = UpdateCheckState.Cancelled; message = "检查已取消；保留上次成功记录"; success = false; failed = false; }
                    _state = state; _message = message;
                    if (success)
                    { _latest = release; _etag = newEtag; _lastSuccess = _now(); savedAt = _lastSuccess; }
                    _failureCount = failed ? Math.Min(_failureCount + 1, 7) : 0;
                    if (_started && _enabled)
                    {
                        TimeSpan delay = failed ? TimeSpan.FromMinutes(Math.Min(720, 15 * (1 << (_failureCount - 1)))) : TimeSpan.FromHours(12);
                        // Disable cancels the request. If enabled again before it finishes, honor the enable delay.
                        if (state == UpdateCheckState.Cancelled) delay = TimeSpan.FromSeconds(30);
                        if (failed && retryAfter.HasValue && retryAfter.Value > delay)
                            delay = retryAfter.Value > TimeSpan.FromHours(12) ? TimeSpan.FromHours(12) : retryAfter.Value;
                        ScheduleLocked(delay);
                    }
                }
            }
            if (savedAt.HasValue) _cache.Save(release, newEtag, savedAt.Value);
            lock (_gate) { _inFlight = null; _cancellation = null; }
            cancellation.Dispose();
            NotifyChanged();
            completion.TrySetResult(true);
        }
        private void ScheduleLocked(TimeSpan delay)
        {
            _nextCheck = _now().Add(delay);
            _timer.Schedule(delay, delegate { BeginCheck(true); });
        }
        private void NotifyChanged()
        {
            Action changed;
            lock (_gate) { if (_disposed) return; changed = Changed; }
            if (changed == null) return;
            foreach (Action subscriber in changed.GetInvocationList())
            {
                lock (_gate) { if (_disposed) return; }
                try { subscriber(); } catch (Exception) { /* A UI subscriber must not stop the update worker. */ }
            }
        }
        private static void Cancel(CancellationTokenSource cancellation)
        {
            if (cancellation == null) return;
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
        public void Dispose()
        {
            CancellationTokenSource cancellation;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true; _enabled = false; _nextCheck = null;
                _timer.Dispose(); cancellation = _cancellation; Changed = null;
            }
            Cancel(cancellation);
        }
    }
    internal sealed class ReleaseRecord
    {
        internal readonly string Tag, Url;
        internal readonly ReleaseVersion Version;
        internal ReleaseRecord(string tag, string url, ReleaseVersion version) { Tag = tag; Url = url; Version = version; }
        internal static ReleaseRecord Parse(string json)
        {
            if (String.IsNullOrEmpty(json) || json.Length > GitHubReleaseTransport.MaximumResponseBytes) throw new InvalidDataException("Invalid release response size.");
            var serializer = new JavaScriptSerializer { MaxJsonLength = GitHubReleaseTransport.MaximumResponseBytes, RecursionLimit = 32 };
            var data = serializer.DeserializeObject(json) as IDictionary<string, object>;
            object draft, prerelease, tagObject, urlObject;
            if (data == null || !data.TryGetValue("draft", out draft) || !(draft is bool)
                || !data.TryGetValue("prerelease", out prerelease) || !(prerelease is bool)
                || !data.TryGetValue("tag_name", out tagObject) || !(tagObject is string)
                || !data.TryGetValue("html_url", out urlObject) || !(urlObject is string)) throw new InvalidDataException("Invalid release fields.");
            if ((bool)draft || (bool)prerelease) return null;
            ReleaseVersion version;
            string tag = (string)tagObject;
            if (!ReleaseVersion.TryParse(tag, out version)) throw new InvalidDataException("Invalid release version.");
            if (version.IsPrerelease) return null;
            Uri url;
            if (!ReleaseLink.TryGet((string)urlObject, tag, out url)) throw new InvalidDataException("Invalid release page.");
            return new ReleaseRecord(tag, url.AbsoluteUri, version);
        }
    }
    internal sealed class ReleaseCache
    {
        private readonly string _path;
        internal ReleaseCache(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory)) return;
            try { _path = Path.Combine(directory, "release-update-cache.json"); } catch (ArgumentException) { }
        }
        internal void Load(out ReleaseRecord release, out string etag, out DateTimeOffset? successful)
        {
            release = null; etag = null; successful = null;
            if (_path == null) return;
            try
            {
                var info = new FileInfo(_path);
                if (!info.Exists || info.Length > 16384) return;
                string json = File.ReadAllText(_path);
                if (json.Length > 16384) return;
                var data = new JavaScriptSerializer { MaxJsonLength = 16384, RecursionLimit = 8 }.DeserializeObject(json) as IDictionary<string, object>;
                object tag, url, stamp, entityTag;
                if (data == null || !data.TryGetValue("tag", out tag) || !(tag is string)
                    || !data.TryGetValue("url", out url) || !(url is string)
                    || !data.TryGetValue("successfulUtc", out stamp) || !(stamp is string)) return;
                ReleaseVersion version;
                Uri validated;
                DateTimeOffset time;
                if (!ReleaseVersion.TryParse((string)tag, out version) || version.IsPrerelease
                    || !ReleaseLink.TryGet((string)url, (string)tag, out validated)
                    || !DateTimeOffset.TryParseExact((string)stamp, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time)) return;
                release = new ReleaseRecord((string)tag, validated.AbsoluteUri, version); successful = time;
                if (data.TryGetValue("etag", out entityTag)) etag = GitHubReleaseTransport.NormalizeETag(entityTag as string);
            }
            catch (Exception) { /* Cache is optional; remote checks remain available. */ }
        }
        internal void Save(ReleaseRecord release, string etag, DateTimeOffset successful)
        {
            if (_path == null || release == null) return;
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                var values = new Dictionary<string, object> {
                    { "tag", release.Tag }, { "url", release.Url }, { "etag", etag },
                    { "successfulUtc", successful.ToString("O", CultureInfo.InvariantCulture) }
                };
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(values), new System.Text.UTF8Encoding(false));
                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
            }
            catch (Exception) { /* A read-only or unavailable cache must not mask a valid remote result. */ }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception) { } }
        }
    }
}
