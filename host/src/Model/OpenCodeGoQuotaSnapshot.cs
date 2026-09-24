using System;

namespace CodexToolsHost.Model
{
    public sealed class OpenCodeGoQuotaWindow
    {
        public OpenCodeGoQuotaWindow(string status, int? usedPercent,
            DateTimeOffset? resetsAt)
        {
            Status = status ?? "";
            UsedPercent = ClampPercent(usedPercent);
            ResetsAt = resetsAt;
        }

        public string Status { get; private set; }
        public int? UsedPercent { get; private set; }
        public DateTimeOffset? ResetsAt { get; private set; }

        public int? RemainingPercent
        {
            get { return UsedPercent.HasValue ? 100 - UsedPercent.Value : (int?)null; }
        }

        public bool IsRateLimited
        {
            get { return string.Equals(Status, "rate-limited", StringComparison.OrdinalIgnoreCase); }
        }

        public static OpenCodeGoQuotaWindow Empty()
        {
            return new OpenCodeGoQuotaWindow("", null, null);
        }

        private static int? ClampPercent(int? value)
        {
            if (!value.HasValue) return null;
            return Math.Max(0, Math.Min(100, value.Value));
        }
    }

    public sealed class OpenCodeGoQuotaSnapshot
    {
        public OpenCodeGoQuotaSnapshot(OpenCodeGoQuotaWindow rolling,
            OpenCodeGoQuotaWindow weekly, OpenCodeGoQuotaWindow monthly,
            DateTimeOffset observedAt, bool isStale)
        {
            Rolling = rolling ?? OpenCodeGoQuotaWindow.Empty();
            Weekly = weekly ?? OpenCodeGoQuotaWindow.Empty();
            Monthly = monthly ?? OpenCodeGoQuotaWindow.Empty();
            ObservedAt = observedAt;
            IsStale = isStale;
        }

        public OpenCodeGoQuotaWindow Rolling { get; private set; }
        public OpenCodeGoQuotaWindow Weekly { get; private set; }
        public OpenCodeGoQuotaWindow Monthly { get; private set; }
        public DateTimeOffset ObservedAt { get; private set; }
        public bool IsStale { get; private set; }

        public static OpenCodeGoQuotaSnapshot EmptyStale()
        {
            return new OpenCodeGoQuotaSnapshot(
                OpenCodeGoQuotaWindow.Empty(), OpenCodeGoQuotaWindow.Empty(),
                OpenCodeGoQuotaWindow.Empty(), DateTimeOffset.MinValue, true);
        }

        public OpenCodeGoQuotaSnapshot AsStale()
        {
            return new OpenCodeGoQuotaSnapshot(
                Rolling, Weekly, Monthly, ObservedAt, true);
        }
    }
}
