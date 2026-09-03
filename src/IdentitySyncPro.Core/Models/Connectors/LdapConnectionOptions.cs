using IdentitySyncPro.Core.Enums;

namespace IdentitySyncPro.Core.Models.Connectors
{
    /// <summary>
    /// Everything needed to open an LDAP channel, shared by every module that talks to a
    /// directory (sync connector, SSPR, services, account status, AD sign-in) so the
    /// channel behaves identically everywhere.
    /// </summary>
    public class LdapConnectionOptions
    {
        public string Server { get; set; } = string.Empty;
        public int Port { get; set; } = 389;

        /// <summary>Service account. Empty = bind as the process identity.</summary>
        public string? Username { get; set; }
        public string? Password { get; set; }

        /// <summary>How the channel is protected. <see cref="LdapSecurityMode.Auto"/> infers from the port.</summary>
        public LdapSecurityMode SecurityMode { get; set; } = LdapSecurityMode.Auto;

        /// <summary>
        /// Accept a server certificate that doesn't chain to a trusted root (internal CA /
        /// self-signed). Only consulted for <see cref="LdapSecurityMode.Ldaps"/> and
        /// <see cref="LdapSecurityMode.StartTls"/>.
        ///
        /// ⚠️ Disables certificate validation, so the channel is encrypted but no longer
        /// proves who is on the other end (man-in-the-middle becomes possible). Prefer
        /// trusting the organisation's CA on the server instead.
        /// </summary>
        public bool AllowUntrustedCertificate { get; set; } = false;

        /// <summary>
        /// Per-request timeout in seconds. Zero or less leaves the .NET default (30s) in place,
        /// so no existing installation changes behaviour unless it opts in.
        ///
        /// Worth raising only where a directory is known to be slow under load: a password write
        /// that hangs is nearly always a domain controller that cannot reach the PDC emulator,
        /// and the real fix is to target a healthy one rather than to wait longer.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 0;

        /// <summary>
        /// Translate the legacy <c>UseSsl</c> boolean into a mode, preserving old behaviour:
        /// true → LDAPS, false → sign &amp; seal.
        /// </summary>
        public static LdapSecurityMode FromUseSsl(bool useSsl)
            => useSsl ? LdapSecurityMode.Ldaps : LdapSecurityMode.SignAndSeal;
    }
}
