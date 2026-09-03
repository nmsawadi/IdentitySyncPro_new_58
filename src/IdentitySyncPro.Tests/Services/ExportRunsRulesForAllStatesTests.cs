using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Metaverse;
using IdentitySyncPro.Core.Models.Rules;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using IdentitySyncPro.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Bulk export branches on lifecycle STATE while rules condition on source STATUS, and the
    /// two diverge exactly where no rule maps. Verifying that an Active account exists used to be
    /// an alternative to running its rules rather than an extra step, with two consequences:
    ///
    ///  * a rule targeting active identities (adding a group, say) never fired at all; and
    ///  * an identity whose source status has no SetState rule stays Active, so a rule such as
    ///    "status not in (1,7) -> move" skipped it silently.
    ///
    /// The second is how it surfaced: of two identities given the same treatment, the withdrawn
    /// one moved and the deferred one did not, because only the first had a rule that changed its
    /// state away from Active.
    /// </summary>
    public class ExportRunsRulesForAllStatesTests
    {
        private const string BaseDn = "DC=test";
        private const string ArchiveOu = "OU=Archive,DC=test";

        private static (LifecycleEngine engine, IServiceScopeFactory scopes, AppDbContext db, Mock<ITargetConnector> ad)
            Setup(string lifecycleState, int sourceStatusCode, params LifecycleRule[] rules)
        {
            var db = TestDbContext.Create();
            var tenant = new TenantSettings
            {
                TenantName = "T", IsActive = true,
                ADUsername = "a", ADPassword = "p", ADBaseDN = BaseDn,
                SourceStatusColumn = "SRC_STATUS"
            };
            db.TenantSettings.Add(tenant);
            db.SaveChanges();

            foreach (var r in rules) { r.TenantId = tenant.Id; db.LifecycleRules.Add(r); }
            db.TenantGroupRules.Add(new TenantGroupRule
            { TenantId = tenant.Id, GroupName = "All-Staff", IsDefault = true });
            db.SaveChanges();

            db.MetaverseEntries.Add(new MetaverseEntry
            {
                TenantId = tenant.Id,
                ExternalId = "1001",
                LifecycleState = lifecycleState,
                SourceStatusCode = sourceStatusCode,
                ADAccountEnabled = true,
                AttributesJson = $$"""{"SRC_STATUS":{{sourceStatusCode}}}""",
                StateChangedDate = DateTime.UtcNow,
                LastExportDate = null
            });
            db.SaveChanges();

            var ad = new Mock<ITargetConnector>();
            ad.Setup(t => t.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            ad.Setup(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);
            ad.Setup(t => t.AddToGroupsAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((string _, IEnumerable<string> g, CancellationToken _) => (true, g.Count(), g.ToList()));

            var services = new ServiceCollection();
            services.AddSingleton(db);
            services.AddSingleton(ad.Object);
            services.AddLogging();
            var provider = services.BuildServiceProvider();

            var engine = new LifecycleEngine(db, Mock.Of<ISourceConnector>(), ad.Object,
                Mock.Of<ILogger<LifecycleEngine>>());

            return (engine, provider.GetRequiredService<IServiceScopeFactory>(), db, ad);
        }

        private static LifecycleRule MoveRule() => new()
        {
            Name = "Archive non-active", Enabled = true, Priority = 60, TriggerType = "OnImport",
            ConditionField = "STATUS_CODE", ConditionOperator = "not_in", ConditionValue = "1",
            ActionType = "MoveOU", ActionValue = "OU=Archive,{BaseDN}"
        };

        private static LifecycleRule AddGroupsRule() => new()
        {
            Name = "Licence for active", Enabled = true, Priority = 55, TriggerType = "OnImport",
            ConditionField = "STATUS_CODE", ConditionOperator = "==", ConditionValue = "1",
            ActionType = "AddGroups", ActionValue = "{GroupRules}"
        };

        [Fact]
        public async Task IdentityLeftActiveByAMissingStateRule_IsStillMoved()
        {
            // Source status 2 has no SetState rule, so the identity is still Active — but the
            // move rule says "not in (1)", which includes 2.
            var (engine, scopes, _, ad) = Setup("Active", sourceStatusCode: 2, MoveRule());

            await engine.BulkExportAsync(scopes);

            ad.Verify(t => t.MoveToOUAsync("1001", ArchiveOu, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ARuleTargetingActiveIdentities_ActuallyRuns()
        {
            // An AddGroups rule for active identities previously never fired in bulk export.
            var (engine, scopes, _, ad) = Setup("Active", sourceStatusCode: 1, AddGroupsRule());

            await engine.BulkExportAsync(scopes);

            ad.Verify(t => t.AddToGroupsAsync("1001",
                It.Is<IEnumerable<string>>(g => g.Single() == "All-Staff"), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ActiveIdentityIsStillVerifiedInAd()
        {
            // The verification step must survive alongside the rules, not be replaced by them.
            var (engine, scopes, db, ad) = Setup("Active", sourceStatusCode: 1, AddGroupsRule());

            await engine.BulkExportAsync(scopes);

            ad.Verify(t => t.ExistsAsync("1001", It.IsAny<CancellationToken>()), Times.Once);
            Assert.True(db.MetaverseEntries.Single().ADAccountEnabled);
        }

        [Fact]
        public async Task AnActiveIdentityWithNoMatchingRule_IsNotMoved()
        {
            // Guards against the fix turning into "act on everything": status 1 does not match
            // a "not in (1)" move rule, so nothing should happen to it.
            var (engine, scopes, _, ad) = Setup("Active", sourceStatusCode: 1, MoveRule());

            await engine.BulkExportAsync(scopes);

            ad.Verify(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task SuspendedIdentity_BehavesAsBefore()
        {
            var (engine, scopes, _, ad) = Setup("Suspended", sourceStatusCode: 4, MoveRule());

            await engine.BulkExportAsync(scopes);

            ad.Verify(t => t.MoveToOUAsync("1001", ArchiveOu, It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
