using IdentitySyncPro.Core.Models.Services;
using IdentitySyncPro.Infrastructure.Services;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// The per-run summary entry.
    ///
    /// Two things must hold. It has to be written for EVERY run, including the ones that changed
    /// nothing — that was the whole reason a run's details page could come up empty. And every
    /// field has to fit its column: this entry is composed at the very end of a run, so a value
    /// one character too long throws on SaveChanges and takes the run's final status with it.
    /// </summary>
    public class SvcRunSummaryTests
    {
        // Mirrors the ServicesDbContext configuration for SvcAuditEntry.
        private const int ActionMax = 50;
        private const int KeyValueMax = 200;
        private const int AttributeNameMax = 200;
        private const int ValueMax = 2000;

        private static SvcRunLog Run(int scanned, int acted, int skipped, int failed = 0) => new()
        {
            Id = 47,
            SvcServiceId = 3,
            StartTime = DateTime.UtcNow,
            TotalRecords = scanned,
            UpdatedRecords = acted,
            SkippedRecords = skipped,
            FailedRecords = failed
        };

        private static void AssertFitsColumns(SvcAuditEntry e)
        {
            Assert.True(e.Action.Length <= ActionMax, $"Action too long: {e.Action.Length}");
            Assert.True(e.KeyValue.Length <= KeyValueMax, $"KeyValue too long: {e.KeyValue.Length}");
            Assert.True((e.AttributeName?.Length ?? 0) <= AttributeNameMax, $"AttributeName too long: {e.AttributeName?.Length}");
            Assert.True((e.OldValue?.Length ?? 0) <= ValueMax, $"OldValue too long: {e.OldValue?.Length}");
            Assert.True((e.NewValue?.Length ?? 0) <= ValueMax, $"NewValue too long: {e.NewValue?.Length}");
        }

        [Fact]
        public void CarriesTheRunCountersAndIsLinkedToTheRun()
        {
            var entry = SvcRunSummary.Build(Run(5000, 12, 4988), serviceId: 3);

            Assert.Equal(SvcRunSummary.ActionName, entry.Action);
            Assert.Equal(47, entry.SvcRunLogId);
            Assert.Equal(3, entry.SvcServiceId);
            Assert.Contains("scanned=5000", entry.AttributeName);
            Assert.Contains("acted=12", entry.AttributeName);
            Assert.Contains("skipped=4988", entry.AttributeName);
            AssertFitsColumns(entry);
        }

        [Fact]
        public void ARunThatChangedNothingStillProducesASummary()
        {
            // The case that made the details page look broken.
            var reasons = new SvcRunSummary.Reasons();
            for (int i = 0; i < 4988; i++) reasons.Add("already disabled");

            var entry = SvcRunSummary.Build(Run(4988, 0, 4988), serviceId: 3, reasons);

            Assert.Equal(SvcRunSummary.ActionName, entry.Action);
            Assert.Contains("acted=0", entry.AttributeName);
            Assert.Contains("4988 × already disabled", entry.OldValue);
            Assert.True(string.IsNullOrEmpty(entry.NewValue));
            AssertFitsColumns(entry);
        }

        [Fact]
        public void SkipReasonsAreCountedAndOrderedByFrequency()
        {
            var reasons = new SvcRunSummary.Reasons();
            for (int i = 0; i < 3; i++) reasons.Add("used within the last 6 month(s)");
            for (int i = 0; i < 10; i++) reasons.Add("already disabled");
            reasons.Add("no readable last-activity date");

            var entry = SvcRunSummary.Build(Run(14, 0, 14), serviceId: 3, reasons);

            Assert.Equal(14, reasons.Total);
            var alreadyAt = entry.OldValue!.IndexOf("already disabled", StringComparison.Ordinal);
            var usedAt = entry.OldValue!.IndexOf("used within", StringComparison.Ordinal);
            Assert.True(alreadyAt >= 0 && usedAt >= 0);
            Assert.True(alreadyAt < usedAt, "the most frequent reason should be listed first");
        }

        [Fact]
        public void ListsTheAccountsActedOn()
        {
            var acted = new[]
            {
                "• maalhareth (Mohammed Al Hareth) — last activity 2025-01-14",
                "• asaad — never used, created 2019-03-02"
            };

            var entry = SvcRunSummary.Build(Run(500, 2, 498), serviceId: 3, actedOn: acted);

            Assert.Contains("maalhareth", entry.NewValue);
            Assert.Contains("asaad", entry.NewValue);
            AssertFitsColumns(entry);
        }

        /// <summary>
        /// A run may disable hundreds of accounts. The list is capped to fit the column — and the
        /// cap has to announce itself, because a silently truncated list reads as the complete
        /// set of everything the run touched.
        /// </summary>
        [Fact]
        public void ALongListIsTruncatedAndSaysSo()
        {
            var acted = Enumerable.Range(1, 500)
                .Select(i => $"• account{i:D4} (Some Display Name {i}) — last activity 2024-01-01")
                .ToList();

            var entry = SvcRunSummary.Build(Run(5000, 500, 4500), serviceId: 3, actedOn: acted);

            AssertFitsColumns(entry);
            Assert.Contains("more", entry.NewValue);
            Assert.Contains("account0001", entry.NewValue);   // the first ones are still listed
        }

        [Fact]
        public void ManyDistinctSkipReasonsStillFitTheColumn()
        {
            var reasons = new SvcRunSummary.Reasons();
            for (int i = 0; i < 200; i++) reasons.Add($"reason number {i} with a fairly long description attached");

            var entry = SvcRunSummary.Build(Run(200, 0, 200), serviceId: 3, reasons);

            AssertFitsColumns(entry);
        }

        [Fact]
        public void AVeryLongNoteCannotOverflowTheColumn()
        {
            var entry = SvcRunSummary.Build(Run(1, 0, 1), serviceId: 3, note: new string('x', 5000));

            AssertFitsColumns(entry);
        }

        [Fact]
        public void EmptyReasonsAreIgnored()
        {
            var reasons = new SvcRunSummary.Reasons();
            reasons.Add("");
            reasons.Add("   ");

            Assert.False(reasons.Any);
            Assert.Equal(0, reasons.Total);
        }
    }
}
