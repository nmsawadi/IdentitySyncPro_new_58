using IdentitySyncPro.Core.Interfaces;
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
    /// Pins the tenant's graduate handling: STATUS_CODE 7 must land the account in the Graduates
    /// OU, not the general "left the university" OU.
    ///
    /// Two rules cooperate at different priorities — one sets the state (P40), a later one moves
    /// the OU (P65) — so this only works because the engine applies EVERY matching rule in
    /// priority order rather than stopping at the first. That is easy to "optimise" away by
    /// accident, and the failure would be silent: the state would still be right while the
    /// account sat in the wrong OU.
    /// </summary>
    public class GraduateOuPlacementTests
    {
        private const string BaseDn = "DC=std,DC=nu,DC=edu,DC=sa";

        private static (LifecycleEngine engine, Mock<ITargetConnector> ad, MetaverseEntry entry)
            Setup(int statusCode)
        {
            var db = TestDbContext.Create();
            var tenant = new TenantSettings
            {
                TenantName = "الطلاب", IsActive = true,
                ADUsername = "a", ADPassword = "p", ADBaseDN = BaseDn
            };
            db.TenantSettings.Add(tenant);
            db.SaveChanges();

            // The tenant's real rule set for the codes involved.
            db.LifecycleRules.AddRange(
                new LifecycleRule
                {
                    TenantId = tenant.Id, Name = "إيقاف هوية منتهية", Enabled = true, Priority = 40,
                    TriggerType = "OnImport", ConditionField = "STATUS_CODE", ConditionOperator = "==",
                    ConditionValue = "7", ActionType = "SetState", ActionValue = "Deprovisioned"
                },
                new LifecycleRule
                {
                    TenantId = tenant.Id, Name = "نقل غير النشطين", Enabled = true, Priority = 60,
                    TriggerType = "OnImport", ConditionField = "STATUS_CODE", ConditionOperator = "not_in",
                    ConditionValue = "1,7", ActionType = "MoveOU", ActionValue = "OU=LeftTheUniversity,{BaseDN}"
                },
                new LifecycleRule
                {
                    TenantId = tenant.Id, Name = "نقل الخريجين", Enabled = true, Priority = 65,
                    TriggerType = "OnImport", ConditionField = "STATUS_CODE", ConditionOperator = "==",
                    ConditionValue = "7", ActionType = "MoveOU", ActionValue = "OU=Graduates,{BaseDN}"
                });
            db.SaveChanges();

            var entry = new MetaverseEntry
            {
                TenantId = tenant.Id,
                ExternalId = "441234567",
                LifecycleState = "Active",
                SourceStatusCode = statusCode,
                ADAccountEnabled = true,
                AttributesJson = $"{{\"STATUS_CODE\":\"{statusCode}\"}}"
            };
            db.MetaverseEntries.Add(entry);
            db.SaveChanges();

            var ad = new Mock<ITargetConnector>();
            ad.Setup(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);

            var engine = new LifecycleEngine(db, Mock.Of<ISourceConnector>(), ad.Object,
                Mock.Of<ILogger<LifecycleEngine>>());

            return (engine, ad, entry);
        }

        [Fact]
        public async Task StatusCode7_MovesTheAccountToTheGraduatesOu()
        {
            var (engine, ad, entry) = Setup(statusCode: 7);

            await engine.ApplyLifecycleRulesAsync(entry);

            ad.Verify(t => t.MoveToOUAsync("441234567", $"OU=Graduates,{BaseDn}", It.IsAny<CancellationToken>()),
                Times.Once, "STATUS_CODE 7 must move the account to the Graduates OU");
        }

        [Fact]
        public async Task StatusCode7_DoesNotLandInTheLeftUniversityOu()
        {
            // The not_in (1,7) rule excludes graduates on purpose; if that exclusion is ever
            // dropped, graduates would be filed with dropouts.
            var (engine, ad, entry) = Setup(statusCode: 7);

            await engine.ApplyLifecycleRulesAsync(entry);

            ad.Verify(t => t.MoveToOUAsync(It.IsAny<string>(), $"OU=LeftTheUniversity,{BaseDn}", It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task StatusCode7_AlsoSetsTheDeprovisionedState()
        {
            // Both rules must fire — the state rule at P40 and the move at P65.
            var (engine, _, entry) = Setup(statusCode: 7);

            await engine.ApplyLifecycleRulesAsync(entry);

            Assert.Equal("Deprovisioned", entry.LifecycleState);
        }

        [Fact]
        public async Task StatusCode7_NeverDisablesTheAccount()
        {
            // Safe Sync: a graduate is moved and de-licensed, never disabled.
            var (engine, ad, entry) = Setup(statusCode: 7);

            await engine.ApplyLifecycleRulesAsync(entry);

            Assert.True(entry.ADAccountEnabled);
            ad.Verify(t => t.DisableAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task OtherInactiveCodes_StillGoToTheLeftUniversityOu()
        {
            // Guards the opposite direction: the graduate rule must not swallow every inactive code.
            var (engine, ad, entry) = Setup(statusCode: 4);

            await engine.ApplyLifecycleRulesAsync(entry);

            ad.Verify(t => t.MoveToOUAsync("441234567", $"OU=LeftTheUniversity,{BaseDn}", It.IsAny<CancellationToken>()),
                Times.Once);
            ad.Verify(t => t.MoveToOUAsync(It.IsAny<string>(), $"OU=Graduates,{BaseDn}", It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task ActiveIdentity_IsNotMovedAtAll()
        {
            var (engine, ad, entry) = Setup(statusCode: 1);

            await engine.ApplyLifecycleRulesAsync(entry);

            ad.Verify(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
    }
}
