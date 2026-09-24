using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodexToolsHost.Model;

namespace CodexToolsHost.Quota
{
    /// <summary>
    /// 桌面版 Codex 真实运行状态监视器。
    /// 桌面 app-server（WindowsApps 里的 codex.exe）通过 SQLite 结构化日志
    /// （~/.codex/logs_*.sqlite）记录 app-server 事件（item/started、item/reasoning/textDelta、
    /// guardianWarning、item/completed 等）。本组件以只读方式轮询该日志库，
    /// 推导 IDLE/RUNNING(思考/运行)/WAITING(等待答复)/ERROR/COMPLETE/OFFLINE 状态，
    /// 与用户实际会话（无论官方模型还是第三方 API）保持一致。
    /// 额度仍由独立 CodexStatusProvider（app-server JSON-RPC）提供。
    /// </summary>
    public sealed class DesktopLogStatusMonitor : ICodexStatusSource, IDisposable
    {
        public const int CodexStateOffline = 0;
        public const int CodexStateIdle = 1;
        public const int CodexStateRunning = 2;
        public const int CodexStateWaiting = 3;
        public const int CodexStateError = 4;
        public const int CodexStateComplete = 5;

        private const int SqliteOk = 0;
        private const int SqliteRow = 100;
        private const int SqliteDone = 101;
        private const int SqliteOpenReadOnly = 0x00000001;
        private const int SqliteOpenUri = 0x00000040;
        internal static readonly IntPtr SqliteTransient = new IntPtr(-1);
        private const string OutgoingEventTarget = "codex_app_server::outgoing_message";

        private const int RunningIdleTimeoutMs = 60000;
        private const int CompleteHoldMs = 6000;
        private const int UuidRefreshMs = 30000;

        private readonly string _codexHome;
        private readonly object _sync = new object();
        private Timer _timer;
        private bool _started;
        private bool _disposed;
        private int _polling;

        private string _logDb;
        private string _activeDb;
        private long _lastRowId;
        private string _desktopUuid;
        private int _desktopPid;
        private DateTime _uuidCheckedAt;
        private int _offlineStreak;

        private int _state = CodexStateOffline;
        private string _statusText = "connecting...";
        private DateTime _lastEventUtc = DateTime.UtcNow;
        private DateTime _completeAtUtc = DateTime.MinValue;
        private int _activeItems;

        public DesktopLogStatusMonitor()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _codexHome = Path.Combine(home, ".codex");
        }

        public event Action Changed;
        public event Action<string> StatusChanged;

        public QuotaSnapshot Quota { get { return QuotaSnapshot.EmptyStale(); } }
        public int State { get { lock (_sync) { return _state; } } }
        public string StatusText { get { lock (_sync) { return _statusText; } } }

        public Task StartAsync()
        {
            lock (_sync)
            {
                if (_started || _disposed) return Task.FromResult(true);
                _started = true;
            }
            PollOnce();
            _timer = new Timer(delegate
            {
                if (Interlocked.Exchange(ref _polling, 1) == 0)
                {
                    try { PollOnce(); }
                    catch (Exception) { }
                    finally { Interlocked.Exchange(ref _polling, 0); }
                }
            }, null, 1000, 1000);
            return Task.FromResult(true);
        }

        public Task RefreshQuotaAsync()
        {
            return Task.FromResult(true);
        }

