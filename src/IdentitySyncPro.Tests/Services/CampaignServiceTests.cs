using Hangfire;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Governance;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Guards the certification engine where it meets the directory.
    ///
    /// Two failures here are worse than anything in the request module, because both end with an
    /// auditor being told access was certified when it was not:
    ///
    /// A group read that stops short produces a campaign missing part of a population. Nobody
    /// declines to review those rows — they were never in it — and the campaign closes clean.
    ///
    /// And the deadline revokes what nobody decided. On a campaign that was actually reviewed that
    /// is the point; on one nobody opened it takes a department's access on no one's judgement.
    /// </summary>
    public class CampaignServiceTests
    {
        private sealed class Harness
        {
            public GovernanceDbContext Gov = null!;
            public AppDbContext App = null!;
            public Mock<ITargetConnector> Target = null!;
            public CampaignService Service = null!;
            public GovCampaign Campaign = null!;
            public List<(string Identity, string[] Groups)> Removed = new();
        }

        private static Harness Build(
            IReadOnlyList<GroupMember>? members = null,
            bool readSucceeds = true,
            bool removeSucceeds = true,
            string? reviewers = "manager1",
            int maxUndecided = 50,
            int reviewDays = 14,
            string status = GovCampaignStatus.Draft)
        {
            var id = Guid.NewGuid();
            var h = new Harness
            {
                Gov = new GovernanceDbContext(new DbContextOptionsBuilder<GovernanceDbContext>()
                    .UseInMemoryDatabase($"gov-{id}").Options),
                App = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase($"app-{id}").Options)
            };

            h.App.TenantSettings.Add(new TenantSettings { Id = 1, TenantName = "الطلاب", IsActive = true });
            h.App.SaveChanges();

            h.Campaign = new GovCampaign
            {
                Id = 1,
                Name = "مراجعة الصلاحيات الإدارية",
                ScopeGroups = "DB-Admin",
                ScopeTenantId = 1,
                ReviewerUsers = reviewers,
                ReviewerNotificationEmail = "reviewers@x.sa",
                ReviewDays = reviewDays,
                MaxUndecidedRevokePercent = maxUndecided,
                Status = status
            };
            h.Gov.Campaigns.Add(h.Campaign);
            h.Gov.SaveChanges();

            members ??= new[]
            {
                new GroupMember("ahmed.s", "أحمد", "CN=ahmed,DC=x"),
                new GroupMember("sara.k", "سارة", "CN=sara,DC=x"),
                new GroupMember("omar.t", "عمر", "CN=omar,DC=x"),
                new GroupMember("lina.m", "لينا", "CN=lina,DC=x")
            };

            h.Target = new Mock<ITargetConnector>();
            h.Target.Setup(t => t.GetGroupMembersAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(readSucceeds
                        ? (true, members, (string?)null)
                        : (false, Array.Empty<GroupMember>(), "the directory stopped answering"));
            h.Target.Setup(t => t.TryIsMemberOfAnyAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(false);
            h.Target.Setup(t => t.RemoveFromSpecificGroupsAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                    .Returns((string identity, IEnumerable<string> groups, CancellationToken _) =>
                    {
                        h.Removed.Add((identity, groups.ToArray()));
                        return Task.FromResult((removeSucceeds, removeSucceeds ? 1 : 0, groups.ToList()));
                    });

            var factory = new Mock<ITenantConnectorFactory>();
            factory.Setup(f => f.CreateTargetConnector(It.IsAny<TenantSettings>())).Returns(h.Target.Object);

            h.Service = new CampaignService(
                h.Gov, h.App, factory.Object, new Mock<IBackgroundJobClient>().Object,
                new Mock<IAuditService>().Object, NullLogger<CampaignService>.Instance);

            return h;
        }

        private static async Task<Harness> Launched(
            int maxUndecided = 50, string? reviewers = "manager1",
            bool removeSucceeds = true, IReadOnlyList<GroupMember>? members = null)
        {
            var h = Build(members: members, reviewers: reviewers,
                          removeSucceeds: removeSucceeds, maxUndecided: maxUndecided);
            var outcome = await h.Service.LaunchAsync(1, "isp-admin");
            Assert.True(outcome.Ok, outcome.Error);
            return h;
        }

        private static async Task LapseAsync(Harness h)
        {
            var c = await h.Gov.Campaigns.FirstAsync();
            c.DueUtc = DateTime.UtcNow.AddMinutes(-1);
            await h.Gov.SaveChangesAsync();
        }

        // ══════════════════════════════════════
        // THE SNAPSHOT
        // ══════════════════════════════════════

        [Fact]
        public async Task LaunchingRecordsEveryMembershipAsPending()
        {
            var h = await Launched();

            var items = await h.Gov.CampaignItems.ToListAsync();
            Assert.Equal(4, items.Count);
            Assert.All(items, i => Assert.Equal(GovReviewDecisions.Pending, i.Decision));
            Assert.All(items, i => Assert.Equal("DB-Admin", i.GroupName));
            Assert.Contains(items, i => i.SubjectDisplayName == "أحمد");

            var campaign = await h.Gov.Campaigns.FirstAsync();
            Assert.Equal(GovCampaignStatus.Active, campaign.Status);
            Assert.NotNull(campaign.DueUtc);
        }

        /// <summary>
        /// The failure this feature would be worthless with: a group read that stopped short, a
        /// campaign reviewed diligently, and an auditor told everything was certified.
        /// </summary>
        [Fact]
        public async Task AGroupThatCannotBeReadInFull_AbortsTheLaunch()
        {
            var h = Build(readSucceeds: false);

            var outcome = await h.Service.LaunchAsync(1, "isp-admin");

            Assert.False(outcome.Ok);
            Assert.Contains("DB-Admin", outcome.Error!);
            Assert.Empty(await h.Gov.CampaignItems.ToListAsync());
            // And the campaign is still a draft, so it can be launched again once the directory is back.
            Assert.Equal(GovCampaignStatus.Draft, (await h.Gov.Campaigns.FirstAsync()).Status);
        }

        /// <summary>An empty scope is a configuration fault, not a clean directory.</summary>
        [Fact]
        public async Task ACampaignThatWouldHaveNoItems_IsNotLaunched()
        {
            var h = Build(members: Array.Empty<GroupMember>());

            var outcome = await h.Service.LaunchAsync(1, "isp-admin");

            Assert.False(outcome.Ok);
            Assert.Contains("Not one membership", outcome.Error!);
            Assert.Equal(GovCampaignStatus.Draft, (await h.Gov.Campaigns.FirstAsync()).Status);
        }

        [Fact]
        public async Task ACampaignWithNoReviewer_IsNotLaunched()
        {
            var h = Build(reviewers: null);
            var outcome = await h.Service.LaunchAsync(1, "isp-admin");

            Assert.False(outcome.Ok);
            Assert.Contains("revoke every membership", outcome.Error!);
        }

        [Fact]
        public async Task ACampaignIsNotLaunchedTwice()
        {
            var h = await Launched();
            var second = await h.Service.LaunchAsync(1, "isp-admin");

            Assert.False(second.Ok);
            Assert.Equal(4, await h.Gov.CampaignItems.CountAsync());
        }

        // ══════════════════════════════════════
        // REVIEWING
        // ══════════════════════════════════════

        [Fact]
        public async Task KeepingAMembership_TouchesNothingInTheDirectory()
        {
            var h = await Launched();
            var item = await h.Gov.CampaignItems.FirstAsync();

            var outcome = await h.Service.DecideAsync(item.Id, "manager1", GovReviewDecisions.Keep, "ما زال يحتاجها");

            Assert.True(outcome.Ok, outcome.Error);
            var saved = await h.Gov.CampaignItems.FirstAsync(i => i.Id == item.Id);
            Assert.Equal(GovReviewDecisions.Keep, saved.Decision);
            Assert.Equal(GovExecutionStatus.None, saved.ExecutionStatus);
            Assert.Equal("manager1", saved.DecidedBy);
            Assert.Null(saved.DecidedOnBehalfOf);
            Assert.Empty(h.Removed);
        }

        [Fact]
        public async Task RevokingAMembership_ReachesTheDirectory()
        {
            var h = await Launched();
            var item = await h.Gov.CampaignItems.FirstAsync();

            await h.Service.DecideAsync(item.Id, "manager1", GovReviewDecisions.Revoke, "غادر الفريق");

            var saved = await h.Gov.CampaignItems.FirstAsync(i => i.Id == item.Id);
            Assert.Equal(GovExecutionStatus.Succeeded, saved.ExecutionStatus);
            Assert.Equal(GovDecisionSources.Reviewer, saved.DecisionSource);

            var (identity, groups) = Assert.Single(h.Removed);
            Assert.Equal(item.SubjectAccount, identity);
            Assert.Equal(new[] { "DB-Admin" }, groups);
        }

        /// <summary>
        /// The record has to name the person who actually decided and the duty they carried. A
        /// certificate saying the manager decided, while the manager was away, is not true — and
        /// nothing else in the row would reveal it.
        /// </summary>
        [Fact]
        public async Task ADelegateDecides_AndBothNamesAreKept()
        {
            var h = await Launched();
            h.Gov.ReviewDelegations.Add(new GovReviewDelegation
            {
                FromUsername = "manager1",
                ToUsername = "deputy1",
                StartUtc = DateTime.UtcNow.AddDays(-1),
                EndUtc = DateTime.UtcNow.AddDays(7)
            });
            await h.Gov.SaveChangesAsync();

            var item = await h.Gov.CampaignItems.FirstAsync();
            var outcome = await h.Service.DecideAsync(item.Id, "deputy1", GovReviewDecisions.Keep, null);

            Assert.True(outcome.Ok, outcome.Error);
            var saved = await h.Gov.CampaignItems.FirstAsync(i => i.Id == item.Id);
            Assert.Equal("deputy1", saved.DecidedBy);
            Assert.Equal("manager1", saved.DecidedOnBehalfOf);
        }

        [Fact]
        public async Task WithoutADelegation_AStandInDecidesNothing()
        {
            var h = await Launched();
            var item = await h.Gov.CampaignItems.FirstAsync();

            var outcome = await h.Service.DecideAsync(item.Id, "deputy1", GovReviewDecisions.Keep, null);

            Assert.False(outcome.Ok);
            Assert.Equal(GovReviewDecisions.Pending, (await h.Gov.CampaignItems.FirstAsync(i => i.Id == item.Id)).Decision);
        }

        [Fact]
        public async Task AnExpiredDelegation_DecidesNothing()
        {
            var h = await Launched();
            h.Gov.ReviewDelegations.Add(new GovReviewDelegation
            {
                FromUsername = "manager1",
                ToUsername = "deputy1",
                StartUtc = DateTime.UtcNow.AddDays(-30),
                EndUtc = DateTime.UtcNow.AddDays(-1)
            });
            await h.Gov.SaveChangesAsync();

            var item = await h.Gov.CampaignItems.FirstAsync();
            Assert.False((await h.Service.DecideAsync(item.Id, "deputy1", GovReviewDecisions.Keep, null)).Ok);
        }

        [Fact]
        public async Task OnlyTheDelegatingReviewerCanEndTheDelegation()
        {
            var h = await Launched();
            var delegation = new GovReviewDelegation
            {
                FromUsername = "manager1",
                ToUsername = "deputy1",
                StartUtc = DateTime.UtcNow.AddDays(-1),
                EndUtc = DateTime.UtcNow.AddDays(7)
            };
            h.Gov.ReviewDelegations.Add(delegation);
            await h.Gov.SaveChangesAsync();

            Assert.False((await h.Service.EndDelegationAsync(delegation.Id, "deputy1")).Ok);
            Assert.True((await h.Service.EndDelegationAsync(delegation.Id, "manager1")).Ok);
            Assert.NotNull((await h.Gov.ReviewDelegations.FirstAsync()).RevokedUtc);
        }

        /// <summary>The reviewer decided, the directory refused — and the two facts stay separate.</summary>
        [Fact]
        public async Task WhenTheDirectoryRefuses_TheDecisionStandsAndTheFailureIsRecorded()
        {
            var h = await Launched(removeSucceeds: false);
            var item = await h.Gov.CampaignItems.FirstAsync();

            await h.Service.DecideAsync(item.Id, "manager1", GovReviewDecisions.Revoke, null);

            var saved = await h.Gov.CampaignItems.FirstAsync(i => i.Id == item.Id);
            Assert.Equal(GovReviewDecisions.Revoke, saved.Decision);
            Assert.Equal(GovExecutionStatus.Failed, saved.ExecutionStatus);
            Assert.False(string.IsNullOrWhiteSpace(saved.ExecutionError));
        }

        // ══════════════════════════════════════
        // THE DEADLINE
        // ══════════════════════════════════════

        /// <summary>A reviewed campaign: the few rows nobody reached are revoked, which is what certification means.</summary>
        [Fact]
        public async Task AReviewedCampaign_RevokesWhatWasLeftUndecided()
        {
            var h = await Launched();
            var items = await h.Gov.CampaignItems.OrderBy(i => i.Id).ToListAsync();
            foreach (var item in items.Take(3))
                await h.Service.DecideAsync(item.Id, "manager1", GovReviewDecisions.Keep, null);

            await LapseAsync(h);
            var result = await h.Service.SweepAsync();

            Assert.Equal(1, result.Closed);
            Assert.Equal(0, result.Halted);
            Assert.Equal(1, result.AutoRevoked);

            var leftover = await h.Gov.CampaignItems.FirstAsync(i => i.Id == items[3].Id);
            Assert.Equal(GovReviewDecisions.Revoke, leftover.Decision);
            Assert.Equal(GovDecisionSources.AutoRevokedUndecided, leftover.DecisionSource);
            Assert.Equal(GovExecutionStatus.Succeeded, leftover.ExecutionStatus);
            Assert.Single(h.Removed);
        }

        /// <summary>
        /// The guard. Nobody reviewed anything, so the deadline is not a verdict — and acting on it
        /// would take four people's access on no one's judgement.
        /// </summary>
        [Fact]
        public async Task AnUnreviewedCampaign_ClosesWithoutRevokingAnything()
        {
            var h = await Launched();
            await LapseAsync(h);

            var result = await h.Service.SweepAsync();

            Assert.Equal(1, result.Closed);
            Assert.Equal(1, result.Halted);
            Assert.Equal(0, result.AutoRevoked);
            Assert.Empty(h.Removed);

            var campaign = await h.Gov.Campaigns.FirstAsync();
            Assert.Equal(GovCampaignStatus.Closed, campaign.Status);
            // The closing note has to say so: hiding it defeats the exercise as surely as revoking blindly would.
            Assert.Contains("unreviewed campaign", campaign.ClosingNote!);
            Assert.All(await h.Gov.CampaignItems.ToListAsync(),
                i => Assert.Equal(GovReviewDecisions.Pending, i.Decision));
        }

        [Fact]
        public async Task AFullyReviewedCampaign_ClosesCleanly()
        {
            var h = await Launched();
            foreach (var item in await h.Gov.CampaignItems.ToListAsync())
                await h.Service.DecideAsync(item.Id, "manager1", GovReviewDecisions.Keep, null);

            await LapseAsync(h);
            var result = await h.Service.SweepAsync();

            Assert.Equal(1, result.Closed);
            Assert.Equal(0, result.Halted);
            Assert.Equal(0, result.AutoRevoked);
            Assert.Contains("Fully reviewed", (await h.Gov.Campaigns.FirstAsync()).ClosingNote!);
        }

        /// <summary>A stricter campaign can demand a complete review before it will act at all.</summary>
        [Fact]
        public async Task AZeroCeiling_StopsOnASingleUndecidedRow()
        {
            var h = await Launched(maxUndecided: 0);
            var items = await h.Gov.CampaignItems.OrderBy(i => i.Id).ToListAsync();
            foreach (var item in items.Take(3))
                await h.Service.DecideAsync(item.Id, "manager1", GovReviewDecisions.Keep, null);

            await LapseAsync(h);
            var result = await h.Service.SweepAsync();

            Assert.Equal(1, result.Halted);
            Assert.Empty(h.Removed);
        }

        [Fact]
        public async Task AClosedCampaign_IsNotSweptAgain()
        {
            var h = await Launched();
            await LapseAsync(h);
            await h.Service.SweepAsync();

            var second = await h.Service.SweepAsync();

            Assert.Equal(0, second.Closed);
        }

        /// <summary>Why the sweep runs on a timer: a revocation lost to a brief outage is access somebody was certified out of and still holds.</summary>
        [Fact]
        public async Task TheSweep_RetriesARevocationThatNeverReachedTheDirectory()
        {
            var h = await Launched(removeSucceeds: false);
            var item = await h.Gov.CampaignItems.FirstAsync();
            await h.Service.DecideAsync(item.Id, "manager1", GovReviewDecisions.Revoke, null);
            Assert.Equal(GovExecutionStatus.Failed, (await h.Gov.CampaignItems.FirstAsync(i => i.Id == item.Id)).ExecutionStatus);

            h.Target.Setup(t => t.RemoveFromSpecificGroupsAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((true, 1, new List<string> { "DB-Admin" }));

            var result = await h.Service.SweepAsync();

            Assert.Equal(1, result.Retried);
            Assert.Equal(GovExecutionStatus.Succeeded, (await h.Gov.CampaignItems.FirstAsync(i => i.Id == item.Id)).ExecutionStatus);
        }

        [Fact]
        public async Task AQuietSweep_ReportsNothing()
        {
            var h = await Launched();
            Assert.Equal(new CampaignService.SweepResult(0, 0, 0, 0, 0), await h.Service.SweepAsync());
        }
    }
}
