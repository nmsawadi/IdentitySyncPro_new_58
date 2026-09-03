using System.Collections;
using System.Reflection;
using System.Text;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Turns the arguments of a console request into the one-line summary stored on the audit
    /// entry — with anything secret replaced before it is written.
    ///
    /// It lives here rather than beside the MVC filter that calls it for one reason: this is the
    /// code that decides whether a password reaches the database, so it has to be unit-testable
    /// without standing up the web application.
    ///
    /// The rule is deliberately the pessimistic one. Names are matched as substrings, so
    /// <c>adPassword</c>, <c>newPassword</c> and <c>testPassword</c> are all covered without
    /// anybody maintaining a list per screen — a denylist needing an entry per field leaks the
    /// first time someone names a field something new.
    /// </summary>
    public static class AuditArgumentRedactor
    {
        public const string Redacted = "***";
        public const string NoArguments = "(no arguments)";

        private const int MaxValueLength = 200;
        private const int MaxTotalLength = 1800;   // AuditEntry.Details is nvarchar(2000)

        private static readonly string[] SecretMarkers =
        {
            "password", "pwd", "secret", "token", "apikey", "api_key", "credential", "otp", "hash"
        };

        /// <summary>True when a parameter or property of this name must never be written.</summary>
        public static bool IsSecret(string name) =>
            SecretMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));

        public static string Describe(IDictionary<string, object?> arguments, string? error = null)
        {
            var sb = new StringBuilder();

            foreach (var (name, value) in arguments)
            {
                if (sb.Length > MaxTotalLength) { sb.Append(", …"); break; }
                if (sb.Length > 0) sb.Append(", ");
                Append(sb, name, value);
            }

            if (!string.IsNullOrWhiteSpace(error))
                sb.Append(sb.Length > 0 ? " | " : "").Append("FAILED: ").Append(Truncate(error!));

            return sb.Length == 0 ? NoArguments : sb.ToString();
        }

        private static void Append(StringBuilder sb, string name, object? value)
        {
            if (IsSecret(name)) { sb.Append(name).Append('=').Append(Redacted); return; }
            if (value == null) { sb.Append(name).Append("=null"); return; }

            var type = value.GetType();
            if (IsSimple(type)) { sb.Append(name).Append('=').Append(Truncate(Convert.ToString(value) ?? "")); return; }

            if (value is IEnumerable list and not string)
            {
                sb.Append(name).Append("=[").Append(list.Cast<object?>().Count()).Append(" item(s)]");
                return;
            }

            AppendObject(sb, name, value, type);
        }

        /// <summary>
        /// One level into a request model — that is where [FromBody] actions carry everything worth
        /// recording. Deeper nesting is not walked: it is where a secret would sit behind a
        /// property name this never inspects.
        /// </summary>
        private static void AppendObject(StringBuilder sb, string name, object value, Type type)
        {
            sb.Append(name).Append("={");
            var first = true;

            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;

                if (!first) sb.Append(", ");
                first = false;

                if (IsSecret(p.Name)) { sb.Append(p.Name).Append('=').Append(Redacted); continue; }

                object? v;
                try { v = p.GetValue(value); }
                catch { sb.Append(p.Name).Append("=?"); continue; }

                if (v == null) sb.Append(p.Name).Append("=null");
                else if (IsSimple(v.GetType())) sb.Append(p.Name).Append('=').Append(Truncate(Convert.ToString(v) ?? ""));
                // A nested object is named, never opened.
                else sb.Append(p.Name).Append('=').Append(v.GetType().Name);
            }

            sb.Append('}');
        }

        private static bool IsSimple(Type t)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)
                   || t == typeof(DateTime) || t == typeof(DateTimeOffset)
                   || t == typeof(Guid) || t == typeof(TimeSpan);
        }

        private static string Truncate(string s) => s.Length <= MaxValueLength ? s : s[..MaxValueLength] + "…";
    }
}
