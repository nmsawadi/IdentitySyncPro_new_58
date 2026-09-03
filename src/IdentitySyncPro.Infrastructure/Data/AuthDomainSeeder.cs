using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Connectors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Data
{
    /// <summary>
    /// Carries the console's existing sign-in reach into its own <see cref="AuthDomain"/> table,
    /// exactly once, when the system is upgraded.
    ///
    /// Before this table existed, console sign-in bound against whatever AD connection it could
    /// find — active sync tenants first, then SSPR domains. Switching to a table of its own with
    /// nothing in it would lock out every AD user on the first deploy, including the administrators
    /// needed to fix it. So the connections that were working are copied in once and become
    /// ordinary editable rows; from then on the two are unrelated.
    ///
    /// "Exactly once" is the whole point of the marker. Re-seeding whenever the table looks empty
    /// would quietly resurrect domains an administrator had deliberately deleted — the module would
    /// still be taking orders from tenants and SSPR, which is the coupling being removed.
    /// </summary>
    public static class AuthDomainSeeder
    {
        internal const string SeededKey = "AuthDomains.SeededFromLegacySources";

        public static async Task SeedOnceAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
        {
            if (await db.AppSettings.AnyAsync(s => s.Key == SeededKey, ct))
                return;

            // Nothing to preserve if nobody signs in with a domain account. The marker is still
            // written, so enabling AD for a user later opens an empty screen to fill in rather than
            // inheriting connections chosen for another purpose.
            var hasAdUsers = await db.AppUsers.AnyAsync(u => u.AuthType == AppUserAuthTypes.ActiveDirectory, ct);
            var alreadyConfigured = await db.AuthDomains.AnyAsync(ct);

            if (hasAdUsers && !alreadyConfigured)
            {
                foreach (var (name, opts) in await LegacySourcesAsync(db, ct))
                {
                    db.AuthDomains.Add(new AuthDomain
                    {
                        Name = name,
                        AdServer = opts.Server ?? string.Empty,
                        AdPort = opts.Port,
                        AdSecurityMode = opts.SecurityMode,
                        AdSecurityModeSet = true,   // the mode is inherited, not defaulted
                        AdUseSsl = opts.SecurityMode == LdapSecurityMode.Ldaps,
                        AdAllowUntrustedCertificate = opts.AllowUntrustedCertificate,
                        SortOrder = db.AuthDomains.Local.Count,
                        IsActive = true
                    });
                }

                if (db.AuthDomains.Local.Count > 0)
                    logger.LogWarning(
                        "Console sign-in now has its own domain list. Imported {Count} connection(s) that AD sign-in " +
                        "was previously borrowing, so existing AD users keep working. Review them under Users → " +
                        "sign-in domains; they are no longer linked to the tenants or SSPR domains they came from.",
                        db.AuthDomains.Local.Count);
                else
                    logger.LogWarning(
                        "AD console users exist but no usable AD connection was found to import. " +
                        "Add a sign-in domain under Users → sign-in domains, or those users cannot sign in.");
            }

            db.AppSettings.Add(new AppSettings { Key = SeededKey, Value = DateTime.UtcNow.ToString("O") });
            await db.SaveChangesAsync(ct);
        }

        /// <summary>
        /// Where console sign-in used to look, in the order it used to look: active tenants with a
        /// real AD server, then active SSPR domains.
        /// </summary>
        private static async Task<List<(string Name, Core.Models.Connectors.LdapConnectionOptions Opts)>>
            LegacySourcesAsync(AppDbContext db, CancellationToken ct)
        {
            var list = new List<(string, Core.Models.Connectors.LdapConnectionOptions)>();

            foreach (var t in await db.TenantSettings.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.Id).ToListAsync(ct))
                if (IsRealServer(t.ADServer))
                    list.Add(($"{t.TenantName}", SourceConnectorFactory.BuildADSettings(t).ToLdapOptions()));

            foreach (var d in await db.SsprDomains.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Id).ToListAsync(ct))
                if (IsRealServer(d.AdServer))
                    list.Add((d.Name, d.ToLdapOptions()));

            // The same server reached two ways is one domain to a person signing in.
            return list
                .GroupBy(x => $"{x.Item2.Server}:{x.Item2.Port}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>
        /// Seeded rows and template placeholders look alike in the database. Importing
        /// "YOUR_AD_SERVER" would produce a domain that fails every bind and explains nothing.
        /// </summary>
        internal static bool IsRealServer(string? server) =>
            !string.IsNullOrWhiteSpace(server) &&
            !server.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase) &&
            !server.Contains("example.local", StringComparison.OrdinalIgnoreCase);
    }
}
