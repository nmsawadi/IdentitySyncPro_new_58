using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Runs <see cref="MappingEngine.ValidateOURule"/> against a tenant's real staged data.
    ///
    /// The checks that need no data (malformed JSON, placeholders naming no column) already run at
    /// save time. The one that matters most does need data: a ValueMappings map that covers some
    /// values and not others is invisible until an account is created into an OU that does not
    /// exist — one failed create per identity, 38 of them before the cause was visible.
    /// </summary>
    public static class OuRulePrecheck
    {
        /// <summary>
        /// Reads the values the tenant's own staged identities hold for every mapped placeholder.
        ///
        /// The staging table is used rather than the source database on purpose: it is what the last
        /// sync actually read, it is local, and it cannot fail or hang the way a live query against
        /// a source the tenant does not own can. It costs one streamed pass over the tenant's rows.
        /// </summary>
        public static async Task<Dictionary<string, IEnumerable<string>>> CollectObservedValuesAsync(
            AppDbContext db, int tenantId, IEnumerable<TenantOURule> rules, CancellationToken ct = default)
        {
            var ruleList = rules.ToList();
            if (ruleList.Count == 0) return new Dictionary<string, IEnumerable<string>>();

            var rows = new List<Dictionary<string, object?>>();

            // Only AttributesJson is pulled, and it is streamed — a large tenant has six figures of
            // rows and materialising whole entities to read one column would be the expensive way
            // to do this.
            var json = db.MetaverseEntries
                .AsNoTracking()
                .Where(e => e.TenantId == tenantId)
                .Select(e => e.AttributesJson)
                .AsAsyncEnumerable();

            await foreach (var raw in json.WithCancellation(ct))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                try
                {
                    // Left case-sensitive to match ResolveOU exactly. A placeholder whose casing
                    // does not match the source column resolves to DEFAULT there, and the
                    // placeholder check already reports that — it is not a mapping gap.
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(raw);
                    if (parsed != null) rows.Add(parsed);
                }
                catch (JsonException)
                {
                    // A single unreadable staged row is not this check's problem.
                }
            }

            return MappingEngine.CollectObservedValues(rows, ruleList);
        }

        /// <summary>
        /// How many staged rows are inspected to learn the tenant's column names. Every row comes
        /// from the same view, so one would do; a sample guards against a row that happens to be
        /// missing a key, without reading six figures of rows to learn a fixed answer.
        /// </summary>
        private const int ColumnNameSampleSize = 200;

        /// <summary>
        /// The column names the tenant's staged identities actually carry.
        /// </summary>
        public static async Task<HashSet<string>> CollectStagedColumnNamesAsync(
            AppDbContext db, int tenantId, CancellationToken ct = default)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var sample = await db.MetaverseEntries
                .AsNoTracking()
                .Where(e => e.TenantId == tenantId)
                .Select(e => e.AttributesJson)
                .Take(ColumnNameSampleSize)
                .ToListAsync(ct);

            foreach (var raw in sample)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                    foreach (var property in doc.RootElement.EnumerateObject())
                        names.Add(property.Name);
                }
                catch (JsonException)
                {
                    // One unreadable staged row is not this check's problem.
                }
            }

            return names;
        }

        /// <summary>
        /// Finds lifecycle rules whose ConditionField names neither a source column nor one of the
        /// synthetic fields.
        ///
        /// This is the fault that cost the most: a ConditionField naming a column that does not
        /// exist reads null, so the condition never matches, so the rule never fires — and nothing
        /// is logged, because a rule that does not match is indistinguishable from a rule whose
        /// turn has not come. Ten rules sat dead across 111,465 identities that way.
        ///
        /// It becomes reachable again the moment anyone retypes a ConditionField, which is exactly
        /// what migrating from the STATUS_CODE alias to a real column name involves.
        /// </summary>
        public static async Task<List<string>> FindRulesNamingUnknownColumnsAsync(
            AppDbContext db, int tenantId, CancellationToken ct = default)
        {
            var rules = await db.LifecycleRules
                .AsNoTracking()
                .Where(r => r.TenantId == tenantId && r.Enabled && r.ConditionField != null)
                .Select(r => new { r.Name, r.Priority, r.ConditionField })
                .ToListAsync(ct);

            if (rules.Count == 0) return new List<string>();

            var columns = await CollectStagedColumnNamesAsync(db, tenantId, ct);

            // Nothing staged means no evidence, not evidence of absence. A tenant configuring its
            // first rules before its first sync must not be told every field is wrong.
            if (columns.Count == 0) return new List<string>();

            foreach (var synthetic in LifecycleEngine.SyntheticRuleFields)
                columns.Add(synthetic);

            var problems = new List<string>();
            foreach (var rule in rules)
            {
                var field = rule.ConditionField!.Trim();

                // An empty condition means "always matches" and is a legitimate rule shape.
                if (field.Length == 0) continue;
                if (columns.Contains(field)) continue;

                problems.Add(
                    $"القاعدة «{rule.Name}» (أولوية {rule.Priority}) تشترط على «{field}» وهو ليس عموداً في المصدر — " +
                    $"ستُقرأ القيمة null فلا تطابق القاعدة أبداً ولن يظهر خطأ / " +
                    $"rule '{rule.Name}' (priority {rule.Priority}) conditions on '{field}', which is not a source " +
                    $"column — it reads as null, so the rule can never match and nothing will report it");
            }

            return problems;
        }

        /// <summary>
        /// Validates every OU rule of a tenant and separates the two kinds of fault, because they
        /// deserve different treatment.
        ///
        /// <para><b>Errors</b> are wrong no matter what the data holds — malformed ValueMappings
        /// JSON, a placeholder naming no source column. Saving a rule with one of those saves a
        /// rule that cannot work.</para>
        ///
        /// <para><b>Warnings</b> are the coverage gaps. They are judged against staged data, which
        /// can be stale, partial, or absent, and the mapping for a new value is often added in the
        /// same sitting as the rule itself. Blocking a save on one would mean an operator cannot
        /// save a rule until the data agrees with it — so these are reported, not enforced.</para>
        /// </summary>
        public static async Task<(List<string> Errors, List<string> Warnings)> ValidateAsync(
            AppDbContext db, int tenantId, IEnumerable<TenantOURule> rules,
            IEnumerable<string>? knownColumns = null, CancellationToken ct = default)
        {
            var ruleList = rules.ToList();
            var observed = await CollectObservedValuesAsync(db, tenantId, ruleList, ct);

            var errors = new List<string>();
            var warnings = new List<string>();

            foreach (var rule in ruleList)
            {
                var structural = MappingEngine.ValidateOURule(rule, knownColumns);
                errors.AddRange(structural.Select(e => $"[{rule.OUTemplate}] {e}"));

                // An empty observed set means "nothing staged yet", not "nothing is mapped" —
                // passing it through would report every mapped value as missing on a fresh tenant.
                if (observed.Count == 0) continue;

                // Whatever the data-aware pass adds beyond the structural pass IS the coverage gap.
                // Deriving it by difference keeps the two lists from drifting apart as
                // ValidateOURule grows new checks.
                var withData = MappingEngine.ValidateOURule(rule, knownColumns, observed);
                warnings.AddRange(withData.Except(structural).Select(e => $"[{rule.OUTemplate}] {e}"));
            }

            return (errors, warnings);
        }
    }
}
