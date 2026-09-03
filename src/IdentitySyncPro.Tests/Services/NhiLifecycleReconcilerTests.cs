using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Models.Governance;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Guards the step that turns a scan into a population with a memory.
    ///
    /// The reconciliation is where continuity lives or dies: get the identity key wrong and every
    /// routine change in the directory looks like an account disappearing and a new one arriving,
    /// which loses the owner and restarts a claim window that was answered months ago. Most of what
    /// follows is that one property, tested from several directions.
    /// </summary>
    public class NhiLifecycleReconcilerTests
    {
        private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        private const int ServiceId = 7;

        private static GovernanceDbContext NewDb() =>
            new(new DbContextOptionsBuilder<GovernanceDbContext>()
                .UseInMemoryDatabase($"nhi-{Guid.NewGuid()}")
                .Options);

        private static NhiLifecycleReconciler Reconciler(GovernanceDbContext db) =>
            new(db, NullLogger<NhiLifecycleReconciler>.Instance);

        private static NhiLifecyclePolicy.LifecycleConfig Config(
            int claimDays = 30, int attestationDays = 180, int graceDays = 14, int maxPercent = 100) =>
            new(true, claimDays, attestationDays, graceDays, GovNhiEnforcement.Report, maxPercent);

        private static NhiLifecycleReconciler.Discovered Found(
            string guid = "aaaaaaaa-1111-1111-1111-111111111111",
            string account = "svc_billing",
            string dn = "CN=svc_billing,OU=Services,DC=example,DC=org",
            bool privileged = false,
            bool self = false,
            string? owner = null) =>
            new(guid, account, dn, account, null, "spn", privileged, true, owner, self);

        // ══════════════════════════════════════
        // الاستمرارية
        // ══════════════════════════════════════

        [Fact]
        public async Task AFirstScanStartsTrackingWhatItFound()
        {
            using var db = NewDb();

            var result = await Reconciler(db).ReconcileAsync(ServiceId, new[] { Found() }, Config(), Now);

            Assert.Equal(1, result.Added);
            Assert.Equal(1, result.Tracked);

            var row = await db.NhiAccounts.SingleAsync();
            Assert.Equal(GovNhiStates.Discovered, row.State);
            Assert.Equal(Now.AddDays(30), row.ClaimDueUtc);
        }

        [Fact]
        public async Task ASecondScanOfTheSameAccountAddsNothing()
        {
            using var db = NewDb();
            await Reconciler(db).ReconcileAsync(ServiceId, new[] { Found() }, Config(), Now);

            var result = await Reconciler(db).ReconcileAsync(ServiceId, new[] { Found() }, Config(), Now.AddDays(1));

            Assert.Equal(0, result.Added);
            Assert.Equal(1, await db.NhiAccounts.CountAsync());
        }

        /// <summary>
        /// The property the whole design rests on. Somebody tidies the directory and moves the
        /// account to another OU; keyed on the distinguished name this would be a disappearance and
        /// an arrival, and the owner would be gone.
        /// </summary>
        [Fact]
        public async Task MovingAnAccountToAnotherOuKeepsItsOwnerAndItsHistory()
        {
            using var db = NewDb();
            var r = Reconciler(db);
            await r.ReconcileAsync(ServiceId, new[] { Found() }, Config(), Now);

            var row = await db.NhiAccounts.SingleAsync();
            row.OwnerUsername = "owner.one";
            row.OwnerConfirmedUtc = Now;
            row.LastAttestedUtc = Now;
            row.State = GovNhiStates.Claimed;
            await db.SaveChangesAsync();

            var moved = Found(dn: "CN=svc_billing,OU=Retired,DC=example,DC=org");
            var result = await Reconciler(db).ReconcileAsync(ServiceId, new[] { moved }, Config(), Now.AddDays(1));

            Assert.Equal(0, result.Added);
            Assert.Equal(0, result.Retired);

            var after = await db.NhiAccounts.SingleAsync();
            Assert.Equal("owner.one", after.OwnerUsername);
            Assert.Equal("CN=svc_billing,OU=Retired,DC=example,DC=org", after.DistinguishedName);
        }

        /// <summary>The same, for a rename: the account name is a display fact, not the identity.</summary>
        [Fact]
        public async Task RenamingAnAccountKeepsItsOwner()
        {
            using var db = NewDb();
            await Reconciler(db).ReconcileAsync(ServiceId, new[] { Found() }, Config(), Now);

            var row = await db.NhiAccounts.SingleAsync();
            row.OwnerUsername = "owner.one";
            row.State = GovNhiStates.Claimed;
            await db.SaveChangesAsync();

            await Reconciler(db).ReconcileAsync(ServiceId, new[] { Found(account: "svc_billing_v2") }, Config(), Now.AddDays(1));

            var after = await db.NhiAccounts.SingleAsync();
            Assert.Equal("owner.one", after.OwnerUsername);
            Assert.Equal("svc_billing_v2", after.Account);
        }

        /// <summary>Two different accounts must not merge just because they look alike.</summary>
        [Fact]
        public async Task TwoAccountsWithDifferentIdentitiesStayTwoAccounts()
        {
            using var db = NewDb();

            await Reconciler(db).ReconcileAsync(ServiceId, new[]
            {
                Found(guid: "aaaaaaaa-1111-1111-1111-111111111111", account: "svc_a"),
                Found(guid: "bbbbbbbb-2222-2222-2222-222222222222", account: "svc_b")
            }, Config(), Now);

            Assert.Equal(2, await db.NhiAccounts.CountAsync());
        }

        /// <summary>Each service keeps its own population — two services scanning one domain are two inventories.</summary>
        [Fact]
        public async Task TheSameAccountUnderTwoServicesIsTrackedTwice()
        {
            using var db = NewDb();
            await Reconciler(db).ReconcileAsync(1, new[] { Found() }, Config(), Now);
            await Reconciler(db).ReconcileAsync(2, new[] { Found() }, Config(), Now);

            Assert.Equal(2, await db.NhiAccounts.CountAsync());
        }

        // ══════════════════════════════════════
        // ⛔ حساب بلا هوية
        // ══════════════════════════════════════

        /// <summary>
        /// An account with no readable objectGUID would be tracked under an empty key, and every
        /// such account would collapse onto one row — a whole domain of service accounts reading as
        /// a single tracked identity. Refused rather than skipped, because skipping shrinks the
        /// population silently and the population is the denominator the ceiling is measured against.
        /// </summary>
        [Fact]
        public async Task AnAccountWithNoReadableIdentityStopsTheRun()
        {
            using var db = NewDb();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Reconciler(db).ReconcileAsync(ServiceId, new[] { Found(guid: "") }, Config(), Now));

            Assert.Contains("objectGUID", ex.Message);
            Assert.Empty(db.NhiAccounts);
        }

        [Fact]
        public async Task AndItNamesTheAccountSoItCanBeFound()
        {
            using var db = NewDb();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Reconciler(db).ReconcileAsync(ServiceId, new[] { Found(guid: " ", account: "svc_nameless") }, Config(), Now));

            Assert.Contains("svc_nameless", ex.Message);
        }

        [Fact]
        public async Task InvalidLifecycleSettingsStopTheRunBeforeAnythingIsWritten()
        {
            using var db = NewDb();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Reconciler(db).ReconcileAsync(ServiceId, new[] { Found() }, Config(claimDays: 0), Now));

            Assert.Empty(db.NhiAccounts);
        }

        // ══════════════════════════════════════
        // الاختفاء والعودة
        // ══════════════════════════════════════

        /// <summary>The row stays: what was quarantined and then deleted is what an auditor asks about.</summary>
        [Fact]
        public async Task AnAccountGoneFromTheDirectoryIsMarkedNotDeleted()
        {
            using var db = NewDb();
            await Reconciler(db).ReconcileAsync(ServiceId, new[] { Found() }, Config(), Now);

            var result = await Reconciler(db).ReconcileAsync(
                ServiceId, Array.Empty<NhiLifecycleReconciler.Discovered>(), Config(), Now.AddDays(1));

            Assert.Equal(1, result.Retired);
            var row = await db.NhiAccounts.SingleAsync();
            Assert.Equal(GovNhiStates.Retired, row.State);
            Assert.NotNull(row.RetiredUtc);
        }

        [Fact]
        public async Task AnAccountThatComesBackKeepsItsOwner()
        {
            using var db = NewDb();
            await Reconciler(db).ReconcileAsync(ServiceId, new[] { Found() }, Config(), Now);

            var row = await db.NhiAccounts.SingleAsync();
            row.OwnerUsername = "owner.one";
            row.State = GovNhiStates.Claimed;
            await db.SaveChangesAsync();

            await Reconciler(db).ReconcileAsync(ServiceId, Array.Empty<NhiLifecycleReconciler.Discovered>(), Config(), Now.AddDays(1));
            await Reconciler(db).ReconcileAsync(ServiceId, new[] { Found() }, Config(), Now.AddDays(2));

            var after = await db.NhiAccounts.SingleAsync();
            Assert.Null(after.RetiredUtc);
            Assert.Equal("owner.one", after.OwnerUsername);
            Assert.Equal(GovNhiStates.Claimed, after.State);
        }

        /// <summary>A retired account is not part of the live population the ceiling is measured against.</summary>
        [Fact]
        public async Task RetiredAccountsAreNotCountedInThePopulation()
        {
            using var db = NewDb();
            await Reconciler(db).ReconcileAsync(ServiceId, new[]
            {
                Found(guid: "aaaaaaaa-1111-1111-1111-111111111111", account: "svc_a"),
                Found(guid: "bbbbbbbb-2222-2222-2222-222222222222", account: "svc_b")
            }, Config(), Now);

            var result = await Reconciler(db).ReconcileAsync(
                ServiceId, new[] { Found(guid: "aaaaaaaa-1111-1111-1111-111111111111", account: "svc_a") },
                Config(), Now.AddDays(1));

            Assert.Equal(1, result.Tracked);
        }

        // ══════════════════════════════════════
        // الحجر
        // ══════════════════════════════════════

        [Fact]
        public async Task AnAccountPastItsClaimWindowIsQuarantined()
        {
            using var db = NewDb();
            await Reconciler(db).ReconcileAsync(ServiceId, new[] { Found() }, Config(claimDays: 1), Now);

            var result = await Reconciler(db).ReconcileAsync(
                ServiceId, new[] { Found() }, Config(claimDays: 1), Now.AddDays(5));

            Assert.Single(result.Quarantine);
            var row = await db.NhiAccounts.SingleAsync();
            Assert.Equal(GovNhiStates.Quarantined, row.State);
            Assert.Equal(GovNhiQuarantineReasons.UnclaimedPastDeadline, row.QuarantineReason);
            Assert.NotNull(row.QuarantinedUtc);
        }

        /// <summary>
        /// The ceiling. Quarantining most of a domain's service accounts is a broken classifier or a
        /// lifecycle switched on with the windows left at a day — not policy working.
        /// </summary>
        [Fact]
        public async Task QuarantiningTooLargeAShareStopsTheWholeSweep()
        {
            using var db = NewDb();
            var found = Enumerable.Range(0, 10)
                .Select(i => Found(guid: $"aaaaaaaa-0000-0000-0000-{i:D12}", account: $"svc_{i}"))
                .ToArray();

            await Reconciler(db).ReconcileAsync(ServiceId, found, Config(claimDays: 1, maxPercent: 20), Now);
            var result = await Reconciler(db).ReconcileAsync(ServiceId, found, Config(claimDays: 1, maxPercent: 20), Now.AddDays(5));

            Assert.NotNull(result.Blocked);
            Assert.Empty(result.Quarantine);
        }

        /// <summary>And nothing is left half-quarantined: the whole sweep is off, not the tail of it.</summary>
        [Fact]
        public async Task AndNoAccountIsQuarantinedWhenTheSweepIsStopped()
        {
            using var db = NewDb();
            var found = Enumerable.Range(0, 10)
                .Select(i => Found(guid: $"aaaaaaaa-0000-0000-0000-{i:D12}", account: $"svc_{i}"))
                .ToArray();

            await Reconciler(db).ReconcileAsync(ServiceId, found, Config(claimDays: 1, maxPercent: 20), Now);
            await Reconciler(db).ReconcileAsync(ServiceId, found, Config(claimDays: 1, maxPercent: 20), Now.AddDays(5));

            Assert.Equal(0, await db.NhiAccounts.CountAsync(a => a.State == GovNhiStates.Quarantined));
        }

        [Fact]
        public async Task UnderTheCeilingTheSweepProceeds()
        {
            using var db = NewDb();
            var found = Enumerable.Range(0, 10)
                .Select(i => Found(guid: $"aaaaaaaa-0000-0000-0000-{i:D12}", account: $"svc_{i}"))
                .ToArray();

            await Reconciler(db).ReconcileAsync(ServiceId, found, Config(claimDays: 30, maxPercent: 20), Now);

            // Nine of the ten are claimed; only one reaches its deadline unowned.
            foreach (var row in await db.NhiAccounts.Take(9).ToListAsync())
            {
                row.OwnerUsername = "owner.one";
                row.OwnerConfirmedUtc = Now;
                row.LastAttestedUtc = Now;
                row.State = GovNhiStates.Claimed;
            }
            await db.SaveChangesAsync();

            var result = await Reconciler(db).ReconcileAsync(ServiceId, found, Config(claimDays: 30, maxPercent: 20), Now.AddDays(31));

            Assert.Null(result.Blocked);
            Assert.Single(result.Quarantine);
        }

        // ══════════════════════════════════════
        // ⛔ حساب النظام نفسه
        // ══════════════════════════════════════

        /// <summary>
        /// The bind account goes unclaimed for exactly as long as nobody thinks to claim the
        /// system's own credentials, so it is among the first to reach a deadline.
        /// </summary>
        [Fact]
        public async Task ABindAccountIsNeverQuarantinedByTheSweep()
        {
            using var db = NewDb();
            var self = Found(self: true);

            await Reconciler(db).ReconcileAsync(ServiceId, new[] { self }, Config(claimDays: 1), Now);
            var result = await Reconciler(db).ReconcileAsync(ServiceId, new[] { self }, Config(claimDays: 1), Now.AddDays(5));

            Assert.Empty(result.Quarantine);
            var row = await db.NhiAccounts.SingleAsync();
            Assert.NotEqual(GovNhiStates.Quarantined, row.State);
        }

        [Fact]
        public async Task ButItIsReportedAsAGapRatherThanPassedOver()
        {
            using var db = NewDb();
            var self = Found(self: true);

            await Reconciler(db).ReconcileAsync(ServiceId, new[] { self }, Config(claimDays: 1), Now);
            var result = await Reconciler(db).ReconcileAsync(ServiceId, new[] { self }, Config(claimDays: 1), Now.AddDays(5));

            Assert.Single(result.WithheldQuarantine);
            Assert.Equal(GovNhiQuarantineReasons.UnclaimedPastDeadline, result.WithheldQuarantine[0].Reason);
        }

        /// <summary>
        /// A spared account must not count towards the ceiling either — otherwise a handful of bind
        /// accounts could stop a sweep that was going to act on nothing else.
        /// </summary>
        [Fact]
        public async Task SparedAccountsDoNotPushTheSweepOverItsCeiling()
        {
            using var db = NewDb();
            var found = Enumerable.Range(0, 10)
                .Select(i => Found(guid: $"aaaaaaaa-0000-0000-0000-{i:D12}", account: $"svc_{i}", self: i < 8))
                .ToArray();

            await Reconciler(db).ReconcileAsync(ServiceId, found, Config(claimDays: 1, maxPercent: 30), Now);
            var result = await Reconciler(db).ReconcileAsync(ServiceId, found, Config(claimDays: 1, maxPercent: 30), Now.AddDays(5));

            // Eight are bind accounts and spared; two are ordinary and quarantined — 20% of ten.
            Assert.Null(result.Blocked);
            Assert.Equal(2, result.Quarantine.Count);
            Assert.Equal(8, result.WithheldQuarantine.Count);
        }

        // ══════════════════════════════════════
        // الإقرار
        // ══════════════════════════════════════

        [Fact]
        public async Task AnOwnerWhoIsLateIsReportedBeforeTheAccountIsTaken()
        {
            using var db = NewDb();
            await Reconciler(db).ReconcileAsync(ServiceId, new[] { Found() }, Config(), Now);

            var row = await db.NhiAccounts.SingleAsync();
            row.OwnerUsername = "owner.one";
            row.OwnerConfirmedUtc = Now.AddDays(-185);
            row.LastAttestedUtc = Now.AddDays(-185);
            row.State = GovNhiStates.Claimed;
            await db.SaveChangesAsync();

            var result = await Reconciler(db).ReconcileAsync(
                ServiceId, new[] { Found() }, Config(attestationDays: 180, graceDays: 14), Now);

            Assert.Single(result.AttestationOverdue);
            Assert.Empty(result.Quarantine);
        }

        [Fact]
        public async Task AndTakenOnceTheGraceRunsOut()
        {
            using var db = NewDb();
            await Reconciler(db).ReconcileAsync(ServiceId, new[] { Found() }, Config(), Now);

            var row = await db.NhiAccounts.SingleAsync();
            row.OwnerUsername = "owner.one";
            row.OwnerConfirmedUtc = Now.AddDays(-200);
            row.LastAttestedUtc = Now.AddDays(-200);
            row.State = GovNhiStates.Claimed;
            await db.SaveChangesAsync();

            var result = await Reconciler(db).ReconcileAsync(
                ServiceId, new[] { Found() }, Config(attestationDays: 180, graceDays: 14), Now);

            Assert.Single(result.Quarantine);
            Assert.Equal(GovNhiQuarantineReasons.AttestationLapsed, result.Quarantine[0].QuarantineReason);
        }

        // ══════════════════════════════════════
        // الاستثناء
        // ══════════════════════════════════════

        [Fact]
        public async Task AnExemptAccountIsLeftAloneWhileItsExemptionHolds()
        {
            using var db = NewDb();
            await Reconciler(db).ReconcileAsync(ServiceId, new[] { Found() }, Config(claimDays: 1), Now);

            var row = await db.NhiAccounts.SingleAsync();
            row.State = GovNhiStates.Exempt;
            row.ExemptReason = "break-glass";
            row.ExemptUntilUtc = Now.AddDays(30);
            await db.SaveChangesAsync();

            var result = await Reconciler(db).ReconcileAsync(ServiceId, new[] { Found() }, Config(claimDays: 1), Now.AddDays(10));

            Assert.Empty(result.Quarantine);
            Assert.Equal(GovNhiStates.Exempt, (await db.NhiAccounts.SingleAsync()).State);
        }

        [Fact]
        public async Task AnExpiredExemptionPutsTheAccountBackInTheLifecycle()
        {
            using var db = NewDb();
            await Reconciler(db).ReconcileAsync(ServiceId, new[] { Found() }, Config(claimDays: 1), Now);

            var row = await db.NhiAccounts.SingleAsync();
            row.State = GovNhiStates.Exempt;
            row.ExemptReason = "break-glass";
            row.ExemptUntilUtc = Now.AddDays(5);
            await db.SaveChangesAsync();

            await Reconciler(db).ReconcileAsync(ServiceId, new[] { Found() }, Config(claimDays: 1), Now.AddDays(10));

            Assert.Equal(GovNhiStates.Discovered, (await db.NhiAccounts.SingleAsync()).State);
        }
    }
}
