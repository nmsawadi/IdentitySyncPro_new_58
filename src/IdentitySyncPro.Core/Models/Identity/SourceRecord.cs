using System.Security.Cryptography;
using System.Text;

namespace IdentitySyncPro.Core.Models.Identity
{
    /// <summary>
    /// A single identity row read from the source view — fully dynamic.
    /// ALL columns are carried by their real names in <see cref="Values"/>;
    /// nothing about the source schema is hardcoded. The engine only needs
    /// two well-known values, extracted using per-tenant column settings:
    ///   Key        — the numeric identifier (SourceKeyColumn)
    ///   StatusCode — the numeric lifecycle status (SourceStatusColumn)
    /// Everything else flows through MappingEngine using the tenant's
    /// attribute mappings, group rules, and OU rules.
    /// </summary>
    public class SourceRecord
    {
        /// <summary>Numeric identifier extracted from the tenant's key column.</summary>
        public int Key { get; set; }

        /// <summary>Numeric status code extracted from the tenant's status column (0 when absent).</summary>
        public int StatusCode { get; set; }

        /// <summary>Optional status description extracted from the tenant's status-description column.</summary>
        public string? StatusDesc { get; set; }

        /// <summary>All source columns by their real names (case-insensitive).</summary>
        public Dictionary<string, object?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        // === Runtime Properties (not persisted, used during AD operations) ===
        public string? Password { get; set; }

        /// <summary>Gets a column value as a trimmed string, or null when absent/empty.</summary>
        public string? GetString(string? column)
        {
            if (string.IsNullOrWhiteSpace(column)) return null;
            if (!Values.TryGetValue(column, out var val) || val == null || val == DBNull.Value) return null;
            var s = val.ToString()?.Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        /// <summary>The raw row for MappingEngine (ApplyMappings / ResolveOU / ResolveGroups).</summary>
        public Dictionary<string, object?> ToDictionary() => Values;

        /// <summary>
        /// SHA256 hash over all columns (ordered by name) for change detection.
        /// Note: adding/removing columns in the source view changes hashes and
        /// triggers a one-time re-check of all records on the next sync.
        /// </summary>
        public string ComputeHash()
        {
            var sb = new StringBuilder();
            foreach (var kvp in Values.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(kvp.Key).Append('=');
                if (kvp.Value != null && kvp.Value != DBNull.Value)
                    sb.Append(kvp.Value.ToString());
                sb.Append('|');
            }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            var hashBytes = SHA256.HashData(bytes);
            return Convert.ToBase64String(hashBytes);
        }

        /// <summary>
        /// Fills all empty/null values with the specified default value.
        /// Used when GlobalDefaultValue is enabled to prevent empty fields from causing sync failures.
        /// </summary>
        public void ApplyGlobalDefaults(string defaultValue)
        {
            foreach (var key in Values.Keys.ToList())
            {
                var v = Values[key];
                if (v == null || v == DBNull.Value || (v is string s && string.IsNullOrWhiteSpace(s)))
                    Values[key] = defaultValue;
            }
        }
    }
}
