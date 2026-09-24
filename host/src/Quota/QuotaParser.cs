using System;
using System.Collections.Generic;
using System.IO;
using CodexToolsHost.Model;

namespace CodexToolsHost.Quota
{
    // Missing fields in notifications mean unchanged; explicit null means removed.
    internal static class QuotaParser
    {
        internal static IDictionary<string, object> Dict(IDictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) ? value as IDictionary<string, object> : null;
        }
        internal static string Text(IDictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : null;
        }
        internal static bool IsCodex(IDictionary<string, object> snapshot)
        {
            string id = Text(snapshot, "limitId");
            return snapshot != null && (string.IsNullOrEmpty(id) || id == "codex");
        }
        internal static QuotaSnapshot Full(IDictionary<string, object> result)
        {
            var bucket = Dict(Dict(result, "rateLimitsByLimitId"), "codex") ?? Dict(result, "rateLimits");
            if (!IsCodex(bucket)) throw new InvalidDataException("Codex quota bucket is unavailable.");
            return Parse(bucket, QuotaSnapshot.EmptyStale(), false);
        }
        internal static QuotaSnapshot Sparse(IDictionary<string, object> bucket, QuotaSnapshot previous)
        {
            if (!IsCodex(bucket)) return null;
            if (!bucket.ContainsKey("primary") && !bucket.ContainsKey("secondary")) return null;
            return Parse(bucket, previous, true);
        }
        private sealed class Window
        {
            internal int? Remaining;
            internal int? Minutes;
            internal DateTimeOffset? Reset;
            internal bool NewUsage;
        }
        private static QuotaSnapshot Parse(IDictionary<string, object> bucket, QuotaSnapshot previous, bool sparse)
        {
            Window primary = ReadWindow(bucket, "primary", previous.PrimaryRemainingPercent,
                previous.PrimaryWindowMinutes, previous.PrimaryResetsAt, sparse);
            Window secondary = ReadWindow(bucket, "secondary", previous.SecondaryRemainingPercent,
                previous.SecondaryWindowMinutes, previous.SecondaryResetsAt, sparse);
            if (!sparse && !primary.Remaining.HasValue && !secondary.Remaining.HasValue)
                throw new InvalidDataException("No usable quota windows were returned.");
            bool fresh = primary.NewUsage || secondary.NewUsage;
            return new QuotaSnapshot(primary.Remaining, secondary.Remaining, primary.Reset, secondary.Reset,
                fresh ? DateTimeOffset.UtcNow : previous.ObservedAt, fresh ? false : previous.IsStale,
                primary.Minutes, secondary.Minutes, fresh ? "" : previous.LastError);
        }
        private static Window ReadWindow(IDictionary<string, object> bucket, string key,
            int? remaining, int? minutes, DateTimeOffset? reset, bool sparse)
        {
            var result = new Window { Remaining = sparse ? remaining : null,
                Minutes = sparse ? minutes : null, Reset = sparse ? reset : null };
            object raw;
            if (!bucket.TryGetValue(key, out raw)) return result;
            if (raw == null) return new Window();
            var window = raw as IDictionary<string, object>;
            if (window == null) throw new InvalidDataException("Invalid quota window.");
            object value;
            if (window.TryGetValue("usedPercent", out value))
            {
                double used = Number(value);
                result.Remaining = (int)Math.Floor(100 - Math.Max(0, Math.Min(100, used)));
                result.NewUsage = true;
            }
            else if (!sparse) throw new InvalidDataException("Quota percentage is missing.");
            if (window.TryGetValue("windowDurationMins", out value))
            {
                if (value == null) result.Minutes = null;
                else { double n = Number(value); if (n <= 0 || n > int.MaxValue || n != Math.Floor(n)) throw new InvalidDataException("Invalid quota window length."); result.Minutes = (int)n; }
            }
            if (window.TryGetValue("resetsAt", out value))
            {
                if (value == null) result.Reset = null;
                else
                {
                    double n = Number(value);
                    if (n != Math.Floor(n) || n < 0 || n > 253402300799L) throw new InvalidDataException("Invalid quota reset time.");
                    result.Reset = DateTimeOffset.FromUnixTimeSeconds((long)n);
                }
            }
            return result;
        }
        private static double Number(object value)
        {
            if (!(value is int || value is long || value is double || value is decimal || value is float))
                throw new InvalidDataException("Expected a numeric quota field.");
            double number = Convert.ToDouble(value);
            if (double.IsNaN(number) || double.IsInfinity(number)) throw new InvalidDataException("Invalid quota number.");
            return number;
        }
    }
}