        public void Stop()
        {
            lock (_sync)
            {
                _started = false;
                if (_timer != null) { _timer.Dispose(); _timer = null; }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
        }

        /* ---------------- 轮询 ---------------- */

        private void PollOnce()
        {
            string db = FindLogDb();
            _logDb = db;
            if (db == null)
            {
                SetState(CodexStateOffline, "NO LOG DB");
                return;
            }
            lock (_sync)
            {
                if (!string.Equals(_activeDb, db, StringComparison.OrdinalIgnoreCase))
                {
                    /* 日志库发生轮转/切换：重置游标，避免旧 id 基线导致新库事件被跳过 */
                    _activeDb = db;
                    _lastRowId = 0;
                }
            }

            EnsureDesktopUuid(db);
            if (_desktopUuid == null)
            {
                _offlineStreak++;
                if (_offlineStreak >= 2)
                    SetState(CodexStateOffline, "DESKTOP OFF");
                return;
            }
            _offlineStreak = 0;

            ProcessNewRows(db);
            ApplyTimeout();
        }

        private string FindLogDb()
        {
            try
            {
                if (!Directory.Exists(_codexHome)) return null;
                string best = null;
                DateTime bestTime = DateTime.MinValue;
                foreach (string file in Directory.GetFiles(_codexHome, "logs_*.sqlite"))
                {
                    try
                    {
                        DateTime t = File.GetLastWriteTimeUtc(file);
                        if (t > bestTime) { bestTime = t; best = file; }
                    }
                    catch (Exception) { }
                }
                return best;
            }
            catch (Exception) { return null; }
        }

        private void EnsureDesktopUuid(string db)
        {
            DateTime now = DateTime.UtcNow;
            lock (_sync)
            {
                if (_desktopUuid != null
                    && _desktopPid > 0
                    && now - _uuidCheckedAt < TimeSpan.FromMilliseconds(UuidRefreshMs))
                    return;
                _uuidCheckedAt = now;
            }

            string uuid = FindDesktopProcessUuid(db);
            lock (_sync)
            {
                bool processChanged = !string.Equals(_desktopUuid, uuid, StringComparison.Ordinal);
                _desktopUuid = uuid;
                if (uuid == null) _desktopPid = 0;
                if (processChanged)
                {
                    /* 新 app-server 进程不能继承旧进程的游标或活动计数。 */
                    _lastRowId = 0;
                    _activeItems = 0;
                    _lastEventUtc = now;
                    _completeAtUtc = DateTime.MinValue;
                }
            }
            if (uuid == null && _state != CodexStateOffline)
                SetState(CodexStateOffline, "DESKTOP OFF");
        }

        /* 从当前进程列表定位桌面 app-server，再读取其最新 uuid。
           避免旧实现每 30 秒 GROUP BY 扫描整张百 MB 日志表。 */
        private string FindDesktopProcessUuid(string db)
        {
            Tuple<string, long> best = null;
            Process[] processes;
            try { processes = Process.GetProcessesByName("codex"); }
            catch (Exception) { return null; }
            foreach (Process process in processes)
            {
                try
                {
                    int pid = process.Id;
                    if (!IsDesktopCodexProcess(pid)) continue;
                    var rows = QueryStrings(db,
                        "SELECT process_uuid, id FROM logs WHERE process_uuid LIKE ? ORDER BY id DESC LIMIT 1",
                        delegate(SqliteReader r)
                        {
                            return Tuple.Create(r.GetString(0), r.GetInt64(1));
                        }, "pid:" + pid + ":%");
                    if (rows.Count > 0 && (best == null || rows[0].Item2 > best.Item2))
                    {
                        best = rows[0];
                        lock (_sync) { _desktopPid = pid; }
                    }
                }
                catch (Exception) { }
                finally { process.Dispose(); }
            }
            return best == null ? null : best.Item1;
        }

        private static int ParsePid(string processUuid)
        {
            if (string.IsNullOrEmpty(processUuid) || !processUuid.StartsWith("pid:", StringComparison.Ordinal))
                return 0;
            int end = processUuid.IndexOf(':', 4);
            string pidText = end < 0 ? processUuid.Substring(4) : processUuid.Substring(4, end - 4);
            int pid;
            return int.TryParse(pidText, out pid) ? pid : 0;
        }

        private static bool IsDesktopCodexProcess(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    if (!p.ProcessName.Equals("codex", StringComparison.OrdinalIgnoreCase)) return false;
                    string path = p.MainModule == null ? "" : p.MainModule.FileName;
                    return path.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0
                        && path.IndexOf("OpenAI.Codex", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch (Exception) { return false; }
        }

        private void ProcessNewRows(string db)
        {
            long lastId;
            string uuid;
            lock (_sync)
            {
                lastId = _lastRowId;
                uuid = _desktopUuid;
            }
            if (uuid == null) return;

            if (_lastRowId == 0)
            {
                /* 首次/切换库：只取当前最大 id 作为基线，不重放历史事件，
                   避免把旧会话的 guardianWarning/item 状态带到新会话 */
                long? max = QueryInt64(db,
                    "SELECT MAX(id) FROM logs WHERE process_uuid = ?", uuid);
                if (max.HasValue) _lastRowId = max.Value;
                if (State == CodexStateOffline)
                    SetState(CodexStateIdle, "IDLE");
                return;
            }

            var rows = QueryStrings(db,
                "SELECT id, ts, feedback_log_body FROM logs "
                + "WHERE id > ? AND process_uuid = ? AND target = ? "
                + "AND feedback_log_body LIKE ? ORDER BY id ASC",
                delegate(SqliteReader r)
                {
                    return new LogRow { Id = r.GetInt64(0), Ts = r.GetInt64(1), Body = r.GetString(2) };
                },
                lastId, uuid, OutgoingEventTarget, "app-server event: %");

            foreach (LogRow row in rows)
            {
                ProcessEvent(row.Body);
                _lastRowId = row.Id;
            }
            if (rows.Count == 0)
            {
                /* 日志表可能刚被清空/轮转：取当前最大 id 作为新基线 */
                var max = QueryInt64(db,
                    "SELECT MAX(id) FROM logs WHERE process_uuid = ?", uuid);
                if (max.HasValue && max.Value > lastId) _lastRowId = max.Value;
            }
        }

        private void ProcessEvent(string body)
        {
            if (string.IsNullOrEmpty(body)) return;
            const string marker = "app-server event: ";
            if (!body.StartsWith(marker, StringComparison.Ordinal)) return;
            string evt = body.Substring(marker.Length);
            int sp = evt.IndexOf(' ');
            if (sp > 0) evt = evt.Substring(0, sp);

            DateTime now = DateTime.UtcNow;
            switch (evt)
            {
                case "item/started":
                    _lastEventUtc = now;
                    _activeItems++;
                    SetState(CodexStateRunning, "RUN");
                    break;
                case "item/completed":
                    _lastEventUtc = now;
                    if (_activeItems > 0) _activeItems--;
                    if (_activeItems == 0)
                    {
                        SetState(CodexStateComplete, "DONE");
                        _completeAtUtc = now;
                    }
                    break;
                case "turn/started":
                    _lastEventUtc = now;
                    _activeItems = 0;
                    SetState(CodexStateRunning, "RUN");
                    break;
                case "turn/completed":
                    /* turn/completed 是权威终态，必须覆盖丢失 item/completed 造成的残留计数。 */
                    _lastEventUtc = now;
                    _activeItems = 0;
                    SetState(CodexStateComplete, "DONE");
                    _completeAtUtc = now;
                    break;
                case "thread/status/changed":
                    /* 日志只记录 targeted_connections，不含真实状态，不能据此推导 RUN。 */
                    break;
                case "item/autoApprovalReview/started":
                    _lastEventUtc = now;
                    SetState(CodexStateRunning, "RUN");
                    break;
                case "item/autoApprovalReview/completed":
                    _lastEventUtc = now;
                    if (_activeItems > 0) SetState(CodexStateRunning, "RUN");
                    break;
                case "item/reasoning/textDelta":
                case "item/reasoning/summaryTextDelta":
                    _lastEventUtc = now;
                    SetState(CodexStateRunning, "THINKING");
                    break;
                case "item/agentMessage/delta":
                    _lastEventUtc = now;
                    SetState(CodexStateRunning, "RESPOND");
                    break;
                case "item/commandExecution/outputDelta":
                case "item/fileChange/patchUpdated":
                    _lastEventUtc = now;
                    SetState(CodexStateRunning, "TOOL");
                    break;
                case "guardianWarning":
                    _lastEventUtc = now;
                    SetState(CodexStateWaiting, "WAIT APPROVAL");
                    break;
                case "warning":
                    _lastEventUtc = now;
                    if (State != CodexStateWaiting)
                        SetState(CodexStateWaiting, "WAIT");
                    break;
                case "error":
                    _lastEventUtc = now;
                    _activeItems = 0;
                    SetState(CodexStateError, "ERROR");
                    break;
                case "thread/closed":
                    _lastEventUtc = now;
                    _activeItems = 0;
                    SetState(CodexStateComplete, "DONE");
                    _completeAtUtc = now;
                    break;
                default:
                    /* 额度、token、diff 等元数据既不改变状态，也不延长 RUN 超时。 */
                    break;
            }
        }

        private void ApplyTimeout()
        {
            DateTime now = DateTime.UtcNow;
            int state = State;
            if (state == CodexStateRunning)
            {
                if ((now - _lastEventUtc).TotalMilliseconds > RunningIdleTimeoutMs)
                    SetState(CodexStateIdle, "IDLE");
            }
            else if (state == CodexStateComplete)
            {
                if (_completeAtUtc != DateTime.MinValue && (now - _completeAtUtc).TotalMilliseconds > CompleteHoldMs)
                    SetState(CodexStateIdle, "IDLE");
            }
        }

        private void SetState(int state, string text)
        {
            bool changed;
            lock (_sync)
            {
                changed = _state != state || !string.Equals(_statusText, text, StringComparison.Ordinal);
                _state = state;
                _statusText = text;
            }
            if (changed)
            {
                var handler = StatusChanged;
                if (handler != null) handler(text);
                RaiseChanged();
            }
        }

        private void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null) handler();
        }

