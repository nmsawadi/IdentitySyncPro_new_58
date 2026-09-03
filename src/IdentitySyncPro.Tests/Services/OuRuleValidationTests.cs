using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Services;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// An OU rule fails in two ways that produce no error at all: malformed ValueMappings JSON
    /// (swallowed by the parse guard) and placeholders naming no source column (replaced with the
    /// literal "DEFAULT"). Both were live in production and only surfaced when account creation
    /// started failing against an OU that does not exist.
    /// </summary>
    public class OuRuleValidationTests
    {
        // The real columns of the tenant's source view.
        private static readonly string[] Columns = { "STUDENT_ID", "GENDER_CODE", "CITY_NO", "CITY_DESC" };

        [Fact]
        public void TheRuleThatShippedBroken_IsRejected()
        {
            var broken = new TenantOURule
            {
                OUTemplate = "OU={GENDER},OU={CITY},{BaseDN}",
                ValueMappings = "{CITY\":{\"14\":\"NAJRAN\"},\"GENDER\":{\"1\":\"MALE\"}\"}"
            };

            var errors = MappingEngine.ValidateOURule(broken, Columns);

            Assert.NotEmpty(errors);
            Assert.Contains(errors, e => e.Contains("JSON"));
            Assert.Contains(errors, e => e.Contains("{GENDER}"));
            Assert.Contains(errors, e => e.Contains("{CITY}"));
        }

        [Fact]
        public void TheCorrectedRule_PassesCleanly()
        {
            var good = new TenantOURule
            {
                OUTemplate = "OU={GENDER_CODE},OU={CITY_NO},{BaseDN}",
                ValueMappings = "{\"CITY_NO\":{\"14\":\"NAJRAN\",\"43\":\"SHARORAH\"}," +
                                "\"GENDER_CODE\":{\"1\":\"MALE\",\"2\":\"FEMALE\"}}"
            };

            Assert.Empty(MappingEngine.ValidateOURule(good, Columns));
        }

        [Fact]
        public void MalformedJson_IsCaught_EvenWithoutKnownColumns()
        {
            var rule = new TenantOURule
            {
                OUTemplate = "OU={CITY_NO},{BaseDN}",
                ValueMappings = "{not json at all"
            };

            Assert.Contains(MappingEngine.ValidateOURule(rule), e => e.Contains("JSON"));
        }

        [Fact]
        public void ValueMappingsKey_WithNoMatchingPlaceholder_IsCaught()
        {
            // A renamed placeholder leaves an orphan key that silently stops translating.
            var rule = new TenantOURule
            {
                OUTemplate = "OU={CITY_NO},{BaseDN}",
                ValueMappings = "{\"CITY\":{\"14\":\"NAJRAN\"}}"
            };

            Assert.Contains(MappingEngine.ValidateOURule(rule, Columns), e => e.Contains("CITY"));
        }

        [Fact]
        public void BaseDN_IsNotTreatedAsASourceColumn()
        {
            var rule = new TenantOURule { OUTemplate = "OU=Students,{BaseDN}" };

            Assert.Empty(MappingEngine.ValidateOURule(rule, Columns));
        }

        [Fact]
        public void WithoutKnownColumns_PlaceholderCheckIsSkipped()
        {
            // Callers that cannot supply the column list must not get false positives.
            var rule = new TenantOURule
            {
                OUTemplate = "OU={ANYTHING},{BaseDN}",
                ValueMappings = "{\"ANYTHING\":{\"1\":\"X\"}}"
            };

            Assert.Empty(MappingEngine.ValidateOURule(rule));
        }

        [Fact]
        public void EmptyTemplate_IsRejected()
        {
            Assert.NotEmpty(MappingEngine.ValidateOURule(new TenantOURule { OUTemplate = "" }, Columns));
        }

        // ------------------------------------------------------------------
        // Third silent fault: a ValueMappings entry that covers some values and not others.
        // ResolveOU passes an unmapped value straight through, so CITY_NO 20 becomes "OU=20"
        // rather than "OU=SHARORAH". Nothing is empty, so no DEFAULT warning fires, and the only
        // symptom is one failed create per account — 38 of them before the cause was visible.
        // ------------------------------------------------------------------

        private static readonly Dictionary<string, IEnumerable<string>> Observed =
            new()
            {
                ["CITY_NO"]     = new[] { "14", "20" },
                ["GENDER_CODE"] = new[] { "1", "2" }
            };

        private static TenantOURule RuleCovering(params string[] cityCodes)
        {
            var city = string.Join(",", cityCodes.Select(c => $"\"{c}\":\"CITY{c}\""));
            return new TenantOURule
            {
                OUTemplate = "OU={GENDER_CODE},OU={CITY_NO},{BaseDN}",
                ValueMappings = $"{{\"CITY_NO\":{{{city}}},\"GENDER_CODE\":{{\"1\":\"MALE\",\"2\":\"FEMALE\"}}}}"
            };
        }

        [Fact]
        public void AValueTheSourceHoldsButTheMapDoesNot_IsCaughtBeforeTheRun()
        {
            // The exact lab failure: the map knew 14 and not 20.
            var errors = MappingEngine.ValidateOURule(RuleCovering("14"), Columns, Observed);

            Assert.Contains(errors, e => e.Contains("CITY_NO") && e.Contains("20"));
        }

        [Fact]
        public void EveryValueMapped_PassesCleanly()
        {
            Assert.Empty(MappingEngine.ValidateOURule(RuleCovering("14", "20"), Columns, Observed));
        }

        [Fact]
        public void TheMessageNamesEveryMissingValue_NotJustTheFirst()
        {
            var observed = new Dictionary<string, IEnumerable<string>>
            {
                ["CITY_NO"]     = new[] { "14", "20", "31", "44" },
                ["GENDER_CODE"] = new[] { "1", "2" }
            };

            var errors = MappingEngine.ValidateOURule(RuleCovering("14"), Columns, observed);
            var message = Assert.Single(errors);

            Assert.Contains("20", message);
            Assert.Contains("31", message);
            Assert.Contains("44", message);
        }

        [Fact]
        public void APlaceholderWithNoMapAtAll_IsLeftAlone()
        {
            // Deliberate: the column already carries the OU name, so there is nothing to map.
            // Flagging it would make the check noisy enough to be ignored.
            var rule = new TenantOURule
            {
                OUTemplate = "OU={CITY_DESC},{BaseDN}",
                ValueMappings = "{\"GENDER_CODE\":{\"1\":\"MALE\",\"2\":\"FEMALE\"}}"
            };
            var observed = new Dictionary<string, IEnumerable<string>>
            {
                ["CITY_DESC"] = new[] { "NAJRAN", "SHARORAH" }
            };

            // Only the orphan GENDER_CODE key is reported; CITY_DESC's values are not second-guessed.
            var errors = MappingEngine.ValidateOURule(rule, Columns, observed);

            Assert.DoesNotContain(errors, e => e.Contains("CITY_DESC"));
        }

        [Fact]
        public void NullAndBlankSourceValues_AreNotReportedAsUnmapped()
        {
            // An empty value is the DEFAULT fault, which ResolveOU already warns about. Reporting
            // it here too would blame the map for something that is not its problem.
            var observed = new Dictionary<string, IEnumerable<string>>
            {
                ["CITY_NO"]     = new[] { "14", "", "   ", null! },
                ["GENDER_CODE"] = new[] { "1", "2" }
            };

            Assert.Empty(MappingEngine.ValidateOURule(RuleCovering("14"), Columns, observed));
        }

        [Fact]
        public void SurroundingWhitespaceDoesNotCreateAPhantomMissingValue()
        {
            // Source columns arrive padded often enough that " 14 " must not be reported as a
            // separate unmapped value from "14".
            var observed = new Dictionary<string, IEnumerable<string>>
            {
                ["CITY_NO"]     = new[] { " 14 ", "14" },
                ["GENDER_CODE"] = new[] { "1", "2" }
            };

            Assert.Empty(MappingEngine.ValidateOURule(RuleCovering("14"), Columns, observed));
        }

        [Fact]
        public void WithoutObservedValues_TheCoverageCheckIsSkipped()
        {
            // Callers that cannot query the source must keep working exactly as before.
            Assert.Empty(MappingEngine.ValidateOURule(RuleCovering("14"), Columns));
        }
    }
}
