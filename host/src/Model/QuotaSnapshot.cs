using System;

namespace CodexToolsHost.Model
{
    public sealed class QuotaSnapshot
    {
        public QuotaSnapshot(
            int? primaryRemainingPercent,
            int? secondaryRemainingPercent,
            DateTimeOffset? primaryResetsAt,
            DateTimeOffset? secondaryResetsAt,
            DateTimeOffset observedAt,
            bool isStale)
            : this(primaryRemainingPercent, secondaryRemainingPercent, primaryResetsAt,
                secondaryResetsAt, observedAt, isStale, null, null, "")
        {
        }

        public QuotaSnapshot(int? primaryRemainingPercent, int? secondaryRemainingPercent,
            DateTimeOffset? primaryResetsAt, DateTimeOffset? secondaryResetsAt,
            DateTimeOffset observedAt, bool isStale, int? primaryWindowMinutes,
            int? secondaryWindowMinutes, string lastError)
        {
            PrimaryRemainingPercent = primaryRemainingPercent;
            SecondaryRemainingPercent = secondaryRemainingPercent;
            PrimaryResetsAt = primaryResetsAt;
            SecondaryResetsAt = secondaryResetsAt;
            ObservedAt = observedAt;
            IsStale = isStale;
            PrimaryWindowMinutes = primaryWindowMinutes;
            SecondaryWindowMinutes = secondaryWindowMinutes;
            LastError = lastError ?? "";
        }

        public int? PrimaryRemainingPercent { get; private set; }
        public int? SecondaryRemainingPercent { get; private set; }
        public DateTimeOffset? PrimaryResetsAt { get; private set; }
        public DateTimeOffset? SecondaryResetsAt { get; private set; }
        public DateTimeOffset ObservedAt { get; private set; }
        public bool IsStale { get; private set; }
        public int? PrimaryWindowMinutes { get; private set; }
        public int? SecondaryWindowMinutes { get; private set; }
        public string LastError { get; private set; }

        public static QuotaSnapshot EmptyStale()
        {
            return new QuotaSnapshot(null, null, null, null, DateTimeOffset.MinValue, true);
        }

        public static QuotaSnapshot FromUsedPercent(
            int? primaryUsed, int? secondaryUsed,
            long? primaryResetUnix, long? secondaryResetUnix,
            DateTimeOffset observedAt)
        {
            return new QuotaSnapshot(ToRemaining(primaryUsed), ToRemaining(secondaryUsed),
                ToDate(primaryResetUnix), ToDate(secondaryResetUnix), observedAt, false);
        }

        public QuotaSnapshot AsStale()
        {
            return AsStale(LastError);
        }

        public QuotaSnapshot AsStale(string error)
        {
            return new QuotaSnapshot(PrimaryRemainingPercent, SecondaryRemainingPercent,
                PrimaryResetsAt, SecondaryResetsAt, ObservedAt, true,
                PrimaryWindowMinutes, SecondaryWindowMinutes, error);
        }

        private static int? ToRemaining(int? used)
        {
            if (!used.HasValue) return null;
            int clamped = Math.Max(0, Math.Min(100, used.Value));
            return 100 - clamped;
        }

        private static DateTimeOffset? ToDate(long? unixSeconds)
        {
            if (!unixSeconds.HasValue) return null;
            try { return DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
    }
}
