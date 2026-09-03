using System.DirectoryServices.Protocols;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// The AD accounts IdentitySyncPro itself binds with, resolved to distinguished names.
    ///
    /// These are the system's own non-human identities: the credentials every sync, every SSPR
    /// reset, and every AD login depends on. They are also the accounts a report will find and a
    /// future action could act on — disabling one stops the product at an hour nobody connects to
    /// the run that did it.
    ///
    /// The list is derived from the settings on every run rather than maintained by hand. A second
    /// list kept in parallel drifts the first time a tenant is added and nobody updates it, and
    /// nothing announces the drift until the day it matters. The manual exclusion group stays
    /// alongside this and is not replaced by it: it covers break-glass accounts and other systems'
    /// service accounts, which the settings cannot know about.
    ///
    /// <para><b>For a read-only report</b> this is a marker: the accounts belong in the inventory,
    /// and hiding them would make the number wrong. <b>Any service that disables, moves, or
    /// quarantines accounts must instead treat <see cref="SelfAccounts.Unresolved"/> as fatal</b> —
    /// an entry from this directory that could not be resolved is an account you cannot prove is
    /// not the one you are about to act on.</para>
    /// </summary>
    public class SelfAccountRegistry
    {
        private const int LdapPageSize = 500;

        private readonly ILogger<SelfAccountRegistry> _logger;

        public SelfAccountRegistry(ILogger<SelfAccountRegistry> logger) => _logger = logger;

        /// <summary>One configured bind credential and what became of it.</summary>
        /// <param name="Source">Where it is configured, for a message an operator can act on.</param>
        /// <param name="Configured">The credential as stored — never a password, only the account name.</param>
        /// <param name="Dn">Its distinguished name, or null when it was not resolved.</param>
        /// <param name="Problem">Why it was not resolved, or null on success.</param>
        /// <param name="ForeignDirectory">True when it belongs to another directory and was deliberately not looked up.</param>
        public sealed record SelfAccount(
            string Source, string Configured, string? Dn, string? Problem, bool ForeignDirectory);

        public sealed record SelfAccounts(IReadOnlyList<SelfAccount> All)
        {
            /// <summary>DNs to recognise, matched case-insensitively as AD returns them inconsistently cased.</summary>
            public HashSet<string> Dns { get; } =
                new(All.Where(a => a.Dn != null).Select(a => a.Dn!), StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Entries from the scanned directory that could not be resolved. Cosmetic for a
            /// report; fatal for anything that acts on accounts.
            /// </summary>
            public IReadOnlyList<SelfAccount> Unresolved =>
                All.Where(a => a.Dn == null && !a.ForeignDirectory).ToList();
        }

        /// <summary>
        /// Collects every configured bind account and resolves the ones belonging to the directory
        /// under <paramref name="scanBase"/>.
        /// </summary>
        public SelfAccounts Resolve(IServiceProvider services, LdapConnection ldap, string scanBase,
            CancellationToken ct = default)
        {
            var found = new List<SelfAccount>();

            foreach (var (source, username, baseDn) in Collect(services))
            {
                ct.ThrowIfCancellationRequested();

                var parsed = BindIdentity.Parse(username);
                if (parsed == null) continue;   // integrated security / no stored bind account

                if (!BindIdentity.SameDirectory(baseDn, scanBase))
                {
                    found.Add(new SelfAccount(source, username!, null, "configured against another directory", true));
                    continue;
                }

                try
                {
                    var dn = ResolveDn(ldap, parsed, scanBase);
                    found.Add(dn == null
                        ? new SelfAccount(source, username!, null, "not found in the scanned scope", false)
                        : new SelfAccount(source, username!, dn, null, false));
                }
                catch (Exception ex)
                {
                    found.Add(new SelfAccount(source, username!, null, ex.Message, false));
                }
            }

            // Two settings pointing at the same account is normal — the same service account often
            // serves a tenant and an SSPR domain — so the log reports distinct accounts, not rows.
            _logger.LogInformation(
                "SelfAccountRegistry: {Resolved} of {Total} configured bind account(s) resolved under {Scope} ({Foreign} in other directories, {Failed} unresolved)",
                found.Count(a => a.Dn != null), found.Count, scanBase,
                found.Count(a => a.ForeignDirectory), found.Count(a => a.Dn == null && !a.ForeignDirectory));

            foreach (var bad in found.Where(a => a.Dn == null && !a.ForeignDirectory))
                _logger.LogWarning("SelfAccountRegistry: {Source} bind account '{Account}' — {Problem}",
                    bad.Source, bad.Configured, bad.Problem);

            return new SelfAccounts(found);
        }

        /// <summary>
        /// Every place a bind credential is stored, across the three databases that hold one.
        ///
        /// A context that is not registered yields a row explaining its absence rather than being
        /// skipped: fewer sources means a weaker guard, and a guard that quietly covers less than
        /// it claims is the thing this class exists to prevent.
        /// </summary>
        internal IEnumerable<(string Source, string? Username, string? BaseDn)> Collect(IServiceProvider services)
        {
            foreach (var row in FromContext<ServicesDbContext>(services, "Services module",
                         db => db.SvcServices.AsNoTracking()
                                 .Select(s => new { s.Name, s.ADUsername, BaseDn = s.ADBaseDN })
                                 .ToList()
                                 .Select(s => ($"service '{s.Name}'", s.ADUsername, s.BaseDn))))
                yield return row;

            foreach (var row in FromContext<AppDbContext>(services, "Tenants / SSPR",
                         db =>
                         {
                             var tenants = db.TenantSettings.AsNoTracking()
                                 .Select(t => new { t.TenantName, t.ADUsername, BaseDn = t.ADBaseDN })
                                 .ToList()
                                 .Select(t => ($"tenant '{t.TenantName}'", (string?)t.ADUsername, (string?)t.BaseDn));

                             var sspr = db.SsprDomains.AsNoTracking()
                                 .Select(d => new { d.Name, d.AdUsername, BaseDn = d.AdBaseDN })
                                 .ToList()
                                 .Select(d => ($"SSPR domain '{d.Name}'", d.AdUsername, (string?)d.BaseDn));

                             return tenants.Concat(sspr).ToList();
                         }))
                yield return row;

            foreach (var row in FromContext<AccountStatusDbContext>(services, "Account status domains",
                         db => db.CustomDomains.AsNoTracking()
                                 .Select(d => new { d.DisplayName, d.Username, d.BaseDN })
                                 .ToList()
                                 .Select(d => ($"status domain '{d.DisplayName}'", d.Username, (string?)d.BaseDN))))
                yield return row;
        }

        private IEnumerable<(string, string?, string?)> FromContext<TContext>(
            IServiceProvider services, string label,
            Func<TContext, IEnumerable<(string, string?, string?)>> read) where TContext : DbContext
        {
            var db = services.GetService<TContext>();
            if (db == null)
            {
                _logger.LogWarning("SelfAccountRegistry: {Label} is not available in this host — its bind accounts are not covered", label);
                return new[] { ($"{label} (unavailable)", (string?)null, (string?)null) };
            }

            try
            {
                return read(db).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SelfAccountRegistry: could not read {Label}", label);
                return new[] { ($"{label} (unreadable: {ex.Message})", (string?)null, (string?)null) };
            }
        }

        /// <summary>Looks the identity up, or confirms a stored DN still exists rather than trusting it.</summary>
        private static string? ResolveDn(LdapConnection ldap, BindIdentity.Parsed parsed, string scanBase)
        {
            if (parsed.Kind == BindIdentity.Kind.DistinguishedName)
            {
                var check = (SearchResponse)ldap.SendRequest(
                    new SearchRequest(parsed.Value, "(objectClass=*)", SearchScope.Base, "distinguishedName"));
                return check.Entries.Count > 0 ? check.Entries[0].DistinguishedName : null;
            }

            var request = new SearchRequest(scanBase, BindIdentity.BuildFilter(parsed), SearchScope.Subtree, "distinguishedName");
            request.Controls.Add(new PageResultRequestControl(LdapPageSize));
            var response = (SearchResponse)ldap.SendRequest(request);
            return response.Entries.Count > 0 ? response.Entries[0].DistinguishedName : null;
        }
    }
}
