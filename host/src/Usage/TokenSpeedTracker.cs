using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodexToolsHost.Usage
{
    public sealed class TokenSpeedSnapshot
    {
        public DateTimeOffset AsOf { get; internal set; }
        public bool IsReady { get; internal set; }
        public double? TokensPerSecond { get; internal set; }
        public int ActiveSessions { get; internal set; }
        public int MeasuredSessions { get; internal set; }
        public int WaitingSessions { get { return ActiveSessions - MeasuredSessions; } }
        public int SessionCount { get; internal set; }
        public string Status { get; internal set; }
        public string CompactText()
        {
            if (!IsReady || !TokensPerSecond.HasValue) return "--/s";
            double value = TokensPerSecond.Value;
            if (value == 0) return "0/s";
            string[] units = { "", "K", "M", "G", "T", "P", "E" }; int index = 0;
            while (value >= 1000 && index < units.Length - 1) { value /= 1000; index++; }
            value = Math.Round(value, value >= 100 ? 0 : 1, MidpointRounding.AwayFromZero);
            if (value >= 1000 && index < units.Length - 1) { value /= 1000; index++; }
            return "~" + value.ToString("0.#", CultureInfo.InvariantCulture) + units[index] + "/s";
        }
    }

    // Same numeric rules as tests/token_rate/live_console.py. The existing bounded
    // usage reader owns discovery, file offsets and cancellation; no second reader.
    internal sealed class TokenSpeedTracker
    {
        private const long CurrentMs = 60000;
        private readonly Dictionary<Tuple<string, string>, Turn> turns = new Dictionary<Tuple<string, string>, Turn>();
        private readonly Dictionary<string, Turn> latest = new Dictionary<string, Turn>(StringComparer.Ordinal);
        private readonly Dictionary<string, Turn> owners = new Dictionary<string, Turn>(StringComparer.Ordinal);

        private sealed class Turn
        {
            internal long Started, At, UsageAt, SpeedAt;
            internal bool HasStart, Finished, BadTiming;
            internal double? Speed;
            internal readonly HashSet<string> Items = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> Responses = new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<Tuple<long, long>> Pending = new List<Tuple<long, long>>();
            internal readonly Queue<Tuple<long, long>> Recent = new Queue<Tuple<long, long>>();
            internal Turn(long at) { Started = At = at; }

            internal void Item(Dictionary<string, object> payload, long stamp)
            {
                At = Math.Max(At, stamp);
                var item = UsageScanner.Object(payload, "item");
                string kind = UsageScanner.Text(item, "type"), id = UsageScanner.Text(item, "id");
                if ((kind != "Reasoning" && kind != "AgentMessage") || id == null || !Items.Add(kind + ":" + id)) return;
                long start, end;
                try { start = UsageScanner.Number(payload, "started_at_ms", true); end = UsageScanner.Number(payload, "completed_at_ms", true); }
                catch (FormatException) { BadTiming = true; return; }
                if (start <= 0 || end <= start || end - start > 600000 || end > stamp + 1000) { BadTiming = true; return; }
                Pending.Add(Tuple.Create(start, end));
            }

            internal bool Usage(Dictionary<string, object> payload, long stamp, string response)
            {
                if (Responses.Contains(response)) return true;
                var usage = UsageScanner.Object(payload, "usage");
                if (usage == null) return false;
                long input, cache, output, reasoning;
                try
                {
                    input = UsageScanner.Number(usage, "input_tokens", false);
                    cache = UsageScanner.Number(usage, "cached_input_tokens", false);
                    output = UsageScanner.Number(usage, "output_tokens", false);
                    reasoning = UsageScanner.Number(usage, "reasoning_output_tokens", false);
                    if (cache > input || reasoning > output || usage.ContainsKey("total_tokens") &&
                        UsageScanner.Number(usage, "total_tokens", false) != checked(input + output)) throw new FormatException();
                }
                catch (Exception ex)
                {
                    if (!(ex is FormatException || ex is OverflowException)) throw;
                    Skip(stamp); return false;
                }
                Responses.Add(response);
                long duration = UnionMs(Pending);
                bool usable = !BadTiming && Pending.All(p => p.Item1 >= UsageAt && p.Item2 <= stamp)
                    && duration >= 250 && output > 0 && output * 1000d / duration <= 100000;
                Skip(stamp); At = Math.Max(At, stamp);
                if (!usable) return true;
                Recent.Enqueue(Tuple.Create(output, duration));
                if (Recent.Count > 5) Recent.Dequeue();
                long measured = Recent.Sum(p => p.Item2);
                if (measured < 1000) return true;
                double raw = Recent.Sum(p => (double)p.Item1) * 1000 / measured;
                if (double.IsNaN(raw) || double.IsInfinity(raw)) return true;
                Speed = Speed.HasValue ? .35 * raw + .65 * Speed.Value : raw;
                SpeedAt = stamp;
                return true;
            }
            internal void Skip(long stamp) { Pending.Clear(); BadTiming = false; UsageAt = Math.Max(UsageAt, stamp); }
        }

        internal static long UnionMs(IEnumerable<Tuple<long, long>> intervals)
        {
            long end = 0, total = 0;
            foreach (var item in intervals.OrderBy(p => p.Item1).ThenBy(p => p.Item2))
            {
                if (item.Item2 <= item.Item1) continue;
                total += Math.Max(0, item.Item2 - Math.Max(item.Item1, end));
                end = Math.Max(end, item.Item2);
            }
            return total;
        }

        internal void Observe(Dictionary<string, object> record, string identity)
        {
            string type = UsageScanner.Text(record, "type");
            var payload = UsageScanner.Object(record, "payload");
            string source = UsageScanner.Text(payload, "thread_id") ?? identity;
            string turnId = UsageScanner.Text(payload, "turn_id"), kind = UsageScanner.Text(payload, "type");
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(turnId)) return;
            if (type != "token_usage_record" && !(type == "event_msg" &&
                (kind == "task_started" || kind == "item_completed" || kind == "task_complete" || kind == "turn_aborted"))) return;
            DateTimeOffset time;
            if (!DateTimeOffset.TryParse(UsageScanner.Text(record, "timestamp"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out time)) return;
            long stamp = time.ToUnixTimeMilliseconds();
            var key = Tuple.Create(source, turnId); Turn turn;
            if (!turns.TryGetValue(key, out turn)) { turn = new Turn(stamp); turns.Add(key, turn); }
            if (type == "event_msg" && kind == "task_started" && !turn.HasStart)
            { turn.Started = stamp; turn.HasStart = true; }
            if (type == "token_usage_record")
            {
                string response = UsageScanner.Text(payload, "response_id");
                if (response == null) return;
                Turn owner;
                if (owners.TryGetValue(response, out owner) && owner != turn) { turn.Skip(stamp); return; }
                if (turn.Usage(payload, stamp, response)) owners[response] = turn;
            }
            else if (kind == "item_completed") turn.Item(payload, stamp);
            else if (kind == "task_complete" || kind == "turn_aborted")
            { turn.Finished = true; turn.At = Math.Max(turn.At, stamp); }
            Turn previous;
            if (!latest.TryGetValue(source, out previous) || turn.HasStart && !previous.HasStart ||
                turn.HasStart == previous.HasStart && turn.Started >= previous.Started) latest[source] = turn;
        }

        internal TokenSpeedSnapshot Snapshot(DateTimeOffset now, bool ready)
        {
            long at = now.ToUnixTimeMilliseconds();
            var active = latest.Values.Where(t => !t.Finished && at >= t.At && at - t.At <= CurrentMs).ToList();
            var measured = active.Where(t => t.HasStart && t.Speed.HasValue && at >= t.SpeedAt && at - t.SpeedAt <= CurrentMs).ToList();
            return new TokenSpeedSnapshot { AsOf = now, IsReady = ready,
                TokensPerSecond = !ready || measured.Count == 0 && active.Count > 0 ? (double?)null : Math.Round(measured.Sum(t => t.Speed.Value), 1),
                ActiveSessions = active.Count, MeasuredSessions = measured.Count, SessionCount = latest.Count,
                Status = !ready ? "扫描中" : active.Count == 0 ? "空闲" :
                    active.Count > measured.Count ? "部分会话待测速" : "本机并行输出" };
        }
    }
}
