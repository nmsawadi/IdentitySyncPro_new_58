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
    /// A full sync and a single/delta sync drive lifecycle rules through different code:
    /// the full sync records the intended actions in BulkApplyRules and performs them in
    /// BulkExport, while a single identity goes through ProcessIdentityAsync, where
    /// ApplyLifecycleRulesAsync performs them inline.
    ///
    /// Everything verified during the bulk migration therefore proves nothing about the path
    /// used by the scheduled delta sync and by the per-identity button, which is what the system
    /// will actually run from now on. These cover that path against the same tenant data.
    /// </summary>
    public class SingleAndDeltaPathTests
    {
        private const string BaseDn = "DC=std,DC=nu,DC=edu,DC=sa";
        private const string GraduatesOu = "OU=Graduates,DC=std,DC=nu,DC=edu,DC=sa";
        private const string LeftOu = "OU=LeftTheUniversity,DC=std,DC=nu,DC=edu,DC=sa";

        private static (LifecycleEngine engine, Mock<ITargetConnector> ad, Infrastructure.Data.AppDbContext db)
            Setup(int sourceStatusCode, string existingState = "Active")
        {
            var db = TestDbContext.Create();
            var tenant = new TenantSettings
            {
                Id = 1, TenantName = "الطلاب", IsActive = true,
                ADUsername = "a", ADPassword = "p", ADBaseDN = BaseDn,
                SourceKeyColumn = "STUDENT_ID", SourceStatusColumn = "STATUSE_CODE"
            };
            db.TenantSettings.Add(tenant);
            db.SaveChanges();

            db.TenantGroupRules.AddRange(
                new TenantGroupRule { TenantId = 1, GroupName = "Lic-Najran", ConditionField = "CITY_NO", ConditionOperator = "==", ConditionValue = "14" },
                new TenantGroupRule { TenantId = 1, GroupName = "Lic-Sharorah", ConditionField = "CITY_NO", ConditionOperator = "==", ConditionValue = "43" });

            // The tenant's live rule set, as imported.
            db.LifecycleRules.AddRange(
                Rule(10, "==", "1", "SetState", "Active"),
                Rule(40, "==", "7", "SetState", "Deprovisioned"),
                Rule(55, "==", "1", "AddGroups", "{GroupRules}"),
                Rule(60, "not_in", "1,7", "MoveOU", "OU=LeftTheUniversity,{BaseDN}"),
                Rule(65, "==", "7", "MoveOU", "OU=Graduates,{BaseDN}"),
                Rule(70, "not_in", "1", "RemoveGroups", "Lic-Najran,Lic-Sharorah"));
            db.SaveChanges();

            db.MetaverseEntries.Add(new MetaverseEntry
            {
                TenantId = 1, ExternalId = "441234567", LifecycleState = existingState,
                SourceStatusCode = sourceStatusCode, ADAccountEnabled = true,
                AttributesJson = $$"""{"STUDENT_ID":441234567,"STATUSE_CODE":{{sourceStatusCode}},"CITY_NO":14}"""
            });
            db.SaveChanges();

            var source = new Mock<ISourceConnector>();
            var record = new SourceRecord { Key = 441234567, StatusCode = sourceStatusCode };
            record.Values["STUDENT_ID"] = 441234567;
            record.Values["STATUSE_CODE"] = sourceStatusCode;
            record.Values["CITY_NO"] = 14;
            source.Setup(s => s.ReadBatchAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { record });

            var ad = new Mock<ITargetConnector>();
            ad.Setup(t => t.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            ad.Setup(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);
            ad.Setup(t => t.AddToGroupsAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((string _, IEnumerable<string> g, CancellationToken _) => (true, g.Count(), g.ToList()));
            ad.Setup(t => t.RemoveFromSpecificGroupsAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((true, 0, new List<string>()));

            var engine = new LifecycleEngine(db, source.Object, ad.Object, Mock.Of<ILogger<LifecycleEngine>>());
            return (engine, ad, db);

            LifecycleRule Rule(int priority, string op, string value, string action, string? actionValue) => new()
            {
                TenantId = 1, Name = $"P{priority}", Enabled = true, Priority = priority,
                TriggerType = "OnImport", ConditionField = "STATUS_CODE",
                ConditionOperator = op, ConditionValue = value,
                ActionType = action, ActionValue = actionValue
            };
        }

        [Fact]
        public async Task SinglePath_NewGraduate_IsDeprovisionedAndMoved()
        {
            // A student who graduates between full syncs — the case the delta sync exists for.
            var (engine, ad, db) = Setup(sourceStatusCode: 7);

            var result = await engine.ProcessIdentityAsync(441234567);

            Assert.True(result.Success, result.Error);
            Assert.Equal("Deprovisioned", db.MetaverseEntries.Single().LifecycleState);
            ad.Verify(t => t.MoveToOUAsync("441234567", GraduatesOu, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task SinglePath_NewlyWithdrawn_IsSuspendedPathAndMoved()
        {
            var (engine, ad, _) = Setup(sourceStatusCode: 10);

            await engine.ProcessIdentityAsync(441234567);

            ad.Verify(t => t.MoveToOUAsync("441234567", LeftOu, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task SinglePath_ActiveStudent_GetsTheLicenceGroupForItsCampus()
        {
            // The AddGroups rule resolves through the tenant's group rules on this path too.
            var (engine, ad, _) = Setup(sourceStatusCode: 1);

            await engine.ProcessIdentityAsync(441234567);

            ad.Verify(t => t.AddToGroupsAsync("441234567",
                It.Is<IEnumerable<string>>(g => g.Single() == "Lic-Najran"), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task SinglePath_ActiveStudent_IsNotMoved()
        {
            var (engine, ad, _) = Setup(sourceStatusCode: 1);

            await engine.ProcessIdentityAsync(441234567);

            ad.Verify(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task SinglePath_AlreadyArchivedGraduate_IsNotReprocessedIntoAnError()
        {
            // Re-running the same identity must be idempotent, not a failure.
            var (engine, ad, db) = Setup(sourceStatusCode: 7, existingState: "Deprovisioned");

            var result = await engine.ProcessIdentityAsync(441234567);

            Assert.True(result.Success, result.Error);
            Assert.Equal("Deprovisioned", db.MetaverseEntries.Single().LifecycleState);
        }

        [Fact]
        public async Task SinglePath_DryRun_ChangesNothingInAd()
        {
            // ProcessIdentityAsync guarded only its export stage, but the rules stage performs
            // the AD actions inline — so a "dry run" from the lifecycle page really moved the
            // account and really changed its groups.
            var (engine, ad, _) = Setup(sourceStatusCode: 7);

            await engine.ProcessIdentityAsync(441234567, dryRun: true);

            ad.Verify(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
            ad.Verify(t => t.RemoveFromSpecificGroupsAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task SinglePath_DryRun_LeavesTheStoredStateAlone()
        {
            // The same run also persisted the state transition and wrote a MetaverseHistory row,
            // so a preview became indistinguishable from a real run after the fact.
            var (engine, _, db) = Setup(sourceStatusCode: 7);

            await engine.ProcessIdentityAsync(441234567, dryRun: true);

            db.ChangeTracker.Clear();
            Assert.Equal("Active", db.MetaverseEntries.Single().LifecycleState);
            Assert.DoesNotContain(db.MetaverseHistory, h => h.ChangeType == "StateChange");
        }

        [Fact]
        public async Task SinglePath_DryRun_StillReportsWhatWouldHappen()
        {
            // A preview that reports nothing is useless — the caller must still see the intent.
            var (engine, _, _) = Setup(sourceStatusCode: 7);

            var result = await engine.ProcessIdentityAsync(441234567, dryRun: true);

            Assert.True(result.Success, result.Error);
            Assert.Contains("Graduates", result.ActionsTaken ?? "");
        }
    }
}
