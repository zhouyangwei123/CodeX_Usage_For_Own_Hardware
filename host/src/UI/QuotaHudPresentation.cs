using System;
using System.Drawing;
using System.Globalization;
using CodexToolsHost.Model;

namespace CodexToolsHost.UI
{
    internal static class QuotaHudPresentation
    {
        private static readonly int[] AllowedScalePercents = { 60, 75, 90, 100 };

        public static string FormatPercent(int? remaining)
        {
            if (!remaining.HasValue) return "--%";
            return Math.Max(0, Math.Min(100, remaining.Value)).ToString(
                CultureInfo.InvariantCulture) + "%";
        }

        public static string FormatApiBalance(string provider, string currency,
            long balanceCents, bool available, bool stale, bool unlimited)
        {
            string name = string.IsNullOrWhiteSpace(provider) ? "API" : provider.Trim();
            if (name.Length > 13) name = name.Substring(0, 12) + "\u2026";
            string unit = string.IsNullOrWhiteSpace(currency)
                ? "--" : currency.Trim().ToUpperInvariant();
            string amount = unlimited ? "UNLIMITED" : (!available && balanceCents <= 0
                ? "--" : (Math.Max(0, balanceCents) / 100m)
                    .ToString("0.00", CultureInfo.InvariantCulture));
            string suffix = stale ? " \u00b7 STALE" : "";
            return name + " \u00b7 " + unit + " " + amount + suffix;
        }

        public static string FormatOpenCodeGoSummary(OpenCodeGoQuotaSnapshot snapshot,
            DateTimeOffset now)
        {
            if (snapshot == null) snapshot = OpenCodeGoQuotaSnapshot.EmptyStale();
            return "GO " + FormatOpenCodeGoWindowLabel("5h", snapshot.Rolling)
                + "  " + FormatOpenCodeGoWindowLabel("7d", snapshot.Weekly)
                + "\r\n   " + FormatOpenCodeGoWindowLabel("月", snapshot.Monthly)
                + "  5h→" + FormatResetCountdown(snapshot.Rolling.ResetsAt, now);
        }

        public static string FormatDeepSeekSummary(string provider, string currency,
            long balanceCents, bool available, bool stale, bool unlimited,
            DateTimeOffset now)
        {
            string first = FormatApiBalance(provider, currency, balanceCents,
                available, false, unlimited);
            string second = stale ? "STALE" : !available && !unlimited
                ? "--" : "已更新 " + now.ToLocalTime().ToString("HH:mm:ss");
            return first + "\r\n   " + second;
        }

        public static string FormatResetCountdown(DateTimeOffset? resetsAt,
            DateTimeOffset now)
        {
            if (!resetsAt.HasValue) return "--:--:--";
            double seconds = (resetsAt.Value - now).TotalSeconds;
            if (seconds <= 0d) return "00:00:00";
            long totalSeconds = (long)Math.Ceiling(seconds);
            long hours = totalSeconds / 3600L;
            int minutes = (int)((totalSeconds % 3600L) / 60L);
            int remainingSeconds = (int)(totalSeconds % 60L);
            return hours.ToString("00", CultureInfo.InvariantCulture) + ":"
                + minutes.ToString("00", CultureInfo.InvariantCulture) + ":"
                + remainingSeconds.ToString("00", CultureInfo.InvariantCulture);
        }

        public static string FormatNetworkSpeed(bool available, uint downloadKiBPerSecond,
            uint uploadKiBPerSecond)
        {
            if (!available) return "↑-- ↓--";
            return "↑" + FormatNetworkMagnitude(uploadKiBPerSecond)
                + " ↓" + FormatNetworkMagnitude(downloadKiBPerSecond);
        }

        private static string FormatNetworkMagnitude(uint kibPerSecond)
        {
            if (kibPerSecond < 1024u)
                return kibPerSecond.ToString(CultureInfo.InvariantCulture) + "K";
            double mebibytes = kibPerSecond / 1024d;
            if (mebibytes < 1024d)
                return mebibytes.ToString("0.0", CultureInfo.InvariantCulture) + "M";
            return (mebibytes / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + "G";
        }

        public static Rectangle EnsureVisible(Rectangle desired, Rectangle[] workAreas)
        {
            if (workAreas == null || workAreas.Length == 0) return desired;
            foreach (Rectangle area in workAreas)
            {
                if (area.Contains(desired)) return desired;
            }

            Rectangle areaToUse = workAreas[0];
            int width = Math.Min(desired.Width, areaToUse.Width);
            int height = Math.Min(desired.Height, areaToUse.Height);
            int x = Math.Max(areaToUse.Left,
                Math.Min(desired.Left, areaToUse.Right - width));
            int y = Math.Max(areaToUse.Top,
                Math.Min(desired.Top, areaToUse.Bottom - height));
            return new Rectangle(x, y, width, height);
        }

        public static bool IsAllowedScalePercent(int percent)
        {
            return Array.IndexOf(AllowedScalePercents, percent) >= 0;
        }

        public static int NormalizeScalePercent(int percent)
        {
            return IsAllowedScalePercent(percent) ? percent : 75;
        }

        public static Size ScaleSize(Size canonical, int scalePercent)
        {
            int percent = NormalizeScalePercent(scalePercent);
            return new Size(
                RoundScaled(canonical.Width, percent),
                RoundScaled(canonical.Height, percent));
        }

        public static Rectangle ScaleRectangle(Rectangle canonical, int scalePercent)
        {
            int percent = NormalizeScalePercent(scalePercent);
            return new Rectangle(
                RoundScaled(canonical.X, percent),
                RoundScaled(canonical.Y, percent),
                RoundScaled(canonical.Width, percent),
                RoundScaled(canonical.Height, percent));
        }

        public static double NormalizeOpacity(double opacity)
        {
            return double.IsNaN(opacity) || double.IsInfinity(opacity)
                || opacity < 0.35d || opacity > 1.0d ? 0.90d : opacity;
        }

        private static int RoundScaled(int value, int scalePercent)
        {
            return (int)Math.Round(value * scalePercent / 100d,
                MidpointRounding.AwayFromZero);
        }

        public static Point DefaultHudLocation(Rectangle workArea, Size hudSize)
        {
            Rectangle desired = new Rectangle(
                workArea.Right - hudSize.Width - 24,
                workArea.Top + 24,
                hudSize.Width,
                hudSize.Height);
            return EnsureVisible(desired, new[] { workArea }).Location;
        }

        private static string FormatOpenCodeGoWindowLabel(string label,
            OpenCodeGoQuotaWindow window)
        {
            if (window != null && window.IsRateLimited)
                return label + "已达上限";
            return label + "余" + FormatPercent(
                window == null ? null : window.RemainingPercent);
        }

    }
}
