using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Models.AccountStatus;
using IdentitySyncPro.Core.Models.Services;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Guards the registry of IdentitySyncPro's own AD bind accounts.
    ///
    /// Nothing here throws when it breaks. A domain prefix split from the wrong side, a UPN searched
    /// as an account name, a DN mistaken for a login — each produces an LDAP filter the directory
    /// accepts, answers with nothing, and never complains about. The account then goes unrecognised,
    /// and the guard silently protects one fewer identity than it reports.
    /// </summary>
    public class SelfAccountRegistryTests
    {
        // ══════════════════════════════════════
        // PARSING THE STORED CREDENTIAL
        // ══════════════════════════════════════

        /// <summary>
        /// The wrong-side split. "NJRAN\svc_sync" must yield the account, not the NetBIOS domain —
        /// searching sAMAccountName for "NJRAN" matches nothing and reads as "no such account".
        /// </summary>
        [Fact]
        public void DomainQualifiedName_KeepsTheAccountNotTheDomain()
        {
            var p = BindIdentity.Parse(@"NJRAN\svc_sync");
            Assert.Equal(BindIdentity.Kind.AccountName, p!.Kind);
            Assert.Equal("svc_sync", p.Value);
        }

        [Theory]
        [InlineData("svc_sync", BindIdentity.Kind.AccountName, "svc_sync")]
        [InlineData(@"NJRAN\svc_sync", BindIdentity.Kind.AccountName, "svc_sync")]
        [InlineData("  NJRAN\\svc_sync  ", BindIdentity.Kind.AccountName, "svc_sync")]
        [InlineData("svc_sync@njran.edu.sa", BindIdentity.Kind.UserPrincipalName, "svc_sync@njran.edu.sa")]
        [InlineData("CN=Sync,OU=Svc,DC=njran,DC=edu", BindIdentity.Kind.DistinguishedName, "CN=Sync,OU=Svc,DC=njran,DC=edu")]
        public void EveryStoredShape_IsRecognised(string stored, BindIdentity.Kind kind, string value)
        {
            var p = BindIdentity.Parse(stored);
            Assert.Equal(kind, p!.Kind);
            Assert.Equal(value, p.Value);
        }

        /// <summary>
        /// A DN is recognised before the separators are looked at, because it can legitimately
        /// contain both of them: a CN carrying an address would otherwise be split at the '@' and
        /// searched as a login.
        /// </summary>
        [Fact]
        public void DnContainingAnAtSign_IsStillADn()
        {
            var p = BindIdentity.Parse("CN=svc@njran.edu.sa,OU=Service Accounts,DC=njran,DC=edu");
            Assert.Equal(BindIdentity.Kind.DistinguishedName, p!.Kind);
        }

        /// <summary>Nothing configured is not an error — integrated security stores no bind account at all.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(@"NJRAN\")]
        public void NothingUsable_YieldsNothing(string? stored)
        {
            Assert.Null(BindIdentity.Parse(stored));
        }

        // ══════════════════════════════════════
        // THE LOOKUP FILTER
        // ══════════════════════════════════════

        [Fact]
        public void AccountName_IsSearchedBySamAccountName()
        {
            var f = BindIdentity.BuildFilter(BindIdentity.Parse(@"NJRAN\svc_sync")!);
            Assert.Equal("(&(objectClass=user)(sAMAccountName=svc_sync))", f);
        }

        /// <summary>
        /// A UPN is searched both ways. The suffix can be changed on the account after the setting
        /// was saved, and the local part is very often the sAMAccountName — searching only the UPN
        /// would drop the account the day somebody renames a domain.
        /// </summary>
        [Fact]
        public void Upn_IsSearchedByBothUpnAndAccountName()
        {
            var f = BindIdentity.BuildFilter(BindIdentity.Parse("svc_sync@njran.edu.sa")!)!;
            Assert.Contains("(userPrincipalName=svc_sync@njran.edu.sa)", f);
            Assert.Contains("(sAMAccountName=svc_sync)", f);
        }

        /// <summary>A DN needs no filter — it is read directly, and a null here is what tells the caller so.</summary>
        [Fact]
        public void Dn_NeedsNoFilter()
        {
            Assert.Null(BindIdentity.BuildFilter(BindIdentity.Parse("CN=Sync,OU=Svc,DC=njran,DC=edu")!));
        }

        /// <summary>A stored credential is still untrusted input by the time it reaches a filter.</summary>
        [Fact]
        public void StoredCredential_IsEscapedIntoTheFilter()
        {
            var f = BindIdentity.BuildFilter(BindIdentity.Parse(@"NJRAN\svc)(objectClass=*")!)!;
            Assert.Contains(@"\29", f);   // the injected ')' is escaped
            Assert.Contains(@"\28", f);   // and the '('
            Assert.EndsWith("))", f);     // the filter still closes exactly where it should
        }

        // ══════════════════════════════════════
        // WHICH DIRECTORY AN ENTRY BELONGS TO
        // ══════════════════════════════════════

        [Theory]
        // Same domain, whatever the OU above it — and however the DN was spaced or cased.
        [InlineData("OU=Svc,DC=njran,DC=edu,DC=sa", "DC=njran,DC=edu,DC=sa", true)]
        [InlineData("OU=Svc, dc=NJRAN, dc=Edu, dc=SA", "DC=njran,DC=edu,DC=sa", true)]
        [InlineData("OU=Staff,DC=njran,DC=edu,DC=sa", "OU=Svc,DC=njran,DC=edu,DC=sa", true)]
        // A child domain still shares the forest suffix.
        [InlineData("DC=sub,DC=njran,DC=edu,DC=sa", "DC=njran,DC=edu,DC=sa", true)]
        // A different directory entirely: this is the entry that must NOT be demanded to resolve.
        [InlineData("DC=other,DC=com", "DC=njran,DC=edu,DC=sa", false)]
        public void SameDirectory_IsJudgedByTheSharedDomainSuffix(string configured, string scanBase, bool same)
        {
            Assert.Equal(same, BindIdentity.SameDirectory(configured, scanBase));
        }

        /// <summary>
        /// A near-miss must not read as a match: "dc=sa" is a suffix of "dc=njransa" as text but not
        /// as a domain, and a false "same directory" would demand a resolution that can never happen.
        /// </summary>
        [Fact]
        public void SimilarLookingDomains_AreNotTheSame()
        {
            Assert.False(BindIdentity.SameDirectory("DC=njransa", "DC=sa"));
        }

        /// <summary>
        /// An unknown suffix is treated as "same", which is the cautious reading: a missing BaseDN
        /// is not evidence that an account lives somewhere else, and assuming it does would drop
        /// the entry out of the guard entirely.
        /// </summary>
        [Theory]
        [InlineData(null, "DC=njran,DC=edu")]
        [InlineData("", "DC=njran,DC=edu")]
        [InlineData("DC=njran,DC=edu", null)]
        public void UnknownSuffix_IsTreatedAsSameDirectory(string? configured, string? scanBase)
        {
            Assert.True(BindIdentity.SameDirectory(configured, scanBase));
        }

        [Theory]
        [InlineData("OU=Svc, DC=njran, DC=edu", "dc=njran,dc=edu")]
        [InlineData("CN=x,OU=y", "")]
        [InlineData(null, "")]
        public void DomainSuffix_IsNormalised(string? dn, string expected)
        {
            Assert.Equal(expected, BindIdentity.DomainSuffix(dn));
        }

        // ══════════════════════════════════════
        // WHAT THE REGISTRY HANDS BACK
        // ══════════════════════════════════════

        private static SelfAccountRegistry.SelfAccount Account(
            string source, string? dn, string? problem = null, bool foreign = false) =>
            new(source, "svc_x", dn, problem, foreign);

        [Fact]
        public void ResolvedAccounts_AreMatchedCaseInsensitively()
        {
            var set = new SelfAccountRegistry.SelfAccounts(new[] { Account("tenant", "CN=Svc,DC=njran,DC=edu") });
            Assert.Contains("cn=svc,dc=njran,dc=edu", set.Dns);
        }

        /// <summary>
        /// The distinction the whole design rests on. An account configured for another directory
        /// cannot resolve here and is not a problem — treating it as one stops every run on a
        /// multi-domain installation. An account from THIS directory that did not resolve is the
        /// one a caller has to act on.
        /// </summary>
        [Fact]
        public void OnlyLocalFailures_CountAsUnresolved()
        {
            var set = new SelfAccountRegistry.SelfAccounts(new[]
            {
                Account("service 'sync'", "CN=Svc,DC=njran,DC=edu"),
                Account("tenant 'other'", null, "configured against another directory", foreign: true),
                Account("SSPR domain 'main'", null, "not found in the scanned scope")
            });

            Assert.Single(set.Dns);
            var unresolved = Assert.Single(set.Unresolved);
            Assert.Equal("SSPR domain 'main'", unresolved.Source);
        }

        /// <summary>
        /// Every entry names where it is configured, because the operator reading the finding has to
        /// know which of four screens to open — "a bind account did not resolve" is not actionable.
        /// </summary>
        [Fact]
        public void UnresolvedEntries_NameTheirSource()
        {
            var set = new SelfAccountRegistry.SelfAccounts(new[]
            {
                Account("service 'إخلاء طرف'", null, "not found in the scanned scope")
            });

            Assert.Contains("إخلاء طرف", set.Unresolved.Single().Source);
            Assert.NotNull(set.Unresolved.Single().Problem);
        }


        // ══════════════════════════════════════
        // COVERING EVERY PLACE A BIND ACCOUNT IS STORED
        // ══════════════════════════════════════

        private static TContext InMemory<TContext>(Func<DbContextOptions<TContext>, TContext> make)
            where TContext : DbContext =>
            make(new DbContextOptionsBuilder<TContext>().UseInMemoryDatabase($"self-{Guid.NewGuid()}").Options);

        /// <summary>All four sources, seeded, behind a container the registry resolves them from.</summary>
        private static ServiceProvider SeededHost(bool withAccountStatus = true)
        {
            var svc = InMemory<ServicesDbContext>(o => new ServicesDbContext(o));
            svc.SvcServices.Add(new SvcService
            {
                Name = "إخلاء طرف", SourceProvider = "Oracle",
                ADUsername = @"NJRAN\svc_offboard", ADBaseDN = "DC=njran,DC=edu"
            });
            svc.SaveChanges();

            var app = InMemory<AppDbContext>(o => new AppDbContext(o));
            app.TenantSettings.Add(new TenantSettings
            {
                TenantName = "الموظفون", ADUsername = @"NJRAN\svc_sync", ADBaseDN = "DC=njran,DC=edu"
            });
            app.SsprDomains.Add(new SsprDomain
            {
                Name = "المجال الرئيسي", AdUsername = "svc_sspr@njran.edu", AdBaseDN = "DC=njran,DC=edu"
            });
            app.SaveChanges();

            var services = new ServiceCollection();
            services.AddSingleton(svc);
            services.AddSingleton(app);

            if (withAccountStatus)
            {
                var acct = InMemory<AccountStatusDbContext>(o => new AccountStatusDbContext(o));
                acct.CustomDomains.Add(new CustomDomain
                {
                    DisplayName = "دومين الحسابات", Username = "CN=Svc Status,OU=Svc,DC=njran,DC=edu",
                    BaseDN = "DC=njran,DC=edu"
                });
                acct.SaveChanges();
                services.AddSingleton(acct);
            }

            return services.BuildServiceProvider();
        }

        private static SelfAccountRegistry NewRegistry() =>
            new(NullLogger<SelfAccountRegistry>.Instance);

        /// <summary>
        /// The coverage question, and the reason the list is derived rather than typed: a bind
        /// account lives in four different screens across three databases. One left out is one
        /// identity the guard never mentions — and nothing about the run would look wrong.
        /// </summary>
        [Fact]
        public void EveryConfiguredBindAccount_IsCollected()
        {
            var collected = NewRegistry().Collect(SeededHost()).ToList();
            var accounts = collected
                .Where(c => c.Username != null)
                .Select(c => BindIdentity.Parse(c.Username)!.Value)
                .ToList();

            Assert.Contains("svc_offboard", accounts);                        // Services module
            Assert.Contains("svc_sync", accounts);                           // IAM tenant
            Assert.Contains("svc_sspr@njran.edu", accounts);                 // SSPR domain
            Assert.Contains("CN=Svc Status,OU=Svc,DC=njran,DC=edu", accounts); // account-status domain
            Assert.Equal(4, accounts.Count);
        }

        /// <summary>Each entry carries the BaseDN it was configured with — that is what decides whether it is expected to resolve here.</summary>
        [Fact]
        public void CollectedEntries_CarryTheirConfiguredBaseDn()
        {
            Assert.All(NewRegistry().Collect(SeededHost()).Where(c => c.Username != null),
                c => Assert.Equal("dc=njran,dc=edu", BindIdentity.DomainSuffix(c.BaseDn)));
        }

        /// <summary>
        /// A source the host does not provide must announce itself. Skipping it silently would mean
        /// the registry quietly covers less than it claims — the exact failure it exists to remove.
        /// </summary>
        [Fact]
        public void AnUnavailableSource_IsReportedNotSkipped()
        {
            var collected = NewRegistry().Collect(SeededHost(withAccountStatus: false)).ToList();

            Assert.Contains(collected, c => c.Source.Contains("unavailable"));
            Assert.DoesNotContain(collected, c => c.Username != null && c.Username.Contains("Svc Status"));
        }

        [Fact]
        public void NoConfiguredAccounts_IsAnEmptySetNotAFailure()
        {
            var set = new SelfAccountRegistry.SelfAccounts(Array.Empty<SelfAccountRegistry.SelfAccount>());
            Assert.Empty(set.Dns);
            Assert.Empty(set.Unresolved);
        }
    }
}
