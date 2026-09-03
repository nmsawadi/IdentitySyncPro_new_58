namespace IdentitySyncPro.Core.Helpers
{
    /// <summary>
    /// Sanitizes input values for use in LDAP search filters.
    /// Prevents LDAP Injection attacks by escaping special characters per RFC 4515.
    /// 
    /// LDAP filter special characters: * ( ) \ NUL /
    /// Each must be escaped as \XX where XX is the hex representation.
    /// </summary>
    public static class LdapSanitizer
    {
        /// <summary>
        /// Escapes special characters in a value before inserting it into an LDAP filter.
        /// Example: EscapeFilterValue("admin*") returns "admin\2a"
        /// 
        /// Usage:
        ///   var filter = $"(sAMAccountName={LdapSanitizer.EscapeFilterValue(username)})";
        /// </summary>
        public static string EscapeFilterValue(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var sb = new System.Text.StringBuilder(value.Length + 10);

            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\':
                        sb.Append("\\5c");
                        break;
                    case '*':
                        sb.Append("\\2a");
                        break;
                    case '(':
                        sb.Append("\\28");
                        break;
                    case ')':
                        sb.Append("\\29");
                        break;
                    case '\0':
                        sb.Append("\\00");
                        break;
                    case '/':
                        sb.Append("\\2f");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Validates that a value is safe for use as a Distinguished Name component.
        /// Returns true if the value contains only alphanumeric characters, spaces, hyphens, and dots.
        /// </summary>
        public static bool IsValidDNComponent(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            foreach (var c in value)
            {
                if (!char.IsLetterOrDigit(c) && c != ' ' && c != '-' && c != '.' && c != '_' && c != '=' && c != ',')
                    return false;
            }

            return true;
        }
    }
}
