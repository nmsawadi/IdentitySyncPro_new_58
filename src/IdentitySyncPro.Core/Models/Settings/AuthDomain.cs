using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Models.Connectors;

namespace IdentitySyncPro.Core.Models.Settings
{
    /// <summary>
    /// One Active Directory domain that console users may sign in against.
    ///
    /// Why this module owns its own domains: console sign-in used to borrow whatever AD connection
    /// happened to exist elsewhere — an active sync tenant's, or an SSPR domain's. That made who
    /// can log in a side effect of unrelated settings: deactivating a tenant, or removing an SSPR
    /// domain, silently changed which directories the console would accept. Every other module that
    /// talks to a directory carries its own connection; this one now does too.
    ///
    /// Note what is deliberately absent: no service account and no Base DN. Console sign-in binds
    /// as the person signing in — their own username and password — so it needs the server, the
    /// port and the channel, and nothing else. A stored credential here would be a secret that
    /// grants nothing, and a Base DN would be a field that no code reads.
    /// </summary>
    public class AuthDomain
    {
        public int Id { get; set; }

        /// <summary>Display name, e.g. "corp.local — الموظفون".</summary>
        public string Name { get; set; } = string.Empty;

        public string AdServer { get; set; } = string.Empty;
        public int AdPort { get; set; } = 389;

        /// <summary>Legacy switch — only consulted while <see cref="AdSecurityModeSet"/> is false.</summary>
        public bool AdUseSsl { get; set; } = false;

        /// <summary>Explicit LDAP channel (overrides <see cref="AdUseSsl"/> once an admin picks one).</summary>
        public LdapSecurityMode AdSecurityMode { get; set; } = LdapSecurityMode.Auto;

        /// <summary>True once an admin chose a mode — separates a real "Auto" from a pre-upgrade row.</summary>
        public bool AdSecurityModeSet { get; set; } = false;

        /// <summary>Accept an internal-CA / self-signed LDAPS certificate.</summary>
        public bool AdAllowUntrustedCertificate { get; set; } = false;

        /// <summary>
        /// Order the domains are tried in. A user who exists in two domains signs in against the
        /// first that accepts them, so the order is a decision worth being able to make.
        /// </summary>
        public int SortOrder { get; set; } = 0;

        public bool IsActive { get; set; } = true;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Project onto the shared LDAP options. Credentials are supplied per sign-in.</summary>
        public LdapConnectionOptions ToLdapOptions() => new()
        {
            Server = AdServer,
            Port = AdPort,
            SecurityMode = AdSecurityModeSet ? AdSecurityMode : LdapConnectionOptions.FromUseSsl(AdUseSsl),
            AllowUntrustedCertificate = AdAllowUntrustedCertificate
        };
    }
}
