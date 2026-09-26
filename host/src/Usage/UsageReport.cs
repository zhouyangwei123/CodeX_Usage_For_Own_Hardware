using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CodexToolsHost.Usage
{
    public sealed class UsageRow
    {
        public DateTime Day { get; internal set; }
        public DateTimeOffset LastActivity { get; internal set; }
        public string SessionId { get; internal set; }
        public string Model { get; internal set; }
        public long InputTokens { get; internal set; }
        public long CachedInputTokens { get; internal set; }
        public long OutputTokens { get; internal set; }
        public long ReasoningOutputTokens { get; internal set; }
        public long TotalTokens { get { return checked(InputTokens + OutputTokens); } }
    }

    public sealed class UsageReport
    {
        public string Source { get; internal set; }
        public DateTimeOffset UpdatedAt { get; internal set; }
        public ReadOnlyCollection<UsageRow> Rows { get; internal set; }
        // Canonical numeric deltas only, before daily grouping. Never log bodies or session labels.
        public ReadOnlyCollection<UsageActivityEvent> RecentActivity { get; internal set; }
        public DateTimeOffset? LastObservedEventAt { get; internal set; }
        // A readable empty source is available; incomplete/failed scans must not imply observed zero.
        public bool ActivitySourceAvailable { get; internal set; }
        public bool IsScanComplete { get; internal set; }
        public bool ScanFailed { get; internal set; }
        public ReadOnlyCollection<string> Warnings { get; internal set; }
        public long BytesRead { get; internal set; }
        public int FilesDiscovered { get; internal set; }
        public int FilesPending { get; internal set; }
        public bool IsImported { get; internal set; }
        public string PeriodKind { get; internal set; }
        public decimal? ImportedApiEstimateUsd { get; internal set; }
        public UsageReport()
        {
            Source = "本机 Codex";
            PeriodKind = "daily";
            Rows = new List<UsageRow>().AsReadOnly();
            RecentActivity = new List<UsageActivityEvent>().AsReadOnly();
            Warnings = new List<string>().AsReadOnly();
        }
        public UsageRow Totals { get { return Sum(Rows, "合计", "全部模型"); } }
        internal static UsageRow Sum(IEnumerable<UsageRow> rows, string session, string model)
        {
            UsageRow result = new UsageRow { SessionId = session, Model = model };
            foreach (UsageRow row in rows)
            {
                result.InputTokens = checked(result.InputTokens + row.InputTokens);
                result.CachedInputTokens = checked(result.CachedInputTokens + row.CachedInputTokens);
                result.OutputTokens = checked(result.OutputTokens + row.OutputTokens);
                result.ReasoningOutputTokens = checked(result.ReasoningOutputTokens + row.ReasoningOutputTokens);
                if (row.LastActivity > result.LastActivity) result.LastActivity = row.LastActivity;
                result.Day = row.Day;
            }
            return result;
        }
        public static string ToCsv(IEnumerable<UsageRow> rows, string source)
        {
            StringBuilder csv = new StringBuilder("来源,日期,会话,模型,输入含缓存,缓存输入,输出,总计,最近活动\r\n");
            foreach (UsageRow row in rows)
            {
                string[] cells = { source, row.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), row.SessionId, row.Model,
                    row.InputTokens.ToString(CultureInfo.InvariantCulture), row.CachedInputTokens.ToString(CultureInfo.InvariantCulture),
                    row.OutputTokens.ToString(CultureInfo.InvariantCulture), row.TotalTokens.ToString(CultureInfo.InvariantCulture),
                    row.LastActivity == default(DateTimeOffset) ? "" : row.LastActivity.ToString("o", CultureInfo.InvariantCulture) };
                csv.AppendLine(string.Join(",", cells.Select(CsvCell).ToArray()));
            }
            return csv.ToString();
        }
        private static string CsvCell(string value)
        {
            value = value ?? "";
            if (value.Length > 0 && "=+-@\t\r\n".IndexOf(value[0]) >= 0) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
