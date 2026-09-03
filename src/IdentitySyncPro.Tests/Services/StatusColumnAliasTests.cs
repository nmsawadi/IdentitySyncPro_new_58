using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Infrastructure.Connectors;
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
    /// Lifecycle rules are written against the canonical STATUS_CODE, but each tenant's source
    /// view names its own columns. One production tenant's column is literally STATUSE_CODE —
    /// with the extra E — so every rule looked up a key that did not exist, read the field as
    /// null, and never matched.
    ///
    /// Nothing reported an error. All ten rules sat dead across 111,465 identities, and graduates
    /// (STATUSE_CODE 7) stayed Active in their original OU. These tests use that tenant's real
    /// attribute payload.
    /// </summary>
    public class StatusColumnAliasTests
    {
        private const string BaseDn = "DC=std,DC=nu,DC=edu,DC=sa";

        // Verbatim from MetaverseEntries.AttributesJson for a real graduate, trimmed to the
        // columns the rules touch. Note the column name: STATUSE_CODE.
        private const string RealGraduateAttributes =
            """{"STUDENT_ID":431840119,"NATIONALITY":"سعودي","GENDER_CODE":2,"STATUSE_CODE":7,"STATUS_DESC":"خريج","CITY_NO":14,"CITY_DESC":"نجران - طلاب وطالبات"}""";

        private static (LifecycleEngine engine, Mock<ITargetConnector> ad, MetaverseEntry entry)
            Setup(string attributesJson, int sourceStatusCode)
        {
            var db = TestDbContext.Create();
            var tenant = new TenantSettings
            {
                TenantName = "الطلاب", IsActive = true,
                ADUsername = "a", ADPassword = "p", ADBaseDN = BaseDn,
                SourceKeyColumn = "STUDENT_ID",
                SourceStatusColumn = "STATUSE_CODE"
            };
            db.TenantSettings.Add(tenant);
            db.SaveChanges();

            // The tenant's real rules — written against STATUS_CODE, as the seed data and UI do.
            db.LifecycleRules.AddRange(
                new LifecycleRule
                {
                    TenantId = tenant.Id, Name = "إيقاف هوية منتهية", Enabled = true, Priority = 40,
                    TriggerType = "OnImport", ConditionField = "STATUS_CODE", ConditionOperator = "==",
                    ConditionValue = "7", ActionType = "SetState", ActionValue = "Deprovisioned"
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
                ExternalId = "431840119",
                LifecycleState = "Active",
                SourceStatusCode = sourceStatusCode,
                ADAccountEnabled = true,
                AttributesJson = attributesJson
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
        public async Task RuleNamingSTATUS_CODE_MatchesATenantWhoseColumnIsSTATUSE_CODE()
        {
            var (engine, _, entry) = Setup(RealGraduateAttributes, sourceStatusCode: 7);

            await engine.ApplyLifecycleRulesAsync(entry);

            Assert.Equal("Deprovisioned", entry.LifecycleState);
        }

        [Fact]
        public async Task TheRealGraduate_IsMovedToTheGraduatesOu()
        {
            var (engine, ad, entry) = Setup(RealGraduateAttributes, sourceStatusCode: 7);

            await engine.ApplyLifecycleRulesAsync(entry);

            ad.Verify(t => t.MoveToOUAsync("431840119", $"OU=Graduates,{BaseDn}", It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task ATenantWhoseColumnReallyIsSTATUS_CODE_StillUsesItsOwnValue()
        {
            // The alias must not shadow a real column of the same name. Here the row says 7 while
            // the extracted SourceStatusCode disagrees — the row must win.
            var (engine, _, entry) = Setup("""{"STATUS_CODE":7}""", sourceStatusCode: 1);

            await engine.ApplyLifecycleRulesAsync(entry);

            Assert.Equal("Deprovisioned", entry.LifecycleState);
        }

        [Fact]
        public async Task ActiveStudent_IsNotAffectedByTheAlias()
        {
            // Guards against the alias making everything match: code 1 must not trigger the
            // graduate rules.
            var (engine, ad, entry) = Setup("""{"STATUSE_CODE":1,"STATUS_DESC":"منتظم"}""", sourceStatusCode: 1);

            await engine.ApplyLifecycleRulesAsync(entry);

            Assert.Equal("Active", entry.LifecycleState);
            ad.Verify(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        // ── What the alias depends on ────────────────────────────────────────
        // STATUS_CODE is derived from entry.SourceStatusCode, which the connectors extract using the
        // tenant's configured status column. "STATUSE_CODE" used to be hardcoded as the default in
        // SourceConnectorFactory — one institution's column name, extra E included. Any other
        // organisation leaving the setting blank got that name, matched nothing, and had StatusCode
        // silently become 0, killing every rule on STATUS_CODE exactly as the original incident did.

        [Fact]
        public void AConfiguredStatusColumn_IsPassedThroughUnchanged()
        {
            var tenant = new TenantSettings
            {
                SourceTableOrView = "V_IDENTITY",
                SourceKeyColumn = "STUDENT_ID", SourceStatusColumn = "STATUSE_CODE"
            };

            Assert.Equal("STATUSE_CODE", SourceConnectorFactory.BuildSqlServerSettings(tenant).StatusColumn);
        }

        [Fact]
        public void ABlankStatusColumn_StaysBlankRatherThanBecomingOneInstitutionsColumnName()
        {
            // The regression that matters for any tenant but the first: guessing here produced a
            // wrong column, and a wrong column produces 0, and 0 produces dead rules — silently.
            var tenant = new TenantSettings
            {
                SourceTableOrView = "V_IDENTITY",
                SourceKeyColumn = "EMP_ID", SourceStatusColumn = null
            };

            var settings = SourceConnectorFactory.BuildSqlServerSettings(tenant);

            Assert.Equal("", settings.StatusColumn);
            Assert.DoesNotContain("STATUSE", settings.StatusColumn);
        }

        [Fact]
        public void AWhitespaceOnlyStatusColumn_IsTreatedAsUnset()
        {
            var tenant = new TenantSettings
            {
                SourceTableOrView = "V_IDENTITY",
                SourceKeyColumn = "EMP_ID", SourceStatusColumn = "   "
            };

            Assert.Equal("", SourceConnectorFactory.BuildSqlServerSettings(tenant).StatusColumn);
        }

        [Fact]
        public void ASurroundingSpaceInTheConfiguredName_IsTrimmed()
        {
            // Typed into a settings field; " STATUSE_CODE " must find the column, not miss it.
            var tenant = new TenantSettings
            {
                SourceTableOrView = "V_IDENTITY",
                SourceKeyColumn = "STUDENT_ID", SourceStatusColumn = " STATUSE_CODE "
            };

            Assert.Equal("STATUSE_CODE", SourceConnectorFactory.BuildSqlServerSettings(tenant).StatusColumn);
        }

        [Fact]
        public async Task WithStatusCodeZero_ARuleOnTheAlias_DoesNotMatch()
        {
            // The consequence of an unset or wrong status column, stated as behaviour: the row still
            // holds the real value, but a rule written against the alias reads 0 and dies. This is
            // why conditioning on the real column name is the safer choice.
            var (engine, ad, entry) = Setup("""{"STATUSE_CODE":7,"STATUS_DESC":"خريج"}""", sourceStatusCode: 0);

            await engine.ApplyLifecycleRulesAsync(entry);

            Assert.NotEqual("Deprovisioned", entry.LifecycleState);
            ad.Verify(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task ARuleOnTheRealColumnName_SurvivesAZeroedStatusCode()
        {
            // The same identity, the same broken StatusCode, but the rule names the actual column —
            // and it still works. The alias is a safety net for old rules, not the better path.
            var db = TestDbContext.Create();
            var tenant = new TenantSettings
            {
                TenantName = "الطلاب", IsActive = true,
                ADUsername = "a", ADPassword = "p", ADBaseDN = BaseDn,
                SourceKeyColumn = "STUDENT_ID", SourceStatusColumn = "STATUSE_CODE"
            };
            db.TenantSettings.Add(tenant);
            db.SaveChanges();

            db.LifecycleRules.Add(new LifecycleRule
            {
                TenantId = tenant.Id, Name = "إيقاف هوية منتهية", Enabled = true, Priority = 40,
                TriggerType = "OnImport", ConditionField = "STATUSE_CODE", ConditionOperator = "==",
                ConditionValue = "7", ActionType = "SetState", ActionValue = "Deprovisioned"
            });
            var entry = new MetaverseEntry
            {
                TenantId = tenant.Id, ExternalId = "431840119", LifecycleState = "Active",
                SourceStatusCode = 0,   // broken alias
                ADAccountEnabled = true,
                AttributesJson = RealGraduateAttributes
            };
            db.MetaverseEntries.Add(entry);
            db.SaveChanges();

            var engine = new LifecycleEngine(db, Mock.Of<ISourceConnector>(),
                Mock.Of<ITargetConnector>(), Mock.Of<ILogger<LifecycleEngine>>());

            await engine.ApplyLifecycleRulesAsync(entry);

            Assert.Equal("Deprovisioned", entry.LifecycleState);
        }

        [Fact]
        public async Task ColumnNameCasing_DoesNotMatter()
        {
            // JsonSerializer returns a case-sensitive dictionary; a rule should not have to match
            // the view's capitalisation.
            var (engine, _, entry) = Setup("""{"status_code":7}""", sourceStatusCode: 7);

            await engine.ApplyLifecycleRulesAsync(entry);

            Assert.Equal("Deprovisioned", entry.LifecycleState);
        }
    }
}
