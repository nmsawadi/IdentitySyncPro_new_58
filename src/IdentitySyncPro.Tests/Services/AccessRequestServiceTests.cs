using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Governance;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Hangfire;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Guards the access-request service where it touches the directory.
    ///
    /// The centre of it is what happens when Active Directory cannot answer. The membership helper
    /// this module builds on was written for SSPR's exclusion list, where an unanswerable directory
    /// means "assume forbidden" and returns <c>true</c>. Asked "is this person an approver?", that
    /// same <c>true</c> hands the approval right to whoever asked, during an outage, silently. Every
    /// permission question here must therefore refuse rather than assume — and say which question
    /// it could not answer.
    /// </summary>
    public class AccessRequestServiceTests
    {
        // ══════════════════════════════════════
        // HARNESS
        // ══════════════════════════════════════

        private sealed class Harness
        {
            public GovernanceDbContext Gov = null!;
            public AppDbContext App = null!;
            public Mock<ITargetConnector> Target = null!;
            public AccessRequestService Service = null!;
            public GovCatalogItem Item = null!;

            public List<(string Identity, string[] Groups)> Added = new();
            public List<(string Identity, string[] Groups)> Removed = new();
        }

        private static Harness Build(
            bool? memberAnswer = false,
            bool addSucceeds = true,
            bool removeSucceeds = true,
            string? approverUsers = "manager1",
            string? approverGroup = null,
            string? eligibleGroup = null,
            int accessDurationDays = 0,
            int decisionDueDays = 7)
        {
            var id = Guid.NewGuid();
            var h = new Harness
            {
                Gov = new GovernanceDbContext(new DbContextOptionsBuilder<GovernanceDbContext>()
                    .UseInMemoryDatabase($"gov-{id}").Options),
                App = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase($"app-{id}").Options)
            };

            h.App.TenantSettings.Add(new TenantSettings { Id = 1, TenantName = "الموظفون", IsActive = true });
            h.App.SaveChanges();

            h.Item = new GovCatalogItem
            {
                Id = 1,
                DisplayName = "شبكة المختبرات",
                TargetType = GovTargetTypes.AdGroup,
                TenantId = 1,
                GroupName = "Lab-Network",
                ApproverUsers = approverUsers,
                ApproverAdGroup = approverGroup,
                EligibleRequesterGroup = eligibleGroup,
                ApproverNotificationEmail = "approvers@x.sa",
                DecisionDueDays = decisionDueDays,
                AccessDurationDays = accessDurationDays,
                IsEnabled = true
            };
            h.Gov.CatalogItems.Add(h.Item);
            h.Gov.SaveChanges();

            h.Target = new Mock<ITargetConnector>();
            h.Target.Setup(t => t.TryIsMemberOfAnyAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(memberAnswer);
            h.Target.Setup(t => t.GetAttributesAsync(It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new Dictionary<string, string> { ["displayName"] = "أحمد", ["mail"] = "a@x.sa" });
            h.Target.Setup(t => t.AddToGroupsAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                    .Returns((string identity, IEnumerable<string> groups, CancellationToken _) =>
                    {
                        h.Added.Add((identity, groups.ToArray()));
                        return Task.FromResult((addSucceeds, addSucceeds ? 1 : 0, groups.ToList()));
                    });
            h.Target.Setup(t => t.RemoveFromSpecificGroupsAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                    .Returns((string identity, IEnumerable<string> groups, CancellationToken _) =>
                    {
                        h.Removed.Add((identity, groups.ToArray()));
                        return Task.FromResult((removeSucceeds, removeSucceeds ? 1 : 0, groups.ToList()));
                    });

            var factory = new Mock<ITenantConnectorFactory>();
            factory.Setup(f => f.CreateTargetConnector(It.IsAny<TenantSettings>())).Returns(h.Target.Object);

            var email = new Mock<IEmailService>();
            email.Setup(e => e.SendAsync(It.IsAny<EmailMessage>())).ReturnsAsync(new EmailResult { Success = true });

            var audit = new Mock<IAuditService>();

            // Notifications are queued, not sent inline — see AccessNotificationJob. The client is
            // mocked because what matters to these tests is that the governance work completes
            // without waiting on a mail server, which is exactly the bug that moved it here.
            var jobs = new Mock<IBackgroundJobClient>();

            h.Service = new AccessRequestService(
                h.Gov, h.App, factory.Object, jobs.Object, audit.Object,
                NullLogger<AccessRequestService>.Instance);

            return h;
        }

        private static Task<AccessRequestService.Outcome> Raise(Harness h, string by = "operator1") =>
            h.Service.CreateAsync(1, "ahmed.s", by, GovChannels.Console, "مشروع بحثي");

        // ══════════════════════════════════════
        // AN UNANSWERABLE DIRECTORY
        // ══════════════════════════════════════

        /// <summary>
        /// The security hole this whole design turns on. If "is this person an approver?" answered
        /// optimistically during an outage, anyone who opened the screen could approve anything —
        /// and the audit trail would record it as a legitimate decision.
        /// </summary>
        [Fact]
        public async Task WhenTheDirectoryCannotSayWhoIsAnApprover_TheDecisionIsRefused()
        {
            var h = Build(approverUsers: null, approverGroup: "Access Approvers");
            var raised = await Raise(h);

            h.Target.Setup(t => t.TryIsMemberOfAnyAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((bool?)null);

            var outcome = await h.Service.DecideAsync(raised.RequestId, "stranger", GovDecisions.Approve, null);

            Assert.False(outcome.Ok);
            Assert.Contains("approver membership", outcome.Error!);
            Assert.Empty(h.Added);
        }

        [Fact]
        public async Task WhenTheDirectoryCannotSayWhoIsEligible_TheRequestIsRefused()
        {
            var h = Build(memberAnswer: null, eligibleGroup: "Lab-Staff");

            var outcome = await Raise(h);

            Assert.False(outcome.Ok);
            Assert.Contains("eligibility", outcome.Error!);
        }

        /// <summary>
        /// A duplicate request would be approved, execute as a no-op, and leave an approval record
        /// implying a grant that never happened — so an unverifiable membership refuses the raise.
        /// </summary>
        [Fact]
        public async Task WhenTheDirectoryCannotSayWhatTheAccountAlreadyHas_TheRequestIsRefused()
        {
            var h = Build(memberAnswer: null);

            var outcome = await Raise(h);

            Assert.False(outcome.Ok);
            Assert.Contains("current membership", outcome.Error!);
        }

        /// <summary>An open item asks the directory nothing about eligibility — no group is a deliberate setting, not an open question.</summary>
        [Fact]
        public async Task AnItemWithNoEligibilityGroup_IsOpenToEveryone()
        {
            var h = Build(eligibleGroup: null);
            Assert.True((await Raise(h)).Ok);
        }

        // ══════════════════════════════════════
        // RAISING
        // ══════════════════════════════════════

        [Fact]
        public async Task AValidRequest_IsStoredPendingAndAwaitingNothingFromTheDirectory()
        {
            var h = Build();
            var outcome = await Raise(h);

            Assert.True(outcome.Ok);
            var stored = await h.Gov.AccessRequests.FirstAsync();
            Assert.Equal(GovRequestStatus.Pending, stored.Status);
            Assert.Equal(GovExecutionStatus.None, stored.ExecutionStatus);
            Assert.NotNull(stored.DecisionDueUtc);
            Assert.Equal("أحمد", stored.SubjectDisplayName);
            Assert.Empty(h.Added);
        }

        [Fact]
        public async Task AccessTheAccountAlreadyHolds_IsRefused()
        {
            var h = Build(memberAnswer: true);
            var outcome = await Raise(h);

            Assert.False(outcome.Ok);
            Assert.Contains("already has this access", outcome.Error!);
        }

        /// <summary>
        /// A catalog item whose approver was removed after requests could be raised against it is
        /// still refused here — the request would otherwise enter a queue nobody can clear.
        /// </summary>
        [Fact]
        public async Task AnItemThatLostItsApprover_AcceptsNothing()
        {
            var h = Build();
            h.Item.ApproverUsers = null;
            h.Item.ApproverAdGroup = null;
            await h.Gov.SaveChangesAsync();

            var outcome = await Raise(h);

            Assert.False(outcome.Ok);
            Assert.Contains("never be decided", outcome.Error!);
        }

        // ══════════════════════════════════════
        // DECIDING AND EXECUTING
        // ══════════════════════════════════════

        [Fact]
        public async Task AnApproval_ReachesActiveDirectory()
        {
            var h = Build();
            var raised = await Raise(h);

            var outcome = await h.Service.DecideAsync(raised.RequestId, "manager1", GovDecisions.Approve, "موافق");

            Assert.True(outcome.Ok);
            var stored = await h.Gov.AccessRequests.FirstAsync();
            Assert.Equal(GovRequestStatus.Approved, stored.Status);
            Assert.Equal(GovExecutionStatus.Succeeded, stored.ExecutionStatus);
            Assert.NotNull(stored.ExecutedUtc);

            var (identity, groups) = Assert.Single(h.Added);
            Assert.Equal("ahmed.s", identity);
            Assert.Equal(new[] { "Lab-Network" }, groups);
        }

        [Fact]
        public async Task ARejection_TouchesNothingInActiveDirectory()
        {
            var h = Build();
            var raised = await Raise(h);

            await h.Service.DecideAsync(raised.RequestId, "manager1", GovDecisions.Reject, "غير مبرّر");

            var stored = await h.Gov.AccessRequests.FirstAsync();
            Assert.Equal(GovRequestStatus.Rejected, stored.Status);
            Assert.Equal(GovExecutionStatus.None, stored.ExecutionStatus);
            Assert.Empty(h.Added);
        }

        /// <summary>
        /// The gap the two status columns exist to hold: the approver decided, the directory
        /// refused. The decision stands, the execution is marked failed with its reason, and the
        /// row is queryable rather than silent.
        /// </summary>
        [Fact]
        public async Task WhenActiveDirectoryRefuses_TheApprovalStandsAndTheFailureIsRecorded()
        {
            var h = Build(addSucceeds: false);
            var raised = await Raise(h);

            await h.Service.DecideAsync(raised.RequestId, "manager1", GovDecisions.Approve, null);

            var stored = await h.Gov.AccessRequests.FirstAsync();
            Assert.Equal(GovRequestStatus.Approved, stored.Status);
            Assert.Equal(GovExecutionStatus.Failed, stored.ExecutionStatus);
            Assert.False(string.IsNullOrWhiteSpace(stored.ExecutionError));
        }

        [Fact]
        public async Task ADecisionIsRecordedWithItsAuthor()
        {
            var h = Build();
            var raised = await Raise(h);

            await h.Service.DecideAsync(raised.RequestId, "manager1", GovDecisions.Approve, "موافق");

            var decision = await h.Gov.RequestDecisions.SingleAsync();
            Assert.Equal("manager1", decision.ApproverUsername);
            Assert.Equal(GovDecisions.Approve, decision.Decision);
            Assert.Equal("موافق", decision.Comment);
        }

        /// <summary>Executing twice would add the membership a second time and re-notify the holder.</summary>
        [Fact]
        public async Task ExecutingAnAlreadyExecutedRequest_DoesNothingAgain()
        {
            var h = Build();
            var raised = await Raise(h);
            await h.Service.DecideAsync(raised.RequestId, "manager1", GovDecisions.Approve, null);

            var outcome = await h.Service.ExecuteAsync(raised.RequestId);

            Assert.True(outcome.Ok);
            Assert.Single(h.Added);
        }

        [Fact]
        public async Task AnUndecidedRequest_CannotBeExecuted()
        {
            var h = Build();
            var raised = await Raise(h);

            var outcome = await h.Service.ExecuteAsync(raised.RequestId);

            Assert.False(outcome.Ok);
            Assert.Empty(h.Added);
        }

        // ══════════════════════════════════════
        // THE SWEEP
        // ══════════════════════════════════════

        /// <summary>
        /// Why the sweep runs on a timer rather than only at decision time: an approval lost to a
        /// brief outage is access somebody was told they had.
        /// </summary>
        [Fact]
        public async Task TheSweep_RetriesAnApprovalThatNeverReachedTheDirectory()
        {
            var h = Build(addSucceeds: false);
            var raised = await Raise(h);
            await h.Service.DecideAsync(raised.RequestId, "manager1", GovDecisions.Approve, null);
            Assert.Equal(GovExecutionStatus.Failed, (await h.Gov.AccessRequests.FirstAsync()).ExecutionStatus);

            // the directory comes back
            h.Target.Setup(t => t.AddToGroupsAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((true, 1, new List<string> { "Lab-Network" }));

            var result = await h.Service.SweepAsync();

            Assert.Equal(1, result.Executed);
            Assert.Equal(GovExecutionStatus.Succeeded, (await h.Gov.AccessRequests.FirstAsync()).ExecutionStatus);
        }

        [Fact]
        public async Task TheSweep_ClosesADecisionNobodyMade()
        {
            var h = Build();
            var raised = await Raise(h);
            var request = await h.Gov.AccessRequests.FirstAsync();
            request.DecisionDueUtc = DateTime.UtcNow.AddMinutes(-1);
            await h.Gov.SaveChangesAsync();

            var result = await h.Service.SweepAsync();

            Assert.Equal(1, result.Expired);
            Assert.Equal(GovRequestStatus.Expired, (await h.Gov.AccessRequests.FirstAsync()).Status);
        }

        [Fact]
        public async Task TheSweep_TakesBackAccessWhosePeriodEnded()
        {
            var h = Build(accessDurationDays: 30);
            var raised = await Raise(h);
            await h.Service.DecideAsync(raised.RequestId, "manager1", GovDecisions.Approve, null);

            var request = await h.Gov.AccessRequests.FirstAsync();
            Assert.NotNull(request.AccessExpiresUtc);
            request.AccessExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
            await h.Gov.SaveChangesAsync();

            var result = await h.Service.SweepAsync();

            Assert.Equal(1, result.Revoked);
            Assert.NotNull((await h.Gov.AccessRequests.FirstAsync()).AccessRevokedUtc);
            var (identity, groups) = Assert.Single(h.Removed);
            Assert.Equal("ahmed.s", identity);
            Assert.Equal(new[] { "Lab-Network" }, groups);
        }

        /// <summary>
        /// Stamping the revocation regardless would mark the access as taken back while the person
        /// still holds it — the very failure a time-bound grant exists to prevent, wearing the
        /// appearance of the fix. Leaving the stamp unset is what makes the next sweep try again.
        /// </summary>
        [Fact]
        public async Task AFailedRevocation_IsNotRecordedAsDone()
        {
            var h = Build(accessDurationDays: 30, removeSucceeds: false);
            var raised = await Raise(h);
            await h.Service.DecideAsync(raised.RequestId, "manager1", GovDecisions.Approve, null);

            var request = await h.Gov.AccessRequests.FirstAsync();
            request.AccessExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
            await h.Gov.SaveChangesAsync();

            var result = await h.Service.SweepAsync();

            Assert.Equal(0, result.Revoked);
            Assert.Equal(1, result.Failed);
            Assert.Null((await h.Gov.AccessRequests.FirstAsync()).AccessRevokedUtc);
        }

        [Fact]
        public async Task PermanentAccess_IsNeverTakenBack()
        {
            var h = Build(accessDurationDays: 0);
            var raised = await Raise(h);
            await h.Service.DecideAsync(raised.RequestId, "manager1", GovDecisions.Approve, null);

            var result = await h.Service.SweepAsync();

            Assert.Equal(0, result.Revoked);
            Assert.Empty(h.Removed);
            Assert.Null((await h.Gov.AccessRequests.FirstAsync()).AccessExpiresUtc);
        }

        [Fact]
        public async Task AQuietSweep_ReportsNothing()
        {
            var h = Build();
            await Raise(h);

            var result = await h.Service.SweepAsync();

            Assert.Equal(new AccessRequestService.SweepResult(0, 0, 0, 0), result);
        }

        // ══════════════════════════════════════
        // WITHDRAWING
        // ══════════════════════════════════════

        [Fact]
        public async Task TheRequesterCanWithdraw_AndNobodyElseCan()
        {
            var h = Build();
            var raised = await Raise(h);

            Assert.False((await h.Service.CancelAsync(raised.RequestId, "operator2")).Ok);
            Assert.True((await h.Service.CancelAsync(raised.RequestId, "operator1")).Ok);
            Assert.Equal(GovRequestStatus.Cancelled, (await h.Gov.AccessRequests.FirstAsync()).Status);
        }
    }
}
