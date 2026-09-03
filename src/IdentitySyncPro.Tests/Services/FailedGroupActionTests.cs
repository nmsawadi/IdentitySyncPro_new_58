using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Metaverse;
using IdentitySyncPro.Core.Models.Rules;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Connectors;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using IdentitySyncPro.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// The group connectors used to return Success = true no matter what happened inside their loop:
    /// a per-group exception was logged as a warning and discarded, and AddToGroups even computed how
    /// many groups it had failed to apply, printed the number, and returned success anyway.
    ///
    /// That mattered because a licence group drives mailbox provisioning in Exchange Online. A
    /// graduate keeping one, or an active identity never getting one, showed up as a single [WRN]
    /// line in a run that reported "0 failed".
    ///
    /// These pin the consequence rather than the LDAP calls: a group action that reports failure must
    /// block the export stamp, exactly as a refused move does — and one that succeeded while changing
    /// nothing must not.
    /// </summary>
    public class FailedGroupActionTests
    {
        private const string BaseDn = "DC=students,DC=lab,DC=local";
        private const string Identity = "440000009";
        private const string LicenceGroups = "Najran-A3-lic,Sharorah-A3-lic";

        private static (LifecycleEngine engine, IServiceScopeFactory scopes, AppDbContext db, Mock<ITargetConnector> ad)
            Setup(string actionType, string? actionValue, string lifecycleState, int statusCode)
        {
            var db = TestDbContext.Create();
            var tenant = new TenantSettings
            {
                TenantName = "الطلاب", IsActive = true,
                ADUsername = "a", ADPassword = "p", ADBaseDN = BaseDn,
                SourceStatusColumn = "STATUSE_CODE"
            };
            db.TenantSettings.Add(tenant);
            db.SaveChanges();

            db.LifecycleRules.Add(new LifecycleRule
            {
                TenantId = tenant.Id, Name = $"قاعدة {actionType}", Enabled = true, Priority = 70,
                TriggerType = "OnImport", ConditionField = "STATUSE_CODE", ConditionOperator = "==",
                ConditionValue = statusCode.ToString(),
                ActionType = actionType, ActionValue = actionValue
            });

            db.MetaverseEntries.Add(new MetaverseEntry
            {
                TenantId = tenant.Id,
                ExternalId = Identity,
                LifecycleState = lifecycleState,
                SourceStatusCode = statusCode,
                ADCurrentOU = $"OU=GRADUATES,{BaseDn}",
                AttributesJson = $$"""{"STATUSE_CODE":{{statusCode}}}""",
                StateChangedDate = DateTime.UtcNow,
                LastExportDate = null
            });
            db.SaveChanges();

            var ad = new Mock<ITargetConnector>();
            ad.Setup(t => t.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

            var services = new ServiceCollection();
            services.AddSingleton(db);
            services.AddSingleton(ad.Object);
            services.AddLogging();
            var provider = services.BuildServiceProvider();

            var engine = new LifecycleEngine(db, Mock.Of<ISourceConnector>(), ad.Object,
                Mock.Of<ILogger<LifecycleEngine>>());

            return (engine, provider.GetRequiredService<IServiceScopeFactory>(), db, ad);
        }

        // ── RemoveGroups naming specific groups (the graduate licence case) ──

        [Fact]
        public async Task AFailedSpecificGroupRemoval_BlocksTheExportStamp()
        {
            var (engine, scopes, db, ad) = Setup("RemoveGroups", LicenceGroups, "Deprovisioned", 7);
            ad.Setup(t => t.RemoveFromSpecificGroupsAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(),
                    It.IsAny<CancellationToken>()))
              .ReturnsAsync((false, 0, new List<string>()));

            var exported = await engine.BulkExportAsync(scopes);

            Assert.Equal(0, exported);
            Assert.Null(db.MetaverseEntries.Single().LastExportDate);
        }

        [Fact]
        public async Task AGroupTheIdentityWasNeverIn_IsNotAFailure()
        {
            // The normal reading of "removed from 1/2 specified groups": the rule names two licence
            // groups and this identity held one. Treating that as a failure would make every single
            // graduate fail forever — which is why RemovedCount is deliberately not the signal.
            var (engine, scopes, db, ad) = Setup("RemoveGroups", LicenceGroups, "Deprovisioned", 7);
            ad.Setup(t => t.RemoveFromSpecificGroupsAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(),
                    It.IsAny<CancellationToken>()))
              .ReturnsAsync((true, 1, new List<string> { "Najran-A3-lic" }));

            var exported = await engine.BulkExportAsync(scopes);

            Assert.Equal(1, exported);
            Assert.NotNull(db.MetaverseEntries.Single().LastExportDate);
        }

        [Fact]
        public async Task RemovingNothingAtAll_WhileReportingSuccess_IsStillNotAFailure()
        {
            // "User is not a member of any groups" — a graduate whose licence was already gone.
            var (engine, scopes, db, ad) = Setup("RemoveGroups", LicenceGroups, "Deprovisioned", 7);
            ad.Setup(t => t.RemoveFromSpecificGroupsAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(),
                    It.IsAny<CancellationToken>()))
              .ReturnsAsync((true, 0, new List<string>()));

            Assert.Equal(1, await engine.BulkExportAsync(scopes));
            Assert.NotNull(db.MetaverseEntries.Single().LastExportDate);
        }

        [Fact]
        public async Task AFailedRemoval_IsRetriedOnceTheCauseIsFixed()
        {
            var (engine, scopes, db, ad) = Setup("RemoveGroups", LicenceGroups, "Deprovisioned", 7);
            ad.Setup(t => t.RemoveFromSpecificGroupsAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(),
                    It.IsAny<CancellationToken>()))
              .ReturnsAsync((false, 0, new List<string>()));

            await engine.BulkExportAsync(scopes);

            ad.Setup(t => t.RemoveFromSpecificGroupsAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(),
                    It.IsAny<CancellationToken>()))
              .ReturnsAsync((true, 1, new List<string> { "Najran-A3-lic" }));

            Assert.Equal(1, await engine.BulkExportAsync(scopes));
            Assert.NotNull(db.MetaverseEntries.Single().LastExportDate);
        }

        // ── RemoveGroups with no ActionValue → remove from ALL ──

        [Fact]
        public async Task AFailedRemoveFromAllGroups_BlocksTheExportStamp()
        {
            var (engine, scopes, db, ad) = Setup("RemoveGroups", null, "Deprovisioned", 7);
            ad.Setup(t => t.RemoveFromAllGroupsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((false, 0, new List<string>()));

            Assert.Equal(0, await engine.BulkExportAsync(scopes));
            Assert.Null(db.MetaverseEntries.Single().LastExportDate);
        }

        // ── Deprovision, whose group removal used to be discarded outright ──

        [Fact]
        public async Task DeprovisionWithAFailedGroupRemoval_IsNotRecordedAsFinished()
        {
            // The worst version of this: the result was not even inspected, so an identity was
            // recorded Deprovisioned while holding every group it had.
            var (engine, scopes, db, ad) = Setup("Deprovision", $"OU=GRADUATES,{{BaseDN}}", "Deprovisioned", 7);
            ad.Setup(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);
            ad.Setup(t => t.RemoveFromAllGroupsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((false, 0, new List<string>()));

            var exported = await engine.BulkExportAsync(scopes);

            Assert.Equal(0, exported);
            Assert.Null(db.MetaverseEntries.Single().LastExportDate);

            var history = db.MetaverseHistory.Single(h => h.ChangeType == "Export");
            Assert.Contains("FAILED", history.Details);
        }

        [Fact]
        public async Task DeprovisionThatFullySucceeds_IsStamped()
        {
            var (engine, scopes, db, ad) = Setup("Deprovision", $"OU=GRADUATES,{{BaseDN}}", "Deprovisioned", 7);
            ad.Setup(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);
            ad.Setup(t => t.RemoveFromAllGroupsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((true, 2, new List<string> { "Najran-A3-lic", "Students" }));

            Assert.Equal(1, await engine.BulkExportAsync(scopes));
            Assert.NotNull(db.MetaverseEntries.Single().LastExportDate);
        }

        // ── AddGroups: a rule naming a group that does not exist in AD ──

        [Fact]
        public async Task AGroupTheRuleNamesButAdDoesNotHave_BlocksTheExportStamp()
        {
            // AddToGroups now reports failure when a named group could not be applied. Without it,
            // an active identity silently never received its licence.
            var (engine, scopes, db, ad) = Setup("AddGroups", LicenceGroups, "Active", 1);
            ad.Setup(t => t.AddToGroupsAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(),
                    It.IsAny<CancellationToken>()))
              .ReturnsAsync((false, 1, new List<string> { "Najran-A3-lic" }));

            Assert.Equal(0, await engine.BulkExportAsync(scopes));
            Assert.Null(db.MetaverseEntries.Single().LastExportDate);
        }

        // ── The connector's own decision ─────────────────────────────────────
        // Mutation testing exposed a real gap: inverting the Success decision inside
        // AddToGroupsAsync killed nothing, because every test above mocks ITargetConnector and never
        // executes the connector. The arithmetic was extracted so it can be tested directly; the
        // LDAP loop around it still cannot be, and remains covered by lab runs only.

        [Theory]
        [InlineData(2, 2, 0, true)]   // both applied
        [InlineData(2, 0, 2, true)]   // already in both — the normal re-apply on every run
        [InlineData(2, 1, 1, true)]   // one added, one already there
        [InlineData(2, 1, 0, false)]  // one group could not be applied at all
        [InlineData(2, 0, 0, false)]  // neither could
        [InlineData(1, 0, 0, false)]  // the single-group licence rule, silently unapplied
        public void TheAddGroupsSuccessDecision(int namedByRule, int added, int already, bool expected)
        {
            Assert.Equal(expected, ActiveDirectoryConnector.AddGroupsSucceeded(namedByRule, added, already));
        }

        [Fact]
        public void AlreadyAMemberIsNeverAFailure_EvenForALargeRule()
        {
            // Guards the exclusion that keeps this from failing every identity on every run: the
            // group is applied at creation, and the lifecycle export re-applies the same rule.
            Assert.True(ActiveDirectoryConnector.AddGroupsSucceeded(namedByRule: 40, added: 0, alreadyMember: 40));
        }

        [Fact]
        public async Task AnIdentityAlreadyInEveryGroup_IsNotAFailure()
        {
            // AddedCount == 0 with Success == true is the normal case: creation added the group and
            // the lifecycle export re-applies the same rule.
            var (engine, scopes, db, ad) = Setup("AddGroups", LicenceGroups, "Active", 1);
            ad.Setup(t => t.AddToGroupsAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(),
                    It.IsAny<CancellationToken>()))
              .ReturnsAsync((true, 0, new List<string>()));

            Assert.Equal(1, await engine.BulkExportAsync(scopes));
            Assert.NotNull(db.MetaverseEntries.Single().LastExportDate);
        }
    }
}
