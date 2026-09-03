namespace IdentitySyncPro.Core.Enums
{
    /// <summary>
    /// How the LDAP channel to a directory is protected.
    ///
    /// This matters beyond privacy: Active Directory REFUSES password writes
    /// (<c>unicodePwd</c>) over an unencrypted channel with <c>WILL_NOT_PERFORM</c>,
    /// no matter how privileged the service account is. Reads still work unencrypted,
    /// which is why a misconfiguration shows up only when a password is set.
    ///
    /// Stored as int so new modes can be added without breaking existing rows.
    /// </summary>
    public enum LdapSecurityMode
    {
        /// <summary>
        /// Pick from the port: 636/3269 → <see cref="Ldaps"/>, anything else →
        /// <see cref="SignAndSeal"/>. Safe default — always encrypted, never needs a
        /// certificate on the plain port.
        /// </summary>
        Auto = 0,

        /// <summary>
        /// Kerberos/NTLM sign &amp; seal over the plaintext port (389/3268).
        /// Encrypted, needs NO certificate. The usual choice for domain-joined servers.
        /// </summary>
        SignAndSeal = 1,

        /// <summary>
        /// LDAPS — TLS from the first byte, normally port 636 (or 3269 for the
        /// global catalog). Requires a valid certificate on the domain controller.
        /// </summary>
        Ldaps = 2,

        /// <summary>
        /// Connect in plaintext (normally 389) then upgrade to TLS via StartTLS.
        /// Requires a certificate on the DC. Used by organisations that mandate TLS
        /// but keep the standard port.
        /// </summary>
        StartTls = 3,

        /// <summary>
        /// No encryption at all. Reads and binds work, but any password write FAILS.
        /// Only for diagnostics or directories that cannot do better — never for SSPR
        /// or account creation.
        /// </summary>
        None = 4
    }
}