        /* ---------------- 极简只读 SQLite（P/Invoke winsqlite3.dll） ---------------- */

        private sealed class LogRow
        {
            public long Id;
            public long Ts;
            public string Body;
        }

        private delegate T RowReader<T>(SqliteReader reader);

        private sealed class SqliteReader
        {
            private readonly IntPtr _stmt;
            public SqliteReader(IntPtr stmt) { _stmt = stmt; }
            public long GetInt64(int col) { return sqlite3_column_int64(_stmt, col); }
            public string GetString(int col)
            {
                int len = sqlite3_column_bytes(_stmt, col);
                if (len <= 0) return "";
                IntPtr p = sqlite3_column_text(_stmt, col);
                if (p == IntPtr.Zero) return "";
                byte[] bytes = new byte[len];
                Marshal.Copy(p, bytes, 0, len);
                return Encoding.UTF8.GetString(bytes);
            }
        }

        private List<T> QueryStrings<T>(string db, string sql, RowReader<T> reader, params object[] args)
        {
            var list = new List<T>();
            IntPtr handle = IntPtr.Zero;
            int openResult = sqlite3_open_v2("file:" + db.Replace('\\', '/') + "?mode=ro",
                out handle, SqliteOpenReadOnly | SqliteOpenUri, null);
            if (openResult != SqliteOk)
            {
                if (handle != IntPtr.Zero) sqlite3_close(handle);
                return list;
            }
            try
            {
                IntPtr stmt = IntPtr.Zero;
                int prepareResult = sqlite3_prepare_v2(handle,
                    Encoding.UTF8.GetBytes(sql + "\0"), -1, out stmt, IntPtr.Zero);
                if (prepareResult != SqliteOk)
                {
                    if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
                    return list;
                }
                try
                {
                    if (args != null)
                    {
                        for (int i = 0; i < args.Length; i++)
                        {
                            if (args[i] is long || args[i] is int)
                                sqlite3_bind_int64(stmt, i + 1, Convert.ToInt64(args[i]));
                            else
                            {
                                byte[] value = Encoding.UTF8.GetBytes(
                                    Convert.ToString(args[i]) + "\0");
                                sqlite3_bind_text(stmt, i + 1, value, -1, SqliteTransient);
                            }
                        }
                    }
                    while (sqlite3_step(stmt) == SqliteRow)
                        list.Add(reader(new SqliteReader(stmt)));
                }
                finally
                {
                    if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
                }
            }
            finally
            {
                if (handle != IntPtr.Zero) sqlite3_close(handle);
            }
            return list;
        }

