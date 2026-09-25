using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
namespace CodexToolsHost.Usage
{
    internal sealed class UsageScanner
    {
        // A refresh is bounded; subsequent refreshes continue the unfinished scan.
        internal const long RefreshByteBudget = 32L * 1024 * 1024;
        private const int MaxLineBytes = 2 * 1024 * 1024;
        private readonly string home;
        private readonly Dictionary<string, FileState> files = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        private List<string> knownPaths;
        private DateTime nextDiscovery;
        private UsageReport lastReport;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = MaxLineBytes, RecursionLimit = 48 };
        private sealed class Counts
        {
            internal long Input, Cache, Output, Reasoning;
            internal string Key { get { return Input + ":" + Cache + ":" + Output + ":" + Reasoning; } }
            internal Counts Add(Counts b) { return new Counts { Input = checked(Input + b.Input), Cache = checked(Cache + b.Cache), Output = checked(Output + b.Output), Reasoning = checked(Reasoning + b.Reasoning) }; }
        }
        private sealed class TokenEvent
        {
            internal DateTimeOffset Time;
            internal Counts Total, Last;
            internal string Model;
            internal bool InheritedContext;
        }
        private sealed class FileState
        {
            internal string Id, Parent, Model = "未知模型";
            internal DateTimeOffset Created;
            internal DateTime Modified;
            internal long Offset, Length;
            internal bool Archive, Oversize, InheritedContext, Partial;
            internal string Anchor;
            internal List<TokenEvent> Events = new List<TokenEvent>();
            internal HashSet<string> Warnings = new HashSet<string>();
        }
        public UsageScanner(string home) { this.home = home; }
        public UsageReport Scan(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            HashSet<string> warnings = new HashSet<string>();
            bool changed = lastReport == null;
            if (knownPaths == null || DateTime.UtcNow >= nextDiscovery)
            {
                List<string> discovered = new List<string>();
                foreach (string folder in new[] { "sessions", "archived_sessions" })
                    Discover(Path.Combine(home, folder), discovered, warnings, cancellation);
                changed = changed || knownPaths == null || !new HashSet<string>(knownPaths, StringComparer.OrdinalIgnoreCase).SetEquals(discovered);
                knownPaths = discovered; nextDiscovery = DateTime.UtcNow.AddSeconds(60);
            }
            List<string> paths = knownPaths;
            HashSet<string> present = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            foreach (string old in files.Keys.Where(x => !present.Contains(x)).ToArray()) { files.Remove(old); changed = true; }
            long read = 0;
            foreach (string path in paths.OrderBy(x => files.ContainsKey(x) ? 1 : 0).ThenBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    FileInfo info = new FileInfo(path);
                    if (!info.Exists) continue; // Keep the prior snapshot until the next discovery resolves an archive move.
                    FileState state;
                    bool exists = files.TryGetValue(path, out state);
                    bool modified = !exists || info.LastWriteTimeUtc != state.Modified || info.Length != state.Length;
                    if (!exists || info.Length < state.Offset ||
                        (info.Length == state.Offset && info.LastWriteTimeUtc != state.Modified) ||
                        (modified && state.Offset > 0 && state.Anchor != null && Anchor(path, state.Offset, ref read) != state.Anchor))
                    {
                        state = new FileState { Archive = path.StartsWith(Path.Combine(home, "archived_sessions") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) };
                        files[path] = state; changed = true;
                    }
                    state.Length = info.Length;
                    if (state.Offset < info.Length && read < RefreshByteBudget && (!state.Partial || modified))
                    { Read(path, state, ref read, cancellation); state.Anchor = Anchor(path, state.Offset, ref read); changed = true; }
                    state.Modified = info.LastWriteTimeUtc;
                }
                catch (IOException) { warnings.Add("部分日志正在移动或无法读取，下次刷新重试。"); }
                catch (UnauthorizedAccessException) { warnings.Add("部分日志无读取权限，覆盖范围不完整。"); }
            }
            cancellation.ThrowIfCancellationRequested();
            int pending = files.Values.Count(x => x.Offset < x.Length && !x.Partial);
            if (!changed && lastReport != null && warnings.Count == 0)
                return new UsageReport { Rows = lastReport.Rows, Warnings = lastReport.Warnings, UpdatedAt = DateTimeOffset.Now,
                    FilesDiscovered = paths.Count, FilesPending = pending, BytesRead = read };
            if (pending > 0) warnings.Add("首次读取进行中；当前为已扫描部分，将自动继续。");
            if (files.Count == 0) warnings.Add("未找到本机 sessions / archived_sessions 日志。");
            List<FileState> canonical = files.Values.Where(x => x.Id != null)
                .GroupBy(x => x.Id, StringComparer.Ordinal).Select(x => SelectCopy(x, warnings)).ToList();
            Dictionary<string, FileState> sessions = canonical.ToDictionary(x => x.Id, StringComparer.Ordinal);
            List<UsageRow> rows = new List<UsageRow>();
            foreach (FileState state in files.Values)
            {
                warnings.UnionWith(state.Warnings);
                if (state.Partial || state.Oversize) warnings.Add("日志末行尚未写完，等待下次刷新。");
                if (state.Id == null && state.Events.Count > 0) warnings.Add("有用量缺少会话标识，已排除以免重复计数。");
            }
            foreach (FileState state in canonical)
            {
                cancellation.ThrowIfCancellationRequested();
                FileState parent = null;
                if (state.Parent != null && !sessions.TryGetValue(state.Parent, out parent)) warnings.Add("部分分叉缺少父会话；无法验证继承历史，结果可能不完整。");
                List<TokenEvent> inheritedCandidates = parent == null ? new List<TokenEvent>() : parent.Events.Where(x => state.Created == default(DateTimeOffset) || x.Time <= state.Created).ToList();
                Counts baseline = new Counts();
                int parentIndex = 0;
                bool prefix = state.Parent != null;
                HashSet<string> seenLast = new HashSet<string>();
                foreach (TokenEvent item in state.Events)
                {
                    Counts delta;
                    if (item.Total != null)
                    {
                        if (item.Total.Input < baseline.Input || item.Total.Output < baseline.Output || item.Total.Cache < baseline.Cache || item.Total.Reasoning < baseline.Reasoning)
                        {
                            warnings.Add("检测到累计计数回退；仅采纳有效的本次用量，其余不作估算。");
                            if (item.Last == null) continue;
                            delta = item.Last;
                        }
                        else delta = new Counts { Input = item.Total.Input - baseline.Input, Cache = item.Total.Cache - baseline.Cache,
                            Output = item.Total.Output - baseline.Output, Reasoning = item.Total.Reasoning - baseline.Reasoning };
                        baseline = item.Total;
                    }
                    else
                    {
                        warnings.Add("部分事件仅有本次计数，按时间与计数去重，精度受日志格式限制。");
                        delta = item.Last;
                        if (!seenLast.Add(item.Time.ToString("o") + ":" + delta.Key)) continue;
                        baseline = baseline.Add(delta);
                    }
                    bool inherited = item.InheritedContext || (state.Parent != null && item.Time < state.Created);
                    if (prefix && parent != null)
                    {
                        // Match the leading cumulative history, including timestamps rewritten on a fork.
                        int matched = -1;
                        for (int i = parentIndex; i < inheritedCandidates.Count; i++)
                        {
                            TokenEvent candidate = inheritedCandidates[i];
                            if (item.Total != null && candidate.Total != null && item.Total.Key == candidate.Total.Key) { matched = i; break; }
                            if (item.Total == null && i == parentIndex && candidate.Total == null && item.Last.Key == candidate.Last.Key) { matched = i; break; }
                        }
                        if (matched >= 0) { inherited = true; parentIndex = matched + 1; }
                        else if (delta.Input != 0 || delta.Output != 0) prefix = false;
                    }
                    else if (prefix && parent == null && !inherited)
                    {
                        // An unanchored first cumulative snapshot may be inherited. Use it only as a baseline.
                        inherited = item.Total != null; prefix = false;
                    }
                    if (inherited || (delta.Input == 0 && delta.Output == 0)) continue;
                    if (delta.Cache > delta.Input || delta.Reasoning > delta.Output)
                    { warnings.Add("Token 子项与总项不一致，已限制缓存/推理子项。"); delta.Cache = Math.Min(delta.Cache, delta.Input); delta.Reasoning = Math.Min(delta.Reasoning, delta.Output); }
                    rows.Add(new UsageRow { Day = item.Time.LocalDateTime.Date, LastActivity = item.Time, SessionId = state.Id,
                        Model = item.Model, InputTokens = delta.Input, CachedInputTokens = delta.Cache, OutputTokens = delta.Output, ReasoningOutputTokens = delta.Reasoning });
                }
            }
            List<UsageRow> grouped = rows.GroupBy(x => new { x.Day, x.SessionId, x.Model }).Select(x => UsageReport.Sum(x, x.Key.SessionId, x.Key.Model))
                .OrderByDescending(x => x.LastActivity).ToList();
            // Always label the data boundary: this is observable local history, not account-wide usage.
            warnings.Add("仅覆盖本机可读 Token 日志；删除、旧版无计数及其他设备的历史不在内。费用未知，未计入订阅额度。");
            lastReport = new UsageReport { Rows = grouped.AsReadOnly(), Warnings = warnings.OrderBy(x => x).ToList().AsReadOnly(), UpdatedAt = DateTimeOffset.Now,
                BytesRead = read, FilesDiscovered = paths.Count, FilesPending = pending };
            return lastReport;
        }
        private static FileState SelectCopy(IEnumerable<FileState> copies, HashSet<string> warnings)
        {
            // Prefer active on a conflict, but an archive may be the verified complete copy after a move.
            List<FileState> ordered = copies.OrderBy(x => x.Archive).ThenByDescending(x => x.Events.Count).ToList();
            FileState chosen = ordered[0];
            foreach (FileState candidate in ordered.Skip(1))
            {
                if (IsEventPrefix(chosen, candidate)) chosen = candidate.Events.Count > chosen.Events.Count ? candidate : chosen;
                else if (!IsEventPrefix(candidate, chosen))
                    warnings.Add("同一会话存在不一致副本，已保留单个副本且未相加；无法确认完整用量。");
            }
            return chosen;
        }
        private static bool IsEventPrefix(FileState prefix, FileState complete)
        {
            if (prefix.Events.Count > complete.Events.Count || prefix.Parent != complete.Parent || prefix.Created != complete.Created) return false;
            for (int i = 0; i < prefix.Events.Count; i++)
            {
                TokenEvent left = prefix.Events[i], right = complete.Events[i];
                if (left.Time != right.Time || left.Model != right.Model || left.InheritedContext != right.InheritedContext ||
                    !SameCounts(left.Total, right.Total) || !SameCounts(left.Last, right.Last)) return false;
            }
            return true;
        }
        private static bool SameCounts(Counts left, Counts right)
        {
            if (left == null || right == null) return left == right;
            return left.Input == right.Input && left.Cache == right.Cache && left.Output == right.Output && left.Reasoning == right.Reasoning;
        }
        private static void Discover(string directory, List<string> paths, HashSet<string> warnings, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory)) return;
            try
            {
                foreach (string file in Directory.GetFiles(directory, "*.jsonl"))
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) paths.Add(file);
                foreach (string child in Directory.GetDirectories(directory))
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) Discover(child, paths, warnings, cancellation);
            }
            catch (IOException) { warnings.Add("部分日志目录暂不可读取。"); }
            catch (UnauthorizedAccessException) { warnings.Add("部分日志目录无读取权限。"); }
        }
        private void Read(string path, FileState state, ref long bytesRead, CancellationToken cancellation)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (MemoryStream pending = new MemoryStream())
            {
                stream.Position = state.Offset;
                state.Partial = false;
                byte[] buffer = new byte[65536];
                try
                {
                    while (bytesRead < RefreshByteBudget)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        int n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, RefreshByteBudget - bytesRead));
                        if (n == 0) break;
                        for (int i = 0; i < n; i++)
                        {
                            if (buffer[i] == 10)
                            {
                                if (!state.Oversize) Parse(Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)pending.Length).TrimStart('\uFEFF'), state);
                                Array.Clear(pending.GetBuffer(), 0, (int)pending.Length); pending.SetLength(0); state.Oversize = false;
                            }
                            else if (!state.Oversize)
                            {
                                if (pending.Length >= MaxLineBytes)
                                { Array.Clear(pending.GetBuffer(), 0, (int)pending.Length); pending.SetLength(0); state.Oversize = true; state.Warnings.Add("超长日志行已跳过；若包含统计事件可能造成缺口。"); }
                                else pending.WriteByte(buffer[i]);
                            }
                        }
                        state.Offset += n; bytesRead += n;
                    }
                }
                finally
                {
                    // Keep only the offset of an unfinished line, never a prompt/body fragment between refreshes.
                    state.Partial = (pending.Length > 0 || state.Oversize) && state.Offset >= state.Length;
                    state.Offset -= pending.Length;
                    Array.Clear(pending.GetBuffer(), 0, (int)pending.Length);
                    Array.Clear(buffer, 0, buffer.Length);
                }
            }
        }
        private static string Anchor(string path, long offset, ref long bytesRead)
        {
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (SHA256 hash = SHA256.Create())
            {
                if (offset > file.Length) return "changed";
                int size = (int)Math.Min(256, offset);
                byte[] bytes = new byte[size];
                file.Position = offset - size;
                int read = file.Read(bytes, 0, size); bytesRead += read;
                string result = Convert.ToBase64String(hash.ComputeHash(bytes, 0, read));
                Array.Clear(bytes, 0, bytes.Length); return result;
            }
        }
        private void Parse(string line, FileState state)
        {
            // Fast filter: prompts, tool outputs and ordinary message content are never deserialized or retained.
            if (line.IndexOf("\"session_meta\"", StringComparison.Ordinal) < 0 && line.IndexOf("\"turn_context\"", StringComparison.Ordinal) < 0 && line.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0) return;
            try
            {
                Dictionary<string, object> value = json.DeserializeObject(line) as Dictionary<string, object>;
                Dictionary<string, object> payload = Object(value, "payload");
                string type = Text(value, "type");
                if (payload == null) return;
                if (type == "session_meta")
                {
                    if (state.Id != null) return;
                    state.Id = SafeLabel(Text(payload, "id"), 100);
                    state.Parent = Text(payload, "forked_from_id");
                    if (string.IsNullOrEmpty(state.Parent)) state.Parent = Text(Object(Object(Object(payload, "source"), "subagent"), "thread_spawn"), "parent_thread_id");
                    DateTimeOffset.TryParse(Text(payload, "timestamp") ?? Text(value, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out state.Created);
                }
                else if (type == "turn_context")
                {
                    state.Model = SafeLabel(Text(payload, "model"), 100) ?? "未知模型";
                    string thread = Text(payload, "thread_id");
                    state.InheritedContext = thread != null && state.Id != null && thread != state.Id;
                }
                else if (type == "event_msg" && Text(payload, "type") == "token_count")
                {
                    Dictionary<string, object> info = Object(payload, "info");
                    if (info == null) return; // quota-only events are intentionally ignored.
                    Counts total = ReadCounts(Object(info, "total_token_usage"));
                    Counts last = ReadCounts(Object(info, "last_token_usage"));
                    if (total == null && last == null) return;
                    DateTimeOffset time;
                    if (!DateTimeOffset.TryParse(Text(value, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time)) throw new FormatException();
                    state.Events.Add(new TokenEvent { Time = time, Total = total, Last = last, Model = state.Model, InheritedContext = state.InheritedContext });
                    if (state.Model == "未知模型") state.Warnings.Add("部分用量缺少模型标记，保留为未知模型。");
                }
            }
            catch (ArgumentException) { state.Warnings.Add("存在无法解析的统计事件，已跳过。"); }
            catch (InvalidOperationException) { state.Warnings.Add("存在无法解析的统计事件，已跳过。"); }
            catch (FormatException) { state.Warnings.Add("存在非法 Token 计数或时间，已跳过。"); }
            catch (OverflowException) { state.Warnings.Add("存在超出范围的 Token 计数，已跳过。"); }
        }
        private static Counts ReadCounts(Dictionary<string, object> value)
        {
            if (value == null) return null;
            Counts result = new Counts { Input = Number(value, "input_tokens", true), Cache = Number(value, "cached_input_tokens", false),
                Output = Number(value, "output_tokens", true), Reasoning = Number(value, "reasoning_output_tokens", false) };
            if (result.Cache > result.Input || result.Reasoning > result.Output) throw new FormatException();
            long sum = checked(result.Input + result.Output);
            if (value.ContainsKey("total_tokens")) { long total = Number(value, "total_tokens", false); if (total != 0 && total != sum) throw new FormatException(); }
            return result;
        }
        internal static long Number(Dictionary<string, object> value, string key, bool required)
        {
            object raw;
            if (value == null || !value.TryGetValue(key, out raw) || raw == null) { if (required) throw new FormatException(); return 0; }
            long number;
            if (raw is bool || !long.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out number) || number < 0 || number > 1000000000000000L) throw new FormatException();
            return number;
        }
        internal static Dictionary<string, object> Object(Dictionary<string, object> value, string key)
        { object raw; return value != null && value.TryGetValue(key, out raw) ? raw as Dictionary<string, object> : null; }
        internal static string Text(Dictionary<string, object> value, string key)
        { object raw; return value != null && value.TryGetValue(key, out raw) ? raw as string : null; }
        internal static string SafeLabel(string value, int limit)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return new string(value.Where(c => !char.IsControl(c)).Take(limit).ToArray());
        }
    }
}
