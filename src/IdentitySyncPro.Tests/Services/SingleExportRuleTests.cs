using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Identity;
using IdentitySyncPro.Core.Models.Metaverse;
using IdentitySyncPro.Core.Models.Rules;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Services;
using IdentitySyncPro.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// The single-identity export used to treat "verify the account exists" as an alternative to
    /// running its rules, so an OnExport rule never fired for an Active identity — the same
    /// either/or already fixed in the bulk path.
    ///
    /// The correction has a trap of its own. The two pipelines divide the work differently:
    /// BulkApplyRules only records an intended MoveOU and leaves BulkExport to carry it out,
    /// while ApplyLifecycleRulesAsync executes every OnImport rule inline. Loading OnImport rules
    /// into the single export as well — the obvious way to "make the paths match" — would repeat
    /// every move and every group change for the same identity.
    /// </summary>
    public class SingleExportRuleTests
    {
        private const string BaseDn = "DC=test";

        private static (LifecycleEngine engine, Mock<ITargetConnector> ad, MetaverseEntry entry)
            Setup(string lifecycleState, int statusCode, params LifecycleRule[] rules)
        {
            var db = TestDbContext.Create();
            var tenant = new TenantSettings
            {
                Id = 1, TenantName = "T", IsActive = true,
                ADUsername = "a", ADPassword = "p", ADBaseDN = BaseDn,
                SourceStatusColumn = "SRC_STATUS"
            };
            db.TenantSettings.Add(tenant);
            db.SaveChanges();

            foreach (var r in rules) { r.TenantId = 1; db.LifecycleRules.Add(r); }
            db.SaveChanges();

            var entry = new MetaverseEntry
            {
                TenantId = 1, ExternalId = "1001", LifecycleState = lifecycleState,
                SourceStatusCode = statusCode, ADAccountEnabled = true,
                AttributesJson = $$"""{"SRC_STATUS":{{statusCode}}}"""
            };
            db.MetaverseEntries.Add(entry);
            db.SaveChanges();

            var source = new Mock<ISourceConnector>();
            var record = new SourceRecord { Key = 1001, StatusCode = statusCode };
            record.Values["SRC_STATUS"] = statusCode;
            source.Setup(s => s.ReadBatchAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { record });

            var ad = new Mock<ITargetConnector>();
            ad.Setup(t => t.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            ad.Setup(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);

            var engine = new LifecycleEngine(db, source.Object, ad.Object, Mock.Of<ILogger<LifecycleEngine>>());
            return (engine, ad, entry);
        }

        private static LifecycleRule Rule(string trigger, string action, string? actionValue) => new()
        {
            Name = $"{trigger}-{action}", Enabled = true, Priority = 50, TriggerType = trigger,
            ConditionField = "STATUS_CODE", ConditionOperator = "==", ConditionValue = "1",
            ActionType = action, ActionValue = actionValue
        };

        [Fact]
        public async Task OnExportRule_RunsForAnActiveIdentity()
        {
            var (engine, ad, entry) = Setup("Active", 1, Rule("OnExport", "MoveOU", "OU=Verified,{BaseDN}"));

            await engine.ExportFromMetaverseAsync(entry);

            ad.Verify(t => t.MoveToOUAsync("1001", "OU=Verified,DC=test", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ActiveIdentityIsStillVerified()
        {
            var (engine, ad, entry) = Setup("Active", 1, Rule("OnExport", "MoveOU", "OU=Verified,{BaseDN}"));

            var result = await engine.ExportFromMetaverseAsync(entry);

            ad.Verify(t => t.ExistsAsync("1001", It.IsAny<CancellationToken>()), Times.Once);
            Assert.Contains("Verified", result.ActionsTaken ?? "");
        }

        [Fact]
        public async Task OnExportRule_StillRunsForANonActiveIdentity()
        {
            var (engine, ad, entry) = Setup("Suspended", 1, Rule("OnExport", "MoveOU", "OU=Verified,{BaseDN}"));

            await engine.ExportFromMetaverseAsync(entry);

            ad.Verify(t => t.MoveToOUAsync("1001", "OU=Verified,DC=test", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task OnImportRule_IsNotRunAgainByTheExportStage()
        {
            // ApplyLifecycleRulesAsync already executed it. Export must not repeat it.
            var (engine, ad, entry) = Setup("Suspended", 1, Rule("OnImport", "MoveOU", "OU=Archive,{BaseDN}"));

            await engine.ExportFromMetaverseAsync(entry);

            ad.Verify(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task FullPipeline_MovesTheAccountExactlyOnce()
        {
            // The guarantee that matters: one OnImport move rule, one LDAP move for the whole
            // Import -> Rules -> Export pipeline.
            var (engine, ad, _) = Setup("Active", 1, Rule("OnImport", "MoveOU", "OU=Archive,{BaseDN}"));

            await engine.ProcessIdentityAsync(1001);

            ad.Verify(t => t.MoveToOUAsync("1001", "OU=Archive,DC=test", It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
