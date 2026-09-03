using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Models.Governance;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Guards the separation-of-duties rules.
    ///
    /// Two failures shape everything here, and neither throws:
    ///
    /// <para><b>A policy that flags everybody.</b> Name one group on both sides and it conflicts
    /// with itself, so every holder violates — a whole department raised overnight by a rule that
    /// reads perfectly well on screen.</para>
    ///
    /// <para><b>A scan that read nothing and reports a clean domain.</b> Zero violations is the
    /// answer everybody wants and exactly what a failed query produces. Silence and safety look
    /// identical from the outside, so the difference has to be asserted rather than assumed.</para>
    /// </summary>
    public class SodPolicyRulesTests
    {
        private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        private static GovSodPolicy Policy(
            string a = "AP-Clerks,Vendor-Admins",
            string b = "Payment-Approvers",
            string enforcement = GovSodEnforcement.Detect,
            string severity = GovSodSeverity.High,
            bool enabled = true,
            int id = 1) =>
            new()
            {
                Id = id,
                Name = "Vendor creation vs payment approval",
                Rationale = "Whoever registers a supplier must not also be able to approve paying one.",
                TenantId = 1,
                DutyAName = "Create suppliers",
                DutyAGroups = a,
                DutyBName = "Approve payments",
                DutyBGroups = b,
                Enforcement = enforcement,
                Severity = severity,
                IsEnabled = enabled
            };

        // ══════════════════════════════════════
        // سلامة القاعدة
        // ══════════════════════════════════════

        [Fact]
        public void ASoundPolicyPasses() => Assert.Null(SodPolicyRules.ValidatePolicy(Policy()));

        /// <summary>
        /// The dangerous one. A group on both sides conflicts with itself, so everybody who holds it
        /// is in violation — and the rule looks entirely reasonable on the screen that created it.
        /// </summary>
        [Fact]
        public void AGroupNamedInBothDutiesIsRefused()
        {
            var problem = SodPolicyRules.ValidatePolicy(Policy(a: "AP-Clerks,Shared", b: "Payment-Approvers,Shared"));

            Assert.NotNull(problem);
            Assert.Contains("Shared", problem);
            Assert.Contains("every holder", problem);
        }

        [Fact]
        public void TheOverlapCheckIgnoresCase()
        {
            Assert.NotNull(SodPolicyRules.ValidatePolicy(Policy(a: "ap-clerks", b: "AP-Clerks")));
        }

        [Theory]
        [InlineData("", "Payment-Approvers")]
        [InlineData("AP-Clerks", "")]
        [InlineData("  ,  ", "Payment-Approvers")]
        public void ADutyWithNoGroupsIsRefused(string a, string b)
        {
            Assert.NotNull(SodPolicyRules.ValidatePolicy(Policy(a: a, b: b)));
        }

        /// <summary>An approver reading a refusal with no reason has nothing to decide on.</summary>
        [Fact]
        public void APolicyWithNoStatedReasonIsRefused()
        {
            var policy = Policy();
            policy.Rationale = "   ";

            Assert.NotNull(SodPolicyRules.ValidatePolicy(policy));
        }

        [Fact]
        public void AnUnknownEnforcementModeIsRefused()
        {
            Assert.NotNull(SodPolicyRules.ValidatePolicy(Policy(enforcement: "Delete")));
        }

        [Fact]
        public void AnUnknownSeverityIsRefused()
        {
            Assert.NotNull(SodPolicyRules.ValidatePolicy(Policy(severity: "Catastrophic")));
        }

        // ══════════════════════════════════════
        // من في تعارض
        // ══════════════════════════════════════

        [Fact]
        public void HoldingBothDutiesIsAViolation()
        {
            var c = SodPolicyRules.Evaluate(Policy(), new[] { "AP-Clerks", "Payment-Approvers", "Everyone" });

            Assert.True(c.Violates);
        }

        /// <summary>"This person violates policy 4" is not something anybody can act on.</summary>
        [Fact]
        public void AndBothSidesAreNamedSoSomebodyCanActOnIt()
        {
            var c = SodPolicyRules.Evaluate(Policy(), new[] { "Vendor-Admins", "Payment-Approvers" });

            Assert.Equal(new[] { "Vendor-Admins" }, c.MatchedA);
            Assert.Equal(new[] { "Payment-Approvers" }, c.MatchedB);
        }

        [Fact]
        public void HoldingOneSideOnlyIsNotAViolation()
        {
            Assert.False(SodPolicyRules.Evaluate(Policy(), new[] { "AP-Clerks", "Everyone" }).Violates);
            Assert.False(SodPolicyRules.Evaluate(Policy(), new[] { "Payment-Approvers" }).Violates);
        }

        [Fact]
        public void HoldingNothingIsNotAViolation()
        {
            Assert.False(SodPolicyRules.Evaluate(Policy(), Array.Empty<string>()).Violates);
        }

        /// <summary>Directories return group names in inconsistent case; a conflict must not hide behind it.</summary>
        [Fact]
        public void MembershipMatchingIgnoresCase()
        {
            Assert.True(SodPolicyRules.Evaluate(Policy(), new[] { "ap-clerks", "PAYMENT-APPROVERS" }).Violates);
        }

        [Fact]
        public void SeveralGroupsOnOneSideAreAllReported()
        {
            var c = SodPolicyRules.Evaluate(Policy(), new[] { "AP-Clerks", "Vendor-Admins", "Payment-Approvers" });

            Assert.Equal(2, c.MatchedA.Count);
        }

        // ══════════════════════════════════════
        // قبل المنح
        // ══════════════════════════════════════

        [Fact]
        public void AGrantThatCompletesAConflictIsSeenBeforeItHappens()
        {
            var c = SodPolicyRules.WouldViolate(Policy(), held: new[] { "AP-Clerks" }, wouldGain: new[] { "Payment-Approvers" });

            Assert.True(c.Violates);
        }

        [Fact]
        public void AGrantThatCreatesNoConflictPasses()
        {
            var c = SodPolicyRules.WouldViolate(Policy(), held: new[] { "AP-Clerks" }, wouldGain: new[] { "Helpdesk" });

            Assert.False(c.Violates);
        }

        /// <summary>
        /// A single request can carry both sides at once. Reasoning about the new group against
        /// existing membership alone would let that through — the person held neither before, so
        /// nothing they already have conflicts with anything.
        /// </summary>
        [Fact]
        public void ARequestCarryingBothSidesAtOnceIsCaught()
        {
            var c = SodPolicyRules.WouldViolate(Policy(),
                held: Array.Empty<string>(),
                wouldGain: new[] { "AP-Clerks", "Payment-Approvers" });

            Assert.True(c.Violates);
        }

        // ══════════════════════════════════════
        // ⛔ صفر مخالفات، أو صفر أسئلة
        // ══════════════════════════════════════

        [Fact]
        public void AScanThatReadEverythingIsTrustworthy()
        {
            Assert.True(SodPolicyRules.MayTrustScan(groupsAsked: 5, groupsRead: 5, membershipsRead: 120).Trustworthy);
        }

        /// <summary>A clean result here would mean the question was never asked.</summary>
        [Fact]
        public void AScanThatCouldNotReadASingleGroupIsNotClean()
        {
            var v = SodPolicyRules.MayTrustScan(groupsAsked: 5, groupsRead: 0, membershipsRead: 0);

            Assert.False(v.Trustworthy);
            Assert.Contains("never asked", v.Reason!);
        }

        /// <summary>The groups it could not read are exactly the ones hiding the conflicts they take part in.</summary>
        [Fact]
        public void APartialScanIsNotClean()
        {
            var v = SodPolicyRules.MayTrustScan(groupsAsked: 5, groupsRead: 3, membershipsRead: 40);

            Assert.False(v.Trustworthy);
            Assert.Contains("3 of 5", v.Reason!);
        }

        /// <summary>
        /// "No policy names a group" and "the groups could not be read" are two different problems
        /// with two different fixes — write a policy, or fix the permissions. Both refuse the scan,
        /// so asserting only on <c>Trustworthy</c> cannot tell them apart: the message is the whole
        /// difference, and an operator handed the wrong one looks in the wrong place.
        /// </summary>
        [Fact]
        public void NoPoliciesIsReportedAsNothingAskedNotAsAFailedRead()
        {
            var v = SodPolicyRules.MayTrustScan(groupsAsked: 0, groupsRead: 0, membershipsRead: 0);

            Assert.False(v.Trustworthy);
            Assert.Contains("no groups were named", v.Reason!);
            Assert.DoesNotContain("could not be read", v.Reason!);
        }

        /// <summary>Every group resolved and not one member came back — a permission problem, not an empty directory.</summary>
        [Fact]
        public void GroupsThatResolveButReturnNobodyAreNotClean()
        {
            var v = SodPolicyRules.MayTrustScan(groupsAsked: 5, groupsRead: 5, membershipsRead: 0);

            Assert.False(v.Trustworthy);
            Assert.Contains("permission", v.Reason!);
        }

        // ══════════════════════════════════════
        // هل يمرّ المنح
        // ══════════════════════════════════════

        private static (GovSodPolicy, SodPolicyRules.Conflict) Hit(GovSodPolicy p) =>
            (p, new SodPolicyRules.Conflict(true, new[] { "AP-Clerks" }, new[] { "Payment-Approvers" }));

        /// <summary>Switching the feature on must not begin by refusing access nobody knew was in conflict.</summary>
        [Fact]
        public void DetectRecordsAndStaysOutOfTheWay()
        {
            var v = SodPolicyRules.MayGrant(new[] { Hit(Policy(enforcement: GovSodEnforcement.Detect)) });

            Assert.True(v.Allowed);
            Assert.Empty(v.Blocking);
            Assert.Empty(v.Warning);
        }

        [Fact]
        public void WarnLetsTheApproverProceedButSaysWhatTheyAreAccepting()
        {
            var v = SodPolicyRules.MayGrant(new[] { Hit(Policy(enforcement: GovSodEnforcement.Warn)) });

            Assert.True(v.Allowed);
            Assert.Single(v.Warning);
            Assert.Contains("Vendor creation", v.Message!);
        }

        [Fact]
        public void BlockRefuses()
        {
            var v = SodPolicyRules.MayGrant(new[] { Hit(Policy(enforcement: GovSodEnforcement.Block)) });

            Assert.False(v.Allowed);
            Assert.Single(v.Blocking);
        }

        /// <summary>The refusal names the policy, because a refusal nobody can trace is one nobody can fix.</summary>
        [Fact]
        public void AndTheRefusalNamesThePolicy()
        {
            var v = SodPolicyRules.MayGrant(new[] { Hit(Policy(enforcement: GovSodEnforcement.Block)) });

            Assert.Contains("Vendor creation vs payment approval", v.Message!);
        }

        [Fact]
        public void ADisabledPolicyDoesNotBlock()
        {
            var v = SodPolicyRules.MayGrant(new[] { Hit(Policy(enforcement: GovSodEnforcement.Block, enabled: false)) });

            Assert.True(v.Allowed);
        }

        /// <summary>Re-refusing a risk somebody already accepted in writing teaches people to route around the system.</summary>
        [Fact]
        public void AnAlreadyMitigatedConflictDoesNotBlockAgain()
        {
            var policy = Policy(enforcement: GovSodEnforcement.Block, id: 7);

            var v = SodPolicyRules.MayGrant(new[] { Hit(policy) }, mitigatedPolicyIds: new[] { 7 });

            Assert.True(v.Allowed);
        }

        [Fact]
        public void AMitigationForADifferentPolicyDoesNotExcuseThisOne()
        {
            var policy = Policy(enforcement: GovSodEnforcement.Block, id: 7);

            Assert.False(SodPolicyRules.MayGrant(new[] { Hit(policy) }, mitigatedPolicyIds: new[] { 9 }).Allowed);
        }

        [Fact]
        public void NoConflictsMeansNoObstacleAndNoNoise()
        {
            var v = SodPolicyRules.MayGrant(Array.Empty<(GovSodPolicy, SodPolicyRules.Conflict)>());

            Assert.True(v.Allowed);
            Assert.Null(v.Message);
        }

        /// <summary>A conflict that was evaluated and did not fire must not block anything.</summary>
        [Fact]
        public void AnEvaluatedButUnviolatedPolicyIsIgnored()
        {
            var quiet = (Policy(enforcement: GovSodEnforcement.Block),
                         new SodPolicyRules.Conflict(false, Array.Empty<string>(), Array.Empty<string>()));

            Assert.True(SodPolicyRules.MayGrant(new[] { quiet }).Allowed);
        }

        /// <summary>A mode nobody recognises must not silently become the strictest one.</summary>
        [Fact]
        public void AnUnknownEnforcementModeDoesNotBlock()
        {
            var v = SodPolicyRules.MayGrant(new[] { Hit(Policy(enforcement: "Nonsense")) });

            Assert.True(v.Allowed);
            Assert.Empty(v.Blocking);
        }

        [Fact]
        public void BlockingWinsOverWarningWhenBothApply()
        {
            var v = SodPolicyRules.MayGrant(new[]
            {
                Hit(Policy(enforcement: GovSodEnforcement.Warn, id: 1)),
                Hit(Policy(enforcement: GovSodEnforcement.Block, id: 2))
            });

            Assert.False(v.Allowed);
            Assert.Single(v.Blocking);
            Assert.Single(v.Warning);
        }

        // ══════════════════════════════════════
        // التخفيف
        // ══════════════════════════════════════

        [Fact]
        public void AValidMitigationPasses()
        {
            Assert.Null(SodPolicyRules.ValidateMitigation("second signature required on every payment", Now.AddDays(90), Now));
        }

        /// <summary>An acceptance with no end is not acceptance, it is forgetting.</summary>
        [Fact]
        public void AMitigationWithNoEndDateIsRefused()
        {
            var problem = SodPolicyRules.ValidateMitigation("compensating control", null, Now);

            Assert.NotNull(problem);
            Assert.Contains("forgetting", problem);
        }

        [Fact]
        public void AMitigationWithNoStatedControlIsRefused()
        {
            Assert.NotNull(SodPolicyRules.ValidateMitigation("  ", Now.AddDays(30), Now));
        }

        [Fact]
        public void AMitigationEndingInThePastIsRefused()
        {
            Assert.NotNull(SodPolicyRules.ValidateMitigation("control", Now.AddDays(-1), Now));
        }

        [Fact]
        public void AMitigationLongerThanTheMaximumIsRefused()
        {
            Assert.NotNull(SodPolicyRules.ValidateMitigation("control", Now.AddDays(400), Now, maxDays: 365));
        }

        [Fact]
        public void AnExpiredMitigationIsNoMitigationAtAll()
        {
            var v = new GovSodViolation { MitigationExpiresUtc = Now.AddDays(-1) };

            Assert.False(SodPolicyRules.IsMitigated(v, Now));
        }

        [Fact]
        public void AMitigationInForceCounts()
        {
            var v = new GovSodViolation { MitigationExpiresUtc = Now.AddDays(1) };

            Assert.True(SodPolicyRules.IsMitigated(v, Now));
        }

        [Fact]
        public void AViolationNobodyMitigatedIsNotMitigated()
        {
            Assert.False(SodPolicyRules.IsMitigated(new GovSodViolation(), Now));
        }

        // ══════════════════════════════════════
        // الترتيب
        // ══════════════════════════════════════

        [Fact]
        public void SeverityOrdersTheWorstFirst()
        {
            var ranks = new[] { GovSodSeverity.Critical, GovSodSeverity.High, GovSodSeverity.Medium, GovSodSeverity.Low }
                .Select(GovSodSeverity.Rank).ToArray();

            Assert.Equal(ranks.OrderBy(r => r), ranks);
        }

        [Fact]
        public void AnUnknownSeveritySortsLastRatherThanFirst()
        {
            Assert.True(GovSodSeverity.Rank("Nonsense") > GovSodSeverity.Rank(GovSodSeverity.Low));
        }
    }
}
