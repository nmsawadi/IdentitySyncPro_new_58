using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Models.Governance;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Guards the rules that decide when a non-human account loses its access.
    ///
    /// The inventory this rides on was written read-only on purpose, with a note saying the first
    /// thing the feature is allowed to do is count. This module is where that stops being true, so
    /// the tests here are weighted towards the ways it could do harm quietly: quarantining
    /// IdentitySyncPro's own bind account, sweeping a whole domain of service accounts because a
    /// window was left at zero, or expiring an exemption nobody remembers granting.
    /// </summary>
    public class NhiLifecyclePolicyTests
    {
        private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        private static NhiLifecyclePolicy.LifecycleConfig Config(
            int claimDays = 30, int attestationDays = 180, int graceDays = 14,
            string enforcement = GovNhiEnforcement.Report, int maxPercent = 20) =>
            new(true, claimDays, attestationDays, graceDays, enforcement, maxPercent);

        private static GovNhiAccount Account(
            string state = GovNhiStates.Discovered,
            string? owner = null,
            DateTime? firstSeen = null,
            DateTime? claimDue = null,
            DateTime? ownerConfirmed = null,
            DateTime? lastAttested = null,
            bool self = false,
            DateTime? exemptUntil = null,
            DateTime? retired = null) =>
            new()
            {
                ObjectGuid = "11111111-1111-1111-1111-111111111111",
                Account = "svc_billing",
                DistinguishedName = "CN=svc_billing,OU=Services,DC=lab,DC=local",
                State = state,
                OwnerUsername = owner,
                FirstSeenUtc = firstSeen ?? Now.AddDays(-1),
                ClaimDueUtc = claimDue,
                OwnerConfirmedUtc = ownerConfirmed,
                LastAttestedUtc = lastAttested,
                IsSelfAccount = self,
                ExemptUntilUtc = exemptUntil,
                RetiredUtc = retired
            };

        // ══════════════════════════════════════
        // إعدادات لا يجوز أن تعمل
        // ══════════════════════════════════════

        [Fact]
        public void AValidConfigurationPasses()
        {
            Assert.Null(NhiLifecyclePolicy.ValidateConfig(Config()));
        }

        /// <summary>
        /// The dangerous one. A claim window of zero days means every account the very first scan
        /// finds is already past its deadline — an entire domain of service accounts quarantined in
        /// one sweep, before anybody has been asked to claim anything.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void AClaimWindowOfZeroDays_IsRefused(int days)
        {
            var problem = NhiLifecyclePolicy.ValidateConfig(Config(claimDays: days));

            Assert.NotNull(problem);
            Assert.Contains("first scan", problem);
        }

        /// <summary>Refused, not quietly clamped: clamping carries out something near what was asked without saying so.</summary>
        [Fact]
        public void AnImpossibleConfiguration_IsRefusedRatherThanCorrected()
        {
            var problem = NhiLifecyclePolicy.ValidateConfig(Config(claimDays: 0));

            Assert.NotNull(problem);
            Assert.DoesNotContain("adjusted", problem, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AZeroAttestationPeriod_IsRefused() =>
            Assert.NotNull(NhiLifecyclePolicy.ValidateConfig(Config(attestationDays: 0)));

        [Fact]
        public void ANegativeGracePeriod_IsRefused() =>
            Assert.NotNull(NhiLifecyclePolicy.ValidateConfig(Config(graceDays: -1)));

        /// <summary>Zero grace is legitimate — quarantine the day attestation lapses — so it must not be refused.</summary>
        [Fact]
        public void AZeroGracePeriod_IsAllowed() =>
            Assert.Null(NhiLifecyclePolicy.ValidateConfig(Config(graceDays: 0)));

        [Theory]
        [InlineData(0)]
        [InlineData(101)]
        public void ACeilingOutsideOneToHundred_IsRefused(int percent) =>
            Assert.NotNull(NhiLifecyclePolicy.ValidateConfig(Config(maxPercent: percent)));

        [Fact]
        public void AnUnknownQuarantineMode_IsRefused()
        {
            var problem = NhiLifecyclePolicy.ValidateConfig(Config(enforcement: "Delete"));

            Assert.NotNull(problem);
            Assert.Contains("Delete", problem);
        }

        // ══════════════════════════════════════
        // المواعيد
        // ══════════════════════════════════════

        [Fact]
        public void TheClaimDeadlineRunsFromTheDayItWasFirstSeen()
        {
            Assert.Equal(Now.AddDays(30), NhiLifecyclePolicy.ClaimDeadline(Now, Config(claimDays: 30)));
        }

        [Fact]
        public void AttestationIsCountedFromTheLastAttestation()
        {
            var a = Account(owner: "nasser", ownerConfirmed: Now.AddDays(-300), lastAttested: Now.AddDays(-10));

            Assert.Equal(Now.AddDays(-10).AddDays(180), NhiLifecyclePolicy.AttestationDue(a, Config()));
        }

        /// <summary>Accepting an account is itself a statement that it is needed today.</summary>
        [Fact]
        public void WithNoAttestationYet_ItIsCountedFromWhenOwnershipWasAccepted()
        {
            var a = Account(owner: "nasser", ownerConfirmed: Now.AddDays(-30));

            Assert.Equal(Now.AddDays(-30).AddDays(180), NhiLifecyclePolicy.AttestationDue(a, Config()));
        }

        [Fact]
        public void AnUnownedAccountHasNoAttestationDate()
        {
            Assert.Null(NhiLifecyclePolicy.AttestationDue(Account(), Config()));
        }

        // ══════════════════════════════════════
        // آلة الحالة
        // ══════════════════════════════════════

        [Fact]
        public void AnUnownedAccountInsideItsWindow_WaitsForAnOwner()
        {
            var v = NhiLifecyclePolicy.Evaluate(Account(claimDue: Now.AddDays(5)), Config(), Now);

            Assert.Equal(GovNhiStates.Discovered, v.TargetState);
            Assert.Null(v.QuarantineReason);
        }

        [Fact]
        public void AnUnownedAccountPastItsWindow_IsQuarantined()
        {
            var v = NhiLifecyclePolicy.Evaluate(Account(claimDue: Now.AddDays(-1)), Config(), Now);

            Assert.Equal(GovNhiStates.Quarantined, v.TargetState);
            Assert.Equal(GovNhiQuarantineReasons.UnclaimedPastDeadline, v.QuarantineReason);
        }

        /// <summary>A row written before the deadline was stored still has one — computed from when it was first seen.</summary>
        [Fact]
        public void WithNoStoredDeadline_ItIsComputedFromFirstSeen()
        {
            var v = NhiLifecyclePolicy.Evaluate(
                Account(firstSeen: Now.AddDays(-31), claimDue: null), Config(claimDays: 30), Now);

            Assert.Equal(GovNhiStates.Quarantined, v.TargetState);
        }

        [Fact]
        public void AnOwnedAccountWithFreshAttestation_StaysClaimed()
        {
            var v = NhiLifecyclePolicy.Evaluate(
                Account(GovNhiStates.Claimed, owner: "nasser", lastAttested: Now.AddDays(-10)), Config(), Now);

            Assert.Equal(GovNhiStates.Claimed, v.TargetState);
            Assert.False(v.AttestationOverdue);
        }

        /// <summary>
        /// The window between "you are late" and "you have lost it". Without this state the owner
        /// gets no warning that is distinguishable from business as usual.
        /// </summary>
        [Fact]
        public void PastAttestationButInsideGrace_IsOverdueAndStillClaimed()
        {
            var v = NhiLifecyclePolicy.Evaluate(
                Account(GovNhiStates.Claimed, owner: "nasser", lastAttested: Now.AddDays(-185)),
                Config(attestationDays: 180, graceDays: 14), Now);

            Assert.Equal(GovNhiStates.Claimed, v.TargetState);
            Assert.True(v.AttestationOverdue);
        }

        [Fact]
        public void PastAttestationAndPastGrace_IsQuarantined()
        {
            var v = NhiLifecyclePolicy.Evaluate(
                Account(GovNhiStates.Claimed, owner: "nasser", lastAttested: Now.AddDays(-195)),
                Config(attestationDays: 180, graceDays: 14), Now);

            Assert.Equal(GovNhiStates.Quarantined, v.TargetState);
            Assert.Equal(GovNhiQuarantineReasons.AttestationLapsed, v.QuarantineReason);
        }

        /// <summary>Claiming once must not buy permanent immunity — that is what makes attestation mean anything.</summary>
        [Fact]
        public void AnOwnerWhoNeverReAttests_EventuallyLosesTheAccount()
        {
            var v = NhiLifecyclePolicy.Evaluate(
                Account(GovNhiStates.Claimed, owner: "nasser", ownerConfirmed: Now.AddDays(-400)), Config(), Now);

            Assert.Equal(GovNhiStates.Quarantined, v.TargetState);
        }

        /// <summary>Releasing it would undo the only thing that makes anybody go looking for an owner.</summary>
        [Fact]
        public void AQuarantinedAccountIsNotReleasedByTheSweep()
        {
            var a = Account(GovNhiStates.Quarantined);
            a.QuarantineReason = GovNhiQuarantineReasons.UnclaimedPastDeadline;

            var v = NhiLifecyclePolicy.Evaluate(a, Config(), Now);

            Assert.Equal(GovNhiStates.Quarantined, v.TargetState);
        }

        [Fact]
        public void AnAccountGoneFromTheDirectory_IsTerminal()
        {
            var v = NhiLifecyclePolicy.Evaluate(Account(retired: Now.AddDays(-2)), Config(), Now);

            Assert.Equal(GovNhiStates.Retired, v.TargetState);
        }

        // ══════════════════════════════════════
        // الاستثناء ينتهي
        // ══════════════════════════════════════

        [Fact]
        public void AnExemptionInForce_HoldsTheAccountOut()
        {
            var v = NhiLifecyclePolicy.Evaluate(
                Account(GovNhiStates.Exempt, exemptUntil: Now.AddDays(10)), Config(), Now);

            Assert.Equal(GovNhiStates.Exempt, v.TargetState);
        }

        /// <summary>The reason the end date is mandatory: it puts the account back and asks again.</summary>
        [Fact]
        public void AnExpiredExemption_ReturnsAnUnownedAccountToTheLifecycle()
        {
            var v = NhiLifecyclePolicy.Evaluate(
                Account(GovNhiStates.Exempt, exemptUntil: Now.AddDays(-1)), Config(), Now);

            Assert.Equal(GovNhiStates.Discovered, v.TargetState);
        }

        [Fact]
        public void AnExpiredExemption_ReturnsAnOwnedAccountToItsOwner()
        {
            var v = NhiLifecyclePolicy.Evaluate(
                Account(GovNhiStates.Exempt, owner: "nasser", exemptUntil: Now.AddDays(-1)), Config(), Now);

            Assert.Equal(GovNhiStates.Claimed, v.TargetState);
        }

        /// <summary>An exemption row with no end date at all must not behave as though it never expires.</summary>
        [Fact]
        public void AnExemptionWithNoEndDate_DoesNotHoldTheAccountOutForever()
        {
            var v = NhiLifecyclePolicy.Evaluate(
                Account(GovNhiStates.Exempt, exemptUntil: null), Config(), Now);

            Assert.NotEqual(GovNhiStates.Exempt, v.TargetState);
        }

        // ══════════════════════════════════════
        // ⛔ حساب النظام نفسه
        // ══════════════════════════════════════

        /// <summary>
        /// The one that stops the product. IdentitySyncPro's bind accounts match every definition
        /// of a non-human account, and they go unclaimed for exactly as long as nobody thinks to
        /// claim the system's own credentials — so they are the accounts most likely to reach a
        /// claim deadline first.
        /// </summary>
        [Fact]
        public void ABindAccountPastItsDeadline_IsNeverQuarantined()
        {
            var v = NhiLifecyclePolicy.Evaluate(
                Account(claimDue: Now.AddDays(-100), self: true), Config(), Now);

            Assert.NotEqual(GovNhiStates.Quarantined, v.TargetState);
            Assert.Equal(GovNhiStates.Discovered, v.TargetState);
        }

        /// <summary>
        /// Withheld, not hidden. An unclaimed bind account is a real gap; swallowing it here would
        /// conceal exactly the accounts nobody is watching.
        /// </summary>
        [Fact]
        public void AndTheWithheldQuarantineIsStillReported()
        {
            var v = NhiLifecyclePolicy.Evaluate(
                Account(claimDue: Now.AddDays(-100), self: true), Config(), Now);

            Assert.Equal(GovNhiQuarantineReasons.UnclaimedPastDeadline, v.SuppressedQuarantine);
            Assert.Contains("withheld", v.Note!);
        }

        [Fact]
        public void ABindAccountWithALapsedAttestation_IsAlsoSpared()
        {
            var v = NhiLifecyclePolicy.Evaluate(
                Account(GovNhiStates.Claimed, owner: "nasser", lastAttested: Now.AddDays(-400), self: true),
                Config(), Now);

            Assert.Equal(GovNhiStates.Claimed, v.TargetState);
            Assert.Equal(GovNhiQuarantineReasons.AttestationLapsed, v.SuppressedQuarantine);
        }

        /// <summary>
        /// There must be no way to ask the policy about a protected account and be told to
        /// quarantine it — the guard is inside the one function every caller uses, not a second
        /// answer each caller has to remember to combine.
        /// </summary>
        [Fact]
        public void NoConfigurationProducesAQuarantineVerdictForABindAccount()
        {
            var states = new[] { GovNhiStates.Discovered, GovNhiStates.Claimed };

            foreach (var state in states)
            foreach (var days in new[] { 1, 30, 365 })
            {
                var a = Account(state, owner: state == GovNhiStates.Claimed ? "nasser" : null,
                    firstSeen: Now.AddYears(-5), claimDue: Now.AddYears(-5),
                    ownerConfirmed: Now.AddYears(-5), lastAttested: Now.AddYears(-5), self: true);

                var v = NhiLifecyclePolicy.Evaluate(a, Config(claimDays: days, attestationDays: days, graceDays: 0), Now);

                Assert.NotEqual(GovNhiStates.Quarantined, v.TargetState);
            }
        }

        [Fact]
        public void ProtectionNamesTheBindAccountExplicitly()
        {
            var reason = NhiLifecyclePolicy.ProtectedReason(Account(self: true));

            Assert.NotNull(reason);
            Assert.Contains("IdentitySyncPro", reason);
        }

        [Fact]
        public void AnOrdinaryAccountIsNotProtected()
        {
            Assert.Null(NhiLifecyclePolicy.ProtectedReason(Account()));
        }

        // ══════════════════════════════════════
        // ⛔ الحارس قبل الكتابة في الدليل
        // ══════════════════════════════════════

        /// <summary>
        /// The registry's own contract: an entry it could not resolve is an account that cannot be
        /// proved <i>not</i> to be the one about to be disabled.
        /// </summary>
        [Theory]
        [InlineData(GovNhiEnforcement.RemovePrivilege)]
        [InlineData(GovNhiEnforcement.Disable)]
        public void AnUnresolvedBindAccount_RefusesEnforcementForTheWholeRun(string mode)
        {
            var right = NhiLifecyclePolicy.MayEnforce(mode, unresolvedSelfAccounts: 1);

            Assert.False(right.Allowed);
            Assert.Contains("bind account", right.Reason!);
        }

        /// <summary>Refused for the run, not skipped per account — a sweep that omits what it could not verify is the failure this codebase keeps refusing.</summary>
        [Fact]
        public void ReportingIsStillAllowedWhenABindAccountIsUnresolved()
        {
            Assert.True(NhiLifecyclePolicy.MayEnforce(GovNhiEnforcement.Report, unresolvedSelfAccounts: 3).Allowed);
        }

        [Theory]
        [InlineData(GovNhiEnforcement.RemovePrivilege)]
        [InlineData(GovNhiEnforcement.Disable)]
        public void WithEveryBindAccountResolved_EnforcementIsAllowed(string mode)
        {
            Assert.True(NhiLifecyclePolicy.MayEnforce(mode, unresolvedSelfAccounts: 0).Allowed);
        }

        [Fact]
        public void AnUnknownModeNeverEnforces()
        {
            Assert.False(NhiLifecyclePolicy.MayEnforce("Delete", 0).Allowed);
        }

        [Fact]
        public void ReportIsTheOnlyModeThatDoesNotTouchTheDirectory()
        {
            Assert.False(GovNhiEnforcement.TouchesDirectory(GovNhiEnforcement.Report));
            Assert.True(GovNhiEnforcement.TouchesDirectory(GovNhiEnforcement.RemovePrivilege));
            Assert.True(GovNhiEnforcement.TouchesDirectory(GovNhiEnforcement.Disable));
        }

        /// <summary>A mode nobody recognises must not be read as permission to act.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Delete")]
        public void AnUnrecognisedMode_DoesNotTouchTheDirectory(string? mode)
        {
            Assert.False(GovNhiEnforcement.TouchesDirectory(mode));
        }

        // ══════════════════════════════════════
        // ⛔ سقف الحجر الجماعي
        // ══════════════════════════════════════

        [Fact]
        public void QuarantiningASmallShareIsAllowed()
        {
            var v = NhiLifecyclePolicy.MayQuarantine(total: 100, quarantining: 10, maxPercent: 20);

            Assert.True(v.Allowed);
        }

        /// <summary>Quarantining most of a domain's service accounts is a broken classifier, not policy working.</summary>
        [Fact]
        public void QuarantiningMostOfThePopulation_StopsTheRun()
        {
            var v = NhiLifecyclePolicy.MayQuarantine(total: 100, quarantining: 60, maxPercent: 20);

            Assert.False(v.Allowed);
            Assert.Contains("ceiling", v.Reason!);
        }

        [Fact]
        public void ExactlyAtTheCeilingIsAllowed()
        {
            Assert.True(NhiLifecyclePolicy.MayQuarantine(total: 100, quarantining: 20, maxPercent: 20).Allowed);
        }

        [Fact]
        public void OneOverTheCeilingIsNot()
        {
            Assert.False(NhiLifecyclePolicy.MayQuarantine(total: 100, quarantining: 21, maxPercent: 20).Allowed);
        }

        /// <summary>
        /// An empty population means the scan found nothing, which is a reason to stop rather than
        /// a green light — the same shape as the empty-source guard in OrphanCleanup.
        /// </summary>
        [Fact]
        public void AnEmptyPopulationRefusesToAct()
        {
            var v = NhiLifecyclePolicy.MayQuarantine(total: 0, quarantining: 0, maxPercent: 20);

            Assert.False(v.Allowed);
            Assert.Contains("empty", v.Reason!);
        }

        [Fact]
        public void QuarantiningNothingOutOfAKnownPopulationIsFine()
        {
            Assert.True(NhiLifecyclePolicy.MayQuarantine(total: 500, quarantining: 0, maxPercent: 20).Allowed);
        }

        // ══════════════════════════════════════
        // أفعال البشر
        // ══════════════════════════════════════

        [Fact]
        public void AnyoneMayClaimAnUnownedAccount()
        {
            Assert.Null(NhiLifecyclePolicy.CanClaim(Account(), "nasser"));
        }

        /// <summary>Otherwise an account could be quietly taken from the person the audit trail names.</summary>
        [Fact]
        public void AClaimDoesNotOverwriteAnExistingOwner()
        {
            var problem = NhiLifecyclePolicy.CanClaim(Account(owner: "nasser"), "someone.else");

            Assert.NotNull(problem);
            Assert.Contains("nasser", problem);
        }

        [Fact]
        public void TheOwnerReClaimingTheirOwnAccountIsNotAnError()
        {
            Assert.Null(NhiLifecyclePolicy.CanClaim(Account(owner: "nasser"), "NASSER"));
        }

        [Fact]
        public void ARetiredAccountCannotBeClaimed()
        {
            Assert.NotNull(NhiLifecyclePolicy.CanClaim(Account(retired: Now), "nasser"));
        }

        /// <summary>A quarantined account can be claimed — that is what quarantine is for.</summary>
        [Fact]
        public void AQuarantinedAccountCanStillBeClaimed()
        {
            Assert.Null(NhiLifecyclePolicy.CanClaim(Account(GovNhiStates.Quarantined), "nasser"));
        }

        [Fact]
        public void OnlyTheOwnerMayAttest()
        {
            Assert.NotNull(NhiLifecyclePolicy.CanAttest(Account(owner: "nasser"), "someone.else"));
            Assert.Null(NhiLifecyclePolicy.CanAttest(Account(owner: "nasser"), "nasser"));
        }

        /// <summary>An attestation on an unowned account records a confirmation nobody answerable gave.</summary>
        [Fact]
        public void AnUnownedAccountCannotBeAttested()
        {
            Assert.NotNull(NhiLifecyclePolicy.CanAttest(Account(), "nasser"));
        }

        [Fact]
        public void OnlyTheOwnerMayRelease()
        {
            Assert.NotNull(NhiLifecyclePolicy.CanDisown(Account(owner: "nasser"), "someone.else"));
            Assert.Null(NhiLifecyclePolicy.CanDisown(Account(owner: "nasser"), "nasser"));
        }

        [Fact]
        public void ThereIsNothingToReleaseOnAnUnownedAccount()
        {
            Assert.NotNull(NhiLifecyclePolicy.CanDisown(Account(), "nasser"));
        }

        // ══════════════════════════════════════
        // الاستثناء يجب أن يقول لماذا وإلى متى
        // ══════════════════════════════════════

        [Fact]
        public void AValidExemptionPasses()
        {
            Assert.Null(NhiLifecyclePolicy.ValidateExemption("break-glass account", Now.AddDays(90), Now));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void AnExemptionWithoutAReasonIsRefused(string? reason)
        {
            Assert.NotNull(NhiLifecyclePolicy.ValidateExemption(reason, Now.AddDays(90), Now));
        }

        /// <summary>A permanent hole opened for a temporary reason, closed by nobody.</summary>
        [Fact]
        public void AnExemptionWithoutAnEndDateIsRefused()
        {
            var problem = NhiLifecyclePolicy.ValidateExemption("break-glass", null, Now);

            Assert.NotNull(problem);
            Assert.Contains("end date", problem);
        }

        [Fact]
        public void AnExemptionEndingInThePastIsRefused()
        {
            Assert.NotNull(NhiLifecyclePolicy.ValidateExemption("break-glass", Now.AddDays(-1), Now));
        }

        /// <summary>Renewal is a decision somebody makes again; a ten-year exemption is one nobody ever revisits.</summary>
        [Fact]
        public void AnExemptionLongerThanTheMaximumIsRefused()
        {
            var problem = NhiLifecyclePolicy.ValidateExemption("break-glass", Now.AddDays(400), Now, maxDays: 365);

            Assert.NotNull(problem);
            Assert.Contains("Renew", problem);
        }
    }
}
