using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Models.Governance;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Guards who gets told about a non-human account, and how often.
    ///
    /// The failure this module exists to prevent is the quiet one: an account walks from discovered
    /// to quarantined and the person answerable for it is never told, because their address could
    /// not be resolved and nothing said so. The lifecycle would have run perfectly and nobody would
    /// have had a chance to act.
    /// </summary>
    public class NhiNotificationPlanTests
    {
        private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        private static NhiLifecyclePolicy.LifecycleConfig Config(
            int claimDays = 30, int attestationDays = 180, int graceDays = 14) =>
            new(true, claimDays, attestationDays, graceDays, GovNhiEnforcement.Report, 20);

        private static NhiNotificationPlan.Timing Timing(int remindEvery = 7, int warnBefore = 14) =>
            new(remindEvery, warnBefore);

        private static GovNhiAccount Account(
            string account = "svc_billing",
            string? owner = null,
            string state = GovNhiStates.Discovered,
            DateTime? claimDue = null,
            DateTime? lastAttested = null,
            DateTime? lastNotified = null,
            DateTime? retired = null,
            DateTime? exemptUntil = null) =>
            new()
            {
                ObjectGuid = Guid.NewGuid().ToString(),
                Account = account,
                DistinguishedName = $"CN={account},OU=Services,DC=example,DC=org",
                OwnerUsername = owner,
                OwnerConfirmedUtc = owner != null ? Now.AddYears(-1) : null,
                State = state,
                FirstSeenUtc = Now.AddDays(-1),
                ClaimDueUtc = claimDue ?? Now.AddDays(29),
                LastAttestedUtc = lastAttested,
                LastNotifiedUtc = lastNotified,
                RetiredUtc = retired,
                ExemptUntilUtc = exemptUntil
            };

        /// <summary>Everyone is reachable — the ordinary case.</summary>
        private static readonly Func<string, string?> Reachable = o => $"{o}@example.org";

        /// <summary>Nobody is — a directory that is down, or owners who are not directory accounts at all.</summary>
        private static readonly Func<string, string?> Unreachable = _ => null;

        // ══════════════════════════════════════
        // ⛔ المالك الذي لا يمكن الوصول إليه
        // ══════════════════════════════════════

        /// <summary>
        /// The guard the module was written for. An owner with no address must be named somewhere,
        /// because their account is heading for quarantine and they will not hear about it.
        /// </summary>
        [Fact]
        public void AnOwnerWithNoAddressIsNamed_NotSkipped()
        {
            var a = Account(owner: "j.okoro", state: GovNhiStates.Claimed, lastAttested: Now.AddDays(-200));

            var plan = NhiNotificationPlan.Build(new[] { a }, Config(), Timing(), Unreachable, Now);

            Assert.Contains("j.okoro", plan.Unreachable);
            Assert.Empty(plan.ByOwner);
        }

        /// <summary>And the account still reaches the operator, so somebody sees it.</summary>
        [Fact]
        public void AndTheAccountStillAppearsInTheDigest()
        {
            var a = Account(owner: "j.okoro", state: GovNhiStates.Claimed, lastAttested: Now.AddDays(-200));

            var plan = NhiNotificationPlan.Build(new[] { a }, Config(), Timing(), Unreachable, Now);

            Assert.Single(plan.Digest);
            Assert.Equal("svc_billing", plan.Digest[0].Account.Account);
        }

        [Fact]
        public void AReachableOwnerIsNotListedAsUnreachable()
        {
            var a = Account(owner: "j.okoro", state: GovNhiStates.Claimed, lastAttested: Now.AddDays(-200));

            var plan = NhiNotificationPlan.Build(new[] { a }, Config(), Timing(), Reachable, Now);

            Assert.Empty(plan.Unreachable);
            Assert.True(plan.ByOwner.ContainsKey("j.okoro"));
        }

        /// <summary>An identifier that is already an address needs no directory at all.</summary>
        [Fact]
        public void AnOwnerWhoseIdentifierIsAnAddressIsReachable()
        {
            var a = Account(owner: "ops@example.org", state: GovNhiStates.Claimed, lastAttested: Now.AddDays(-200));

            var plan = NhiNotificationPlan.Build(new[] { a }, Config(), Timing(),
                o => o.Contains('@') ? o : null, Now);

            Assert.Empty(plan.Unreachable);
            Assert.True(plan.ByOwner.ContainsKey("ops@example.org"));
        }

        // ══════════════════════════════════════
        // رسالة واحدة لكل شخص
        // ══════════════════════════════════════

        /// <summary>Twelve notices is a filter rule; one notice is a task.</summary>
        [Fact]
        public void OneOwnerWithManyAccountsGetsOneMessage()
        {
            var accounts = Enumerable.Range(0, 12)
                .Select(i => Account($"svc_{i}", owner: "j.okoro", state: GovNhiStates.Claimed,
                                     lastAttested: Now.AddDays(-200)))
                .ToArray();

            var plan = NhiNotificationPlan.Build(accounts, Config(), Timing(), Reachable, Now);

            Assert.Single(plan.ByOwner);
            Assert.Equal(12, plan.ByOwner["j.okoro"].Count);
        }

        [Fact]
        public void DifferentOwnersGetDifferentMessages()
        {
            var accounts = new[]
            {
                Account("svc_a", owner: "j.okoro", state: GovNhiStates.Claimed, lastAttested: Now.AddDays(-200)),
                Account("svc_b", owner: "a.mensah", state: GovNhiStates.Claimed, lastAttested: Now.AddDays(-200))
            };

            var plan = NhiNotificationPlan.Build(accounts, Config(), Timing(), Reachable, Now);

            Assert.Equal(2, plan.ByOwner.Count);
        }

        /// <summary>Directories are inconsistent about case; one person must not become two mailboxes.</summary>
        [Fact]
        public void TheSameOwnerInDifferentCaseIsOnePerson()
        {
            var accounts = new[]
            {
                Account("svc_a", owner: "J.Okoro", state: GovNhiStates.Claimed, lastAttested: Now.AddDays(-200)),
                Account("svc_b", owner: "j.okoro", state: GovNhiStates.Claimed, lastAttested: Now.AddDays(-200))
            };

            var plan = NhiNotificationPlan.Build(accounts, Config(), Timing(), Reachable, Now);

            Assert.Single(plan.ByOwner);
        }

        // ══════════════════════════════════════
        // التباعد
        // ══════════════════════════════════════

        /// <summary>A notice that arrives every morning is one people learn to file unread.</summary>
        [Fact]
        public void AnAccountMentionedThisWeekIsNotMentionedAgain()
        {
            var a = Account(owner: "j.okoro", state: GovNhiStates.Claimed,
                            lastAttested: Now.AddDays(-200), lastNotified: Now.AddDays(-2));

            var plan = NhiNotificationPlan.Build(new[] { a }, Config(), Timing(remindEvery: 7), Reachable, Now);

            Assert.True(plan.Empty);
        }

        [Fact]
        public void OnceTheIntervalHasPassedItIsMentionedAgain()
        {
            var a = Account(owner: "j.okoro", state: GovNhiStates.Claimed,
                            lastAttested: Now.AddDays(-200), lastNotified: Now.AddDays(-8));

            var plan = NhiNotificationPlan.Build(new[] { a }, Config(), Timing(remindEvery: 7), Reachable, Now);

            Assert.Single(plan.Digest);
        }

        [Fact]
        public void AnAccountNeverMentionedIsMentioned()
        {
            var a = Account(owner: "j.okoro", state: GovNhiStates.Claimed,
                            lastAttested: Now.AddDays(-200), lastNotified: null);

            Assert.Single(NhiNotificationPlan.Build(new[] { a }, Config(), Timing(), Reachable, Now).Digest);
        }

        // ══════════════════════════════════════
        // ما الذي يستحق الذكر
        // ══════════════════════════════════════

        [Fact]
        public void AQuarantinedAccountIsAlwaysWorthSaying()
        {
            var a = Account(owner: "j.okoro", state: GovNhiStates.Quarantined);

            var plan = NhiNotificationPlan.Build(new[] { a }, Config(), Timing(), Reachable, Now);

            Assert.Equal(NhiNotificationPlan.Reason.Quarantined, plan.Digest[0].Reason);
        }

        [Fact]
        public void AnOverdueAttestationIsReported()
        {
            var a = Account(owner: "j.okoro", state: GovNhiStates.Claimed, lastAttested: Now.AddDays(-181));

            var plan = NhiNotificationPlan.Build(new[] { a }, Config(attestationDays: 180), Timing(), Reachable, Now);

            Assert.Equal(NhiNotificationPlan.Reason.AttestationOverdue, plan.Digest[0].Reason);
        }

        /// <summary>Warning before the deadline is the whole point — after it, the account is already gone.</summary>
        [Fact]
        public void AnAttestationComingUpIsReportedBeforeItLapses()
        {
            var a = Account(owner: "j.okoro", state: GovNhiStates.Claimed, lastAttested: Now.AddDays(-170));

            var plan = NhiNotificationPlan.Build(new[] { a }, Config(attestationDays: 180), Timing(warnBefore: 14), Reachable, Now);

            Assert.Equal(NhiNotificationPlan.Reason.AttestationDueSoon, plan.Digest[0].Reason);
        }

        [Fact]
        public void AnAttestationFarInTheFutureIsNotWorthAMessage()
        {
            var a = Account(owner: "j.okoro", state: GovNhiStates.Claimed, lastAttested: Now.AddDays(-10));

            Assert.True(NhiNotificationPlan.Build(new[] { a }, Config(attestationDays: 180), Timing(), Reachable, Now).Empty);
        }

        [Fact]
        public void AnUnownedAccountNearingItsDeadlineReachesTheDigest()
        {
            var a = Account(claimDue: Now.AddDays(5));

            var plan = NhiNotificationPlan.Build(new[] { a }, Config(), Timing(warnBefore: 14), Reachable, Now);

            Assert.Equal(NhiNotificationPlan.Reason.ClaimDueSoon, plan.Digest[0].Reason);
        }

        /// <summary>Nobody owns it, so there is nobody to write to — the digest is the only channel it has.</summary>
        [Fact]
        public void AnUnownedAccountProducesNoPersonalMessage()
        {
            var a = Account(claimDue: Now.AddDays(5));

            var plan = NhiNotificationPlan.Build(new[] { a }, Config(), Timing(), Reachable, Now);

            Assert.Empty(plan.ByOwner);
            Assert.Empty(plan.Unreachable);
            Assert.Single(plan.Digest);
        }

        [Fact]
        public void AnUnownedAccountPastItsDeadlineIsMarkedAsSuch()
        {
            var a = Account(claimDue: Now.AddDays(-1));

            var plan = NhiNotificationPlan.Build(new[] { a }, Config(), Timing(), Reachable, Now);

            Assert.Equal(NhiNotificationPlan.Reason.ClaimOverdue, plan.Digest[0].Reason);
        }

        // ══════════════════════════════════════
        // ما لا يُذكر
        // ══════════════════════════════════════

        [Fact]
        public void AnExemptAccountIsNotChasedWhileItsExemptionHolds()
        {
            var a = Account(state: GovNhiStates.Exempt, claimDue: Now.AddDays(-100), exemptUntil: Now.AddDays(30));

            Assert.True(NhiNotificationPlan.Build(new[] { a }, Config(), Timing(), Reachable, Now).Empty);
        }

        [Fact]
        public void AnAccountGoneFromTheDirectoryIsNotChased()
        {
            var a = Account(state: GovNhiStates.Retired, retired: Now.AddDays(-1), claimDue: Now.AddDays(-100));

            Assert.True(NhiNotificationPlan.Build(new[] { a }, Config(), Timing(), Reachable, Now).Empty);
        }

        [Fact]
        public void AFreshlyClaimedAccountIsLeftAlone()
        {
            var a = Account(owner: "j.okoro", state: GovNhiStates.Claimed, lastAttested: Now);

            Assert.True(NhiNotificationPlan.Build(new[] { a }, Config(), Timing(), Reachable, Now).Empty);
        }

        // ══════════════════════════════════════
        // الترتيب
        // ══════════════════════════════════════

        /// <summary>A reader who stops after three lines should have read the three that matter.</summary>
        [Fact]
        public void TheMostUrgentIsFirst()
        {
            var accounts = new[]
            {
                Account("svc_soon", owner: "j.okoro", state: GovNhiStates.Claimed, lastAttested: Now.AddDays(-170)),
                Account("svc_held", owner: "j.okoro", state: GovNhiStates.Quarantined),
                Account("svc_late", owner: "j.okoro", state: GovNhiStates.Claimed, lastAttested: Now.AddDays(-190))
            };

            var plan = NhiNotificationPlan.Build(accounts, Config(attestationDays: 180, graceDays: 30), Timing(), Reachable, Now);

            Assert.Equal("svc_held", plan.Digest[0].Account.Account);
            Assert.Equal("svc_late", plan.Digest[1].Account.Account);
            Assert.Equal("svc_soon", plan.Digest[2].Account.Account);
        }

        [Fact]
        public void AnEmptyPopulationProducesNothingToSend()
        {
            Assert.True(NhiNotificationPlan.Build(
                Array.Empty<GovNhiAccount>(), Config(), Timing(), Reachable, Now).Empty);
        }

        [Fact]
        public void EveryReasonHasABilingualLabel()
        {
            foreach (var reason in Enum.GetValues<NhiNotificationPlan.Reason>())
            {
                var label = NhiNotificationPlan.Label(reason);

                // Both halves present, and not the bare enum name falling through the switch —
                // which is what a newly added reason would produce if nobody gave it a label.
                Assert.Contains(" / ", label);
                Assert.NotEqual(reason.ToString(), label);
            }
        }
    }
}
