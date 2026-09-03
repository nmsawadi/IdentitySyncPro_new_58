using IdentitySyncPro.Core.Models.Services;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// The audit log's per-run filter — what the magnifier on the run-history screen links to
    /// (<c>AuditLog?id=…&amp;runId=…</c>).
    ///
    /// Reported symptom: the combined log lists entries, but opening a single run's details shows
    /// nothing, for every service. These tests pin the filter itself so the failure can be located
    /// in the query or ruled out of it.
    /// </summary>
    public class AuditLogRunFilterTests
    {
        private static ServicesDbContext NewDb()
        {
            var options = new DbContextOptionsBuilder<ServicesDbContext>()
                .UseInMemoryDatabase($"audit-{Guid.NewGuid()}")
                .Options;
            return new ServicesDbContext(options);
        }

        /// <summary>Mirrors ServicesController.BuildAuditQuery.</summary>
        private static IQueryable<SvcAuditEntry> BuildAuditQuery(
            ServicesDbContext db, int serviceId, long? runId, string? action, string? q,
            DateTime? dateFrom, DateTime? dateTo)
        {
            var query = db.SvcAuditEntries.Where(a => a.SvcServiceId == serviceId);

            if (runId.HasValue)
                query = query.Where(a => a.SvcRunLogId == runId.Value);

            if (!string.IsNullOrEmpty(action))
                query = query.Where(a => a.Action == action);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                query = query.Where(a => a.KeyValue.Contains(term) || (a.ADIdentity != null && a.ADIdentity.Contains(term)));
            }

            if (dateFrom.HasValue)
                query = query.Where(a => a.Timestamp >= dateFrom.Value);
            if (dateTo.HasValue)
                query = query.Where(a => a.Timestamp < dateTo.Value.AddDays(1));

            return query;
        }

        private static ServicesDbContext SeededDb()
        {
            var db = NewDb();

            // Two runs of the same service, plus a second service that must never leak in.
            db.SvcAuditEntries.AddRange(
                new SvcAuditEntry { Id = 1, SvcServiceId = 1, SvcRunLogId = 10, Timestamp = DateTime.UtcNow.AddMinutes(-30), Action = "InactiveDisabled", KeyValue = "maalhareth" },
                new SvcAuditEntry { Id = 2, SvcServiceId = 1, SvcRunLogId = 10, Timestamp = DateTime.UtcNow.AddMinutes(-29), Action = "InactiveDisabled", KeyValue = "asaad" },
                new SvcAuditEntry { Id = 3, SvcServiceId = 1, SvcRunLogId = 11, Timestamp = DateTime.UtcNow.AddMinutes(-5), Action = "InactiveDisabled", KeyValue = "kalotaibi" },
                new SvcAuditEntry { Id = 4, SvcServiceId = 2, SvcRunLogId = 12, Timestamp = DateTime.UtcNow, Action = "Update", KeyValue = "other-service" });
            db.SaveChanges();
            return db;
        }

        [Fact]
        public void WithoutRunId_ReturnsEveryEntryForTheService()
        {
            using var db = SeededDb();

            var rows = BuildAuditQuery(db, serviceId: 1, runId: null, null, null, null, null).ToList();

            Assert.Equal(3, rows.Count);
            Assert.DoesNotContain(rows, r => r.KeyValue == "other-service");
        }

        [Fact]
        public void WithRunId_ReturnsOnlyThatRun()
        {
            // The magnifier's exact query. An empty result here would mean the filter is at fault.
            using var db = SeededDb();

            var rows = BuildAuditQuery(db, serviceId: 1, runId: 10, null, null, null, null).ToList();

            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal(10, r.SvcRunLogId));
        }

        [Fact]
        public void WithTheOtherRunId_ReturnsTheOtherRun()
        {
            using var db = SeededDb();

            var rows = BuildAuditQuery(db, serviceId: 1, runId: 11, null, null, null, null).ToList();

            Assert.Single(rows);
            Assert.Equal("kalotaibi", rows[0].KeyValue);
        }

        [Fact]
        public void ARunBelongingToAnotherServiceReturnsNothing()
        {
            // Service and run are both applied, so a run id from a different service cannot
            // surface that service's entries.
            using var db = SeededDb();

            Assert.Empty(BuildAuditQuery(db, serviceId: 1, runId: 12, null, null, null, null).ToList());
        }

        [Fact]
        public void RunIdZeroIsTreatedAsARealFilter_NotAsAbsent()
        {
            // A run id that never matched anything must return nothing rather than silently
            // falling back to "all entries" — the two look identical on screen otherwise.
            using var db = SeededDb();

            Assert.Empty(BuildAuditQuery(db, serviceId: 1, runId: 0, null, null, null, null).ToList());
        }

        /// <summary>
        /// The run looked up to explain an empty result. It is scoped to the service as well as
        /// the id, so a run id belonging to another service cannot be described on this service's
        /// page — the counters shown would be someone else's.
        /// </summary>
        [Fact]
        public void SelectedRunLookupIsScopedToTheService()
        {
            using var db = NewDb();
            db.SvcRunLogs.AddRange(
                new SvcRunLog { Id = 10, SvcServiceId = 1, StartTime = DateTime.UtcNow, Status = "Completed", TotalRecords = 5000, UpdatedRecords = 0, SkippedRecords = 5000 },
                new SvcRunLog { Id = 12, SvcServiceId = 2, StartTime = DateTime.UtcNow, Status = "Completed", TotalRecords = 7, UpdatedRecords = 7 });
            db.SaveChanges();

            var own = db.SvcRunLogs.FirstOrDefault(l => l.Id == 10 && l.SvcServiceId == 1);
            var foreignRun = db.SvcRunLogs.FirstOrDefault(l => l.Id == 12 && l.SvcServiceId == 1);

            Assert.NotNull(own);
            Assert.Null(foreignRun);
        }

        /// <summary>
        /// The case the operator actually hit: a run that scanned thousands of accounts, skipped
        /// every one of them, and therefore wrote no audit entries at all. The entries are empty
        /// while the run's own counters are not — which is what the screen now shows instead of
        /// "run the service first".
        /// </summary>
        [Fact]
        public void ARunThatChangedNothingHasNoEntriesButRealCounters()
        {
            using var db = NewDb();
            db.SvcRunLogs.Add(new SvcRunLog
            {
                Id = 20,
                SvcServiceId = 1,
                StartTime = DateTime.UtcNow,
                Status = "Completed",
                TotalRecords = 5000,
                UpdatedRecords = 0,
                SkippedRecords = 5000
            });
            db.SaveChanges();

            var entries = BuildAuditQuery(db, serviceId: 1, runId: 20, null, null, null, null).ToList();
            var run = db.SvcRunLogs.First(l => l.Id == 20);

            Assert.Empty(entries);
            Assert.Equal(5000, run.TotalRecords);
            Assert.Equal(0, run.UpdatedRecords);
            Assert.Equal(5000, run.SkippedRecords);
        }

        [Fact]
        public void RunIdCombinesWithTheActionFilter()
        {
            using var db = SeededDb();

            var rows = BuildAuditQuery(db, serviceId: 1, runId: 10, action: "InactiveDisabled", null, null, null).ToList();
            Assert.Equal(2, rows.Count);

            Assert.Empty(BuildAuditQuery(db, serviceId: 1, runId: 10, action: "Update", null, null, null).ToList());
        }
    }
}
