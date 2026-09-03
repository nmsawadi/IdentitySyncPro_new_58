using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using IdentitySyncPro.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Console sign-in against its own domain list.
    ///
    /// What this replaces: sign-in used to bind against whichever AD connection happened to exist
    /// elsewhere — an active sync tenant's, then an SSPR domain's. Who could log in was therefore a
    /// side effect of unrelated settings, and deactivating a tenant silently changed it.
    ///
    /// Two things have to hold. The candidate list must come from AuthDomains and honour active/
    /// order/channel exactly, because a wrong channel fails a bind in a way that looks identical to
    /// a wrong password. And the one-time import must be exactly once — re-running it whenever the
    /// table looks empty would resurrect domains an administrator deleted on purpose, which is the
    /// coupling being removed.
    /// </summary>
    public class AuthDomainTests
    {
        private static AuthDomain Domain(
            string name = "corp", string server = "dc01.corp.local", int port = 389,
            bool active = true, int order = 0,
            LdapSecurityMode mode = LdapSecurityMode.Auto, bool modeSet = true, bool useSsl = false) => new()
            {
                Name = name,
                AdServer = server,
                AdPort = port,
                IsActive = active,
                SortOrder = order,
                AdSecurityMode = mode,
                AdSecurityModeSet = modeSet,
                AdUseSsl = useSsl
            };

        // ── Candidate selection ──

        [Fact]
        public void OnlyActiveDomainsAreTried()
        {
            var candidates = AuthService.BuildCandidates(new[]
            {
                Domain("live", "dc1.corp.local"),
                Domain("retired", "dc2.corp.local", active: false)
            });

            Assert.Single(candidates);
            Assert.Equal("dc1.corp.local", candidates[0].Opts.Server);
        }

        /// <summary>
        /// A user existing in two domains signs in against whichever answers first, so the order is
        /// a real decision and has to be the administrator's, not the insertion order.
        /// </summary>
        [Fact]
        public void DomainsAreTriedInTheConfiguredOrder()
        {
            var candidates = AuthService.BuildCandidates(new[]
            {
                new AuthDomain { Id = 1, Name = "second", AdServer = "b.corp.local", IsActive = true, SortOrder = 20 },
                new AuthDomain { Id = 2, Name = "first",  AdServer = "a.corp.local", IsActive = true, SortOrder = 10 },
                new AuthDomain { Id = 3, Name = "third",  AdServer = "c.corp.local", IsActive = true, SortOrder = 20 }
            });

            // SortOrder decides; Id breaks the tie so the order is stable rather than arbitrary.
            Assert.Equal(new[] { "a.corp.local", "b.corp.local", "c.corp.local" },
                candidates.Select(c => c.Opts.Server).ToArray());
        }

        [Fact]
        public void ADomainWithNoServerIsSkipped()
        {
            // Nothing to bind against; attempting it produces a confusing error instead of none.
            Assert.Empty(AuthService.BuildCandidates(new[] { Domain(server: "  ") }));
        }

        [Fact]
        public void NoDomainsMeansNoCandidates()
        {
            Assert.Empty(AuthService.BuildCandidates(Array.Empty<AuthDomain>()));
        }

        /// <summary>
        /// The channel must survive the trip to the bind. A domain saved as sign-and-seal that
        /// connects unencrypted still reads fine and fails in ways that get blamed on credentials.
        /// </summary>
        [Fact]
        public void TheChosenChannelIsCarriedThrough()
        {
            var candidates = AuthService.BuildCandidates(new[]
            {
                Domain(mode: LdapSecurityMode.Ldaps, port: 636),
                Domain(name: "seal", server: "dc02.corp.local", mode: LdapSecurityMode.SignAndSeal)
            });

            Assert.Equal(LdapSecurityMode.Ldaps, candidates[0].Opts.SecurityMode);
            Assert.Equal(LdapSecurityMode.SignAndSeal, candidates[1].Opts.SecurityMode);
        }

        /// <summary>
        /// A row written before the channel field existed has no explicit mode, and must keep
        /// behaving exactly as its old UseSsl flag said — otherwise upgrading changes the channel
        /// under a working installation.
        /// </summary>
        [Theory]
        [InlineData(true, LdapSecurityMode.Ldaps)]
        [InlineData(false, LdapSecurityMode.SignAndSeal)]
        public void ARowOlderThanTheChannelFieldFollowsItsLegacyFlag(bool useSsl, LdapSecurityMode expected)
        {
            var candidates = AuthService.BuildCandidates(new[]
            {
                Domain(modeSet: false, useSsl: useSsl, port: useSsl ? 636 : 389)
            });

            Assert.Equal(expected, candidates[0].Opts.SecurityMode);
        }

        [Fact]
        public void TheUntrustedCertificateChoiceIsCarriedThrough()
        {
            var d = Domain(mode: LdapSecurityMode.Ldaps, port: 636);
            d.AdAllowUntrustedCertificate = true;

            Assert.True(AuthService.BuildCandidates(new[] { d })[0].Opts.AllowUntrustedCertificate);
        }

        // ── One-time import ──

        private static ILogger Logger() => Mock.Of<ILogger>();

        private static AppDbContext DbWith(bool adUser, params object[] rows)
        {
            var db = TestDbContext.Create();
            if (adUser)
                db.AppUsers.Add(new AppUser { Username = "domainuser", AuthType = AppUserAuthTypes.ActiveDirectory });
            foreach (var r in rows) db.Add(r);
            db.SaveChanges();
            return db;
        }

        private static SsprDomain Sspr(string name, string server, bool active = true) =>
            new() { Name = name, AdServer = server, AdPort = 389, IsActive = active, AdBaseDN = "DC=corp,DC=local" };

        [Fact]
        public async Task ExistingAdUsersKeepWorkingAfterTheUpgrade()
        {
            using var db = DbWith(adUser: true, Sspr("SSPR corp", "dc01.corp.local"));

            await AuthDomainSeeder.SeedOnceAsync(db, Logger());

            var imported = db.AuthDomains.Single();
            Assert.Equal("dc01.corp.local", imported.AdServer);
            Assert.True(imported.IsActive);
            // The inherited channel must be explicit, or the imported row would re-derive it from a
            // default UseSsl and could land on a different channel than the one that was working.
            Assert.True(imported.AdSecurityModeSet);
        }

        /// <summary>
        /// The guard that matters most: an administrator who deletes an imported domain must not
        /// find it back after the next restart.
        /// </summary>
        [Fact]
        public async Task DeletedDomainsAreNotResurrectedOnTheNextBoot()
        {
            using var db = DbWith(adUser: true, Sspr("SSPR corp", "dc01.corp.local"));

            await AuthDomainSeeder.SeedOnceAsync(db, Logger());
            db.AuthDomains.RemoveRange(db.AuthDomains);
            await db.SaveChangesAsync();

            await AuthDomainSeeder.SeedOnceAsync(db, Logger());   // next restart

            Assert.Empty(db.AuthDomains);
        }

        [Fact]
        public async Task ImportDoesNotRunWhenDomainsWereAlreadyConfigured()
        {
            using var db = DbWith(adUser: true, Sspr("SSPR corp", "dc01.corp.local"));
            db.AuthDomains.Add(Domain("chosen by the admin", "dc99.corp.local"));
            await db.SaveChangesAsync();

            await AuthDomainSeeder.SeedOnceAsync(db, Logger());

            Assert.Equal("dc99.corp.local", db.AuthDomains.Single().AdServer);
        }

        /// <summary>
        /// With no AD users there is nothing to preserve, and inheriting connections chosen for
        /// syncing or password reset would silently widen who can sign in later.
        /// </summary>
        [Fact]
        public async Task NothingIsImportedWhenNobodySignsInWithADomainAccount()
        {
            using var db = DbWith(adUser: false, Sspr("SSPR corp", "dc01.corp.local"));

            await AuthDomainSeeder.SeedOnceAsync(db, Logger());

            Assert.Empty(db.AuthDomains);
            // The marker is still written, so enabling AD for a user later starts from a blank
            // screen rather than inheriting these connections at that point.
            Assert.True(db.AppSettings.Any(s => s.Key == AuthDomainSeeder.SeededKey));
        }

        [Theory]
        [InlineData("YOUR_AD_SERVER")]
        [InlineData("dc.example.local")]
        public async Task PlaceholderServersAreNotImported(string server)
        {
            // Seed templates carry these. Importing one produces a domain that fails every bind
            // and explains nothing about why.
            using var db = DbWith(adUser: true, Sspr("template", server));

            await AuthDomainSeeder.SeedOnceAsync(db, Logger());

            Assert.Empty(db.AuthDomains);
        }

        [Fact]
        public async Task TheSameServerReachedTwoWaysBecomesOneDomain()
        {
            using var db = DbWith(adUser: true,
                Sspr("SSPR corp", "dc01.corp.local"),
                Sspr("SSPR corp duplicate", "DC01.CORP.LOCAL"));

            await AuthDomainSeeder.SeedOnceAsync(db, Logger());

            Assert.Single(db.AuthDomains);
        }

        [Fact]
        public async Task InactiveSsprDomainsAreNotImported()
        {
            using var db = DbWith(adUser: true, Sspr("switched off", "dc01.corp.local", active: false));

            await AuthDomainSeeder.SeedOnceAsync(db, Logger());

            Assert.Empty(db.AuthDomains);
        }
    }
}
