using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Models.Governance;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Guards the certification rules.
    ///
    /// This module takes access away from people on a timer, so its failures are louder than the
    /// request module's. A campaign with no reviewer is not a stalled queue — it is a scheduled
    /// outage, because the deadline revokes everything nobody certified. A delegation with no end
    /// is a permanent transfer of authority that still signs the original reviewer's name. And an
    /// unreviewed campaign, acted on literally, strips a department overnight on the strength of
    /// nobody's judgement.
    ///
    /// None of those throws.
    /// </summary>
    public class CampaignPolicyTests
    {
        private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

        private static GovCampaign Campaign(
            string? groups = "Domain Admins", int? tenantId = 1, string? catalogIds = null,
            string? reviewers = "manager1", string? reviewerGroup = null,
            int reviewDays = 14, int maxUndecided = 50,
            string status = GovCampaignStatus.Active) =>
            new()
            {
                Id = 1,
                Name = "مراجعة الصلاحيات الإدارية",
                ScopeGroups = groups,
                ScopeTenantId = tenantId,
                ScopeCatalogItemIds = catalogIds,
                ReviewerUsers = reviewers,
                ReviewerAdGroup = reviewerGroup,
                ReviewDays = reviewDays,
                MaxUndecidedRevokePercent = maxUndecided,
                Status = status
            };

        private static GovCampaignItem Item(
            string subject = "ahmed.s", string decision = GovReviewDecisions.Pending) =>
            new() { Id = 1, CampaignId = 1, SubjectAccount = subject, GroupName = "Domain Admins", TenantId = 1, Decision = decision };

        private static GovReviewDelegation Delegation(
            string from = "manager1", string to = "deputy1",
            int startsInDays = 0, int endsInDays = 14, DateTime? revoked = null) =>
            new()
            {
                FromUsername = from,
                ToUsername = to,
                StartUtc = Now.AddDays(startsInDays),
                EndUtc = Now.AddDays(endsInDays),
                RevokedUtc = revoked
            };

        // ══════════════════════════════════════
        // LAUNCHING
        // ══════════════════════════════════════

        [Fact]
        public void AUsableCampaign_IsAccepted() => Assert.Null(CampaignPolicy.ValidateCampaign(Campaign()));

        /// <summary>
        /// The one that matters most. With no reviewer, nobody decides, the deadline arrives, and
        /// every membership in scope is revoked — a scheduled outage wearing the shape of a policy.
        /// </summary>
        [Fact]
        public void ACampaignNobodyCanReview_IsRefused()
        {
            var problem = CampaignPolicy.ValidateCampaign(Campaign(reviewers: null, reviewerGroup: null));
            Assert.NotNull(problem);
            Assert.Contains("revoke every membership", problem!);
        }

        [Theory]
        [InlineData("manager1", null)]
        [InlineData(null, "Access Reviewers")]
        public void EitherReviewerKind_IsEnough(string? users, string? group)
        {
            Assert.Null(CampaignPolicy.ValidateCampaign(Campaign(reviewers: users, reviewerGroup: group)));
        }

        [Fact]
        public void ACampaignWithNoScope_IsRefused()
        {
            var problem = CampaignPolicy.ValidateCampaign(Campaign(groups: null, catalogIds: null));
            Assert.Contains("no scope", problem!);
        }

        /// <summary>A group name says nothing about which directory holds it.</summary>
        [Fact]
        public void ExplicitGroupsWithoutATenant_AreRefused()
        {
            var problem = CampaignPolicy.ValidateCampaign(Campaign(tenantId: null));
            Assert.Contains("tenant", problem!);
        }

        /// <summary>Catalog-sourced scope carries its own tenant, so it needs none of its own.</summary>
        [Fact]
        public void CatalogScopeAlone_NeedsNoTenant()
        {
            Assert.Null(CampaignPolicy.ValidateCampaign(
                Campaign(groups: null, tenantId: null, catalogIds: "1,2")));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void ACampaignWithNoReviewWindow_IsRefused(int days) =>
            Assert.NotNull(CampaignPolicy.ValidateCampaign(Campaign(reviewDays: days)));

        [Theory]
        [InlineData(-1)]
        [InlineData(101)]
        public void AnImpossibleCeiling_IsRefused(int percent) =>
            Assert.NotNull(CampaignPolicy.ValidateCampaign(Campaign(maxUndecided: percent)));

        [Theory]
        [InlineData("1,2,3", new[] { 1, 2, 3 })]
        [InlineData("1, 1, 2", new[] { 1, 2 })]
        [InlineData("1,x,2", new[] { 1, 2 })]
        [InlineData("", new int[0])]
        [InlineData(null, new int[0])]
        public void CatalogIds_AreParsedLeniently(string? csv, int[] expected) =>
            Assert.Equal(expected, CampaignPolicy.ParseIds(csv));

        // ══════════════════════════════════════
        // DELEGATION
        // ══════════════════════════════════════

        [Fact]
        public void AWellFormedDelegation_IsAccepted() =>
            Assert.Null(CampaignPolicy.ValidateDelegation(Delegation(), Now));

        [Fact]
        public void DelegatingToYourself_IsRefused() =>
            Assert.NotNull(CampaignPolicy.ValidateDelegation(Delegation(from: "manager1", to: "Manager1"), Now));

        [Fact]
        public void ADelegationThatEndsBeforeItStarts_IsRefused() =>
            Assert.NotNull(CampaignPolicy.ValidateDelegation(Delegation(startsInDays: 5, endsInDays: 2), Now));

        /// <summary>
        /// A window already closed grants nothing and still reads as cover on the screen — the
        /// reviewer would leave believing somebody had it.
        /// </summary>
        [Fact]
        public void ADelegationAlreadyInThePast_IsRefused()
        {
            var problem = CampaignPolicy.ValidateDelegation(Delegation(startsInDays: -20, endsInDays: -5), Now);
            Assert.Contains("already passed", problem!);
        }

        [Fact]
        public void ADelegationInForce_IsRecognised() =>
            Assert.True(CampaignPolicy.IsInForce(Delegation(), Now));

        [Theory]
        [InlineData(3, 10)]     // not started yet
        [InlineData(-20, -5)]   // already finished
        public void ADelegationOutsideItsWindow_IsNotInForce(int start, int end) =>
            Assert.False(CampaignPolicy.IsInForce(Delegation(startsInDays: start, endsInDays: end), Now));

        /// <summary>Coming back early has to end it — otherwise the stand-in keeps the authority after the reason for it is gone.</summary>
        [Fact]
        public void ARevokedDelegation_IsNotInForce() =>
            Assert.False(CampaignPolicy.IsInForce(Delegation(revoked: Now.AddDays(-1)), Now));

        [Fact]
        public void TheAuthorityCarried_IsThatOfTheDelegators()
        {
            var authority = CampaignPolicy.AuthorityOf("deputy1", new[]
            {
                Delegation(from: "manager1", to: "deputy1"),
                Delegation(from: "manager2", to: "deputy1"),
                Delegation(from: "manager3", to: "somebody-else")
            }, Now);

            Assert.Equal(new[] { "manager1", "manager2" }, authority);
        }

        [Fact]
        public void AuthorityIsMatchedCaseInsensitively() =>
            Assert.Single(CampaignPolicy.AuthorityOf("Deputy1", new[] { Delegation(to: "deputy1") }, Now));

        /// <summary>
        /// One hop, by construction. A chain would leave a certificate signed by somebody two
        /// removes from anyone ever answerable for the access, with nothing in the record to say so.
        /// </summary>
        [Fact]
        public void DelegationDoesNotChain()
        {
            var delegations = new[]
            {
                Delegation(from: "manager1", to: "deputy1"),
                Delegation(from: "deputy1", to: "assistant1")
            };

            // The assistant carries the deputy's authority — and not, through them, the manager's.
            var carried = CampaignPolicy.AuthorityOf("assistant1", delegations, Now);
            Assert.Equal(new[] { "deputy1" }, carried);
            Assert.DoesNotContain("manager1", carried);
        }

        // ══════════════════════════════════════
        // REVIEWING
        // ══════════════════════════════════════

        private static readonly string[] NoDelegations = Array.Empty<string>();

        [Fact]
        public void AConfiguredReviewer_MayDecideAsThemselves()
        {
            var right = CampaignPolicy.CanReview(Campaign(), Item(), "manager1", true, NoDelegations);

            Assert.True(right.Allowed);
            Assert.Null(right.OnBehalfOf);
        }

        /// <summary>The stand-in decides, and the record says whose duty it was.</summary>
        [Fact]
        public void ADelegate_MayDecideOnTheReviewersBehalf()
        {
            var right = CampaignPolicy.CanReview(
                Campaign(), Item(), "deputy1", isConfiguredReviewer: false, new[] { "manager1" });

            Assert.True(right.Allowed);
            Assert.Equal("manager1", right.OnBehalfOf);
        }

        [Fact]
        public void SomebodyWithNeitherRight_IsRefused() =>
            Assert.False(CampaignPolicy.CanReview(Campaign(), Item(), "stranger", false, NoDelegations).Allowed);

        /// <summary>
        /// The same hole as self-approval, and least visible here: one row among hundreds, marked
        /// "Keep" by the person who holds it.
        /// </summary>
        [Fact]
        public void NobodyCertifiesTheirOwnMembership()
        {
            var right = CampaignPolicy.CanReview(
                Campaign(), Item(subject: "manager1"), "manager1", isConfiguredReviewer: true, NoDelegations);

            Assert.False(right.Allowed);
            Assert.Contains("your own membership", right.Problem!);
        }

        [Theory]
        [InlineData(GovReviewDecisions.Keep)]
        [InlineData(GovReviewDecisions.Revoke)]
        public void AnAlreadyDecidedItem_IsNotDecidedTwice(string decision) =>
            Assert.False(CampaignPolicy.CanReview(
                Campaign(), Item(decision: decision), "manager1", true, NoDelegations).Allowed);

        [Theory]
        [InlineData(GovCampaignStatus.Draft)]
        [InlineData(GovCampaignStatus.Closed)]
        public void ACampaignNotRunning_AcceptsNoDecision(string status) =>
            Assert.False(CampaignPolicy.CanReview(
                Campaign(status: status), Item(), "manager1", true, NoDelegations).Allowed);

        [Fact]
        public void OnlyARevocation_ReachesTheDirectory()
        {
            Assert.Equal(GovExecutionStatus.Pending, CampaignPolicy.ExecutionAfter(GovReviewDecisions.Revoke));
            Assert.Equal(GovExecutionStatus.None, CampaignPolicy.ExecutionAfter(GovReviewDecisions.Keep));
        }

        [Theory]
        [InlineData("keep")]
        [InlineData("Pending")]
        [InlineData("")]
        public void AnUnknownReviewDecision_Throws(string decision) =>
            Assert.Throws<InvalidOperationException>(() => CampaignPolicy.ExecutionAfter(decision));

        // ══════════════════════════════════════
        // THE DEADLINE, AND THE CEILING ON IT
        // ══════════════════════════════════════

        /// <summary>A few rows nobody got to is a reviewer who did the work — revoking those is what certification means.</summary>
        [Fact]
        public void AMostlyReviewedCampaign_MayRevokeWhatIsLeft()
        {
            var verdict = CampaignPolicy.MayAutoRevoke(total: 100, undecided: 8, maxUndecidedPercent: 50);

            Assert.True(verdict.Allowed);
            Assert.Null(verdict.Reason);
        }

        /// <summary>
        /// The guard. A campaign where almost nothing was decided is not a verdict on the access —
        /// it is a reviewer who never opened it. Acting on that takes a department's access
        /// overnight on nobody's judgement.
        /// </summary>
        [Fact]
        public void AnUnreviewedCampaign_RevokesNothing()
        {
            var verdict = CampaignPolicy.MayAutoRevoke(total: 100, undecided: 97, maxUndecidedPercent: 50);

            Assert.False(verdict.Allowed);
            Assert.Contains("unreviewed campaign", verdict.Reason!);
            Assert.Contains("97", verdict.Reason!);      // the numbers travel with the refusal
            Assert.Contains("50%", verdict.Reason!);
        }

        [Theory]
        [InlineData(100, 50, 50, true)]    // exactly at the ceiling is still within it
        [InlineData(100, 51, 50, false)]
        [InlineData(3, 2, 50, false)]      // 67% — small campaigns are judged by the same share
        [InlineData(3, 1, 50, true)]       // 33%
        public void TheCeilingIsAShareNotACount(int total, int undecided, int max, bool allowed) =>
            Assert.Equal(allowed, CampaignPolicy.MayAutoRevoke(total, undecided, max).Allowed);

        /// <summary>Nothing left undecided is trivially allowed — there is nothing for the guard to stop.</summary>
        [Fact]
        public void AFullyReviewedCampaign_PassesTheGuard() =>
            Assert.True(CampaignPolicy.MayAutoRevoke(total: 100, undecided: 0, maxUndecidedPercent: 0).Allowed);

        /// <summary>An empty campaign is a configuration fault, not a clean bill of health.</summary>
        [Fact]
        public void AnEmptyCampaign_IsRefusedRatherThanCalledClean()
        {
            var verdict = CampaignPolicy.MayAutoRevoke(total: 0, undecided: 0, maxUndecidedPercent: 50);
            Assert.False(verdict.Allowed);
            Assert.NotNull(verdict.Reason);
        }

        /// <summary>A ceiling of zero means "revoke only when every single row was decided" — a legitimate, strict setting.</summary>
        [Fact]
        public void AZeroCeiling_DemandsACompleteReview() =>
            Assert.False(CampaignPolicy.MayAutoRevoke(total: 100, undecided: 1, maxUndecidedPercent: 0).Allowed);

        [Fact]
        public void AnActiveCampaignPastItsDeadline_HasLapsed()
        {
            var c = Campaign();
            c.DueUtc = Now.AddSeconds(-1);
            Assert.True(CampaignPolicy.HasLapsed(c, Now));
        }

        [Fact]
        public void AClosedCampaign_NeverLapsesAgain()
        {
            var c = Campaign(status: GovCampaignStatus.Closed);
            c.DueUtc = Now.AddDays(-30);
            Assert.False(CampaignPolicy.HasLapsed(c, Now));
        }

        /// <summary>A draft has no deadline to miss — it was never put in front of anyone.</summary>
        [Fact]
        public void ADraft_NeverLapses()
        {
            var c = Campaign(status: GovCampaignStatus.Draft);
            c.DueUtc = Now.AddDays(-30);
            Assert.False(CampaignPolicy.HasLapsed(c, Now));
        }

        [Fact]
        public void TheDeadlineFollowsTheReviewWindow() =>
            Assert.Equal(Now.AddDays(14), CampaignPolicy.Deadline(Campaign(reviewDays: 14), Now));
    }
}
