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
        {
            PrimaryRemainingPercent = primaryRemainingPercent;
            SecondaryRemainingPercent = secondaryRemainingPercent;
            PrimaryResetsAt = primaryResetsAt;
            SecondaryResetsAt = secondaryResetsAt;
            ObservedAt = observedAt;
            IsStale = isStale;
        }

        public int? PrimaryRemainingPercent { get; private set; }
        public int? SecondaryRemainingPercent { get; private set; }
        public DateTimeOffset? PrimaryResetsAt { get; private set; }
        public DateTimeOffset? SecondaryResetsAt { get; private set; }
        public DateTimeOffset ObservedAt { get; private set; }
        public bool IsStale { get; private set; }

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
            return new QuotaSnapshot(PrimaryRemainingPercent, SecondaryRemainingPercent,
                PrimaryResetsAt, SecondaryResetsAt, ObservedAt, true);
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
