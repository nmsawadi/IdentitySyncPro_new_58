using System.Text.RegularExpressions;

namespace IdentitySyncPro.Core.Helpers
{
    /// <summary>
    /// Validates a table or view name before it is placed into SQL text.
    ///
    /// Object names cannot be passed as parameters, so column discovery has to build the statement
    /// as a string. That made the source table name — which arrives in a request body — a direct
    /// injection point:
    ///
    ///   SELECT TOP 0 * FROM [{name}]           a "]" in the name closes the bracket early
    ///   SELECT * FROM {name} WHERE ROWNUM = 0  no quoting at all
    ///
    /// The endpoint is Admin-only, so this is not privilege escalation. It is still arbitrary SQL
    /// against the source database from a single request field, and an admin session obtained any
    /// other way inherits it.
    ///
    /// The answer is a whitelist, not escaping: anything that is not plainly an identifier is
    /// refused. Legitimate names are letters, digits and underscores, optionally schema-qualified —
    /// no spaces, quotes, brackets, semicolons or comment markers, so nothing that could terminate
    /// the identifier or the statement survives validation.
    /// </summary>
    public static class SqlIdentifierGuard
    {
        // Deliberately narrower than what SQL Server and Oracle actually allow. A view named with
        // spaces or punctuation would be rejected here — the tenant can rename it or the pattern can
        // be widened knowingly. Guessing wider by default is how a guard stops guarding.
        private static readonly Regex Identifier =
            new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private const int MaxLength = 128; // SQL Server sysname; Oracle 12.2+ allows 128 as well.

        /// <summary>
        /// True when <paramref name="name"/> is safe to place into SQL text as an object name.
        /// </summary>
        public static bool IsValidObjectName(string? name) =>
            !string.IsNullOrWhiteSpace(name)
            && name.Length <= MaxLength
            && Identifier.IsMatch(name);

        /// <summary>
        /// Returns the name bracket-quoted for SQL Server, or throws when it is not a valid
        /// identifier. Schema-qualified names are quoted part by part, because "[dbo.Users]" would
        /// name a single object literally called "dbo.Users" rather than Users in dbo.
        /// </summary>
        public static string QuoteSqlServer(string? name)
        {
            if (!IsValidObjectName(name))
                throw new ArgumentException($"'{name}' is not a valid table or view name.", nameof(name));

            return string.Join(".", name!.Split('.').Select(part => "[" + part + "]"));
        }

        /// <summary>
        /// Returns the name for Oracle, or throws when it is not a valid identifier. Left unquoted:
        /// Oracle folds unquoted identifiers to upper case, and quoting here would make an existing
        /// lower-case-named view unreachable. Validation is what makes it safe, not quoting.
        /// </summary>
        public static string ForOracle(string? name)
        {
            if (!IsValidObjectName(name))
                throw new ArgumentException($"'{name}' is not a valid table or view name.", nameof(name));

            return name!;
        }
    }
}
