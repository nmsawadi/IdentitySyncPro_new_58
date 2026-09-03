using IdentitySyncPro.Core.Helpers;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Column discovery builds its statement as text, because an object name cannot be a parameter.
    /// That made the table name — which arrives in a request body — an injection point:
    ///
    ///   SELECT TOP 0 * FROM [{name}]            a "]" closes the bracket early
    ///   SELECT * FROM {name} WHERE ROWNUM = 0   no quoting at all
    ///
    /// Found by the .NET security analyzers (CA3001) once they were enabled. The endpoint is
    /// Admin-only so it is not privilege escalation, but it is arbitrary SQL against the source
    /// database from one request field.
    /// </summary>
    public class SqlIdentifierGuardTests
    {
        [Theory]
        [InlineData("Students")]
        [InlineData("V_IDENTITY_DATA")]
        [InlineData("dbo.Employees")]
        [InlineData("HR.V_STAFF")]
        [InlineData("_internal")]
        [InlineData("T1")]
        public void PlainIdentifiersAreAccepted(string name)
        {
            Assert.True(SqlIdentifierGuard.IsValidObjectName(name));
        }

        [Theory]
        // The bracket escape that made the SQL Server statement injectable.
        [InlineData("X]; DROP TABLE Users--")]
        [InlineData("Users]")]
        // Oracle had no quoting at all, so anything that continues the statement worked.
        [InlineData("V_X WHERE 1=1 UNION SELECT password FROM dba_users")]
        [InlineData("V_X--")]
        [InlineData("V_X/*")]
        [InlineData("V_X;SELECT 1")]
        // Quote and whitespace forms.
        [InlineData("'; DROP TABLE X--")]
        [InlineData("\"Users\"")]
        [InlineData("two words")]
        [InlineData("tab\tname")]
        [InlineData("new\nline")]
        // Shapes that are not identifiers at all.
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData("1StartsWithDigit")]
        [InlineData("a.b.c")]      // more than one qualifier
        [InlineData(".Leading")]
        [InlineData("Trailing.")]
        public void AnythingThatIsNotAPlainIdentifierIsRefused(string? name)
        {
            Assert.False(SqlIdentifierGuard.IsValidObjectName(name));
        }

        [Fact]
        public void AnOverlongNameIsRefused()
        {
            Assert.False(SqlIdentifierGuard.IsValidObjectName(new string('A', 129)));
            Assert.True(SqlIdentifierGuard.IsValidObjectName(new string('A', 128)));
        }

        [Fact]
        public void SqlServerQuotingBracketsEachPartSeparately()
        {
            // "[dbo.Employees]" would name one object literally called "dbo.Employees" rather than
            // Employees inside dbo — a quoting bug that silently looks at the wrong thing.
            Assert.Equal("[Students]", SqlIdentifierGuard.QuoteSqlServer("Students"));
            Assert.Equal("[dbo].[Employees]", SqlIdentifierGuard.QuoteSqlServer("dbo.Employees"));
        }

        [Fact]
        public void OracleNamesAreLeftUnquoted()
        {
            // Oracle folds unquoted identifiers to upper case; quoting here would make a view
            // created with a lower-case name unreachable. Validation is what makes it safe.
            Assert.Equal("V_IDENTITY_DATA", SqlIdentifierGuard.ForOracle("V_IDENTITY_DATA"));
        }

        [Theory]
        [InlineData("X]; DROP TABLE Users--")]
        [InlineData("V_X WHERE 1=1")]
        [InlineData("")]
        public void QuotingRefusesRatherThanSanitises(string name)
        {
            // Both quoting helpers throw instead of returning a cleaned string. A helper that
            // silently repairs its input invites callers to stop checking.
            Assert.Throws<ArgumentException>(() => SqlIdentifierGuard.QuoteSqlServer(name));
            Assert.Throws<ArgumentException>(() => SqlIdentifierGuard.ForOracle(name));
        }
    }
}
