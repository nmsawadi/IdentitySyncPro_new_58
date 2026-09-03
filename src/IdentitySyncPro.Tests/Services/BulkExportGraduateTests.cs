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
    /// A full sync applies lifecycle rules in two stages: BulkApplyRules changes state and records
    /// the intended AD actions, and BulkExport performs them. BulkExport used to filter out
    /// Deprovisioned entries on the assumption that a finished identity needs nothing more in AD.
    ///
    /// That is precisely backwards for a tenant whose rules archive graduates: status 7 sets the
    /// state to Deprovisioned, and the same state then removed the entry from the export set, so
    /// the move to the archive OU could never run. The database read "Deprovisioned" while the
    /// account sat in its original OU, with nothing logged as wrong — the symptom was an
    /// ADCurrentOU that stayed null.
    /// </summary>
    public class BulkExportGraduateTests
    {
        private const string BaseDn = "DC=std,DC=nu,DC=edu,DC=sa";
        private const string GraduatesOu = "OU=Graduates,DC=std,DC=nu,DC=edu,DC=sa";

        private static (LifecycleEngine engine, IServiceScopeFactory scopes, AppDbContext db, Mock<ITargetConnector> ad)
            Setup(string lifecycleState, DateTime? lastExport = null)
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
                TenantId = tenant.Id, Name = "نقل الخريجين", Enabled = true, Priority = 65,
                TriggerType = "OnImport", ConditionField = "STATUS_CODE", ConditionOperator = "==",
                ConditionValue = "7", ActionType = "MoveOU", ActionValue = "OU=Graduates,{BaseDN}"
            });

            db.MetaverseEntries.Add(new MetaverseEntry
            {
                TenantId = tenant.Id,
                ExternalId = "431840119",
                LifecycleState = lifecycleState,
                SourceStatusCode = 7,
                ADAccountEnabled = true,
                AttributesJson = """{"STATUSE_CODE":7,"STATUS_DESC":"خريج"}""",
                StateChangedDate = DateTime.UtcNow,
                LastExportDate = lastExport
            });
            db.SaveChanges();

            var ad = new Mock<ITargetConnector>();
            ad.Setup(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);

            var services = new ServiceCollection();
            services.AddSingleton(db);
            services.AddSingleton(ad.Object);
            services.AddLogging();
            var provider = services.BuildServiceProvider();

            var engine = new LifecycleEngine(db, Mock.Of<ISourceConnector>(), ad.Object,
                Mock.Of<ILogger<LifecycleEngine>>());

            return (engine, provider.GetRequiredService<IServiceScopeFactory>(), db, ad);
        }

        [Fact]
        public async Task DeprovisionedGraduate_IsExportedAndMoved()
        {
            var (engine, scopes, _, ad) = Setup("Deprovisioned");

            var exported = await engine.BulkExportAsync(scopes);

            Assert.Equal(1, exported);
            ad.Verify(t => t.MoveToOUAsync("431840119", GraduatesOu, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task DeprovisionedGraduate_GetsItsCurrentOuRecorded()
        {
            // ADCurrentOU staying null is how this surfaced in production.
            var (engine, scopes, db, _) = Setup("Deprovisioned");

            await engine.BulkExportAsync(scopes);

            Assert.Equal(GraduatesOu, db.MetaverseEntries.Single().ADCurrentOU);
        }

        [Fact]
        public async Task PendingIdentity_IsStillExcluded()
        {
            // Pending has no AD account yet — the main sync creates it, export must not touch it.
            var (engine, scopes, _, ad) = Setup("Pending");

            var exported = await engine.BulkExportAsync(scopes);

            Assert.Equal(0, exported);
            ad.Verify(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task AlreadyExportedSinceItsLastStateChange_IsNotReExported()
        {
            // The delta condition must still hold, or every run re-exports every archived identity.
            var (engine, scopes, db, ad) = Setup("Deprovisioned");
            var entry = db.MetaverseEntries.Single();
            entry.LastExportDate = entry.StateChangedDate.AddMinutes(1);
            db.SaveChanges();

            var exported = await engine.BulkExportAsync(scopes);

            Assert.Equal(0, exported);
            ad.Verify(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task SuspendedIdentity_StillExportsAsBefore()
        {
            // Guards the states that already worked.
            var (engine, scopes, _, ad) = Setup("Suspended");

            var exported = await engine.BulkExportAsync(scopes);

            Assert.Equal(1, exported);
            ad.Verify(t => t.MoveToOUAsync("431840119", GraduatesOu, It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
