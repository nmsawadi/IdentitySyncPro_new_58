using IdentitySyncPro.Core.Models.Metaverse;
using IdentitySyncPro.Core.Models.Rules;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using IdentitySyncPro.Tests.Helpers;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// The quietest failure in the system: a lifecycle rule whose ConditionField names a column that
    /// does not exist reads null, so the condition never matches, so the rule never fires. Nothing is
    /// logged, because a rule that does not match is indistinguishable from a rule whose turn has not
    /// come. Ten rules sat dead across 111,465 identities that way, and graduates stayed Active.
    ///
    /// It becomes reachable again the moment anyone retypes a ConditionField — which is exactly what
    /// migrating rules off the STATUS_CODE alias onto a real column name involves.
    /// </summary>
    public class DeadRuleFieldPrecheckTests
    {
        // A real staged payload, trimmed. Note the column name: STATUSE_CODE, with the extra E.
        private const string StagedRow =
            """{"STUDENT_ID":431840119,"GENDER_CODE":2,"STATUSE_CODE":7,"STATUS_DESC":"خريج","CITY_NO":14}""";

        private static (AppDbContext db, int tenantId) Tenant(string? stagedJson = StagedRow)
        {
            var db = TestDbContext.Create();
            var tenant = new TenantSettings
            {
                TenantName = "الطلاب", IsActive = true,
                ADUsername = "a", ADPassword = "p", ADBaseDN = "DC=students,DC=lab,DC=local",
                SourceStatusColumn = "STATUSE_CODE"
            };
            db.TenantSettings.Add(tenant);
            db.SaveChanges();

            if (stagedJson != null)
            {
                db.MetaverseEntries.Add(new MetaverseEntry
                {
                    TenantId = tenant.Id, ExternalId = "431840119",
                    LifecycleState = "Active", AttributesJson = stagedJson
                });
                db.SaveChanges();
            }

            return (db, tenant.Id);
        }

        private static void AddRule(AppDbContext db, int tenantId, string? conditionField,
            bool enabled = true, string name = "قاعدة")
        {
            db.LifecycleRules.Add(new LifecycleRule
            {
                TenantId = tenantId, Name = name, Enabled = enabled, Priority = 40,
                TriggerType = "OnImport", ConditionField = conditionField,
                ConditionOperator = "==", ConditionValue = "7",
                ActionType = "SetState", ActionValue = "Deprovisioned"
            });
            db.SaveChanges();
        }

        [Fact]
        public async Task ATypoInTheColumnName_IsReportedWithTheRuleThatCarriesIt()
        {
            // The migration hazard: STATUS_CODE → STATUSE_CODE, mistyped.
            var (db, tenantId) = Tenant();
            AddRule(db, tenantId, "STATUSE_COD", name: "إيقاف هوية منتهية");

            var problems = await OuRulePrecheck.FindRulesNamingUnknownColumnsAsync(db, tenantId);

            var message = Assert.Single(problems);
            Assert.Contains("STATUSE_COD", message);
            Assert.Contains("إيقاف هوية منتهية", message);
        }

        [Fact]
        public async Task TheRealColumnName_IsAccepted()
        {
            var (db, tenantId) = Tenant();
            AddRule(db, tenantId, "STATUSE_CODE");

            Assert.Empty(await OuRulePrecheck.FindRulesNamingUnknownColumnsAsync(db, tenantId));
        }

        [Fact]
        public async Task TheSyntheticStatusCodeAlias_IsAccepted_ThoughItIsNoColumn()
        {
            // STATUS_CODE is injected by BuildRuleAttributes and appears in no view. Reporting it
            // would condemn every rule this system shipped with — including production's.
            var (db, tenantId) = Tenant();
            AddRule(db, tenantId, "STATUS_CODE");

            Assert.Empty(await OuRulePrecheck.FindRulesNamingUnknownColumnsAsync(db, tenantId));
        }

        [Fact]
        public async Task TheSyntheticIdentityIdAlias_IsAlsoAccepted()
        {
            var (db, tenantId) = Tenant();
            AddRule(db, tenantId, "IDENTITY_ID");

            Assert.Empty(await OuRulePrecheck.FindRulesNamingUnknownColumnsAsync(db, tenantId));
        }

        [Fact]
        public async Task EverySyntheticFieldTheEngineInjects_IsAccepted()
        {
            // Pins the two lists together. If BuildRuleAttributes gains a field and the check does
            // not learn about it, a working rule gets reported as dead.
            var (db, tenantId) = Tenant();
            foreach (var field in LifecycleEngine.SyntheticRuleFields)
                AddRule(db, tenantId, field, name: $"قاعدة {field}");

            Assert.Empty(await OuRulePrecheck.FindRulesNamingUnknownColumnsAsync(db, tenantId));
        }

        [Fact]
        public async Task CasingDoesNotMatter()
        {
            var (db, tenantId) = Tenant();
            AddRule(db, tenantId, "statuse_code");

            Assert.Empty(await OuRulePrecheck.FindRulesNamingUnknownColumnsAsync(db, tenantId));
        }

        [Fact]
        public async Task SurroundingWhitespace_IsNotATypo()
        {
            // Typed into a settings field; " STATUSE_CODE " must not be reported.
            var (db, tenantId) = Tenant();
            AddRule(db, tenantId, " STATUSE_CODE ");

            Assert.Empty(await OuRulePrecheck.FindRulesNamingUnknownColumnsAsync(db, tenantId));
        }

        [Fact]
        public async Task AnEmptyConditionField_IsALegitimateRuleShape()
        {
            // No condition means "always matches" — a real rule shape, not a fault.
            var (db, tenantId) = Tenant();
            AddRule(db, tenantId, "");

            Assert.Empty(await OuRulePrecheck.FindRulesNamingUnknownColumnsAsync(db, tenantId));
        }

        [Fact]
        public async Task ADisabledRule_IsNotReported()
        {
            // A disabled rule fires for nobody; complaining about it is noise.
            var (db, tenantId) = Tenant();
            AddRule(db, tenantId, "NONSENSE_COLUMN", enabled: false);

            Assert.Empty(await OuRulePrecheck.FindRulesNamingUnknownColumnsAsync(db, tenantId));
        }

        [Fact]
        public async Task ATenantWithNothingStaged_IsNotAccusedOfAnything()
        {
            // No staged rows is no evidence, not evidence of absence. A tenant configuring its
            // first rules before its first sync must not be told every field is wrong.
            var (db, tenantId) = Tenant(stagedJson: null);
            AddRule(db, tenantId, "ANYTHING_AT_ALL");

            Assert.Empty(await OuRulePrecheck.FindRulesNamingUnknownColumnsAsync(db, tenantId));
        }

        [Fact]
        public async Task AnotherTenantsColumns_DoNotVouchForThisOne()
        {
            var (db, tenantId) = Tenant();
            var other = new TenantSettings
            {
                TenantName = "الموظفون", IsActive = true,
                ADUsername = "a", ADPassword = "p", ADBaseDN = "DC=lab,DC=local"
            };
            db.TenantSettings.Add(other);
            db.SaveChanges();
            db.MetaverseEntries.Add(new MetaverseEntry
            {
                TenantId = other.Id, ExternalId = "e1", LifecycleState = "Active",
                AttributesJson = """{"EMP_STATUS":1}"""
            });
            db.SaveChanges();

            AddRule(db, tenantId, "EMP_STATUS");

            Assert.NotEmpty(await OuRulePrecheck.FindRulesNamingUnknownColumnsAsync(db, tenantId));
        }

        [Fact]
        public async Task EveryDeadRuleIsReported_NotJustTheFirst()
        {
            // The original incident was ten rules at once. Reporting one would send the operator
            // round the loop nine more times.
            var (db, tenantId) = Tenant();
            AddRule(db, tenantId, "WRONG_A", name: "قاعدة أ");
            AddRule(db, tenantId, "WRONG_B", name: "قاعدة ب");
            AddRule(db, tenantId, "STATUSE_CODE", name: "قاعدة سليمة");

            var problems = await OuRulePrecheck.FindRulesNamingUnknownColumnsAsync(db, tenantId);

            Assert.Equal(2, problems.Count);
            Assert.Contains(problems, p => p.Contains("WRONG_A"));
            Assert.Contains(problems, p => p.Contains("WRONG_B"));
        }
    }
}
