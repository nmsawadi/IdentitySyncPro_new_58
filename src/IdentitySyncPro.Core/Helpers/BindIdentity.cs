namespace IdentitySyncPro.Core.Helpers
{
    /// <summary>
    /// Turns a stored AD bind credential into something that can be looked up in the directory.
    ///
    /// The settings store whatever an administrator typed — the field's placeholder says
    /// <c>domain\admin</c>, and <c>AuthType.Negotiate</c> accepts three more shapes besides — while
    /// everything read out of the directory is keyed by distinguished name. So the two can never be
    /// compared as strings: a configured bind account has to be resolved before it can be
    /// recognised among the accounts a report just found.
    ///
    /// The parsing lives here, apart from the LDAP call, because it is the half that can be tested:
    /// a domain prefix dropped from the wrong side, or a UPN searched as a sAMAccountName, produces
    /// no match and no error — the account simply goes unrecognised.
    /// </summary>
    public static class BindIdentity
    {
        /// <summary>How a stored credential should be looked up.</summary>
        public enum Kind
        {
            /// <summary>Already a distinguished name — use it directly, but confirm it exists.</summary>
            DistinguishedName,
            /// <summary>A user principal name (user@domain); may also be stored as the account name.</summary>
            UserPrincipalName,
            /// <summary>A bare or domain-qualified account name.</summary>
            AccountName
        }

        public sealed record Parsed(Kind Kind, string Value);

        /// <summary>
        /// Reduces a stored credential to the form it should be searched by.
        ///
        /// <c>NJRAN\svc_sync</c> keeps the part after the backslash, not before it — a NetBIOS
        /// prefix searched as a sAMAccountName matches nothing at all.
        /// </summary>
        public static Parsed? Parse(string? configured)
        {
            var v = configured?.Trim();
            if (string.IsNullOrEmpty(v)) return null;

            // A DN is recognised before anything else: it can legitimately contain both '\' (as an
            // RDN escape) and '@' (inside a CN), so testing for those first would mis-split it.
            if (LooksLikeDn(v)) return new Parsed(Kind.DistinguishedName, v);

            var slash = v.LastIndexOf('\\');
            if (slash >= 0)
            {
                var account = v[(slash + 1)..].Trim();
                return account.Length == 0 ? null : new Parsed(Kind.AccountName, account);
            }

            return v.Contains('@')
                ? new Parsed(Kind.UserPrincipalName, v)
                : new Parsed(Kind.AccountName, v);
        }

        /// <summary>A DN is the one form that carries its own location, and the only one that needs no search.</summary>
        public static bool LooksLikeDn(string value) =>
            value.Contains('=') && value.Contains(',') &&
            value.TrimStart().StartsWith("CN=", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The LDAP filter that finds this identity, or null for a DN (which is read directly).
        ///
        /// A UPN is searched by both userPrincipalName and sAMAccountName: the two are frequently
        /// equal in the local part, and an account whose UPN suffix was changed after the setting
        /// was saved would otherwise stop being recognised.
        /// </summary>
        public static string? BuildFilter(Parsed parsed) => parsed.Kind switch
        {
            Kind.DistinguishedName => null,
            Kind.UserPrincipalName => BuildUpnFilter(parsed.Value),
            _ => $"(&(objectClass=user)(sAMAccountName={LdapSanitizer.EscapeFilterValue(parsed.Value)}))"
        };

        private static string BuildUpnFilter(string upn)
        {
            var local = upn.Split('@')[0];
            return "(&(objectClass=user)(|" +
                   $"(userPrincipalName={LdapSanitizer.EscapeFilterValue(upn)})" +
                   $"(sAMAccountName={LdapSanitizer.EscapeFilterValue(local)})))";
        }

        /// <summary>
        /// Whether a configured connection points at the same directory as the scope being scanned,
        /// judged by their shared DC= suffix.
        ///
        /// This is what keeps a multi-tenant installation working. A tenant bound to another domain
        /// can never resolve inside this one, so demanding that it resolve would stop every run on
        /// any installation with more than one directory — while treating an unresolved account
        /// from *this* directory as harmless would hollow out the guard. The suffix decides which
        /// of the two an entry is.
        /// </summary>
        public static bool SameDirectory(string? configuredBaseDn, string? scanBase)
        {
            var a = DomainSuffix(configuredBaseDn);
            var b = DomainSuffix(scanBase);
            // An unknown suffix on either side is treated as "same": the entry then has to resolve,
            // which is the cautious reading — a missing BaseDN is not evidence of a foreign domain.
            if (a.Length == 0 || b.Length == 0) return true;
            return a.EndsWith(b, StringComparison.Ordinal) || b.EndsWith(a, StringComparison.Ordinal);
        }

        /// <summary>The DC= components of a DN, normalised — "OU=Svc, DC=njran, DC=edu" becomes "dc=njran,dc=edu".</summary>
        public static string DomainSuffix(string? dn)
        {
            if (string.IsNullOrWhiteSpace(dn)) return string.Empty;
            var parts = dn.Split(',')
                          .Select(p => p.Trim())
                          .Where(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
                          .Select(p => p.ToLowerInvariant());
            return string.Join(",", parts);
        }
    }
}
