using System.Text;
using System.Text.RegularExpressions;

namespace IdentitySyncPro.Core.Helpers
{
    /// <summary>
    /// Decides what counts as a non-human account (service account, bot, workload identity) and
    /// how risky each one is.
    ///
    /// Every rule here is configuration, never code: "svc_*" is one site's convention, "sa-" is
    /// another's, and a third tags them with an extension attribute instead. A naming convention
    /// compiled into this file would make the report wrong at the next installation while still
    /// producing a confident-looking number.
    ///
    /// The logic lives in Core rather than in the executor because the executor cannot run without
    /// a directory, and the parts worth guarding — the empty-classifier guard, the wildcard
    /// escaping, the Any/All combination — are exactly the parts that fail silently.
    /// </summary>
    public static class NonHumanClassifier
    {
        public const string ModeAny = "Any";
        public const string ModeAll = "All";

        /// <summary>An attribute name is interpolated into an LDAP filter as a name, not a value, so it cannot be escaped into safety — it has to be rejected instead.</summary>
        private static readonly Regex AttributeNameShape = new(@"^[A-Za-z][A-Za-z0-9\-]*$", RegexOptions.Compiled);

        // The two object classes that are non-human by definition rather than by local convention.
        public const string GmsaClass = "msDS-GroupManagedServiceAccount";
        public const string MsaClass = "msDS-ManagedServiceAccount";

        /// <summary>The configured classifier, parsed once per run.</summary>
        public sealed record Signals(
            string[] NamePatterns,
            string[] OrganizationalUnits,
            string[] Groups,
            (string Attr, string Value)[] AttributeRules,
            bool NoKeyAttribute,
            bool PasswordNeverExpires,
            bool HasServicePrincipalName,
            bool IncludeManagedServiceAccounts,
            string Mode)
        {
            public bool RequiresAll => string.Equals(Mode, ModeAll, StringComparison.OrdinalIgnoreCase);

            /// <summary>How many directory-matched conditions are configured. Managed service accounts are not counted: they join the result by definition, not by a rule.</summary>
            public int SelectorCount =>
                NamePatterns.Length + OrganizationalUnits.Length + Groups.Length + AttributeRules.Length
                + (NoKeyAttribute ? 1 : 0) + (PasswordNeverExpires ? 1 : 0) + (HasServicePrincipalName ? 1 : 0);
        }

        /// <summary>
        /// Rejects a classifier that cannot mean what it appears to mean.
        ///
        /// Both empty cases are silent failures pointing in opposite directions, and both would
        /// otherwise produce a completed run carrying a plausible number. Under "Any", no
        /// condition matches nothing and the report reads as "this domain has no service
        /// accounts" — the most dangerous wrong answer this feature can give. Under "All", no
        /// condition matches every account in scope and the report reads as "everything here is
        /// non-human".
        /// </summary>
        /// <returns>null when the configuration is usable, otherwise the reason it is not.</returns>
        public static string? Validate(Signals s)
        {
            if (s.SelectorCount > 0) return null;

            if (s.RequiresAll)
                return "No classifier rule is configured and the match mode is \"All\" — an empty set of "
                     + "conditions would match every account in scope. Configure at least one rule.";

            return s.IncludeManagedServiceAccounts
                ? null   // a gMSA/MSA-only inventory is a deliberate and meaningful configuration
                : "No classifier rule is configured — the report would find nothing and read as "
                     + "\"there are no non-human accounts\". Configure at least one rule (name pattern, "
                     + "OU, group, attribute, no key attribute, password-never-expires, or SPN).";
        }

        /// <summary>Splits a comma-separated setting, dropping blanks and duplicates. Blank input yields an empty array, never null — an absent rule is "no rule", not "unknown".</summary>
        public static string[] SplitList(string? csv) =>
            string.IsNullOrWhiteSpace(csv)
                ? Array.Empty<string>()
                : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .ToArray();

        /// <summary>
        /// Parses "attr=value, attr2=value2" rules.
        ///
        /// A malformed entry throws instead of being skipped: a dropped rule narrows the result
        /// set, and a narrower inventory is indistinguishable from a cleaner directory.
        /// </summary>
        public static (string Attr, string Value)[] ParseAttributeRules(string? csv)
        {
            var rules = new List<(string, string)>();
            foreach (var raw in SplitList(csv))
            {
                var i = raw.IndexOf('=');
                if (i <= 0 || i == raw.Length - 1)
                    throw new InvalidOperationException(
                        $"Attribute rule '{raw}' is not in the form attribute=value.");

                var attr = raw[..i].Trim();
                var value = raw[(i + 1)..].Trim();
                if (!AttributeNameShape.IsMatch(attr))
                    throw new InvalidOperationException(
                        $"'{attr}' is not a valid AD attribute name in rule '{raw}'.");
                if (value.Length == 0)
                    throw new InvalidOperationException($"Attribute rule '{raw}' has an empty value.");

                rules.Add((attr, value));
            }
            return rules.ToArray();
        }

