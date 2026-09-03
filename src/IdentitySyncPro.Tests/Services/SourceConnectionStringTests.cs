using IdentitySyncPro.Core.Models.Settings;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Guards how a tenant reaches its source database.
    ///
    /// Windows authentication for a SQL Server source was missing — the application's own database
    /// had offered it from the beginning, and the source side had not, so a SQL Server that only
    /// accepts Windows authentication could not be a source at all. The asymmetry was an omission
    /// rather than a decision, and it is the sort that survives for years because nobody with a
    /// password-authenticated source ever meets it.
    /// </summary>
    public class SourceConnectionStringTests
    {
        private static TenantSettings Sql(bool integrated = false) => new()
        {
            SourceProvider = "SqlServer",
            SourceHost = "db.example.org",
            SourcePort = 1433,
            SourceDatabase = "People",
            SourceUsername = "reader",
            SourcePassword = "s3cret",
            SourceIntegratedSecurity = integrated
        };

        [Fact]
        public void ASqlServerSourceStillDefaultsToItsStoredCredentials()
        {
            var cs = Sql().SourceConnectionString;

            Assert.Contains("User Id=reader", cs);
            Assert.Contains("Password=s3cret", cs);
            Assert.DoesNotContain("Integrated Security", cs);
        }

        /// <summary>An upgrade must not change how an existing tenant connects.</summary>
        [Fact]
        public void TheDefaultIsOff()
        {
            Assert.False(new TenantSettings().SourceIntegratedSecurity);
        }

        [Fact]
        public void WindowsAuthenticationDropsTheCredentialsFromTheConnection()
        {
            var cs = Sql(integrated: true).SourceConnectionString;

            Assert.Contains("Integrated Security=True", cs);
            Assert.DoesNotContain("User Id=", cs);
            Assert.DoesNotContain("s3cret", cs);
        }

        [Fact]
        public void TheHostAndDatabaseSurviveEitherWay()
        {
            foreach (var integrated in new[] { true, false })
            {
                var cs = Sql(integrated).SourceConnectionString;
                Assert.Contains("Server=db.example.org,1433", cs);
                Assert.Contains("Database=People", cs);
            }
        }

        /// <summary>
        /// The other providers take their own credentials and have no Windows-authentication mode.
        /// A switch that silently did nothing on three of four providers would be worse than absent.
        /// </summary>
        [Theory]
        [InlineData("Oracle")]
        [InlineData("PostgreSQL")]
        [InlineData("MySQL")]
        public void OtherProvidersAreUnaffectedByTheSwitch(string provider)
        {
            var tenant = Sql(integrated: true);
            tenant.SourceProvider = provider;

            var cs = tenant.SourceConnectionString;

            Assert.DoesNotContain("Integrated Security", cs);
            Assert.Contains("reader", cs);
        }

        // ══════════════════════════════════════
        // ⛔ النسخة المسمّاة
        // ══════════════════════════════════════

        /// <summary>
        /// A named instance and a port contradict each other. Written as <c>HOST\INSTANCE,1433</c>
        /// the client uses the port, ignores the instance, tries the default instance, finds
        /// nothing, and times out with "the server was not found" — an error that points at the
        /// network and not at the setting.
        ///
        /// The effect was that no institution running a named instance could configure a source at
        /// all. Found by trying to run a real sync against one.
        /// </summary>
        [Fact]
        public void ANamedInstanceIsNotGivenAPort()
        {
            var tenant = Sql();
            tenant.SourceHost = @"PCDEV\SQLEXPRESS";

            var cs = tenant.SourceConnectionString;

            Assert.Contains(@"Server=PCDEV\SQLEXPRESS;", cs);
            Assert.DoesNotContain("1433", cs);
        }

        [Fact]
        public void APlainHostStillGetsItsPort()
        {
            Assert.Contains("Server=db.example.org,1433;", Sql().SourceConnectionString);
        }

        [Fact]
        public void ANamedInstanceWorksUnderWindowsAuthenticationToo()
        {
            var tenant = Sql(integrated: true);
            tenant.SourceHost = @"PCDEV\SQLEXPRESS";

            var cs = tenant.SourceConnectionString;

            Assert.Contains(@"Server=PCDEV\SQLEXPRESS;", cs);
            Assert.Contains("Integrated Security=True", cs);
        }

        /// <summary>A port of zero means "not set", and must not become a literal ",0".</summary>
        [Fact]
        public void AnUnsetPortIsOmitted()
        {
            var tenant = Sql();
            tenant.SourcePort = 0;

            Assert.Contains("Server=db.example.org;", tenant.SourceConnectionString);
        }

        // ══════════════════════════════════════
        // البديل حيلة AD وحدها
        // ══════════════════════════════════════

        /// <summary>
        /// Active Directory refuses a write that sets an attribute to an empty string, so
        /// institutions put a placeholder in the gap. SCIM has no such constraint: sending the
        /// placeholder writes nonsense into the target as though it were data.
        ///
        /// A source row with no email address produced <c>emails[0].value = "."</c> in a real SCIM
        /// service — a syntactically invalid address a stricter service would reject and a lenient
        /// one would store and hand on. Found by running a sync end to end; no unit test on the
        /// setting, the mapping or the connector would have caught it, because each was correct.
        /// </summary>
        [Fact]
        public void ThePlaceholderIsNotSentToASourceThatDoesNotNeedIt()
        {
            var tenant = new TenantSettings { GlobalDefaultValue = ".", TargetProvider = "Scim" };

            Assert.Equal(string.Empty, tenant.EffectiveGlobalDefault);
        }

        [Fact]
        public void ActiveDirectoryStillGetsThePlaceholder()
        {
            var tenant = new TenantSettings { GlobalDefaultValue = ".", TargetProvider = "ActiveDirectory" };

            Assert.Equal(".", tenant.EffectiveGlobalDefault);
        }

        /// <summary>A pre-upgrade tenant has NULL here and is an Active Directory tenant.</summary>
        [Fact]
        public void ATenantWithNoTargetRecordedIsTreatedAsActiveDirectory()
        {
            var tenant = new TenantSettings { GlobalDefaultValue = "-", TargetProvider = null };

            Assert.Equal("-", tenant.EffectiveGlobalDefault);
        }

        /// <summary>
        /// The two questions are asked separately even though both mean "is this Active Directory"
        /// today — a third provider could answer them differently, and reusing one for the other is
        /// how a new connector quietly changes unrelated behaviour.
        /// </summary>
        [Fact]
        public void ThePlaceholderQuestionIsAskedOnItsOwnTerms()
        {
            Assert.True(TargetProviders.UsesEmptyAttributePlaceholder("ActiveDirectory"));
            Assert.False(TargetProviders.UsesEmptyAttributePlaceholder("Scim"));
        }

        [Fact]
        public void ATenantWithNoSourceHostHasNoConnectionString()
        {
            var tenant = Sql();
            tenant.SourceHost = "";

            Assert.Equal(string.Empty, tenant.SourceConnectionString);
        }
    }
}
