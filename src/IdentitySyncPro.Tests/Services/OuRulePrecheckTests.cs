using IdentitySyncPro.Core.Models.Metaverse;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using IdentitySyncPro.Tests.Helpers;
using System.Text.Json;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// ValidateOURule can find a coverage gap, but only if it is handed the values the source
    /// actually holds. These pin the wiring that supplies them: what gets collected, what
    /// deliberately does not, and which faults block a save versus merely warn.
    /// </summary>
    public class OuRulePrecheckTests
    {
        private const string BaseDn = "DC=students,DC=lab,DC=local";

        private static TenantOURule MappedRule(params string[] mappedCityCodes)
        {
            var city = string.Join(",", mappedCityCodes.Select(c => $"\"{c}\":\"CITY{c}\""));
            return new TenantOURule
            {
                OUTemplate = "OU={GENDER_CODE},OU={CITY_NO},{BaseDN}",
                ValueMappings = $"{{\"CITY_NO\":{{{city}}},\"GENDER_CODE\":{{\"1\":\"MALE\",\"2\":\"FEMALE\"}}}}"
            };
        }

        private static Dictionary<string, object?> Row(string json) =>
            JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;

        // ── CollectObservedValues ────────────────────────────────────────────

        [Fact]
        public void ANumericJsonValue_ReadsAsTheSameStringTheMapIsKeyedBy()
        {
            // The staged row holds CITY_NO as a JSON number, the map is keyed by the string "20".
            // If these two stringify differently the whole check is worthless — it would either
            // report every value as unmapped or none of them.
            var rows = new[] { Row("""{"CITY_NO":20,"GENDER_CODE":1}""") };

            var observed = MappingEngine.CollectObservedValues(rows, new[] { MappedRule("14") });

            Assert.Equal(new[] { "20" }, observed["CITY_NO"]);
        }

        [Fact]
        public void OnlyPlaceholdersThatHaveAMap_AreCollected()
        {
            // A placeholder with no map is passed through by design; collecting its values would
            // only feed the check something it is documented to ignore.
            var rule = new TenantOURule
            {
                OUTemplate = "OU={CITY_DESC},OU={CITY_NO},{BaseDN}",
                ValueMappings = "{\"CITY_NO\":{\"14\":\"NAJRAN\"}}"
            };
            var rows = new[] { Row("""{"CITY_NO":"14","CITY_DESC":"نجران"}""") };

            var observed = MappingEngine.CollectObservedValues(rows, new[] { rule });

            Assert.True(observed.ContainsKey("CITY_NO"));
            Assert.False(observed.ContainsKey("CITY_DESC"));
        }

        [Fact]
        public void AHighCardinalityColumn_IsDroppedRatherThanTruncated()
        {
            // Truncating would produce a value set that is real but incomplete, and the check would
            // then report gaps that are not gaps. Dropping the column says nothing instead.
            var rule = new TenantOURule
            {
                OUTemplate = "OU={STUDENT_ID},{BaseDN}",
                ValueMappings = "{\"STUDENT_ID\":{\"1\":\"X\"}}"
            };
            var rows = Enumerable.Range(1, 50).Select(i => Row($$"""{"STUDENT_ID":"{{i}}"}""")).ToList();

            var observed = MappingEngine.CollectObservedValues(rows, new[] { rule }, maxDistinctPerColumn: 10);

            Assert.False(observed.ContainsKey("STUDENT_ID"));
        }

        [Fact]
        public void AColumnJustUnderTheCap_IsKeptWhole()
        {
            var rule = new TenantOURule
            {
                OUTemplate = "OU={CITY_NO},{BaseDN}",
                ValueMappings = "{\"CITY_NO\":{\"1\":\"X\"}}"
            };
            var rows = Enumerable.Range(1, 10).Select(i => Row($$"""{"CITY_NO":"{{i}}"}""")).ToList();

            var observed = MappingEngine.CollectObservedValues(rows, new[] { rule }, maxDistinctPerColumn: 10);

            Assert.Equal(10, observed["CITY_NO"].Count());
        }

        [Fact]
        public void MalformedValueMappings_DoesNotThrowHere()
        {
            // ValidateOURule reports the bad JSON; this must not fall over on the way past it.
            var rule = new TenantOURule { OUTemplate = "OU={CITY_NO},{BaseDN}", ValueMappings = "{not json" };

            Assert.Empty(MappingEngine.CollectObservedValues(new[] { Row("""{"CITY_NO":"14"}""") }, new[] { rule }));
        }

        // ── ValidateAsync against staged data ────────────────────────────────

        private static (AppDbContext db, int tenantId) Staged(params string[] attributeJson)
        {
            var db = TestDbContext.Create();
            var tenant = new TenantSettings
            {
                TenantName = "الطلاب", IsActive = true,
                ADUsername = "a", ADPassword = "p", ADBaseDN = BaseDn
            };
            db.TenantSettings.Add(tenant);
            db.SaveChanges();

            var n = 0;
            foreach (var json in attributeJson)
            {
                db.MetaverseEntries.Add(new MetaverseEntry
                {
                    TenantId = tenant.Id,
                    ExternalId = $"44000000{n++}",
                    LifecycleState = "Active",
                    AttributesJson = json
                });
            }
            db.SaveChanges();

            return (db, tenant.Id);
        }

        [Fact]
        public async Task TheLabFailure_IsReportedBeforeTheRun()
        {
            // The map knew 14 and not 20, and 38 accounts failed to create before anyone knew why.
            var (db, tenantId) = Staged(
                """{"CITY_NO":14,"GENDER_CODE":1}""",
                """{"CITY_NO":20,"GENDER_CODE":1}""");

            var (errors, warnings) = await OuRulePrecheck.ValidateAsync(db, tenantId, new[] { MappedRule("14") });

            Assert.Empty(errors);
            Assert.Contains(warnings, w => w.Contains("CITY_NO") && w.Contains("20"));
        }

        [Fact]
        public async Task ACoverageGapIsAWarning_NotAnError()
        {
            // It is judged against data that can be stale or partial, and the mapping is often
            // added in the same sitting as the rule. It must not block the save.
            var (db, tenantId) = Staged("""{"CITY_NO":20,"GENDER_CODE":1}""");

            var (errors, warnings) = await OuRulePrecheck.ValidateAsync(db, tenantId, new[] { MappedRule("14") });

            Assert.Empty(errors);
            Assert.NotEmpty(warnings);
        }

        [Fact]
        public async Task MalformedJson_IsStillAnError()
        {
            // Wrong regardless of what the data holds — this one does block.
            var (db, tenantId) = Staged("""{"CITY_NO":14}""");
            var rule = new TenantOURule { OUTemplate = "OU={CITY_NO},{BaseDN}", ValueMappings = "{not json" };

            var (errors, _) = await OuRulePrecheck.ValidateAsync(db, tenantId, new[] { rule });

            Assert.Contains(errors, e => e.Contains("JSON"));
        }

        [Fact]
        public async Task AFullyCoveredMap_ProducesNothing()
        {
            var (db, tenantId) = Staged(
                """{"CITY_NO":14,"GENDER_CODE":1}""",
                """{"CITY_NO":20,"GENDER_CODE":2}""");

            var (errors, warnings) = await OuRulePrecheck.ValidateAsync(db, tenantId, new[] { MappedRule("14", "20") });

            Assert.Empty(errors);
            Assert.Empty(warnings);
        }

        [Fact]
        public async Task ATenantWithNothingStaged_IsNotAccusedOfAnything()
        {
            // No staged rows means no evidence, not evidence of absence. A fresh tenant configuring
            // its first rule must not be told every mapped value is missing.
            var (db, tenantId) = Staged();

            var (errors, warnings) = await OuRulePrecheck.ValidateAsync(db, tenantId, new[] { MappedRule("14") });

            Assert.Empty(errors);
            Assert.Empty(warnings);
        }

        [Fact]
        public async Task AnotherTenantsStagedData_IsNotUsed()
        {
            // The check must be scoped to the tenant whose rule is being validated, or one tenant's
            // city codes become another's phantom gaps.
            var (db, tenantId) = Staged("""{"CITY_NO":14,"GENDER_CODE":1}""");

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
                AttributesJson = """{"CITY_NO":99,"GENDER_CODE":1}"""
            });
            db.SaveChanges();

            var (_, warnings) = await OuRulePrecheck.ValidateAsync(db, tenantId, new[] { MappedRule("14") });

            Assert.Empty(warnings);
        }
    }
}
