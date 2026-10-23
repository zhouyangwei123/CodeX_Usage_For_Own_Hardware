using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CodexToolsHost.Model;
using CodexToolsHost.Usage;

namespace CodexToolsHost.Protocol
{
    public sealed class OledUsageFrame
    {
        public const int HeaderSize = 52, MaxPoints = 76;
        public const byte QuotaKnown = 1, QuotaStale = 2, UsageKnown = 4, Partial = 8,
            UsageStale = 16, ApiKnown = 32, ApiStale = 64, ResetKnown = 128;
        public byte View, Flags, Remaining = 255, Month, Day;
        public uint ResetSeconds;
        public ulong Upper = 1, Current, Input, Output, BalanceCents;
        public byte[] Points = new byte[0];

        public bool Has(byte flag) { return (Flags & flag) != 0; }
        public byte[] Encode()
        {
            if (Points.Length > MaxPoints) throw new ArgumentException("Too many OLED points.");
            var data = new byte[HeaderSize + Points.Length];
            data[0] = 1; data[1] = View; data[2] = Flags; data[3] = Remaining;
            Write(data, 4, ResetSeconds, 4); Write(data, 8, Upper, 8); Write(data, 16, Current, 8);
            Write(data, 24, Input, 8); Write(data, 32, Output, 8); Write(data, 40, BalanceCents, 8);
            data[48] = (byte)Points.Length; data[50] = Month; data[51] = Day;
            Array.Copy(Points, 0, data, HeaderSize, Points.Length);
            return data;
        }
        private static void Write(byte[] data, int offset, ulong value, int count)
        { for (int i = 0; i < count; i++) data[offset + i] = (byte)(value >> (8 * i)); }

        public static string FormatCount(ulong value, bool whole = false)
        { return FormatNumber(value, whole); }
        private static string FormatNumber(decimal value, bool whole)
        {
            decimal unit = 1; int index = 0;
            string[] suffix = { "", "K", "M", "G", "T", "P", "E" };
            while (value / unit >= 1000 && index < suffix.Length - 1) { unit *= 1000; index++; }
            decimal n = value / unit;
            n = Math.Round(n, whole || n >= 100 ? 0 : 1, MidpointRounding.AwayFromZero);
            if (n >= 1000 && index < suffix.Length - 1) { index++; n = Math.Round(n / 1000, 1, MidpointRounding.AwayFromZero); }
            return n.ToString("0.#", CultureInfo.InvariantCulture) + suffix[index];
        }
        public string ApiText()
        {
            if (!Has(ApiKnown) || Has(ApiStale)) return "API --";
            if (BalanceCents == ulong.MaxValue) return "API INF";
            return "API " + (BalanceCents < 10000 ? (BalanceCents / 100m).ToString("0.00", CultureInfo.InvariantCulture)
                : FormatNumber(BalanceCents / 100m, false));
        }
        public string CurrentText()
        { return (Has(UsageKnown) && !Has(UsageStale) ? FormatCount(Current) : "--") + (View == 0 ? "/m" : "/d"); }
        public string DetailText()
        {
            if (View != 0) return (Has(UsageKnown) && !Has(UsageStale) && Month > 0
                ? Month.ToString("00") + "-" + Day.ToString("00") : "-- --") + " " + ApiText();
            bool valid = Has(UsageKnown) && !Has(UsageStale);
            string detail = "U" + (valid ? FormatCount(Input) : "--") + " D" + (valid ? FormatCount(Output) : "--") + " " + ApiText();
            if (detail.Length > 21 && valid) detail = "U" + FormatCount(Input, true) + " D" + FormatCount(Output, true) + " " + ApiText();
            return detail;
        }
    }

