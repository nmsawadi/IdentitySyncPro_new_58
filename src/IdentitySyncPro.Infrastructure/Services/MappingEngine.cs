using IdentitySyncPro.Core.Models.Settings;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Dynamic mapping engine that reads from TenantAttributeMapping, TenantGroupRule, and TenantOURule
    /// to transform raw database rows into AD-ready attribute dictionaries.
    /// Replaces hardcoded Identity computed properties.
    /// </summary>
    public class MappingEngine
    {
        /// <summary>
        /// Well-known multi-valued AD attributes that can hold multiple values.
        /// When mapping rows target these, values are accumulated (pipe-delimited) rather than overwritten.
        /// </summary>
        private static readonly HashSet<string> MultiValuedAttributes = new(StringComparer.OrdinalIgnoreCase)
        {
            "proxyAddresses", "otherMailbox", "url", "otherTelephone",
            "otherHomePhone", "otherFacsimileTelephoneNumber"
        };

        /// <summary>
        /// Applies attribute mappings to a raw database row and returns a dictionary of AD attributes.
        /// </summary>
        /// <param name="sourceRow">Raw data from source database</param>
        /// <param name="mappings">Tenant attribute mappings</param>
        /// <param name="globalDefaultValue">Optional global default value for empty fields</param>
        public static Dictionary<string, string> ApplyMappings(
            Dictionary<string, object?> sourceRow,
            List<TenantAttributeMapping> mappings,
            string? globalDefaultValue = null)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var mapping in mappings.OrderBy(m => m.SortOrder))
            {
                // Check condition
                if (!string.IsNullOrEmpty(mapping.Condition))
                {
                    if (!EvaluateCondition(sourceRow, mapping.Condition))
                        continue;
                }

                // Get source value
                string? rawValue = null;
                if (sourceRow.TryGetValue(mapping.SourceColumn, out var val) && val != null && val != DBNull.Value)
                {
                    rawValue = val.ToString()?.Trim();
                }

                // Apply mapping-specific default if empty
                if (string.IsNullOrEmpty(rawValue) && !string.IsNullOrEmpty(mapping.DefaultValue))
                {
                    rawValue = mapping.DefaultValue;
                }

                // Apply global default if empty and UseGlobalDefaultForEmptyFields is enabled
                if (string.IsNullOrEmpty(rawValue) && !string.IsNullOrEmpty(globalDefaultValue))
                {
                    rawValue = globalDefaultValue;
                }

                // Skip if still empty
                if (string.IsNullOrEmpty(rawValue))
                {
                    if (mapping.IsRequired)
                    {
                        if (!string.IsNullOrEmpty(globalDefaultValue))
                        {
                            rawValue = globalDefaultValue;
                        }
                        else
                        {
                            throw new InvalidOperationException($"Required field '{mapping.SourceColumn}' is empty");
                        }
                    }
                    continue;
                }

                // Apply transform
                string finalValue = ApplyTransform(rawValue, mapping.Transform, sourceRow);

                // Multi-valued attribute support (e.g. proxyAddresses)
                if (MultiValuedAttributes.Contains(mapping.TargetAttribute) && result.ContainsKey(mapping.TargetAttribute))
                {
                    result[mapping.TargetAttribute] += "|" + finalValue;
                }
                else
                {
                    result[mapping.TargetAttribute] = finalValue;
                }
            }

            return result;
        }

        // ═══ Provisioning Gate ═══

        /// <summary>Whether an identity may be provisioned, and why not when it may not.</summary>
        public readonly record struct CreationGateResult(bool Allowed, string Reason);

        public const string CreationModeAlways = "Always";
        public const string CreationModeNever = "Never";
        public const string CreationModeConditional = "Conditional";

        /// <summary>
        /// Decides whether a source identity with no AD account should get one.
        ///
        /// Every branch that denies creation must state a reason: an identity that is silently
        /// not provisioned looks identical to one that does not exist, and the operator has no
        /// way to tell "policy says no" apart from "the sync never saw this person".
        ///
        /// Unset mode means Always. Tenants configured before this setting existed carry no
        /// value, and reading that as "never create" would stop provisioning for a live tenant
        /// without raising anything.
        /// </summary>
        public static CreationGateResult ShouldCreateAccount(
            string? mode,
            string? conditionField,
            string? conditionOperator,
            string? conditionValue,
            Dictionary<string, object?> sourceRow)
        {
            if (string.IsNullOrWhiteSpace(mode) ||
                mode.Trim().Equals(CreationModeAlways, StringComparison.OrdinalIgnoreCase))
                return new CreationGateResult(true, "");

            if (mode.Trim().Equals(CreationModeNever, StringComparison.OrdinalIgnoreCase))
                return new CreationGateResult(false, "account creation is disabled for this tenant");

            if (mode.Trim().Equals(CreationModeConditional, StringComparison.OrdinalIgnoreCase))
            {
                // Fail closed on an incomplete condition. EvaluateSimpleCondition treats a missing
                // operator or value as "matches", so an unconfigured condition would provision
                // every identity — the exact opposite of what choosing Conditional asks for.
                if (string.IsNullOrWhiteSpace(conditionField) ||
                    string.IsNullOrWhiteSpace(conditionOperator) ||
                    string.IsNullOrWhiteSpace(conditionValue))
                    return new CreationGateResult(false,
                        "creation condition is incomplete — no account is created until it is configured");

                var fieldValue = GetFieldValue(sourceRow, conditionField);

                // A condition naming a column the view does not have can never be satisfied.
                // Reported separately because it reads as a policy decision otherwise, which is
                // how a misspelled column silently disabled ten lifecycle rules once already.
                if (!sourceRow.ContainsKey(conditionField))
                    return new CreationGateResult(false,
                        $"creation condition names column '{conditionField}', which the source does not return");

                return EvaluateSimpleCondition(fieldValue, conditionOperator, conditionValue)
                    ? new CreationGateResult(true, "")
                    : new CreationGateResult(false,
                        $"creation condition not met ({conditionField} {conditionOperator} {conditionValue})");
            }

            // An unrecognised mode is a configuration fault, not an instruction. Creating accounts
            // on a value nobody can interpret is the one outcome that cannot be undone.
            return new CreationGateResult(false, $"unknown account creation mode '{mode}'");
        }

        /// <summary>
        /// Determines the target OU for a identity based on OU rules.
        /// </summary>
        /// <summary>
        /// Checks an OU rule for the two faults that otherwise fail silently at runtime:
        /// malformed <c>ValueMappings</c> JSON (swallowed by the parse guard in ResolveOU), and
        /// placeholders that name no real source column (replaced with the literal "DEFAULT").
        ///
        /// Both were live in production: a rule reading
        /// <c>OU={GENDER},OU={CITY},{BaseDN}</c> against columns named GENDER_CODE and CITY_NO
        /// resolved to <c>OU=DEFAULT,OU=DEFAULT,...</c> — an OU that does not exist, so every
        /// new account creation would have failed.
        /// </summary>
        /// <param name="knownColumns">
        /// Source view columns, when available. Null skips the placeholder check.
        /// </param>
        /// <param name="distinctSourceValues">
        /// The distinct values each placeholder column actually holds in the source, when
        /// available. Null skips the coverage check.
        ///
        /// This catches the third silent fault: a ValueMappings entry that covers some values and
        /// not others. ResolveOU passes an unmapped value through unchanged, so CITY_NO 20 becomes
        /// "OU=20" instead of "OU=SHARORAH" — a DN that does not exist. Nothing is empty, so no
        /// DEFAULT warning fires, and the failure only appears one account at a time on create.
        /// Checking the map against the data BEFORE a run turns 38 dead-letter entries into one
        /// message naming the missing value.
        /// </param>
        public static List<string> ValidateOURule(
            TenantOURule rule,
            IEnumerable<string>? knownColumns = null,
            IReadOnlyDictionary<string, IEnumerable<string>>? distinctSourceValues = null)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(rule.OUTemplate))
            {
                errors.Add("قالب الـ OU فارغ / OU template is empty");
                return errors;
            }

            Dictionary<string, Dictionary<string, string>>? mappings = null;
            if (!string.IsNullOrWhiteSpace(rule.ValueMappings))
            {
                try
                {
                    mappings = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(rule.ValueMappings);
                }
                catch (JsonException ex)
                {
                    errors.Add($"ValueMappings ليست JSON صالحة / not valid JSON: {ex.Message}");
                }
            }

            // {BaseDN} is substituted separately and is not a source column.
            var placeholders = Regex.Matches(rule.OUTemplate, @"\{(\w+)\}")
                .Select(m => m.Groups[1].Value)
                .Where(p => !p.Equals("BaseDN", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (knownColumns != null)
            {
                var cols = new HashSet<string>(knownColumns, StringComparer.OrdinalIgnoreCase);
                foreach (var p in placeholders.Where(p => !cols.Contains(p)))
                    errors.Add($"العنصر النائب {{{p}}} لا يطابق أي عمود في المصدر — سيُستبدل بـ DEFAULT / " +
                               $"placeholder {{{p}}} matches no source column — it will resolve to DEFAULT");
            }

            if (mappings != null)
            {
                foreach (var key in mappings.Keys.Where(k =>
                             !placeholders.Contains(k, StringComparer.OrdinalIgnoreCase)))
                    errors.Add($"مفتاح ValueMappings \"{key}\" لا يقابل أي عنصر نائب في القالب / " +
                               $"ValueMappings key \"{key}\" matches no placeholder in the template");
            }

            // Coverage: every value the source actually holds must have a map entry, otherwise
            // ResolveOU passes it through raw and builds a DN that does not exist.
            //
            // Only placeholders that HAVE a map are checked. A placeholder with no map at all is a
            // deliberate choice — the column already carries the OU name — and flagging it would
            // make this noisy enough to ignore.
            if (mappings != null && distinctSourceValues != null)
            {
                foreach (var placeholder in placeholders)
                {
                    if (!mappings.TryGetValue(placeholder, out var valueMap) || valueMap.Count == 0)
                        continue;
                    if (!distinctSourceValues.TryGetValue(placeholder, out var observed) || observed == null)
                        continue;

                    var unmapped = observed
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Select(v => v.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(v => !valueMap.ContainsKey(v))
                        .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (unmapped.Count > 0)
                        errors.Add(
                            $"العمود {placeholder} يحمل قيماً بلا خريطة في ValueMappings: {string.Join(", ", unmapped)} — " +
                            $"ستدخل كما هي في الـ DN وسيفشل إنشاء تلك الحسابات / " +
                            $"{placeholder} holds values with no ValueMappings entry: {string.Join(", ", unmapped)} — " +
                            $"they go into the DN unchanged and account creation will fail for them");
                }
            }

            return errors;
        }

        public static string ResolveOU(
            Dictionary<string, object?> sourceRow,
            List<TenantOURule> rules,
            string baseDN,
            ILogger? logger = null)
        {
            foreach (var rule in rules.OrderBy(r => r.Priority))
            {
                // Check condition if present
                if (!string.IsNullOrEmpty(rule.ConditionField))
                {
                    var fieldValue = GetFieldValue(sourceRow, rule.ConditionField);
                    if (!EvaluateSimpleCondition(fieldValue, rule.ConditionOperator, rule.ConditionValue))
                        continue;
                }

                // Apply template
                var ou = rule.OUTemplate;

                // Replace {BaseDN}
                ou = ou.Replace("{BaseDN}", baseDN, StringComparison.OrdinalIgnoreCase);

                // Replace field placeholders with mapped values
                var placeholders = Regex.Matches(ou, @"\{(\w+)\}");
                foreach (Match match in placeholders)
                {
                    var placeholder = match.Groups[1].Value;
                    var fieldValue = GetFieldValue(sourceRow, placeholder);

                    // Check value mappings
                    if (!string.IsNullOrEmpty(rule.ValueMappings) && !string.IsNullOrEmpty(fieldValue))
                    {
                        try
                        {
                            var mappingDict = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(rule.ValueMappings);
                            if (mappingDict != null && mappingDict.TryGetValue(placeholder, out var valueMap))
                            {
                                if (valueMap.TryGetValue(fieldValue, out var mappedValue))
                                {
                                    fieldValue = mappedValue;
                                }
                                else
                                {
                                    // A map exists for this placeholder but has no entry for this
                                    // value, so the RAW value goes into the DN: "OU=20" instead of
                                    // "OU=SHARORAH". Nothing is empty, so the DEFAULT warning below
                                    // never fires and the only symptom is one failed create per
                                    // account — which is how a single missing map entry cost 38
                                    // accounts before anyone knew why.
                                    logger?.LogWarning(
                                        "OU rule {RuleId}: {{{Placeholder}}} value \"{Value}\" has no entry in " +
                                        "ValueMappings — the raw value goes into the DN, and that OU almost " +
                                        "certainly does not exist",
                                        rule.Id, placeholder, fieldValue);
                                }
                            }
                        }
                        catch (JsonException ex)
                        {
                            // Previously swallowed in silence, which is how malformed mappings
                            // reached production and quietly produced unmapped OU names.
                            logger?.LogWarning(
                                "OU rule {RuleId}: ValueMappings is not valid JSON ({Error}) — values will not be translated",
                                rule.Id, ex.Message);
                        }
                    }

                    if (string.IsNullOrEmpty(fieldValue))
                    {
                        // "DEFAULT" is almost never a real OU, so this becomes a failed account
                        // creation later. Say so here, where the cause is still visible.
                        logger?.LogWarning(
                            "OU rule {RuleId}: placeholder {{{Placeholder}}} matched no source column — " +
                            "falling back to \"DEFAULT\", which will likely fail on create",
                            rule.Id, placeholder);
                    }

                    ou = ou.Replace($"{{{placeholder}}}", fieldValue ?? "DEFAULT");
                }

                return ou;
            }

            // Fallback: return baseDN
            return baseDN;
        }

        /// <summary>
        /// Determines which groups a identity should belong to.
        /// </summary>
        public static List<string> ResolveGroups(
            Dictionary<string, object?> sourceRow,
            List<TenantGroupRule> rules)
        {
            var groups = new List<string>();

            foreach (var rule in rules)
            {
                if (rule.IsDefault)
                {
                    groups.Add(rule.GroupDN ?? rule.GroupName);
                    continue;
                }

                if (!string.IsNullOrEmpty(rule.ConditionField))
                {
                    var fieldValue = GetFieldValue(sourceRow, rule.ConditionField);
                    if (EvaluateSimpleCondition(fieldValue, rule.ConditionOperator, rule.ConditionValue))
                    {
                        groups.Add(rule.GroupDN ?? rule.GroupName);
                    }
                }
            }

            return groups;
        }

        /// <summary>
        /// Gets the identifier value (sAMAccountName) from the mapping results.
        /// </summary>
        public static string? GetIdentifier(
            Dictionary<string, object?> sourceRow,
            List<TenantAttributeMapping> mappings)
        {
            var idMapping = mappings.FirstOrDefault(m => m.IsIdentifier);
            if (idMapping == null) return null;

            // A Username: pattern names its own columns, so it does not depend on this mapping's
            // SourceColumn at all. Requiring that column to hold a value would mean an empty
            // FIRST_NAME silently produced no name — and the caller's fallback would then use the
            // source key, quietly giving one employee a numeric account name among letters.
            if (idMapping.Transform?.StartsWith("Username:", StringComparison.OrdinalIgnoreCase) == true)
            {
                var generated = ApplyTransform("", idMapping.Transform, sourceRow);
                return string.IsNullOrWhiteSpace(generated) ? null : generated;
            }

            if (sourceRow.TryGetValue(idMapping.SourceColumn, out var val) && val != null)
            {
                var rawValue = val.ToString()?.Trim() ?? "";
                return ApplyTransform(rawValue, idMapping.Transform, sourceRow);
            }
            return null;
        }

        // ═══ Transform Engine ═══

        private static string ApplyTransform(string value, string? transform, Dictionary<string, object?> sourceRow)
        {
            if (string.IsNullOrEmpty(transform)) return value;

            // Format:{0}@domain.com
            if (transform.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                var format = transform.Substring(7);
                return string.Format(format, value);
            }

            // Concat:{FirstName} {LastName}
            if (transform.StartsWith("Concat:", StringComparison.OrdinalIgnoreCase))
            {
                var template = transform.Substring(7);
                var result = Regex.Replace(template, @"\{(\w+)\}", m =>
                {
                    var field = m.Groups[1].Value;
                    return GetFieldValue(sourceRow, field) ?? "";
                });
                return result.Trim();
            }

            // Map:1=ValueA,2=ValueB
            if (transform.StartsWith("Map:", StringComparison.OrdinalIgnoreCase))
            {
                var mapStr = transform.Substring(4);
                var pairs = mapStr.Split(',');
                foreach (var pair in pairs)
                {
                    var kv = pair.Split('=', 2);
                    if (kv.Length == 2 && kv[0].Trim() == value)
                        return kv[1].Trim();
                }
                return value;
            }

            // Static:Identity (ignores source value, returns constant)
            if (transform.StartsWith("Static:", StringComparison.OrdinalIgnoreCase))
            {
                return transform.Substring(7);
            }

            // Truncate:4 (limit length, for initials)
            if (transform.StartsWith("Truncate:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(transform.Substring(9), out var maxLen) && value.Length > maxLen)
                    return value[..maxLen];
                return value;
            }

            // Username:{FIRST:1}{SECOND:1}{LAST}|lower|max:20
            if (transform.StartsWith("Username:", StringComparison.OrdinalIgnoreCase))
            {
                return BuildUsername(transform.Substring(9), sourceRow);
            }

            return transform.ToLower() switch
            {
                "toupper" => value.ToUpper(),
                "tolower" => value.ToLower(),
                "getinitials" => value.Length > 4 ? value[..1] : value,
                "trim" => value.Trim(),
                _ => value
            };
        }

        // ═══ Username Engine ═══

        /// <summary>
        /// Characters an account name may keep. Everything else (spaces, commas, quotes,
        /// Arabic diacritics, LDAP-significant punctuation) is dropped, because the result is
        /// used both as sAMAccountName and inside the account's DN.
        /// </summary>
        private const string UsernameAllowedPunctuation = ".-_";

        /// <summary>The sAMAccountName limit in Active Directory. Longer names are rejected on create.</summary>
        public const int SamAccountNameMaxLength = 20;

        /// <summary>
        /// Builds an account name from a per-tenant pattern, so each organisation expresses its
        /// own naming policy as configuration rather than code.
        ///
        /// Pattern placeholders — <c>{COLUMN}</c> takes the whole column, <c>{COLUMN:N}</c> takes
        /// its first N characters. Literal text between placeholders is kept, which is what makes
        /// separator styles (<c>{FIRST}.{LAST}</c>) expressible.
        ///
        /// Each column value is normalised BEFORE the first N characters are taken — spaces and
        /// diacritics are removed first — so <c>{LAST:3}</c> of "al hareth" is "alh", not "al".
        /// Taking the substring first would silently produce a different name for every
        /// multi-word family name.
        ///
        /// Options are appended pipe-separated:
        ///   <c>lower</c> (default) · <c>upper</c> · <c>preserve</c> — letter case
        ///   <c>max:N</c> — truncate to N characters (default 20, the AD limit)
        ///
        /// Examples, all against FIRST_NAME="Mohammed" SECOND_NAME="ali" LAST_NAME="al hareth":
        ///   <c>{FIRST_NAME:1}{SECOND_NAME:1}{LAST_NAME}</c> → maalhareth
        ///   <c>{FIRST_NAME}.{LAST_NAME}</c>                 → mohammed.alhareth
        ///   <c>{FIRST_NAME:1}{LAST_NAME}</c>                → malhareth
        ///   <c>{LAST_NAME}{FIRST_NAME:1}</c>                → alharethm
        ///   <c>{FIRST_NAME:1}{LAST_NAME:7}|upper</c>        → MALHARET
        /// </summary>
        public static string BuildUsername(string spec, Dictionary<string, object?> sourceRow)
        {
            if (string.IsNullOrWhiteSpace(spec)) return string.Empty;

            var parts = spec.Split('|');
            var pattern = parts[0];

            var casing = "lower";
            var maxLength = SamAccountNameMaxLength;

            foreach (var option in parts.Skip(1).Select(o => o.Trim()).Where(o => o.Length > 0))
            {
                if (option.Equals("lower", StringComparison.OrdinalIgnoreCase) ||
                    option.Equals("upper", StringComparison.OrdinalIgnoreCase) ||
                    option.Equals("preserve", StringComparison.OrdinalIgnoreCase))
                {
                    casing = option.ToLowerInvariant();
                }
                else if (option.StartsWith("max:", StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(option.Substring(4), out var parsedMax) && parsedMax > 0)
                {
                    maxLength = parsedMax;
                }
            }

            // {COLUMN} or {COLUMN:N}
            var built = Regex.Replace(pattern, @"\{(\w+)(?::(\d+))?\}", match =>
            {
                var column = match.Groups[1].Value;
                var raw = GetFieldValue(sourceRow, column);
                var normalized = NormalizeNamePart(raw);

                if (normalized.Length == 0) return "";

                if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var take) && take > 0)
                    return normalized.Length <= take ? normalized : normalized[..take];

                return normalized;
            });

            // Strip anything the pattern's literal text introduced that AD will not accept.
            var sb = new StringBuilder(built.Length);
            foreach (var ch in built)
            {
                if (char.IsLetterOrDigit(ch) || UsernameAllowedPunctuation.Contains(ch))
                    sb.Append(ch);
            }
            var result = sb.ToString();

            result = casing switch
            {
                "upper" => result.ToUpperInvariant(),
                "preserve" => result,
                _ => result.ToLowerInvariant()
            };

            if (result.Length > maxLength)
                result = result[..maxLength];

            // A name ending in a separator is legal in AD but reads as a truncation artefact.
            return result.TrimEnd(UsernameAllowedPunctuation.ToCharArray());
        }

        /// <summary>
        /// Strips spaces and combining marks from one name component so that initials are taken
        /// from letters rather than from whatever character happened to fall first.
        /// </summary>
        private static string NormalizeNamePart(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);

            foreach (var ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsWhiteSpace(ch)) continue;
                sb.Append(ch);
            }

            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        /// <summary>
        /// Appends a discriminator to a taken account name, truncating the base so the result
        /// still fits the AD limit — "mohammed.alhareth" + 2 must not become 21 characters.
        /// </summary>
        public static string ApplyCollisionSuffix(string baseName, int number, string? format = null, int maxLength = SamAccountNameMaxLength)
        {
            var suffix = number.ToString();
            var template = string.IsNullOrWhiteSpace(format) ? "{base}{n}" : format;

            var room = maxLength - suffix.Length;
            var trimmedBase = baseName.Length > room && room > 0 ? baseName[..room] : baseName;
            trimmedBase = trimmedBase.TrimEnd(UsernameAllowedPunctuation.ToCharArray());

            return template
                .Replace("{base}", trimmedBase, StringComparison.OrdinalIgnoreCase)
                .Replace("{n}", suffix, StringComparison.OrdinalIgnoreCase);
        }

        // ═══ Condition Engine ═══

        private static bool EvaluateCondition(Dictionary<string, object?> sourceRow, string conditionJson)
        {
            try
            {
                var cond = JsonSerializer.Deserialize<JsonElement>(conditionJson);
                var field = cond.GetProperty("field").GetString() ?? "";
                var op = cond.GetProperty("op").GetString() ?? "==";
                var val = cond.GetProperty("value").GetString() ?? "";

                var fieldValue = GetFieldValue(sourceRow, field);
                return EvaluateSimpleCondition(fieldValue, op, val);
            }
            catch
            {
                return true; // If condition is invalid, apply mapping anyway
            }
        }

        private static bool EvaluateSimpleCondition(string? fieldValue, string? op, string? conditionValue)
        {
            if (string.IsNullOrEmpty(op) || string.IsNullOrEmpty(conditionValue))
                return true;

            fieldValue ??= "";

            return op switch
            {
                "==" => fieldValue.Equals(conditionValue, StringComparison.OrdinalIgnoreCase),
                "!=" => !fieldValue.Equals(conditionValue, StringComparison.OrdinalIgnoreCase),
                "in" => conditionValue.Split(',').Any(v => v.Trim().Equals(fieldValue, StringComparison.OrdinalIgnoreCase)),
                "not_in" => !conditionValue.Split(',').Any(v => v.Trim().Equals(fieldValue, StringComparison.OrdinalIgnoreCase)),
                "gt" => double.TryParse(fieldValue, out var fv) && double.TryParse(conditionValue, out var cv) && fv > cv,
                "lt" => double.TryParse(fieldValue, out var fv2) && double.TryParse(conditionValue, out var cv2) && fv2 < cv2,
                _ => true
            };
        }

        private static string? GetFieldValue(Dictionary<string, object?> sourceRow, string fieldName)
        {
            if (sourceRow.TryGetValue(fieldName, out var val) && val != null && val != DBNull.Value)
                return val.ToString()?.Trim();
            return null;
        }

        /// <summary>
        /// Merges the ValueMappings of several OU rules into one map, as JSON.
        ///
        /// A lifecycle rule can now carry placeholders in its ActionValue
        /// (<c>OU={GENDER_CODE},OU=GRADUATES,{BaseDN}</c>), and those need the same translation the
        /// OU rules already define — the institution has stated once that GENDER_CODE 1 is "MALE",
        /// and restating it on every lifecycle rule would be a second copy to keep in step.
        ///
        /// Earlier rules win on a conflict, matching the priority order the caller passes in.
        /// Rules with unparseable ValueMappings are skipped; ValidateOURule reports those.
        /// </summary>
        public static string? MergeValueMappings(IEnumerable<TenantOURule> rules)
        {
            var merged = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var rule in rules)
            {
                if (string.IsNullOrWhiteSpace(rule.ValueMappings)) continue;

                Dictionary<string, Dictionary<string, string>>? map;
                try
                {
                    map = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(rule.ValueMappings);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (map == null) continue;

                foreach (var (placeholder, values) in map)
                {
                    if (!merged.TryGetValue(placeholder, out var target))
                        merged[placeholder] = target = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var (from, to) in values)
                        if (!target.ContainsKey(from)) target[from] = to;
                }
            }

            return merged.Count == 0 ? null : JsonSerializer.Serialize(merged);
        }

        /// <summary>
        /// A ValueMappings gap is only visible against the data the source actually holds, so the
        /// coverage check in <see cref="ValidateOURule"/> needs the observed values. This gathers
        /// them from rows the tenant has already staged.
        ///
        /// It reads values through the same <c>GetFieldValue</c> that <see cref="ResolveOU"/> uses —
        /// deliberately, not incidentally. Any difference in how a value is stringified or trimmed
        /// would make the check report values the engine never sees, or stay silent on ones it does.
        /// </summary>
        /// <param name="maxDistinctPerColumn">
        /// A column with thousands of distinct values is an identifier, not an OU discriminator, and
        /// listing every one of them as unmapped is noise nobody reads. Such a column is dropped
        /// rather than truncated: a partial value set would report gaps that may not be gaps.
        /// </param>
        public static Dictionary<string, IEnumerable<string>> CollectObservedValues(
            IEnumerable<Dictionary<string, object?>> sourceRows,
            IEnumerable<TenantOURule> rules,
            int maxDistinctPerColumn = 200)
        {
            // Only placeholders that actually have a map are worth collecting. A placeholder with
            // no map is passed straight through by design, and ValidateOURule leaves it alone.
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in rules)
            {
                if (string.IsNullOrWhiteSpace(rule.ValueMappings)) continue;

                Dictionary<string, Dictionary<string, string>>? map;
                try
                {
                    map = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(rule.ValueMappings);
                }
                catch (JsonException)
                {
                    continue; // ValidateOURule reports the malformed JSON itself.
                }
                if (map == null) continue;

                foreach (var placeholder in Regex.Matches(rule.OUTemplate ?? "", @"\{(\w+)\}")
                             .Select(m => m.Groups[1].Value)
                             .Where(p => !p.Equals("BaseDN", StringComparison.OrdinalIgnoreCase)))
                {
                    if (map.ContainsKey(placeholder)) wanted.Add(placeholder);
                }
            }

            if (wanted.Count == 0) return new Dictionary<string, IEnumerable<string>>();

            var collected = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var abandoned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in sourceRows)
            {
                foreach (var column in wanted)
                {
                    if (abandoned.Contains(column)) continue;

                    var value = GetFieldValue(row, column);
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    if (!collected.TryGetValue(column, out var set))
                        collected[column] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    set.Add(value);

                    if (set.Count > maxDistinctPerColumn)
                    {
                        abandoned.Add(column);
                        collected.Remove(column);
                    }
                }
            }

            return collected.ToDictionary(k => k.Key, k => (IEnumerable<string>)k.Value,
                StringComparer.OrdinalIgnoreCase);
        }
    }
}
