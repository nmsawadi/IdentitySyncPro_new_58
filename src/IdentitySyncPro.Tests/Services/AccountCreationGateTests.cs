using System.Collections.Generic;
using IdentitySyncPro.Infrastructure.Services;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// The provisioning gate decides whether a source identity with no AD account gets one.
    ///
    /// Its failure mode is asymmetric: wrongly refusing leaves a person without an account, which
    /// someone notices and reports. Wrongly allowing writes accounts into a live directory for
    /// everyone in a source view — which nobody asked for and nothing undoes. Every ambiguous
    /// case below therefore asserts a refusal.
    /// </summary>
    public class AccountCreationGateTests
    {
        private static Dictionary<string, object?> Row(params (string Key, object? Value)[] values)
        {
            var row = new Dictionary<string, object?>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in values) row[key] = value;
            return row;
        }

        // ── Backward compatibility ────────────────────────────────────────────
        // A tenant that predates this setting has no value stored. Reading that as "never create"
        // would stop provisioning for a live tenant without raising a single error.

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void UnsetMode_CreatesAsBefore(string? mode)
        {
            var result = MappingEngine.ShouldCreateAccount(mode, null, null, null, Row(("EMP_STATUS", 1)));

            Assert.True(result.Allowed);
        }

        [Fact]
        public void AlwaysMode_CreatesRegardlessOfSourceValues()
        {
            var result = MappingEngine.ShouldCreateAccount("Always", null, null, null, Row(("EMP_STATUS", 99)));

            Assert.True(result.Allowed);
        }

        // ── Never ─────────────────────────────────────────────────────────────

        [Fact]
        public void NeverMode_RefusesAndSaysWhy()
        {
            var result = MappingEngine.ShouldCreateAccount("Never", null, null, null, Row(("EMP_STATUS", 1)));

            Assert.False(result.Allowed);
            Assert.NotEmpty(result.Reason);
        }

        [Fact]
        public void NeverMode_IgnoresAnyConfiguredCondition()
        {
            // Never means never: a leftover condition from a previous mode must not resurrect
            // provisioning for the rows that happen to match it.
            var result = MappingEngine.ShouldCreateAccount("Never", "EMP_STATUS", "==", "1", Row(("EMP_STATUS", 1)));

            Assert.False(result.Allowed);
        }

        // ── Conditional ───────────────────────────────────────────────────────

        [Fact]
        public void Conditional_CreatesWhenConditionMatches()
        {
            var result = MappingEngine.ShouldCreateAccount("Conditional", "EMP_STATUS", "==", "1", Row(("EMP_STATUS", 1)));

            Assert.True(result.Allowed);
        }

        [Fact]
        public void Conditional_RefusesWhenConditionDoesNotMatch()
        {
            var result = MappingEngine.ShouldCreateAccount("Conditional", "EMP_STATUS", "==", "1", Row(("EMP_STATUS", 7)));

            Assert.False(result.Allowed);
            Assert.Contains("EMP_STATUS", result.Reason);
        }

        [Fact]
        public void Conditional_SupportsListOperators()
        {
            var row = Row(("EMP_STATUS", 5));

            Assert.True(MappingEngine.ShouldCreateAccount("Conditional", "EMP_STATUS", "in", "4,5,6", row).Allowed);
            Assert.False(MappingEngine.ShouldCreateAccount("Conditional", "EMP_STATUS", "not_in", "4,5,6", row).Allowed);
        }

        // ── Fail-closed cases: the whole point of the gate ────────────────────

        [Theory]
        [InlineData(null, "==", "1")]   // no column
        [InlineData("EMP_STATUS", null, "1")]   // no operator
        [InlineData("EMP_STATUS", "==", null)]  // no value
        [InlineData("", "", "")]
        public void Conditional_WithIncompleteCondition_RefusesRatherThanCreatingEveryone(
            string? field, string? op, string? value)
        {
            // EvaluateSimpleCondition treats a missing operator or value as "matches", so without
            // an explicit guard a half-filled condition would provision the entire source view.
            var result = MappingEngine.ShouldCreateAccount("Conditional", field, op, value, Row(("EMP_STATUS", 1)));

            Assert.False(result.Allowed);
        }

        [Fact]
        public void Conditional_NamingAColumnTheSourceLacks_RefusesAndNamesTheColumn()
        {
            // The STATUS_CODE / STATUSE_CODE class of fault. A missing column reads as an empty
            // value, which silently satisfies nothing — the reason has to say which column.
            var result = MappingEngine.ShouldCreateAccount(
                "Conditional", "EMP_STATUSE", "==", "1", Row(("EMP_STATUS", 1)));

            Assert.False(result.Allowed);
            Assert.Contains("EMP_STATUSE", result.Reason);
        }

        [Fact]
        public void UnknownMode_RefusesRatherThanGuessing()
        {
            var result = MappingEngine.ShouldCreateAccount("Enabled", null, null, null, Row(("EMP_STATUS", 1)));

            Assert.False(result.Allowed);
            Assert.Contains("Enabled", result.Reason);
        }

        [Fact]
        public void ModeMatchingIsCaseAndWhitespaceInsensitive()
        {
            // Values arrive from a dropdown, but also from settings import and hand-edited SQL.
            Assert.False(MappingEngine.ShouldCreateAccount("never", null, null, null, Row()).Allowed);
            Assert.False(MappingEngine.ShouldCreateAccount("  NEVER  ", null, null, null, Row()).Allowed);
            Assert.True(MappingEngine.ShouldCreateAccount("always", null, null, null, Row()).Allowed);
        }

        /// <summary>
        /// Guards the opposite failure: a gate that refuses everything would stop provisioning for
        /// the tenants that depend on it, which the tests above would not catch on their own.
        /// </summary>
        [Fact]
        public void DefaultConfiguration_StillProvisions()
        {
            var row = Row(("EMP_NO", 4471), ("EMP_STATUS", 1));

            Assert.True(MappingEngine.ShouldCreateAccount(null, null, null, null, row).Allowed);
            Assert.True(MappingEngine.ShouldCreateAccount("Always", "EMP_STATUS", "==", "1", row).Allowed);
        }
    }
}
