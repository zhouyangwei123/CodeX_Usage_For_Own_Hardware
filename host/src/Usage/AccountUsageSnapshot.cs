using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using CodexToolsHost.Quota;

namespace CodexToolsHost.Usage
{
    public sealed class AccountUsageDay
    {
        public DateTime Date { get; internal set; }
        public long Tokens { get; internal set; }
    }

    public sealed class AccountUsageSnapshot
    {
        public DateTimeOffset ObservedAt { get; internal set; }
        public long? LifetimeTokens { get; internal set; }
        public ReadOnlyCollection<AccountUsageDay> Days { get; internal set; }
        public bool IsStale { get; internal set; }
        public string Status { get; internal set; }

        public static AccountUsageSnapshot Empty()
        { return new AccountUsageSnapshot { Days = new List<AccountUsageDay>().AsReadOnly(), IsStale = true, Status = "账号统计不可用" }; }

        public AccountUsageSnapshot AsStale(string status)
        { return new AccountUsageSnapshot { ObservedAt = ObservedAt, LifetimeTokens = LifetimeTokens, Days = Days, IsStale = true, Status = status }; }

        public static AccountUsageSnapshot Parse(IDictionary<string, object> response, DateTimeOffset now)
        {
            var result = Empty();
            var summary = QuotaParser.Dict(response, "summary");
            object raw;
            if (response != null && response.TryGetValue("summary", out raw) && raw != null && summary == null)
                throw new InvalidDataException("Invalid account usage summary.");
            if (summary != null && summary.TryGetValue("lifetimeTokens", out raw) && raw != null)
                result.LifetimeTokens = Count(raw);
            var days = new SortedDictionary<DateTime, long>();
            if (response != null && response.TryGetValue("dailyUsageBuckets", out raw) && raw != null)
            {
                var array = raw as IList;
                if (array == null) throw new InvalidDataException("Invalid daily usage buckets.");
                foreach (object item in array)
                {
                    var row = item as IDictionary<string, object>;
                    DateTime date;
                    if (row == null || !DateTime.TryParseExact(QuotaParser.Text(row, "startDate"), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out date) || !row.TryGetValue("tokens", out raw))
                        throw new InvalidDataException("Invalid daily usage bucket.");
                    if (days.ContainsKey(date)) throw new InvalidDataException("Duplicate daily usage bucket.");
                    days.Add(date, Count(raw));
                }
            }
            result.Days = days.Select(pair => new AccountUsageDay { Date = pair.Key, Tokens = pair.Value }).ToList().AsReadOnly();
            result.ObservedAt = now; result.IsStale = false;
            result.Status = result.Days.Count == 0 ? "账号无按日统计" : "账号服务端按日汇总";
            return result;
        }

        private static long Count(object raw)
        {
            if (!(raw is int || raw is long || raw is decimal || raw is double)) throw new InvalidDataException("Invalid token count.");
            decimal count;
            try { count = Convert.ToDecimal(raw, CultureInfo.InvariantCulture); }
            catch (Exception ex) { if (!(ex is OverflowException || ex is FormatException)) throw; throw new InvalidDataException("Invalid token count."); }
            if (count < 0 || count > long.MaxValue || count != decimal.Truncate(count)) throw new InvalidDataException("Invalid token count.");
            return (long)count;
        }
    }

    public interface IAccountUsageSource
    {
        AccountUsageSnapshot AccountUsage { get; }
    }
}
