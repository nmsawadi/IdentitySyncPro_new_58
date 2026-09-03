using System.Reflection;
using System.Text.RegularExpressions;
using IdentitySyncPro.Core.Models.Services;
using IdentitySyncPro.Core.Models.Settings;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Holds the boot-time schema and the entity classes to the same answer about what may be null.
    ///
    /// This exists because the disagreement is invisible until production data meets it. A column
    /// added as NULL — which every upgrade column is, so that existing rows are not disturbed —
    /// paired with a non-nullable string property means Entity Framework calls <c>GetString</c> on
    /// a NULL and throws <c>SqlNullValueException</c> while materialising the entity. Before any of
    /// the feature's own code runs.
    ///
    /// And the blast radius is not the new feature. It is <b>every screen that loads that entity</b>:
    /// one nullable column on TenantSettings took out the settings screen, the access catalog, and
    /// every sync that reads a tenant. The feature it belonged to was never even reached.
    ///
    /// <b>Why no other test catches it:</b> the in-memory provider the rest of the suite uses has
    /// no SQL null semantics. It stores and returns a C# null happily. Only a real SQL Server
    /// reader throws — so the agreement has to be checked as a fact about the two declarations
    /// rather than observed at runtime.
    ///
    /// Found the hard way: <c>TargetProvider</c> shipped as a non-nullable string over a NULL
    /// column, in a design that had deliberately chosen NULL to mean "Active Directory".
    /// </summary>
    public class NullableColumnAgreementTests
    {
        /// <summary>
        /// The boot sequence's idempotent upgrades, read from source.
        ///
        /// Parsed rather than hand-listed on purpose: a hand-written list is a third declaration to
        /// keep in step with the other two, and would be the next thing to fall out of date.
        /// </summary>
        private static IEnumerable<(string Table, string Column, bool Nullable)> UpgradeColumns()
        {
            var program = FindProgramCs();
            var text = File.ReadAllText(program);

            // ... ALTER TABLE <Table> ADD <Column> <type...> NULL|NOT NULL ...
            var pattern = new Regex(
                @"ALTER TABLE (?<table>\w+) ADD (?<column>\w+) (?<type>[\w()]+(?:\s*\(\s*\w+\s*\))?)(?<rest>[^""]*)",
                RegexOptions.IgnoreCase);

            foreach (Match m in pattern.Matches(text))
            {
                var rest = m.Groups["rest"].Value;
                var notNull = rest.Contains("NOT NULL", StringComparison.OrdinalIgnoreCase);
                yield return (m.Groups["table"].Value, m.Groups["column"].Value, !notNull);
            }
        }

        private static string FindProgramCs()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "IdentitySyncPro.Web", "Program.cs");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("Could not locate IdentitySyncPro.Web/Program.cs from the test output folder.");
        }

        private static readonly Dictionary<string, Type> Entities = new(StringComparer.OrdinalIgnoreCase)
        {
            ["TenantSettings"] = typeof(TenantSettings),
            ["Svc_Services"] = typeof(SvcService)
        };

        /// <summary>
        /// Every column the upgrade adds as NULL must map to a property that can hold one.
        ///
        /// String properties only: a value type would have to be declared <c>Nullable&lt;T&gt;</c>
        /// to compile against a null anyway, and the reference-type case is the one the compiler
        /// stays quiet about.
        /// </summary>
        [Fact]
        public void EveryNullableUpgradeColumn_MapsToAPropertyThatCanBeNull()
        {
            var context = new NullabilityInfoContext();
            var offenders = new List<string>();
            var checkedCount = 0;

            foreach (var (table, column, nullable) in UpgradeColumns())
            {
                if (!nullable) continue;
                if (!Entities.TryGetValue(table, out var entity)) continue;

                var property = entity.GetProperty(column, BindingFlags.Public | BindingFlags.Instance);
                if (property == null || property.PropertyType != typeof(string)) continue;

                checkedCount++;
                if (context.Create(property).WriteState != NullabilityState.Nullable)
                    offenders.Add($"{table}.{column} is added as NULL but {entity.Name}.{property.Name} is a non-nullable string");
            }

            // If the parse ever stops matching, this test would pass by examining nothing at all —
            // which is the same silence it was written to prevent.
            Assert.True(checkedCount > 0, "No nullable string upgrade columns were found — the parse has probably broken.");
            Assert.Empty(offenders);
        }

        /// <summary>
        /// The specific case that caused it, pinned by name.
        ///
        /// The general check above would catch a regression here too, but naming it keeps the
        /// reason attached to the thing: NULL on this column is not an accident to be tidied away,
        /// it is the value that means "Active Directory" for every tenant configured before the
        /// second target existed.
        /// </summary>
        [Fact]
        public void TargetProvider_AcceptsTheNullThatMeansActiveDirectory()
        {
            var property = typeof(TenantSettings).GetProperty(nameof(TenantSettings.TargetProvider))!;

            Assert.Equal(NullabilityState.Nullable, new NullabilityInfoContext().Create(property).WriteState);
            Assert.Equal(TargetProviders.ActiveDirectory, TargetProviders.Normalise(null));
        }
    }
}
