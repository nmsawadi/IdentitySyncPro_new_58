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
    /// LastExportDate is the marker that tells the next run an entry is finished: the export set is
    /// <c>LastExportDate == null || StateChangedDate &gt; LastExportDate</c>. Stamping it after a
    /// failed action is therefore not a cosmetic miscount — it is permanent. The action never ran,
    /// the state will never change again, and the entry is never reconsidered.
    ///
    /// Seen in the lab: a MoveOU to an OU that did not exist logged an error, reported
    /// "1 exported, 0 failed", and left ADCurrentOU empty. Creating the missing OU afterwards
    /// changed nothing — the account stayed where it was, and no error was ever raised again.
    /// Two entries in the database were already stranded this way.
    /// </summary>
    public class FailedExportIsNotStampedTests
    {
        private const string BaseDn = "DC=students,DC=lab,DC=local";
        private const string TargetOu = "OU=GRADUATES,DC=students,DC=lab,DC=local";
        private const string Identity = "440000006";

        private static (LifecycleEngine engine, IServiceScopeFactory scopes, AppDbContext db, Mock<ITargetConnector> ad)
            Setup(bool moveSucceeds)
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
                ConditionValue = "7", ActionType = "MoveOU", ActionValue = "OU=GRADUATES,{BaseDN}"
            });

            db.MetaverseEntries.Add(new MetaverseEntry
            {
                TenantId = tenant.Id,
                ExternalId = Identity,
                LifecycleState = "Deprovisioned",
                SourceStatusCode = 7,
                ADAccountEnabled = true,
                AttributesJson = """{"STATUSE_CODE":7,"STATUS_DESC":"خريج"}""",
                StateChangedDate = DateTime.UtcNow,
                LastExportDate = null
            });
            db.SaveChanges();

            var ad = new Mock<ITargetConnector>();
            ad.Setup(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(moveSucceeds);

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
        public async Task AFailedMove_LeavesLastExportDateUnstamped()
        {
            var (engine, scopes, db, _) = Setup(moveSucceeds: false);

            await engine.BulkExportAsync(scopes);

            Assert.Null(db.MetaverseEntries.Single().LastExportDate);
        }

        [Fact]
        public async Task AFailedMove_IsNotCountedAsExported()
        {
            var (engine, scopes, _, _) = Setup(moveSucceeds: false);

            Assert.Equal(0, await engine.BulkExportAsync(scopes));
        }

        [Fact]
        public async Task AFailedMove_IsRetriedOnTheNextRun()
        {
            // The whole point. The first run fails, the operator creates the missing OU, and the
            // second run must pick the same entry up again without anything else changing.
            var (engine, scopes, db, ad) = Setup(moveSucceeds: false);

            await engine.BulkExportAsync(scopes);

            ad.Setup(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);

            var exported = await engine.BulkExportAsync(scopes);

            Assert.Equal(1, exported);
            Assert.Equal(TargetOu, db.MetaverseEntries.Single().ADCurrentOU);
            Assert.NotNull(db.MetaverseEntries.Single().LastExportDate);
        }

        [Fact]
        public async Task AFailedMove_SaysSoInTheAuditTrail()
        {
            // An export that did nothing used to write no history row at all, so the database held
            // no record that anything had been attempted.
            var (engine, scopes, db, _) = Setup(moveSucceeds: false);

            await engine.BulkExportAsync(scopes);

            var history = db.MetaverseHistory.Where(h => h.ChangeType == "Export").ToList();
            Assert.Single(history);
            Assert.Contains("FAILED", history[0].Details);
            Assert.Contains("GRADUATES", history[0].Details);
        }

        [Fact]
        public async Task ASuccessfulMove_IsStillStampedAndCounted()
        {
            // Guards the path that already worked: the fix must not withhold the stamp from a
            // clean run, or every identity re-exports on every sync forever.
            var (engine, scopes, db, _) = Setup(moveSucceeds: true);

            var exported = await engine.BulkExportAsync(scopes);

            Assert.Equal(1, exported);
            Assert.NotNull(db.MetaverseEntries.Single().LastExportDate);
            Assert.Equal(TargetOu, db.MetaverseEntries.Single().ADCurrentOU);
        }

        [Fact]
        public async Task ASuccessfulMove_IsNotExportedTwice()
        {
            // The counterpart to the retry test: a stamped entry must drop out of the export set.
            var (engine, scopes, _, ad) = Setup(moveSucceeds: true);

            await engine.BulkExportAsync(scopes);
            var second = await engine.BulkExportAsync(scopes);

            Assert.Equal(0, second);
            ad.Verify(t => t.MoveToOUAsync(Identity, TargetOu, It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