        /// <summary>Throws unless the name is usable as an LDAP attribute name. For settings that name an attribute rather than carry a value.</summary>
        public static string RequireAttributeName(string? name, string settingLabel)
        {
            var n = name?.Trim();
            if (string.IsNullOrEmpty(n) || !AttributeNameShape.IsMatch(n))
                throw new InvalidOperationException($"{settingLabel} '{name}' is not a valid AD attribute name.");
            return n;
        }

        /// <summary>
        /// Escapes a value for an LDAP filter while keeping '*' as a wildcard.
        ///
        /// <see cref="LdapSanitizer.EscapeFilterValue"/> escapes '*' along with everything else,
        /// which is right for a value and wrong for a pattern: "svc_*" would be searched for
        /// literally and match the one account actually named "svc_*". Everything other than the
        /// wildcard is still escaped, so an injected parenthesis stays inert.
        /// </summary>
        public static string EscapePreservingWildcards(string pattern) =>
            string.Join("*", pattern.Split('*').Select(LdapSanitizer.EscapeFilterValue));

        /// <summary>An LDAP OR filter over sAMAccountName for the configured name patterns, or null when there are none.</summary>
        public static string? BuildNameFilter(string[] patterns)
        {
            if (patterns.Length == 0) return null;
            var sb = new StringBuilder();
            foreach (var p in patterns)
                sb.Append($"(sAMAccountName={EscapePreservingWildcards(p)})");
            return patterns.Length == 1 ? sb.ToString() : $"(|{sb})";
        }

        /// <summary>
        /// Folds the per-signal DN sets into the final matched set: union under "Any",
        /// intersection under "All".
        ///
        /// Under "All" an empty input list returns empty rather than "everything". The caller has
        /// already been stopped by <see cref="Validate"/>; this keeps the wrong answer from being
        /// reachable by a second route.
        /// </summary>
        public static HashSet<string> Combine(IEnumerable<IReadOnlyCollection<string>> sets, bool requireAll)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var first = true;

            foreach (var set in sets)
            {
                if (!requireAll) { result.UnionWith(set); continue; }
                if (first) { result.UnionWith(set); first = false; }
                else result.IntersectWith(set);
                if (result.Count == 0) break;
            }
            return result;
        }

        // ══════════════════════════════════════
        // RISK EVALUATION
        // ══════════════════════════════════════

        /// <summary>What the directory says about one matched account, reduced to what the risk rules need.</summary>
        public sealed record AccountFacts(
            bool Enabled,
            bool HasOwner,
            bool Privileged,
            bool PasswordNeverExpires,
            DateTime? PasswordLastSet,
            DateTime? LastActivity,
            DateTime? Expires);

        public const string ActionPrivileged = "NhiPrivileged";
        public const string ActionUnowned = "NhiUnowned";
        public const string ActionOwned = "NhiOwned";

        /// <summary>
        /// The finding's action — which is also how the audit log filters and how the summary
        /// email groups its counts.
        ///
        /// Ownership is the split that matters: it is the one fact the directory cannot tell you
        /// and the one every attestation cycle starts from. Administrative rights outrank it,
        /// because a service account inside Domain Admins is the row to open first whether or not
        /// somebody's name is on it.
        /// </summary>
        public static string ChooseAction(bool privileged, bool hasOwner) =>
            privileged ? ActionPrivileged : hasOwner ? ActionOwned : ActionUnowned;

        /// <summary>
        /// The risk flags for one account as short, stable tokens. An empty list means nothing was
        /// wrong — a result worth showing rather than a row to hide.
        /// </summary>
        public static IReadOnlyList<string> EvaluateRisks(
            AccountFacts a, int credentialMaxAgeDays, int dormantDays, DateTime nowUtc)
        {
            var risks = new List<string>();

            if (!a.Enabled) risks.Add("disabled");
            if (a.Privileged) risks.Add("privileged");
            if (!a.HasOwner) risks.Add("no-owner");

            // A disabled account cannot be exercised, so a stale credential or a silent year on it
            // is not a live exposure. Reporting it as one buries the enabled accounts that are.
            if (!a.Enabled) return risks;

            if (credentialMaxAgeDays > 0)
            {
                if (a.PasswordLastSet == null) risks.Add("credential-age-unknown");
                else
                {
                    var age = (int)(nowUtc - a.PasswordLastSet.Value).TotalDays;
                    if (age > credentialMaxAgeDays) risks.Add($"credential-{age}d");
                }
            }

            if (dormantDays > 0 && a.LastActivity != null)
            {
                var idle = (int)(nowUtc - a.LastActivity.Value).TotalDays;
                if (idle > dormantDays) risks.Add($"dormant-{idle}d");
            }

            if (a.PasswordNeverExpires) risks.Add("pwd-never-expires");
            if (a.Expires == null) risks.Add("no-expiry");

            return risks;
        }
    }
}
