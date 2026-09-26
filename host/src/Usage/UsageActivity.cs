using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CodexToolsHost.Usage
{
    public sealed class UsageActivityEvent
    {
        public DateTimeOffset Time { get; internal set; }
        public long InputTokens { get; internal set; }
        public long OutputTokens { get; internal set; }
    }
    public sealed class UsageActivityPoint
    {
        public DateTimeOffset Time { get; internal set; }
        public long InputTokens { get; internal set; }
        public long OutputTokens { get; internal set; }
        public bool HasData { get; internal set; }
    }
    public sealed class UsageActivitySnapshot
    {
        public DateTimeOffset AsOf { get; internal set; }
        public DateTimeOffset? LastEventAt { get; internal set; }
        public bool IsReady { get; internal set; }
        public bool IsStale { get; internal set; }
        public bool IsPartial { get; internal set; }
        public long InputTokensPerMinute { get; internal set; }
        public long OutputTokensPerMinute { get; internal set; }
        public long HourTokens { get; internal set; }
        public ReadOnlyCollection<UsageActivityPoint> Points { get; internal set; }
        public string Status { get; internal set; }
    }
    public static class UsageActivity
    {
        internal const int RetentionSeconds = 3670;
        internal const int MaximumEvents = 50000;
        // The service normally scans every 5s. Six missed intervals distinguish a stopped
        // or failed reader from healthy, arbitrarily long periods with no token records.
        public const int StaleAfterSeconds = 30;
        public static UsageActivitySnapshot Create(UsageReport report, DateTimeOffset now)
        {
            bool ready = report != null && !report.IsImported && report.ActivitySourceAvailable;
            bool stale = report != null && (report.ScanFailed || (ready && now - report.UpdatedAt > TimeSpan.FromSeconds(StaleAfterSeconds)));
            bool partial = report != null && (report.ScanFailed || (ready && (!report.IsScanComplete || report.FilesPending > 0)));
            bool scanning = report != null && report.FilesPending > 0;
            DateTimeOffset end = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.FromSeconds(10).Ticks, TimeSpan.Zero);
            DateTimeOffset first = end.AddHours(-1), oldest = first.AddSeconds(-60);
            List<UsageActivityEvent> events = ready && report.RecentActivity != null
                ? report.RecentActivity.Where(x => x != null && x.Time > oldest && x.Time <= now)
                    .OrderBy(x => x.Time).Take(MaximumEvents + 1).ToList()
                : new List<UsageActivityEvent>();
            if (events.Count > MaximumEvents) { partial = true; events.RemoveAt(events.Count - 1); }
            UsageActivitySnapshot result = new UsageActivitySnapshot {
                AsOf = now, IsReady = ready, IsStale = stale, IsPartial = partial,
                LastEventAt = ready && report.LastObservedEventAt <= now ? report.LastObservedEventAt : null,
                Status = report != null && report.ScanFailed ? "读取失败" :
                    report == null || report.UpdatedAt == default(DateTimeOffset) ? "扫描中" :
                    !ready ? "不可用" : stale ? "数据过期" : scanning ? "扫描中" : partial ? "部分记录" : "正常"
            };
            foreach (UsageActivityEvent item in events)
            {
                if (!result.LastEventAt.HasValue || item.Time > result.LastEventAt.Value) result.LastEventAt = item.Time;
                if (item.Time > now.AddSeconds(-60))
                {
                    result.InputTokensPerMinute = checked(result.InputTokensPerMinute + item.InputTokens);
                    result.OutputTokensPerMinute = checked(result.OutputTokensPerMinute + item.OutputTokens);
                }
                if (item.Time > now.AddHours(-1)) result.HourTokens = checked(result.HourTokens + item.InputTokens + item.OutputTokens);
            }
            List<UsageActivityPoint> points = new List<UsageActivityPoint>(361);
            int left = 0, right = 0;
            long input = 0, output = 0;
            for (int i = 0; i <= 360; i++)
            {
                DateTimeOffset time = first.AddSeconds(i * 10);
                while (right < events.Count && events[right].Time <= time)
                {
                    input = checked(input + events[right].InputTokens);
                    output = checked(output + events[right].OutputTokens);
                    right++;
                }
                while (left < right && events[left].Time <= time.AddSeconds(-60))
                { input -= events[left].InputTokens; output -= events[left].OutputTokens; left++; }
                // Partial results still represent observed local records. The presenter must show
                // the partial label/lower-bound totals; pending or unhealthy readers show no curve.
                points.Add(new UsageActivityPoint { Time = time, InputTokens = input, OutputTokens = output, HasData = ready && !stale && !scanning });
            }
            result.Points = points.AsReadOnly();
            return result;
        }
    }
}
