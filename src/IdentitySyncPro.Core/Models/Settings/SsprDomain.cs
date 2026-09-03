using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Models.Connectors;

namespace IdentitySyncPro.Core.Models.Settings
{
    /// <summary>
    /// One Active Directory domain served by the Self-Service Password Reset
    /// portal. Each domain carries its own AD connection plus the AD attributes
    /// that hold the user's national ID and mobile number (names differ per org).
    /// </summary>
    public class SsprDomain
    {
        public int Id { get; set; }

        /// <summary>Display name (e.g. "موظفو الوزارة - corp.gov.sa").</summary>
        public string Name { get; set; } = string.Empty;

        // === AD connection (own service account with reset rights) ===
        public string AdServer { get; set; } = string.Empty;
        public int AdPort { get; set; } = 389;

        /// <summary>Legacy switch — only used when <see cref="AdSecurityModeSet"/> is false.</summary>
        public bool AdUseSsl { get; set; } = false;

        /// <summary>
        /// Explicit LDAP channel mode (overrides <see cref="AdUseSsl"/> once chosen).
        /// SSPR writes passwords, so the channel MUST be encrypted — an unencrypted one
        /// fails with WILL_NOT_PERFORM however privileged the service account is.
        /// </summary>
        public LdapSecurityMode AdSecurityMode { get; set; } = LdapSecurityMode.Auto;

        /// <summary>True once an admin picked a mode — distinguishes a real "Auto" from a pre-upgrade row.</summary>
        public bool AdSecurityModeSet { get; set; } = false;

        /// <summary>Accept an internal-CA / self-signed LDAPS certificate.</summary>
        public bool AdAllowUntrustedCertificate { get; set; } = false;

        public string? AdUsername { get; set; }
        public string? AdPassword { get; set; }   // encrypted at rest
        public string AdBaseDN { get; set; } = string.Empty;

        /// <summary>Project the AD fields onto the shared LDAP options.</summary>
        public LdapConnectionOptions ToLdapOptions() => new()
        {
            Server = AdServer,
            Port = AdPort,
            Username = AdUsername,
            Password = AdPassword,
            SecurityMode = AdSecurityModeSet ? AdSecurityMode : LdapConnectionOptions.FromUseSsl(AdUseSsl),
            AllowUntrustedCertificate = AdAllowUntrustedCertificate
        };

        // === Verification attributes (as stored in THIS domain's AD) ===
        /// <summary>AD attribute holding the national ID (e.g. employeeNumber, extensionAttribute1).</summary>
        public string NationalIdAttribute { get; set; } = "employeeNumber";

        /// <summary>AD attribute holding the mobile number the OTP is sent to (e.g. mobile, telephoneNumber).</summary>
        public string MobileAttribute { get; set; } = "mobile";

        /// <summary>Comma-separated AD groups DENIED self-service reset (admins, service accounts...).</summary>
        public string? ExcludedGroups { get; set; }

        public bool IsActive { get; set; } = true;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }
}
