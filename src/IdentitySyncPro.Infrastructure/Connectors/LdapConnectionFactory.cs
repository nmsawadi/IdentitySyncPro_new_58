using System.DirectoryServices.Protocols;
using System.Net;
using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Models.Connectors;

namespace IdentitySyncPro.Infrastructure.Connectors
{
    /// <summary>
    /// The single place that builds an <see cref="LdapConnection"/>.
    ///
    /// Every module used to roll its own, and they had drifted apart — some enabled SSL but
    /// never sign &amp; seal, one enabled neither, one set the SSL flag without ever turning
    /// TLS on. The visible symptom was always the same: reads worked, password writes died
    /// with WILL_NOT_PERFORM. Funnelling everything through here keeps the channel
    /// consistent (and encrypted) across the product.
    ///
    /// The returned connection is NOT bound — the caller still calls <c>Bind()</c>.
    /// </summary>
    public static class LdapConnectionFactory
    {
        /// <summary>Global catalog / LDAPS ports that imply TLS when the mode is Auto.</summary>
        private const int LdapsPort = 636;
        private const int GlobalCatalogSslPort = 3269;

        /// <summary>
        /// Request timeout applied to every LDAP channel in the product when the caller does not
        /// override it. 0 keeps the .NET default (30s), so an installation that sets nothing
        /// behaves exactly as before.
        ///
        /// Deliberately global rather than per-tenant: every module here talks to the same
        /// directory — sync, SSPR, services, account status, sign-in — and a timeout that only
        /// covers sync would leave SSPR, which also writes passwords, exposed to the same stall.
        /// Set once at startup from <c>SyncSettings:LdapTimeoutSeconds</c>.
        /// </summary>
        public static int DefaultTimeoutSeconds { get; set; }

        /// <summary>
        /// Resolve <see cref="LdapSecurityMode.Auto"/> against the port. Any explicit mode is
        /// returned untouched, so an admin can always override the inference.
        /// </summary>
        public static LdapSecurityMode Resolve(LdapSecurityMode mode, int port)
        {
            if (mode != LdapSecurityMode.Auto) return mode;
            return port is LdapsPort or GlobalCatalogSslPort
                ? LdapSecurityMode.Ldaps
                : LdapSecurityMode.SignAndSeal;
        }

        /// <summary>Human-readable description of what a mode+port combination will actually do.</summary>
        public static string Describe(LdapSecurityMode mode, int port)
        {
            var effective = Resolve(mode, port);
            var suffix = mode == LdapSecurityMode.Auto ? " (auto)" : "";
            return effective switch
            {
                LdapSecurityMode.Ldaps => $"LDAPS/TLS on port {port}{suffix}",
                LdapSecurityMode.StartTls => $"StartTLS on port {port}{suffix}",
                LdapSecurityMode.SignAndSeal => $"Kerberos/NTLM sign & seal on port {port}{suffix}",
                LdapSecurityMode.None => $"UNENCRYPTED on port {port} — password writes will fail{suffix}",
                _ => $"port {port}{suffix}"
            };
        }

        /// <summary>True when the resolved mode encrypts the channel (i.e. password writes can succeed).</summary>
        public static bool IsEncrypted(LdapSecurityMode mode, int port)
            => Resolve(mode, port) != LdapSecurityMode.None;

        /// <summary>Build a configured, unbound connection.</summary>
        public static LdapConnection Create(LdapConnectionOptions options)
        {
            var effective = Resolve(options.SecurityMode, options.Port);

            var connection = new LdapConnection(new LdapDirectoryIdentifier(options.Server, options.Port));

            if (!string.IsNullOrEmpty(options.Username))
                connection.Credential = new NetworkCredential(options.Username, options.Password);

            // Negotiate (Kerberos, falling back to NTLM) is what AD expects and is also what
            // makes sign & seal possible.
            connection.AuthType = AuthType.Negotiate;
            connection.SessionOptions.ProtocolVersion = 3;
            connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;

            // Request timeout. The .NET default is 30 seconds, which turned out to be a thin
            // margin: a production password write that normally takes 273 ms was measured at
            // 21 seconds against a domain controller that could not promptly reach the PDC
            // emulator, and timed out on the next attempt. Raising it does not fix a broken DC —
            // pin a healthy one — but it stops a slow moment from being read as a hard failure.
            var timeout = options.TimeoutSeconds > 0 ? options.TimeoutSeconds : DefaultTimeoutSeconds;
            if (timeout > 0)
                connection.Timeout = TimeSpan.FromSeconds(timeout);

            // Must be installed before any TLS handshake happens.
            if (options.AllowUntrustedCertificate &&
                effective is LdapSecurityMode.Ldaps or LdapSecurityMode.StartTls)
            {
                connection.SessionOptions.VerifyServerCertificate = (_, _) => true;
            }

            switch (effective)
            {
                case LdapSecurityMode.Ldaps:
                    // TLS from connect. Sealing must NOT be combined with it.
                    connection.SessionOptions.SecureSocketLayer = true;
                    break;

                case LdapSecurityMode.StartTls:
                    // Plain connect, then upgrade in place. Throws if the DC has no usable cert.
                    connection.SessionOptions.StartTransportLayerSecurity(null);
                    break;

                case LdapSecurityMode.SignAndSeal:
                    connection.SessionOptions.Sealing = true;
                    connection.SessionOptions.Signing = true;
                    break;

                case LdapSecurityMode.None:
                    // Deliberately nothing — caller opted out of encryption.
                    break;
            }

            return connection;
        }

        /// <summary>Convenience overload for the legacy <c>UseSsl</c> boolean call sites.</summary>
        public static LdapConnection Create(string server, int port, string? username, string? password, bool useSsl)
            => Create(new LdapConnectionOptions
            {
                Server = server,
                Port = port,
                Username = username,
                Password = password,
                SecurityMode = LdapConnectionOptions.FromUseSsl(useSsl)
            });
    }
}
