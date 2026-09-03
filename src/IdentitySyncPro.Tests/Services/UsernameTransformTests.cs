using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Services;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// The Username: transform exists because account naming is policy, not code: one tenant
    /// wants maalhareth, another mohammed.alhareth, another the employee number. These tests pin
    /// the shapes real institutions ask for, and — just as importantly — pin that a tenant which
    /// does NOT use the transform is completely unaffected by it.
    /// </summary>
    public class UsernameTransformTests
    {
        /// <summary>The worked example: Mohammed ali al hareth.</summary>
        private static Dictionary<string, object?> Employee() => new(StringComparer.OrdinalIgnoreCase)
        {
            ["EMP_NO"] = 4471,
            ["FIRST_NAME"] = "Mohammed",
            ["SECOND_NAME"] = "ali",
            ["LAST_NAME"] = "al hareth",
            ["EMP_STATUS"] = 1
        };

        private static string Build(string spec) => MappingEngine.BuildUsername(spec, Employee());

        // ═══ The requested policy ═══

        [Fact]
        public void FirstInitial_SecondInitial_FullFamilyName_IsTheRequestedShape()
        {
            Assert.Equal("maalhareth", Build("{FIRST_NAME:1}{SECOND_NAME:1}{LAST_NAME}"));
        }

        /// <summary>
        /// The family name is two words. Normalising each component BEFORE taking its first N
        /// characters is what makes this predictable — the space must not occupy one of them.
        /// </summary>
        [Fact]
        public void MultiWordFamilyName_LosesItsSpace_NotItsLetters()
        {
            Assert.Equal("alh", Build("{LAST_NAME:3}"));
        }

        // ═══ Other institutions' policies ═══

        [Theory]
        [InlineData("{FIRST_NAME}.{LAST_NAME}", "mohammed.alhareth")]
        [InlineData("{FIRST_NAME:1}{LAST_NAME}", "malhareth")]
        [InlineData("{LAST_NAME}{FIRST_NAME:1}", "alharethm")]
        [InlineData("{FIRST_NAME}_{EMP_NO}", "mohammed_4471")]
        [InlineData("{FIRST_NAME:1}{SECOND_NAME:1}{LAST_NAME:7}", "maalharet")]
        [InlineData("{EMP_NO}", "4471")]
        public void EachPolicyShape_IsExpressibleAsConfiguration(string spec, string expected)
        {
            Assert.Equal(expected, Build(spec));
        }

        [Fact]
        public void CaseOption_IsHonoured()
        {
            Assert.Equal("MAALHARETH", Build("{FIRST_NAME:1}{SECOND_NAME:1}{LAST_NAME}|upper"));
            Assert.Equal("maalhareth", Build("{FIRST_NAME:1}{SECOND_NAME:1}{LAST_NAME}|lower"));
        }

        // ═══ AD constraints ═══

        [Fact]
        public void ResultIsCappedAtTheSamAccountNameLimit()
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["FIRST_NAME"] = "Abdulrahman",
                ["LAST_NAME"] = "Almohammedalsaleh"
            };

            var result = MappingEngine.BuildUsername("{FIRST_NAME}.{LAST_NAME}", row);

            Assert.Equal(MappingEngine.SamAccountNameMaxLength, result.Length);
            Assert.Equal("abdulrahman.almohamm", result);
        }

        [Fact]
        public void MaxOption_OverridesTheDefaultCap()
        {
            Assert.Equal("maalh", Build("{FIRST_NAME:1}{SECOND_NAME:1}{LAST_NAME}|max:5"));
        }

        /// <summary>
        /// The value becomes both sAMAccountName and part of the account's DN, so characters that
        /// are significant to LDAP must not survive.
        /// </summary>
        [Fact]
        public void CharactersIllegalInAnAccountName_AreStripped()
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["FIRST_NAME"] = "O'Brien, Jr.",
                ["LAST_NAME"] = "Al-Hareth"
            };

            var result = MappingEngine.BuildUsername("{FIRST_NAME:1}{LAST_NAME}", row);

            Assert.Equal("oal-hareth", result);
            Assert.DoesNotContain("'", result);
            Assert.DoesNotContain(",", result);
        }

        [Fact]
        public void MissingColumn_YieldsNothingForThatPlaceholder_RatherThanTheWordDefault()
        {
            var result = Build("{FIRST_NAME:1}{NO_SUCH_COLUMN:1}{LAST_NAME}");

            Assert.Equal("malhareth", result);
            Assert.DoesNotContain("default", result, StringComparison.OrdinalIgnoreCase);
        }

        // ═══ Collision discriminators ═══

        [Fact]
        public void CollisionSuffix_AppendsTheDiscriminator()
        {
            Assert.Equal("maalhareth2", MappingEngine.ApplyCollisionSuffix("maalhareth", 2));
            Assert.Equal("maalhareth3", MappingEngine.ApplyCollisionSuffix("maalhareth", 3));
        }

        /// <summary>
        /// A name already at the limit must give up characters to make room for the number,
        /// otherwise the discriminated name is rejected by AD — the collision "fix" would itself
        /// be the failure.
        /// </summary>
        [Fact]
        public void CollisionSuffix_KeepsTheResultWithinTheAdLimit()
        {
            var atLimit = new string('a', MappingEngine.SamAccountNameMaxLength);

            var result = MappingEngine.ApplyCollisionSuffix(atLimit, 12);

            Assert.Equal(MappingEngine.SamAccountNameMaxLength, result.Length);
            Assert.EndsWith("12", result);
        }

        [Fact]
        public void CollisionFormat_IsConfigurable()
        {
            Assert.Equal("maalhareth.2", MappingEngine.ApplyCollisionSuffix("maalhareth", 2, "{base}.{n}"));
        }

        // ═══ GetIdentifier with a Username: pattern ═══

        /// <summary>
        /// The identifier mapping still has to name some source column, but a Username: pattern
        /// names its own. This pins that the pattern's columns are what count.
        /// </summary>
        [Fact]
        public void GetIdentifier_BuildsFromThePattern_NotTheMappingsSourceColumn()
        {
            var mappings = new List<TenantAttributeMapping>
            {
                new()
                {
                    SourceColumn = "FIRST_NAME",
                    TargetAttribute = "sAMAccountName",
                    IsIdentifier = true,
                    Transform = "Username:{FIRST_NAME:1}{SECOND_NAME:1}{LAST_NAME}"
                }
            };

            Assert.Equal("maalhareth", MappingEngine.GetIdentifier(Employee(), mappings));
        }

        /// <summary>
        /// The mapping's own SourceColumn being empty must not suppress generation — the caller
        /// falls back to the numeric source key when GetIdentifier returns null, which would hand
        /// one employee a numeric account name while everyone else got letters.
        /// </summary>
        [Fact]
        public void GetIdentifier_StillGenerates_WhenTheMappingsOwnColumnIsEmpty()
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["EMP_NO"] = 4471,
                ["UNUSED_COLUMN"] = "",
                ["FIRST_NAME"] = "Mohammed",
                ["SECOND_NAME"] = "ali",
                ["LAST_NAME"] = "al hareth"
            };

            var mappings = new List<TenantAttributeMapping>
            {
                new()
                {
                    SourceColumn = "UNUSED_COLUMN",
                    TargetAttribute = "sAMAccountName",
                    IsIdentifier = true,
                    Transform = "Username:{FIRST_NAME:1}{SECOND_NAME:1}{LAST_NAME}"
                }
            };

            Assert.Equal("maalhareth", MappingEngine.GetIdentifier(row, mappings));
        }

        /// <summary>
        /// When the pattern resolves to nothing, null is the honest answer — it makes the caller
        /// skip the record instead of inventing a name.
        /// </summary>
        [Fact]
        public void GetIdentifier_ReturnsNull_WhenEveryPatternColumnIsEmpty()
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["EMP_NO"] = 4471,
                ["FIRST_NAME"] = "",
                ["SECOND_NAME"] = null,
                ["LAST_NAME"] = "   "
            };

            var mappings = new List<TenantAttributeMapping>
            {
                new()
                {
                    SourceColumn = "FIRST_NAME",
                    TargetAttribute = "sAMAccountName",
                    IsIdentifier = true,
                    Transform = "Username:{FIRST_NAME:1}{SECOND_NAME:1}{LAST_NAME}"
                }
            };

            Assert.Null(MappingEngine.GetIdentifier(row, mappings));
        }

        // ═══ The students tenant must be unaffected ═══

        /// <summary>
        /// The students tenant maps its numeric identity column straight to sAMAccountName with no
        /// transform. This is the regression that matters: the new transform must be inert for any
        /// mapping that does not name it.
        /// </summary>
        [Fact]
        public void AMappingWithNoTransform_StillPassesTheValueThroughUnchanged()
        {
            var studentRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["STUDENT_ID"] = 441000123,
                ["STATUSE_CODE"] = 1
            };

            var mappings = new List<TenantAttributeMapping>
            {
                new() { SourceColumn = "STUDENT_ID", TargetAttribute = "sAMAccountName", IsIdentifier = true }
            };

            var mapped = MappingEngine.ApplyMappings(studentRow, mappings);

            Assert.Equal("441000123", mapped["sAMAccountName"]);
            Assert.Equal("441000123", MappingEngine.GetIdentifier(studentRow, mappings));
        }

        /// <summary>
        /// The pre-existing transforms must behave exactly as before — the Username: branch was
        /// inserted among them and must not shadow any of them.
        /// </summary>
        [Theory]
        [InlineData("Format:{0}@corp.com", "a.saad@corp.com")]
        [InlineData("ToUpper", "A.SAAD")]
        [InlineData("Truncate:3", "a.s")]
        [InlineData("Static:Employee", "Employee")]
        public void ExistingTransforms_AreUnchanged(string transform, string expected)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["LOGIN"] = "a.saad" };
            var mappings = new List<TenantAttributeMapping>
            {
                new() { SourceColumn = "LOGIN", TargetAttribute = "mail", Transform = transform }
            };

            Assert.Equal(expected, MappingEngine.ApplyMappings(row, mappings)["mail"]);
        }
    }
}