    public sealed class OledUsageProjector
    {
        private readonly object gate = new object();
        private readonly ulong[] tops = new ulong[2];
        public static ulong NiceCeiling(ulong peak)
        {
            decimal decade = 1;
            while (peak > decade * 10) decade *= 10;
            foreach (int step in new[] { 1, 2, 5, 10 }) if (step * decade >= peak) return (ulong)(step * decade);
            throw new OverflowException("OLED scale exceeds supported counts.");
        }
        public OledUsageFrame Create(bool accountView, QuotaSnapshot quota, UsageActivitySnapshot local,
            AccountUsageSnapshot account, long balanceCents, bool apiAvailable, bool apiStale, bool unlimited,
            DateTimeOffset now)
        {
            lock (gate)
            {
                var frame = new OledUsageFrame { View = (byte)(accountView ? 1 : 0), BalanceCents = unlimited ? ulong.MaxValue : (ulong)Math.Max(0, balanceCents) };
                if (apiAvailable || unlimited) frame.Flags |= OledUsageFrame.ApiKnown;
                if (apiStale) frame.Flags |= OledUsageFrame.ApiStale;
                if (quota != null)
                {
                    int? remaining = quota.PrimaryWindowMinutes == 10080 ? quota.PrimaryRemainingPercent :
                        quota.SecondaryWindowMinutes == 10080 ? quota.SecondaryRemainingPercent : null;
                    DateTimeOffset? reset = quota.PrimaryWindowMinutes == 10080 ? quota.PrimaryResetsAt :
                        quota.SecondaryWindowMinutes == 10080 ? quota.SecondaryResetsAt : null;
                    if (remaining.HasValue) { frame.Flags |= OledUsageFrame.QuotaKnown; frame.Remaining = (byte)Math.Max(0, Math.Min(100, remaining.Value)); }
                    if (quota.IsStale) frame.Flags |= OledUsageFrame.QuotaStale;
                    if (reset.HasValue) { frame.Flags |= OledUsageFrame.ResetKnown; frame.ResetSeconds = (uint)Math.Max(0, Math.Min(uint.MaxValue, (reset.Value - now).TotalSeconds)); }
                }
                var values = new List<ulong?>();
                if (accountView)
                {
                    if (account != null && (account.IsStale || now - account.ObservedAt > TimeSpan.FromMinutes(15)))
                        frame.Flags |= OledUsageFrame.UsageStale;
                    if (account != null && !account.IsStale && now - account.ObservedAt <= TimeSpan.FromMinutes(15) && account.Days.Count > 0)
                    {
                        DateTime end = account.Days[account.Days.Count - 1].Date;
                        var days = account.Days.ToDictionary(d => d.Date, d => d.Tokens);
                        for (int i = 13; i >= 0; i--) { long count; values.Add(days.TryGetValue(end.AddDays(-i), out count) ? (ulong?)count : null); }
                        frame.Current = (ulong)account.Days[account.Days.Count - 1].Tokens;
                        frame.Month = (byte)end.Month; frame.Day = (byte)end.Day;
                        frame.Flags |= OledUsageFrame.UsageKnown;
                        if (values.Any(v => !v.HasValue)) frame.Flags |= OledUsageFrame.Partial;
                    }
                }
                else if (local != null)
                {
                    if (local.IsStale) frame.Flags |= OledUsageFrame.UsageStale;
                    if (local.IsPartial) frame.Flags |= OledUsageFrame.Partial;
                    if (local.IsReady && !local.IsStale && local.Status != "扫描中" && local.Points != null && local.Points.Count > 0)
                    {
                        frame.Input = (ulong)local.InputTokensPerMinute; frame.Output = (ulong)local.OutputTokensPerMinute;
                        frame.Current = (ulong)checked(local.InputTokensPerMinute + local.OutputTokensPerMinute);
                        for (int bin = 0; bin < OledUsageFrame.MaxPoints - 1; bin++)
                        {
                            int first = bin * (local.Points.Count - 1) / (OledUsageFrame.MaxPoints - 1);
                            int last = (bin + 1) * (local.Points.Count - 1) / (OledUsageFrame.MaxPoints - 1);
                            ulong peak = 0; bool known = true;
                            for (int j = first; j <= last; j++)
                            { var p = local.Points[j]; known &= p.HasData; peak = Math.Max(peak, (ulong)checked(p.InputTokens + p.OutputTokens)); }
                            values.Add(known ? (ulong?)peak : null);
                        }
                        values.Add(frame.Current);
                        frame.Flags |= OledUsageFrame.UsageKnown;
                    }
                }
                if (values.Count > 0)
                {
                    ulong peak = values.Where(v => v.HasValue).Select(v => v.Value).DefaultIfEmpty(0UL).Max();
                    ulong next = NiceCeiling(peak), prior = tops[frame.View];
                    if (prior == 0 || peak > prior || next < prior && peak <= next * .8m) tops[frame.View] = next;
                    frame.Upper = tops[frame.View];
                    frame.Points = values.Select(v => v.HasValue ? (byte)Math.Round(v.Value * 34m / frame.Upper, MidpointRounding.AwayFromZero) : (byte)255).ToArray();
                }
                return frame;
            }
        }
    }
}
