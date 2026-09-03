using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Governance;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Guards what a person can do to a non-human account, and — mostly — what they cannot.
    ///
    /// Ownership here is an ordinary identifier with no shape imposed on it: nothing assumes the
    /// owner is staff rather than a contractor, that they carry a number, or that they exist in any
    /// directory. What an institution's people are called is the institution's business.
    /// </summary>
    public class NhiLifecycleServiceTests
    {
        private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        private static GovernanceDbContext NewDb() =>
            new(new DbContextOptionsBuilder<GovernanceDbContext>()
                .UseInMemoryDatabase($"nhi-svc-{Guid.NewGuid()}")
                .Options);

        private static NhiLifecycleService Service(GovernanceDbContext db) =>
            new(db, Mock.Of<IAuditService>(), NullLogger<NhiLifecycleService>.Instance);

        private static async Task<GovNhiAccount> Seed(GovernanceDbContext db, Action<GovNhiAccount>? set = null)
        {
            var a = new GovNhiAccount
            {
                ObjectGuid = Guid.NewGuid().ToString(),
                ServiceId = 1,
                Account = "svc_billing",
                DistinguishedName = "CN=svc_billing,OU=Services,DC=example,DC=org",
                State = GovNhiStates.Discovered,
                FirstSeenUtc = Now.AddDays(-10),
                ClaimDueUtc = Now.AddDays(20)
            };
            set?.Invoke(a);
            db.NhiAccounts.Add(a);
            await db.SaveChangesAsync();
            return a;
        }

        // ══════════════════════════════════════
        // المطالبة
        // ══════════════════════════════════════

        [Fact]
        public async Task ClaimingRecordsTheOwnerAndStartsTheAttestationClock()
        {
            using var db = NewDb();
            var a = await Seed(db);

            var outcome = await Service(db).ClaimAsync(a.Id, "j.okoro", Now);

            Assert.True(outcome.Ok);
            var after = await db.NhiAccounts.SingleAsync();
            Assert.Equal("j.okoro", after.OwnerUsername);
            Assert.Equal(GovNhiStates.Claimed, after.State);
            Assert.Equal(Now, after.LastAttestedUtc);   // claiming it says it is needed today
        }

        /// <summary>Any identifier a person signed in with. No format is assumed or enforced.</summary>
        [Theory]
        [InlineData("j.okoro")]
        [InlineData("440000001")]
        [InlineData("ahmed.sawadi@example.org")]
        [InlineData("EXAMPLE\\svcowner")]
        public async Task AnyKindOfIdentifierCanOwnAnAccount(string username)
        {
            using var db = NewDb();
            var a = await Seed(db);

            Assert.True((await Service(db).ClaimAsync(a.Id, username, Now)).Ok);
            Assert.Equal(username, (await db.NhiAccounts.SingleAsync()).OwnerUsername);
        }

        /// <summary>Quarantine exists to make somebody go looking for an owner; finding one ends it.</summary>
        [Fact]
        public async Task ClaimingAQuarantinedAccountReleasesIt()
        {
            using var db = NewDb();
            var a = await Seed(db, x =>
            {
                x.State = GovNhiStates.Quarantined;
                x.QuarantineReason = GovNhiQuarantineReasons.UnclaimedPastDeadline;
                x.QuarantinedUtc = Now.AddDays(-1);
            });

            Assert.True((await Service(db).ClaimAsync(a.Id, "j.okoro", Now)).Ok);

            var after = await db.NhiAccounts.SingleAsync();
            Assert.Equal(GovNhiStates.Claimed, after.State);
            Assert.Null(after.QuarantineReason);
        }

        /// <summary>
        /// A claim is a governance record, not a directory write. The account was disabled by a run
        /// that held a connection; re-enabling it belongs to a run that holds one too, and the row
        /// keeps saying what was actually done rather than implying it was undone.
        /// </summary>
        [Fact]
        public async Task ClaimingDoesNotSilentlyUndoWhatWasDoneToTheDirectory()
        {
            using var db = NewDb();
            var a = await Seed(db, x =>
            {
                x.State = GovNhiStates.Quarantined;
                x.QuarantineEffect = GovNhiQuarantineEffects.Disabled;
                x.Enabled = false;
            });

            await Service(db).ClaimAsync(a.Id, "j.okoro", Now);

            var after = await db.NhiAccounts.SingleAsync();
            Assert.Equal(GovNhiQuarantineEffects.Disabled, after.QuarantineEffect);
            Assert.False(after.Enabled);
        }

        [Fact]
        public async Task AnAccountCannotBeTakenFromItsOwner()
        {
            using var db = NewDb();
            var a = await Seed(db, x => { x.OwnerUsername = "first.owner"; x.State = GovNhiStates.Claimed; });

            var outcome = await Service(db).ClaimAsync(a.Id, "second.person", Now);

            Assert.False(outcome.Ok);
            Assert.Contains("first.owner", outcome.Problem!);
            Assert.Equal("first.owner", (await db.NhiAccounts.SingleAsync()).OwnerUsername);
        }

        [Fact]
        public async Task ClaimingSomethingUntrackedIsRefusedRatherThanCreatingIt()
        {
            using var db = NewDb();

            Assert.False((await Service(db).ClaimAsync(4242, "j.okoro", Now)).Ok);
            Assert.Empty(db.NhiAccounts);
        }

        // ══════════════════════════════════════
        // الإفراج
        // ══════════════════════════════════════

        /// <summary>
        /// Always allowed for the owner: people staying nominally answerable for accounts they know
        /// nothing about is worse than an honest gap.
        /// </summary>
        [Fact]
        public async Task TheOwnerCanHandTheAccountBack()
        {
            using var db = NewDb();
            var a = await Seed(db, x => { x.OwnerUsername = "j.okoro"; x.State = GovNhiStates.Claimed; });

            Assert.True((await Service(db).DisownAsync(a.Id, "j.okoro", Now)).Ok);

            var after = await db.NhiAccounts.SingleAsync();
            Assert.Null(after.OwnerUsername);
            Assert.Equal(GovNhiStates.Discovered, after.State);
        }

        /// <summary>"Three people have declined this account" is worth knowing.</summary>
        [Fact]
        public async Task AndWhoDeclinedItIsRecorded()
        {
            using var db = NewDb();
            var a = await Seed(db, x => { x.OwnerUsername = "j.okoro"; x.State = GovNhiStates.Claimed; });

            await Service(db).DisownAsync(a.Id, "j.okoro", Now);

            var after = await db.NhiAccounts.SingleAsync();
            Assert.Equal("j.okoro", after.DisownedBy);
            Assert.Equal(Now, after.DisownedUtc);
        }

        /// <summary>Releasing must buy no extension, or an account could be kept alive by passing it around.</summary>
        [Fact]
        public async Task ReleasingDoesNotResetTheClaimDeadline()
        {
            using var db = NewDb();
            var due = Now.AddDays(3);
            var a = await Seed(db, x =>
            {
                x.OwnerUsername = "j.okoro";
                x.State = GovNhiStates.Claimed;
                x.ClaimDueUtc = due;
            });

            await Service(db).DisownAsync(a.Id, "j.okoro", Now);

            Assert.Equal(due, (await db.NhiAccounts.SingleAsync()).ClaimDueUtc);
        }

        [Fact]
        public async Task SomebodyElseCannotReleaseAnAccountTheyDoNotOwn()
        {
            using var db = NewDb();
            var a = await Seed(db, x => { x.OwnerUsername = "j.okoro"; x.State = GovNhiStates.Claimed; });

            Assert.False((await Service(db).DisownAsync(a.Id, "someone.else", Now)).Ok);
            Assert.Equal("j.okoro", (await db.NhiAccounts.SingleAsync()).OwnerUsername);
        }

        // ══════════════════════════════════════
        // الإقرار
        // ══════════════════════════════════════

        [Fact]
        public async Task AttestingRestartsTheClock()
        {
            using var db = NewDb();
            var a = await Seed(db, x =>
            {
                x.OwnerUsername = "j.okoro";
                x.State = GovNhiStates.Claimed;
                x.LastAttestedUtc = Now.AddDays(-200);
            });

            Assert.True((await Service(db).AttestAsync(a.Id, "j.okoro", "still used by the billing integration", Now)).Ok);

            var after = await db.NhiAccounts.SingleAsync();
            Assert.Equal(Now, after.LastAttestedUtc);
            Assert.Equal("still used by the billing integration", after.AttestationNote);
        }

        /// <summary>
        /// An attestation by anybody else records a confirmation nobody answerable gave — worse than
        /// no attestation, because it reads as one.
        /// </summary>
        [Fact]
        public async Task OnlyTheOwnerCanAttest()
        {
            using var db = NewDb();
            var a = await Seed(db, x => { x.OwnerUsername = "j.okoro"; x.State = GovNhiStates.Claimed; });

            var outcome = await Service(db).AttestAsync(a.Id, "a.manager", null, Now);

            Assert.False(outcome.Ok);
            Assert.Contains("j.okoro", outcome.Problem!);
        }

        [Fact]
        public async Task AnUnownedAccountCannotBeAttested()
        {
            using var db = NewDb();
            var a = await Seed(db);

            Assert.False((await Service(db).AttestAsync(a.Id, "j.okoro", null, Now)).Ok);
        }

        [Fact]
        public async Task AttestingAQuarantinedAccountRestoresIt()
        {
            using var db = NewDb();
            var a = await Seed(db, x =>
            {
                x.OwnerUsername = "j.okoro";
                x.State = GovNhiStates.Quarantined;
                x.QuarantineReason = GovNhiQuarantineReasons.AttestationLapsed;
            });

            await Service(db).AttestAsync(a.Id, "j.okoro", null, Now);

            var after = await db.NhiAccounts.SingleAsync();
            Assert.Equal(GovNhiStates.Claimed, after.State);
            Assert.Null(after.QuarantineReason);
        }

        // ══════════════════════════════════════
        // الاستثناء
        // ══════════════════════════════════════

        [Fact]
        public async Task AnExemptionRecordsWhoGrantedItAndWhy()
        {
            using var db = NewDb();
            var a = await Seed(db);

            Assert.True((await Service(db).ExemptAsync(a.Id, "an.admin", "break-glass account", Now.AddDays(90), Now)).Ok);

            var after = await db.NhiAccounts.SingleAsync();
            Assert.Equal(GovNhiStates.Exempt, after.State);
            Assert.Equal("an.admin", after.ExemptBy);
            Assert.Equal("break-glass account", after.ExemptReason);
        }

        /// <summary>A permanent hole opened for a temporary reason, closed by nobody.</summary>
        [Fact]
        public async Task AnExemptionWithNoEndDateIsRefused()
        {
            using var db = NewDb();
            var a = await Seed(db);

            var outcome = await Service(db).ExemptAsync(a.Id, "an.admin", "break-glass", null, Now);

            Assert.False(outcome.Ok);
            Assert.Equal(GovNhiStates.Discovered, (await db.NhiAccounts.SingleAsync()).State);
        }

        [Fact]
        public async Task AnExemptionWithNoReasonIsRefused()
        {
            using var db = NewDb();
            var a = await Seed(db);

            Assert.False((await Service(db).ExemptAsync(a.Id, "an.admin", "  ", Now.AddDays(30), Now)).Ok);
        }

        [Fact]
        public async Task AnExemptionCanBeEndedEarly()
        {
            using var db = NewDb();
            var a = await Seed(db, x =>
            {
                x.State = GovNhiStates.Exempt;
                x.ExemptReason = "break-glass";
                x.ExemptUntilUtc = Now.AddDays(90);
            });

            Assert.True((await Service(db).EndExemptionAsync(a.Id, "an.admin", Now)).Ok);
            Assert.Equal(GovNhiStates.Discovered, (await db.NhiAccounts.SingleAsync()).State);
        }

        [Fact]
        public async Task EndingAnExemptionReturnsAnOwnedAccountToItsOwner()
        {
            using var db = NewDb();
            var a = await Seed(db, x =>
            {
                x.OwnerUsername = "j.okoro";
                x.State = GovNhiStates.Exempt;
                x.ExemptReason = "break-glass";
                x.ExemptUntilUtc = Now.AddDays(90);
            });

            await Service(db).EndExemptionAsync(a.Id, "an.admin", Now);

            Assert.Equal(GovNhiStates.Claimed, (await db.NhiAccounts.SingleAsync()).State);
        }

        // ══════════════════════════════════════
        // شاشة المالك
        // ══════════════════════════════════════

        [Fact]
        public async Task AnOwnerSeesTheirOwnAccountsAndNobodyElseS()
        {
            using var db = NewDb();
            await Seed(db, x => { x.Account = "svc_a"; x.OwnerUsername = "j.okoro"; });
            await Seed(db, x => { x.Account = "svc_b"; x.OwnerUsername = "someone.else"; });
            await Seed(db, x => { x.Account = "svc_c"; });

            var mine = await Service(db).OwnedByAsync("j.okoro");

            Assert.Single(mine);
            Assert.Equal("svc_a", mine[0].Account);
        }

        /// <summary>Directories return names in inconsistent case; an owner must not lose their list to it.</summary>
        [Fact]
        public async Task TheOwnerListIsNotCaseSensitive()
        {
            using var db = NewDb();
            await Seed(db, x => x.OwnerUsername = "J.Okoro");

            Assert.Single(await Service(db).OwnedByAsync("j.okoro"));
        }
    }
}
