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
    /// A lifecycle rule carries a single condition, so it cannot express "active AND on this
    /// campus". Naming the licence groups literally therefore required one AddGroups rule per
    /// campus with no status condition — which added every identity, graduates and withdrawn
    /// students included, to an Office licence group that a later rule then removed.
    ///
    /// Across 91,504 accounts that is not just wasted work: if directory sync runs inside that
    /// window a licence is really assigned and withdrawn, driving mailbox provisioning and
    /// deprovisioning in Exchange Online.
    ///
    /// ActionValue = {GroupRules} defers to the tenant's own group rules — the same rules used
    /// when an account is created — so one status-conditional rule covers every campus.
    /// </summary>
    public class AddGroupsFromGroupRulesTests
    {
        private const string Najran = "Lic-O365-STD-Najran-Group";
        private const string Sharorah = "Lic-O365-STD-Sharorah-Group";

        private static (LifecycleEngine engine, Mock<ITargetConnector> ad, MetaverseEntry entry)
            Setup(int statusCode, int cityNo, string addGroupsActionValue)
        {
            var db = TestDbContext.Create();
            var tenant = new TenantSettings
            {
                TenantName = "الطلاب", IsActive = true,
                ADUsername = "a", ADPassword = "p", ADBaseDN = "DC=std,DC=nu,DC=edu,DC=sa",
                SourceStatusColumn = "STATUSE_CODE"
            };
            db.TenantSettings.Add(tenant);
            db.SaveChanges();

            // The tenant's real group rules: one licence group per campus.
            db.TenantGroupRules.AddRange(
                new TenantGroupRule { TenantId = tenant.Id, GroupName = Najran, ConditionField = "CITY_NO", ConditionOperator = "==", ConditionValue = "14" },
                new TenantGroupRule { TenantId = tenant.Id, GroupName = Sharorah, ConditionField = "CITY_NO", ConditionOperator = "==", ConditionValue = "43" });

            db.LifecycleRules.Add(new LifecycleRule
            {
                TenantId = tenant.Id, Name = "إضافة مجموعة الترخيص", Enabled = true, Priority = 55,
                TriggerType = "OnImport", ConditionField = "STATUS_CODE", ConditionOperator = "==",
                ConditionValue = "1", ActionType = "AddGroups", ActionValue = addGroupsActionValue
            });
            db.SaveChanges();

            var entry = new MetaverseEntry
            {
                TenantId = tenant.Id,
                ExternalId = "441234567",
                LifecycleState = "Active",
                SourceStatusCode = statusCode,
                ADAccountEnabled = true,
                AttributesJson = $$"""{"STATUSE_CODE":{{statusCode}},"CITY_NO":{{cityNo}}}"""
            };
            db.MetaverseEntries.Add(entry);
            db.SaveChanges();

            var ad = new Mock<ITargetConnector>();
            ad.Setup(t => t.AddToGroupsAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((string _, IEnumerable<string> g, CancellationToken _) => (true, g.Count(), g.ToList()));

            var engine = new LifecycleEngine(db, Mock.Of<ISourceConnector>(), ad.Object,
                Mock.Of<ILogger<LifecycleEngine>>());

            return (engine, ad, entry);
        }

        [Fact]
        public async Task ActiveStudentInNajran_GetsOnlyTheNajranGroup()
        {
            var (engine, ad, entry) = Setup(statusCode: 1, cityNo: 14, "{GroupRules}");

            await engine.ApplyLifecycleRulesAsync(entry);

            ad.Verify(t => t.AddToGroupsAsync("441234567",
                It.Is<IEnumerable<string>>(g => g.Single() == Najran), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ActiveStudentInSharorah_GetsOnlyTheSharorahGroup()
        {
            var (engine, ad, entry) = Setup(statusCode: 1, cityNo: 43, "{GroupRules}");

            await engine.ApplyLifecycleRulesAsync(entry);

            ad.Verify(t => t.AddToGroupsAsync("441234567",
                It.Is<IEnumerable<string>>(g => g.Single() == Sharorah), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Graduate_IsNeverAddedToALicenceGroup()
        {
            // The whole point: status 7 must not touch licence groups at all, so there is no
            // window in which a licence could be assigned and then withdrawn.
            var (engine, ad, entry) = Setup(statusCode: 7, cityNo: 14, "{GroupRules}");

            await engine.ApplyLifecycleRulesAsync(entry);

            ad.Verify(t => t.AddToGroupsAsync(It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task WithdrawnStudent_IsNeverAddedToALicenceGroup()
        {
            var (engine, ad, entry) = Setup(statusCode: 10, cityNo: 43, "{GroupRules}");

            await engine.ApplyLifecycleRulesAsync(entry);

            ad.Verify(t => t.AddToGroupsAsync(It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ALiterallyNamedGroup_StillWorks()
        {
            // Backward compatibility: existing rules that name their groups keep behaving.
            var (engine, ad, entry) = Setup(statusCode: 1, cityNo: 14, "Some-Other-Group");

            await engine.ApplyLifecycleRulesAsync(entry);

            ad.Verify(t => t.AddToGroupsAsync("441234567",
                It.Is<IEnumerable<string>>(g => g.Single() == "Some-Other-Group"), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task CityMatchingNoGroupRule_AddsNothing()
        {
            // An unmapped campus must not fall back to "add everything".
            var (engine, ad, entry) = Setup(statusCode: 1, cityNo: 99, "{GroupRules}");

            await engine.ApplyLifecycleRulesAsync(entry);

            ad.Verify(t => t.AddToGroupsAsync(It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