        private long? QueryInt64(string db, string sql, object arg)
        {
            IntPtr handle = IntPtr.Zero;
            int openResult = sqlite3_open_v2("file:" + db.Replace('\\', '/') + "?mode=ro",
                out handle, SqliteOpenReadOnly | SqliteOpenUri, null);
            if (openResult != SqliteOk)
            {
                if (handle != IntPtr.Zero) sqlite3_close(handle);
                return null;
            }
            try
            {
                IntPtr stmt = IntPtr.Zero;
                int prepareResult = sqlite3_prepare_v2(handle,
                    Encoding.UTF8.GetBytes(sql + "\0"), -1, out stmt, IntPtr.Zero);
                if (prepareResult != SqliteOk)
                {
                    if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
                    return null;
                }
                try
                {
                    if (arg is long || arg is int)
                        sqlite3_bind_int64(stmt, 1, Convert.ToInt64(arg));
                    else
                    {
                        byte[] value = Encoding.UTF8.GetBytes(Convert.ToString(arg) + "\0");
                        sqlite3_bind_text(stmt, 1, value, -1, SqliteTransient);
                    }
                    if (sqlite3_step(stmt) == SqliteRow)
                        return sqlite3_column_int64(stmt, 0);
                    return null;
                }
                finally
                {
                    if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
                }
            }
            finally
            {
                if (handle != IntPtr.Zero) sqlite3_close(handle);
            }
        }

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2(string filename, out IntPtr db, int flags, string vfs);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr db);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr stmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_column_int64(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_bytes(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] value, int n, IntPtr destructor);
    }
}
