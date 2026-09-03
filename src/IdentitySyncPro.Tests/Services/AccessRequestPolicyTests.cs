using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Models.Governance;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Guards the access-request rules.
    ///
    /// Every case here is a request system that keeps working while doing the wrong thing: a
    /// catalog item nobody can approve, an approver clearing their own path, a second decision
    /// landing on a request that was already rejected, an expiry quietly withdrawing a grant that
    /// was already made. None of them throws on its own; each leaves a row that reads as governed.
    /// </summary>
    public class AccessRequestPolicyTests
    {
        private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

        private static GovCatalogItem Item(
            string? approverGroup = "Access Approvers", string? approverUsers = null,
            int dueDays = 7, int durationDays = 0, bool enabled = true) =>
            new()
            {
                Id = 1,
                DisplayName = "شبكة المختبرات",
                TargetType = GovTargetTypes.AdGroup,
                TenantId = 1,
                GroupName = "Lab-Network",
                ApproverAdGroup = approverGroup,
                ApproverUsers = approverUsers,
                DecisionDueDays = dueDays,
                AccessDurationDays = durationDays,
                IsEnabled = enabled
            };

        private static GovAccessRequest Request(
            string status = GovRequestStatus.Pending, string requestedBy = "operator1",
            string subject = "ahmed.s", DateTime? due = null,
            string execution = GovExecutionStatus.None,
            DateTime? accessExpires = null, DateTime? revoked = null) =>
            new()
            {
                Id = 10,
                CatalogItemId = 1,
                SubjectAccount = subject,
                RequestedBy = requestedBy,
                Justification = "مشروع بحثي",
                Status = status,
                DecisionDueUtc = due,
                ExecutionStatus = execution,
                AccessExpiresUtc = accessExpires,
                AccessRevokedUtc = revoked
            };

        // ══════════════════════════════════════
        // THE CATALOG ITEM
        // ══════════════════════════════════════

        [Fact]
        public void AUsableCatalogItem_IsAccepted()
        {
            Assert.Null(AccessRequestPolicy.ValidateCatalogItem(Item()));
        }

        /// <summary>
        /// The black hole. Requests against an approver-less item are accepted, sit as "Pending"
        /// forever, and appear on nobody's queue — a broken workflow that looks like a busy one.
        /// </summary>
        [Fact]
        public void ACatalogItemNobodyCanApprove_IsRefused()
        {
            var problem = AccessRequestPolicy.ValidateCatalogItem(Item(approverGroup: null, approverUsers: null));
            Assert.NotNull(problem);
            Assert.Contains("never be decided", problem!);
        }

        /// <summary>Either kind of approver on its own is enough — the group and the user list are alternatives, not a pair.</summary>
        [Theory]
        [InlineData("Access Approvers", null)]
        [InlineData(null, "manager1")]
        [InlineData(null, "manager1, manager2")]
        public void EitherApproverKind_IsEnough(string? group, string? users)
        {
            Assert.Null(AccessRequestPolicy.ValidateCatalogItem(Item(approverGroup: group, approverUsers: users)));
        }

        /// <summary>A blank or comma-only user list is not an approver list, however it is spelled.</summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(" , , ")]
        public void AnEmptyApproverList_DoesNotCountAsAnApprover(string users)
        {
            Assert.NotNull(AccessRequestPolicy.ValidateCatalogItem(Item(approverGroup: null, approverUsers: users)));
        }

        [Theory]
        [InlineData("", "Lab-Network", 1, "display name")]
        [InlineData("شبكة", "", 1, "AD group")]
        [InlineData("شبكة", "Lab-Network", 0, "tenant")]
        public void AnIncompleteCatalogItem_NamesWhatIsMissing(string name, string group, int tenantId, string expected)
        {
            var item = Item();
            item.DisplayName = name;
            item.GroupName = group;
            item.TenantId = tenantId;

            var problem = AccessRequestPolicy.ValidateCatalogItem(item);
            Assert.NotNull(problem);
            Assert.Contains(expected, problem!);
        }

        [Fact]
        public void AnUnknownTargetType_IsRefused()
        {
            var item = Item();
            item.TargetType = "OuMove";   // not built yet — accepting it would produce requests nothing can execute
            Assert.NotNull(AccessRequestPolicy.ValidateCatalogItem(item));
        }

        // ══════════════════════════════════════
        // RAISING A REQUEST
        // ══════════════════════════════════════

        [Fact]
        public void AWellFormedRequest_IsAccepted()
        {
            Assert.Null(AccessRequestPolicy.ValidateNewRequest(
                Item(), "ahmed.s", "operator1", "مشروع بحثي",
                subjectIsAlreadyMember: false, requesterIsEligible: true));
        }

        /// <summary>
        /// Access the account already holds must be refused at the door. Approved, it would execute
        /// as a no-op and leave an approval record implying a grant that never took place.
        /// </summary>
        [Fact]
        public void AccessAlreadyHeld_IsRefused()
        {
            var problem = AccessRequestPolicy.ValidateNewRequest(
                Item(), "ahmed.s", "operator1", "مشروع بحثي",
                subjectIsAlreadyMember: true, requesterIsEligible: true);
            Assert.Contains("already has this access", problem!);
        }

        /// <summary>The one field an auditor reads first. Blank makes the whole record decorative.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("    ")]
        public void AMissingJustification_IsRefused(string? justification)
        {
            var problem = AccessRequestPolicy.ValidateNewRequest(
                Item(), "ahmed.s", "operator1", justification,
                subjectIsAlreadyMember: false, requesterIsEligible: true);
            Assert.Contains("justification", problem!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AnIneligibleRequester_IsRefused()
        {
            Assert.NotNull(AccessRequestPolicy.ValidateNewRequest(
                Item(), "ahmed.s", "operator1", "مشروع بحثي",
                subjectIsAlreadyMember: false, requesterIsEligible: false));
        }

        [Fact]
        public void ADisabledCatalogItem_AcceptsNothing()
        {
            Assert.NotNull(AccessRequestPolicy.ValidateNewRequest(
                Item(enabled: false), "ahmed.s", "operator1", "مشروع بحثي",
                subjectIsAlreadyMember: false, requesterIsEligible: true));
        }

        // ══════════════════════════════════════
        // DEADLINES
        // ══════════════════════════════════════

        [Fact]
        public void ADecisionWindow_BecomesADeadline()
        {
            Assert.Equal(Now.AddDays(7), AccessRequestPolicy.DecisionDeadline(Item(dueDays: 7), Now));
        }

        /// <summary>Zero means "no window", not "due immediately" — the difference between a queue and an empty one.</summary>
        [Fact]
        public void NoDecisionWindow_MeansNoDeadline()
        {
            Assert.Null(AccessRequestPolicy.DecisionDeadline(Item(dueDays: 0), Now));
        }

        [Fact]
        public void AnAccessDuration_BecomesAnExpiry()
        {
            Assert.Equal(Now.AddDays(30), AccessRequestPolicy.AccessDeadline(Item(durationDays: 30), Now));
        }

        [Fact]
        public void NoAccessDuration_MeansPermanentAccess()
        {
            Assert.Null(AccessRequestPolicy.AccessDeadline(Item(durationDays: 0), Now));
        }

        // ══════════════════════════════════════
        // DECIDING
        // ══════════════════════════════════════

        [Fact]
        public void AConfiguredApprover_MayDecide()
        {
            Assert.Null(AccessRequestPolicy.ValidateDecision(
                Request(), "manager1", isConfiguredApprover: true, Now));
        }

        /// <summary>The oldest hole in every request system.</summary>
        [Fact]
        public void TheRequester_CannotApproveTheirOwnRequest()
        {
            var problem = AccessRequestPolicy.ValidateDecision(
                Request(requestedBy: "manager1"), "manager1", isConfiguredApprover: true, Now);
            Assert.Contains("raised yourself", problem!);
        }

        /// <summary>The same hole from the other side: approving access granted to your own account.</summary>
        [Fact]
        public void TheSubject_CannotApproveAccessToTheirOwnAccount()
        {
            var problem = AccessRequestPolicy.ValidateDecision(
                Request(subject: "manager1"), "manager1", isConfiguredApprover: true, Now);
            Assert.Contains("your own account", problem!);
        }

        /// <summary>Usernames are not case-sensitive, and neither is the self-approval bar.</summary>
        [Fact]
        public void SelfApproval_IsBarredRegardlessOfCasing()
        {
            Assert.NotNull(AccessRequestPolicy.ValidateDecision(
                Request(requestedBy: "Manager1"), "manager1", isConfiguredApprover: true, Now));
        }

        [Fact]
        public void SomeoneWhoIsNotAnApprover_IsRefused()
        {
            Assert.NotNull(AccessRequestPolicy.ValidateDecision(
                Request(), "curious.user", isConfiguredApprover: false, Now));
        }

        /// <summary>A request decided twice would record two conflicting outcomes and execute the second.</summary>
        [Theory]
        [InlineData(GovRequestStatus.Approved)]
        [InlineData(GovRequestStatus.Rejected)]
        [InlineData(GovRequestStatus.Cancelled)]
        [InlineData(GovRequestStatus.Expired)]
        public void AnAlreadySettledRequest_CannotBeDecidedAgain(string status)
        {
            var problem = AccessRequestPolicy.ValidateDecision(
                Request(status: status), "manager1", isConfiguredApprover: true, Now);
            Assert.Contains("cannot be decided again", problem!);
        }

        [Fact]
        public void APastDeadline_ClosesTheDecision()
        {
            var problem = AccessRequestPolicy.ValidateDecision(
                Request(due: Now.AddMinutes(-1)), "manager1", isConfiguredApprover: true, Now);
            Assert.Contains("window", problem!);
        }

        [Fact]
        public void ADeadlineStillAhead_LeavesTheDecisionOpen()
        {
            Assert.Null(AccessRequestPolicy.ValidateDecision(
                Request(due: Now.AddMinutes(1)), "manager1", isConfiguredApprover: true, Now));
        }

        // ══════════════════════════════════════
        // WHAT A DECISION PRODUCES
        // ══════════════════════════════════════

        [Theory]
        [InlineData(GovDecisions.Approve, GovRequestStatus.Approved)]
        [InlineData(GovDecisions.Reject, GovRequestStatus.Rejected)]
        public void EachDecision_ProducesItsStatus(string decision, string status)
        {
            Assert.Equal(status, AccessRequestPolicy.StatusAfter(decision));
        }

        /// <summary>
        /// An unrecognised decision must throw. Defaulting would turn a typo or a tampered form
        /// field into an outcome the audit trail then records as deliberate.
        /// </summary>
        [Theory]
        [InlineData("approve")]
        [InlineData("Maybe")]
        [InlineData("")]
        public void AnUnknownDecision_Throws(string decision)
        {
            Assert.Throws<InvalidOperationException>(() => AccessRequestPolicy.StatusAfter(decision));
        }

        /// <summary>Only an approval creates work for the directory; a rejection is finished the moment it is recorded.</summary>
        [Fact]
        public void OnlyAnApproval_QueuesExecution()
        {
            Assert.Equal(GovExecutionStatus.Pending, AccessRequestPolicy.ExecutionStatusAfter(GovDecisions.Approve));
            Assert.Equal(GovExecutionStatus.None, AccessRequestPolicy.ExecutionStatusAfter(GovDecisions.Reject));
        }

        // ══════════════════════════════════════
        // WITHDRAWING
        // ══════════════════════════════════════

        [Fact]
        public void TheRequester_MayWithdrawWhileUndecided()
        {
            Assert.Null(AccessRequestPolicy.ValidateCancel(Request(requestedBy: "operator1"), "operator1"));
        }

        [Fact]
        public void SomebodyElse_CannotWithdrawYourRequest()
        {
            Assert.NotNull(AccessRequestPolicy.ValidateCancel(Request(requestedBy: "operator1"), "operator2"));
        }

        [Fact]
        public void ASettledRequest_CannotBeWithdrawn()
        {
            Assert.NotNull(AccessRequestPolicy.ValidateCancel(
                Request(status: GovRequestStatus.Approved, requestedBy: "operator1"), "operator1"));
        }

        // ══════════════════════════════════════
        // EXPIRY
        // ══════════════════════════════════════

        [Fact]
        public void AnUndecidedRequestPastItsDeadline_HasExpired()
        {
            Assert.True(AccessRequestPolicy.HasExpired(Request(due: Now.AddSeconds(-1)), Now));
        }

        [Fact]
        public void ARequestWithNoDeadline_NeverExpires()
        {
            Assert.False(AccessRequestPolicy.HasExpired(Request(due: null), Now));
        }

        /// <summary>
        /// The dangerous one. An approved request awaiting execution still carries its old decision
        /// deadline; expiring it would silently withdraw a grant that was already decided and is
        /// merely a moment away from reaching the directory.
        /// </summary>
        [Fact]
        public void AnApprovedRequest_NeverExpires()
        {
            Assert.False(AccessRequestPolicy.HasExpired(
                Request(status: GovRequestStatus.Approved, due: Now.AddDays(-30)), Now));
        }

        // ══════════════════════════════════════
        // TIME-BOUND ACCESS
        // ══════════════════════════════════════

        [Fact]
        public void GrantedAccessPastItsExpiry_HasLapsed()
        {
            Assert.True(AccessRequestPolicy.AccessHasLapsed(
                Request(status: GovRequestStatus.Approved, execution: GovExecutionStatus.Succeeded,
                        accessExpires: Now.AddSeconds(-1)), Now));
        }

        /// <summary>
        /// Revoking on the strength of an approval alone would try to remove a membership that was
        /// never added, and record a revocation for access the person never had.
        /// </summary>
        [Fact]
        public void AccessThatNeverReachedTheDirectory_DoesNotLapse()
        {
            Assert.False(AccessRequestPolicy.AccessHasLapsed(
                Request(status: GovRequestStatus.Approved, execution: GovExecutionStatus.Pending,
                        accessExpires: Now.AddSeconds(-1)), Now));

            Assert.False(AccessRequestPolicy.AccessHasLapsed(
                Request(status: GovRequestStatus.Approved, execution: GovExecutionStatus.Failed,
                        accessExpires: Now.AddSeconds(-1)), Now));
        }

        [Fact]
        public void AccessAlreadyRevoked_DoesNotLapseTwice()
        {
            Assert.False(AccessRequestPolicy.AccessHasLapsed(
                Request(status: GovRequestStatus.Approved, execution: GovExecutionStatus.Succeeded,
                        accessExpires: Now.AddDays(-5), revoked: Now.AddDays(-4)), Now));
        }

        [Fact]
        public void PermanentAccess_NeverLapses()
        {
            Assert.False(AccessRequestPolicy.AccessHasLapsed(
                Request(status: GovRequestStatus.Approved, execution: GovExecutionStatus.Succeeded,
                        accessExpires: null), Now));
        }

        [Fact]
        public void AccessStillWithinItsWindow_DoesNotLapse()
        {
            Assert.False(AccessRequestPolicy.AccessHasLapsed(
                Request(status: GovRequestStatus.Approved, execution: GovExecutionStatus.Succeeded,
                        accessExpires: Now.AddDays(1)), Now));
        }
    }
}
