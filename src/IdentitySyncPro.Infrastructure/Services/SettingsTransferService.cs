using ClosedXML.Excel;
using IdentitySyncPro.Core.Models.Rules;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Exports a tenant's rule configuration to a single workbook and imports it back.
    ///
    /// Why this exists: rules were previously moved between environments by hand-written SQL,
    /// which is where two silent production faults came from — an OU rule whose placeholders
    /// named no real column (resolving to "OU=DEFAULT"), and a lifecycle rule whose comparison
    /// operator was lost in the round trip (an empty operator makes the rule match every
    /// identity). Import therefore validates first and never writes without an explicit apply.
    ///
    /// Deliberately covers rules only. Connection settings and credentials are not exported:
    /// they are environment-specific, and their passwords are encrypted at rest with a key that
    /// does not travel with the workbook.
    /// </summary>
    public class SettingsTransferService
    {
        private readonly AppDbContext _db;
        private readonly ILogger<SettingsTransferService> _logger;

        public SettingsTransferService(AppDbContext db, ILogger<SettingsTransferService> logger)
        {
            _db = db;
            _logger = logger;
        }

        // Sheet names are part of the file contract — import matches on them.
        private const string SheetMappings = "AttributeMappings";
        private const string SheetOU = "OURules";
        private const string SheetGroups = "GroupRules";
        private const string SheetLifecycle = "LifecycleRules";

        // ═══════════════════════════════════════
        // EXPORT
        // ═══════════════════════════════════════

        public async Task<byte[]> ExportAsync(int tenantId, CancellationToken ct = default)
        {
            var mappings = await _db.TenantAttributeMappings.AsNoTracking()
                .Where(m => m.TenantId == tenantId).OrderBy(m => m.SortOrder).ToListAsync(ct);
            var ouRules = await _db.TenantOURules.AsNoTracking()
                .Where(o => o.TenantId == tenantId).OrderBy(o => o.Priority).ToListAsync(ct);
            var groupRules = await _db.TenantGroupRules.AsNoTracking()
                .Where(g => g.TenantId == tenantId).ToListAsync(ct);
            var lifecycle = await _db.LifecycleRules.AsNoTracking()
                .Where(l => l.TenantId == tenantId).OrderBy(l => l.Priority).ToListAsync(ct);

            using var wb = new XLWorkbook();

            Fill(wb.Worksheets.Add(SheetMappings),
                new[] { "SourceColumn", "TargetAttribute", "Transform", "DefaultValue", "IsRequired", "IsIdentifier", "SortOrder", "Condition" },
                mappings.Select(m => new object?[] { m.SourceColumn, m.TargetAttribute, m.Transform, m.DefaultValue, m.IsRequired, m.IsIdentifier, m.SortOrder, m.Condition }));

            Fill(wb.Worksheets.Add(SheetOU),
                new[] { "OUTemplate", "Priority", "ConditionField", "ConditionOperator", "ConditionValue", "ValueMappings", "Description" },
                ouRules.Select(o => new object?[] { o.OUTemplate, o.Priority, o.ConditionField, o.ConditionOperator, o.ConditionValue, o.ValueMappings, o.Description }));

            Fill(wb.Worksheets.Add(SheetGroups),
                new[] { "GroupName", "GroupDN", "IsDefault", "ConditionField", "ConditionOperator", "ConditionValue", "Description" },
                groupRules.Select(g => new object?[] { g.GroupName, g.GroupDN, g.IsDefault, g.ConditionField, g.ConditionOperator, g.ConditionValue, g.Description }));

            Fill(wb.Worksheets.Add(SheetLifecycle),
                new[] { "Name", "Description", "Enabled", "Priority", "TriggerType", "ConditionField", "ConditionOperator", "ConditionValue", "ActionType", "ActionValue", "GracePeriodDays" },
                lifecycle.Select(l => new object?[] { l.Name, l.Description, l.Enabled, l.Priority, l.TriggerType, l.ConditionField, l.ConditionOperator, l.ConditionValue, l.ActionType, l.ActionValue, l.GracePeriodDays }));

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        }

        private static void Fill(IXLWorksheet ws, string[] headers, IEnumerable<object?[]> rows)
        {
            for (int c = 0; c < headers.Length; c++)
            {
                var cell = ws.Cell(1, c + 1);
                cell.Value = headers[c];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E78");
            }

            int r = 2;
            foreach (var row in rows)
            {
                for (int c = 0; c < row.Length; c++)
                {
                    var cell = ws.Cell(r, c + 1);
                    // Operators like "==" must be stored as text; Excel would treat a leading
                    // '=' as a formula and the value would come back empty on re-import.
                    if (row[c] is string s)
                    {
                        cell.SetValue(s);
                        cell.Style.NumberFormat.Format = "@";
                    }
                    else if (row[c] is bool b) cell.Value = b;
                    else if (row[c] is int i) cell.Value = i;
                    else if (row[c] != null) cell.SetValue(row[c]!.ToString());
                }
                r++;
            }

            ws.Columns().AdjustToContents();
            ws.SheetView.FreezeRows(1);
        }

        // ═══════════════════════════════════════
        // IMPORT
        // ═══════════════════════════════════════

        public class ImportPreview
        {
            /// <summary>Blocking problems — apply is refused while any exist.</summary>
            public List<string> Errors { get; } = new();
            /// <summary>Non-blocking, but worth a human look before applying.</summary>
            public List<string> Warnings { get; } = new();
            public List<SectionSummary> Sections { get; } = new();
            public bool CanApply => Errors.Count == 0 && Sections.Any(s => s.Present);
        }

        public class SectionSummary
        {
            public string Name { get; set; } = "";
            public bool Present { get; set; }
            public int IncomingRows { get; set; }
            public int ExistingRows { get; set; }
            /// <summary>Sample of what the rows say, for the confirmation screen.</summary>
            public List<string> Sample { get; } = new();
        }

        /// <summary>
        /// Parses and validates a workbook without touching the database.
        /// A sheet that is absent is left alone on apply — it is not treated as "delete all".
        /// </summary>
        public async Task<(ImportPreview Preview, ParsedSettings? Parsed)> PreviewAsync(
            Stream file, int tenantId, CancellationToken ct = default)
        {
            var preview = new ImportPreview();
            XLWorkbook wb;
            try
            {
                wb = new XLWorkbook(file);
            }
            catch (Exception ex)
            {
                preview.Errors.Add($"تعذّرت قراءة الملف كمصنّف Excel / Could not read the file as a workbook: {ex.Message}");
                return (preview, null);
            }

            using (wb)
            {
                var parsed = new ParsedSettings();

                ParseMappings(wb, tenantId, parsed, preview);
                ParseOURules(wb, tenantId, parsed, preview);
                ParseGroupRules(wb, tenantId, parsed, preview);
                ParseLifecycleRules(wb, tenantId, parsed, preview);

                if (preview.Sections.All(s => !s.Present))
                {
                    preview.Errors.Add(
                        $"لم يُعثر على أي ورقة معروفة. الأوراق المتوقعة: {SheetMappings} / {SheetOU} / {SheetGroups} / {SheetLifecycle}");
                    return (preview, null);
                }

                await CrossValidateAsync(parsed, preview, tenantId, ct);
                return (preview, parsed);
            }
        }

        /// <summary>
        /// Replaces, per section, only the sections present in the workbook.
        /// One transaction: a failure part-way leaves the tenant's configuration untouched
        /// rather than half-imported.
        /// </summary>
        public async Task<int> ApplyAsync(ParsedSettings parsed, int tenantId, CancellationToken ct = default)
        {
            // The in-memory provider used by tests has no transaction support; against SQL Server
            // this is a real transaction and is what keeps a partial import from landing.
            var useTransaction = _db.Database.IsRelational();
            await using var tx = useTransaction ? await _db.Database.BeginTransactionAsync(ct) : null;
            int total = 0;

            if (parsed.Mappings != null)
            {
                _db.TenantAttributeMappings.RemoveRange(
                    _db.TenantAttributeMappings.Where(m => m.TenantId == tenantId));
                await _db.TenantAttributeMappings.AddRangeAsync(parsed.Mappings, ct);
                total += parsed.Mappings.Count;
            }

            if (parsed.OURules != null)
            {
                _db.TenantOURules.RemoveRange(_db.TenantOURules.Where(o => o.TenantId == tenantId));
                await _db.TenantOURules.AddRangeAsync(parsed.OURules, ct);
                total += parsed.OURules.Count;
            }

            if (parsed.GroupRules != null)
            {
                _db.TenantGroupRules.RemoveRange(_db.TenantGroupRules.Where(g => g.TenantId == tenantId));
                await _db.TenantGroupRules.AddRangeAsync(parsed.GroupRules, ct);
                total += parsed.GroupRules.Count;
            }

            if (parsed.LifecycleRules != null)
            {
                _db.LifecycleRules.RemoveRange(_db.LifecycleRules.Where(l => l.TenantId == tenantId));
                await _db.LifecycleRules.AddRangeAsync(parsed.LifecycleRules, ct);
                total += parsed.LifecycleRules.Count;
            }

            await _db.SaveChangesAsync(ct);
            if (tx != null) await tx.CommitAsync(ct);

            _logger.LogWarning("Settings import applied for tenant {TenantId}: {Count} rows across {Sections} section(s)",
                tenantId, total, new[] { parsed.Mappings, (object?)parsed.OURules, parsed.GroupRules, parsed.LifecycleRules }.Count(x => x != null));

            return total;
        }

        public class ParsedSettings
        {
            // null = the sheet was absent, so that section is left untouched on apply.
            public List<TenantAttributeMapping>? Mappings { get; set; }
            public List<TenantOURule>? OURules { get; set; }
            public List<TenantGroupRule>? GroupRules { get; set; }
            public List<LifecycleRule>? LifecycleRules { get; set; }
        }

        // ═══════════════════════════════════════
        // PARSING
        // ═══════════════════════════════════════

        private static IXLWorksheet? Find(XLWorkbook wb, string name) =>
            wb.Worksheets.FirstOrDefault(w => w.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Header name → column number, so column order in the file does not matter.</summary>
        private static Dictionary<string, int> HeaderMap(IXLWorksheet ws)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in ws.Row(1).CellsUsed())
            {
                var key = cell.GetString().Trim();
                if (key.Length > 0 && !map.ContainsKey(key)) map[key] = cell.Address.ColumnNumber;
            }
            return map;
        }

        /// <summary>
        /// Reads a cell as text. The string "NULL" is treated as empty — SQL tools export empty
        /// columns that way, and importing it literally would store the four characters N-U-L-L.
        /// </summary>
        private static string? Str(IXLRow row, Dictionary<string, int> map, string col)
        {
            if (!map.TryGetValue(col, out var c)) return null;
            var v = row.Cell(c).GetString().Trim();
            if (v.Length == 0 || v.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return null;
            return v;
        }

        private static bool Bool(IXLRow row, Dictionary<string, int> map, string col, bool fallback = false)
        {
            var s = Str(row, map, col);
            if (s == null) return fallback;
            if (bool.TryParse(s, out var b)) return b;
            if (int.TryParse(s, out var i)) return i != 0;
            return s.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || s.Equals("نعم", StringComparison.Ordinal);
        }

        private static int? Int(IXLRow row, Dictionary<string, int> map, string col)
        {
            var s = Str(row, map, col);
            return int.TryParse(s, out var i) ? i : null;
        }

        private static IEnumerable<IXLRow> DataRows(IXLWorksheet ws) =>
            ws.RowsUsed().Where(r => r.RowNumber() > 1);

        private void ParseMappings(XLWorkbook wb, int tenantId, ParsedSettings parsed, ImportPreview p)
        {
            var ws = Find(wb, SheetMappings);
            var section = new SectionSummary { Name = SheetMappings, Present = ws != null };
            p.Sections.Add(section);
            section.ExistingRows = _db.TenantAttributeMappings.Count(m => m.TenantId == tenantId);
            if (ws == null) return;

            var map = HeaderMap(ws);
            // Count only THIS sheet's header problems — an earlier sheet's errors must not stop
            // this one from being parsed, or the preview silently shows it as having no rows.
            var missing = new[] { "SourceColumn", "TargetAttribute" }.Where(c => !map.ContainsKey(c)).ToList();
            foreach (var required in missing)
                p.Errors.Add($"[{SheetMappings}] عمود مفقود / missing column: {required}");
            if (missing.Count > 0) return;

            var list = new List<TenantAttributeMapping>();
            foreach (var row in DataRows(ws))
            {
                var src = Str(row, map, "SourceColumn");
                var tgt = Str(row, map, "TargetAttribute");
                if (src == null && tgt == null) continue;
                if (src == null || tgt == null)
                {
                    p.Errors.Add($"[{SheetMappings}] السطر {row.RowNumber()}: SourceColumn و TargetAttribute إلزاميان");
                    continue;
                }

                list.Add(new TenantAttributeMapping
                {
                    TenantId = tenantId,
                    SourceColumn = src,
                    TargetAttribute = tgt,
                    Transform = Str(row, map, "Transform"),
                    DefaultValue = Str(row, map, "DefaultValue"),
                    IsRequired = Bool(row, map, "IsRequired"),
                    IsIdentifier = Bool(row, map, "IsIdentifier"),
                    SortOrder = Int(row, map, "SortOrder") ?? list.Count,
                    Condition = Str(row, map, "Condition")
                });
            }

            section.IncomingRows = list.Count;
            section.Sample.AddRange(list.Take(5).Select(m =>
                $"{m.SourceColumn} → {m.TargetAttribute}{(m.IsIdentifier ? " (معرّف)" : "")}"));

            var identifiers = list.Count(m => m.IsIdentifier);
            if (identifiers == 0)
                p.Errors.Add($"[{SheetMappings}] لا يوجد أي ربط معلَّم كـ IsIdentifier — بدونه لا يمكن اشتقاق اسم الحساب");
            else if (identifiers > 1)
                p.Errors.Add($"[{SheetMappings}] يوجد {identifiers} روابط معلَّمة IsIdentifier — يجب أن يكون واحداً فقط");

            parsed.Mappings = list;
        }

        private void ParseOURules(XLWorkbook wb, int tenantId, ParsedSettings parsed, ImportPreview p)
        {
            var ws = Find(wb, SheetOU);
            var section = new SectionSummary { Name = SheetOU, Present = ws != null };
            p.Sections.Add(section);
            section.ExistingRows = _db.TenantOURules.Count(o => o.TenantId == tenantId);
            if (ws == null) return;

            var map = HeaderMap(ws);
            if (!map.ContainsKey("OUTemplate"))
            {
                p.Errors.Add($"[{SheetOU}] عمود مفقود / missing column: OUTemplate");
                return;
            }

            var list = new List<TenantOURule>();
            foreach (var row in DataRows(ws))
            {
                var tpl = Str(row, map, "OUTemplate");
                if (tpl == null) continue;

                list.Add(new TenantOURule
                {
                    TenantId = tenantId,
                    OUTemplate = tpl,
                    Priority = Int(row, map, "Priority") ?? (list.Count + 1),
                    ConditionField = Str(row, map, "ConditionField"),
                    ConditionOperator = Str(row, map, "ConditionOperator"),
                    ConditionValue = Str(row, map, "ConditionValue"),
                    ValueMappings = Str(row, map, "ValueMappings"),
                    Description = Str(row, map, "Description")
                });
            }

            section.IncomingRows = list.Count;
            section.Sample.AddRange(list.Take(5).Select(o => $"P{o.Priority}: {o.OUTemplate}"));
            parsed.OURules = list;
        }

        private void ParseGroupRules(XLWorkbook wb, int tenantId, ParsedSettings parsed, ImportPreview p)
        {
            var ws = Find(wb, SheetGroups);
            var section = new SectionSummary { Name = SheetGroups, Present = ws != null };
            p.Sections.Add(section);
            section.ExistingRows = _db.TenantGroupRules.Count(g => g.TenantId == tenantId);
            if (ws == null) return;

            var map = HeaderMap(ws);
            if (!map.ContainsKey("GroupName"))
            {
                p.Errors.Add($"[{SheetGroups}] عمود مفقود / missing column: GroupName");
                return;
            }

            var list = new List<TenantGroupRule>();
            foreach (var row in DataRows(ws))
            {
                var name = Str(row, map, "GroupName");
                if (name == null) continue;

                var rule = new TenantGroupRule
                {
                    TenantId = tenantId,
                    GroupName = name,
                    GroupDN = Str(row, map, "GroupDN"),
                    IsDefault = Bool(row, map, "IsDefault"),
                    ConditionField = Str(row, map, "ConditionField"),
                    ConditionOperator = Str(row, map, "ConditionOperator"),
                    ConditionValue = Str(row, map, "ConditionValue"),
                    Description = Str(row, map, "Description")
                };

                // A condition field with no operator evaluates as "always true", which silently
                // applies the group to every identity — exactly the licence-group fault found
                // in production.
                if (rule.ConditionField != null && rule.ConditionOperator == null)
                    p.Errors.Add($"[{SheetGroups}] السطر {row.RowNumber()} ({name}): ConditionField موجود بلا ConditionOperator — القاعدة ستطابق كل هوية");

                list.Add(rule);
            }

            section.IncomingRows = list.Count;
            section.Sample.AddRange(list.Take(5).Select(g =>
                g.ConditionField != null
                    ? $"{g.ConditionField} {g.ConditionOperator} {g.ConditionValue} → {g.GroupName}"
                    : $"(الجميع) → {g.GroupName}"));
            parsed.GroupRules = list;
        }

        private static readonly HashSet<string> ValidOperators =
            new(StringComparer.OrdinalIgnoreCase) { "==", "!=", "in", "not_in" };

        /// <summary>
        /// A distinguished name is a comma-separated list of type=value pairs, so the minimum
        /// test is that it carries at least one '='. Deliberately loose: {BaseDN} is substituted
        /// later, and organisations use whatever OU layout they like.
        /// </summary>
        private static bool LooksLikeDn(string value) => value.Contains('=');

        private void ParseLifecycleRules(XLWorkbook wb, int tenantId, ParsedSettings parsed, ImportPreview p)
        {
            var ws = Find(wb, SheetLifecycle);
            var section = new SectionSummary { Name = SheetLifecycle, Present = ws != null };
            p.Sections.Add(section);
            section.ExistingRows = _db.LifecycleRules.Count(l => l.TenantId == tenantId);
            if (ws == null) return;

            var map = HeaderMap(ws);
            // Scoped to this sheet — see the note in ParseMappings.
            var missing = new[] { "Name", "ActionType" }.Where(c => !map.ContainsKey(c)).ToList();
            foreach (var required in missing)
                p.Errors.Add($"[{SheetLifecycle}] عمود مفقود / missing column: {required}");
            if (missing.Count > 0) return;

            var list = new List<LifecycleRule>();
            foreach (var row in DataRows(ws))
            {
                var name = Str(row, map, "Name");
                if (name == null) continue;

                var rule = new LifecycleRule
                {
                    TenantId = tenantId,
                    Name = name,
                    Description = Str(row, map, "Description"),
                    Enabled = Bool(row, map, "Enabled", true),
                    Priority = Int(row, map, "Priority") ?? 100,
                    TriggerType = Str(row, map, "TriggerType") ?? "OnImport",
                    ConditionField = Str(row, map, "ConditionField"),
                    ConditionOperator = Str(row, map, "ConditionOperator"),
                    ConditionValue = Str(row, map, "ConditionValue"),
                    ActionType = Str(row, map, "ActionType") ?? "SetState",
                    ActionValue = Str(row, map, "ActionValue"),
                    GracePeriodDays = Int(row, map, "GracePeriodDays"),
                    CreatedDate = DateTime.UtcNow,
                    ModifiedDate = DateTime.UtcNow
                };

                // The fault that motivated this whole feature: Excel treats a leading '=' as a
                // formula, so "==" round-trips as an empty cell. An empty operator makes
                // EvaluateCondition return true, and the rule then matches every identity.
                if (rule.ConditionField != null && rule.ConditionOperator == null)
                    p.Errors.Add($"[{SheetLifecycle}] السطر {row.RowNumber()} ({name}): ConditionField موجود بلا ConditionOperator — القاعدة ستطابق كل هوية");
                else if (rule.ConditionOperator != null && !ValidOperators.Contains(rule.ConditionOperator))
                    p.Errors.Add($"[{SheetLifecycle}] السطر {row.RowNumber()} ({name}): عامل غير معروف \"{rule.ConditionOperator}\" — المسموح: == != in not_in");

                if (rule.Name.Contains("سماح") && rule.GracePeriodDays is null or 0)
                    p.Warnings.Add($"[{SheetLifecycle}] ({name}): الاسم يذكر مهلة سماح لكن GracePeriodDays فارغ — التنفيذ سيكون فورياً");

                // MoveOU, Reactivate and Deprovision treat ActionValue as a distinguished name and
                // hand it straight to AD. A value that is not a DN — a lifecycle state name is the
                // easy mistake, since the same column holds one for SetState — produces a failed
                // move for every matching identity, with nothing to explain it but an LDAP error.
                if (rule.ActionType is "MoveOU" or "Reactivate" or "Deprovision"
                    && !string.IsNullOrWhiteSpace(rule.ActionValue)
                    && !LooksLikeDn(rule.ActionValue))
                {
                    p.Errors.Add($"[{SheetLifecycle}] السطر {row.RowNumber()} ({name}): ActionValue لـ {rule.ActionType} " +
                                 $"يجب أن يكون مسار OU مثل \"OU=Archive,{{BaseDN}}\" — القيمة \"{rule.ActionValue}\" ليست مساراً / " +
                                 $"ActionValue for {rule.ActionType} must be an OU path, not \"{rule.ActionValue}\"");
                }

                list.Add(rule);
            }

            section.IncomingRows = list.Count;
            section.Sample.AddRange(list.OrderBy(l => l.Priority).Take(5).Select(l =>
                $"P{l.Priority}: {l.ConditionField} {l.ConditionOperator} {l.ConditionValue} → {l.ActionType}: {l.ActionValue}"));
            parsed.LifecycleRules = list;
        }

        // ═══════════════════════════════════════
        // CROSS-SECTION VALIDATION
        // ═══════════════════════════════════════

        /// <summary>
        /// Best-known set of source view column names.
        ///
        /// Attribute mappings alone are NOT enough: a column can be used purely as a rule
        /// condition and never mapped to an AD attribute. A real tenant had exactly that —
        /// CITY_NO drove the group and OU rules but was never mapped (CITY_DESC was mapped
        /// instead), so validating against mapped columns only would have rejected a correct
        /// configuration. Condition fields from every rule type are folded in here.
        ///
        /// Still an approximation — the view may hold columns this system has never referenced —
        /// so callers should treat a miss as "probably wrong", not as proof.
        /// </summary>
        private async Task<List<string>> KnownSourceColumnsAsync(ParsedSettings parsed, int tenantId, CancellationToken ct)
        {
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddAll(IEnumerable<string?> values)
            {
                foreach (var v in values.Where(v => !string.IsNullOrWhiteSpace(v)))
                    cols.Add(v!.Trim());
            }

            AddAll(parsed.Mappings?.Select(m => m.SourceColumn)
                ?? await _db.TenantAttributeMappings.Where(m => m.TenantId == tenantId)
                       .Select(m => m.SourceColumn).ToListAsync(ct));

            AddAll(parsed.GroupRules?.Select(g => g.ConditionField)
                ?? await _db.TenantGroupRules.Where(g => g.TenantId == tenantId)
                       .Select(g => g.ConditionField).ToListAsync(ct));

            AddAll(parsed.OURules?.Select(o => o.ConditionField)
                ?? await _db.TenantOURules.Where(o => o.TenantId == tenantId)
                       .Select(o => o.ConditionField).ToListAsync(ct));

            AddAll(parsed.LifecycleRules?.Select(l => l.ConditionField)
                ?? await _db.LifecycleRules.Where(l => l.TenantId == tenantId)
                       .Select(l => l.ConditionField).ToListAsync(ct));

            return cols.ToList();
        }

        private async Task CrossValidateAsync(ParsedSettings parsed, ImportPreview p, int tenantId, CancellationToken ct)
        {
            var columns = await KnownSourceColumnsAsync(parsed, tenantId, ct);

            if (parsed.OURules != null)
            {
                foreach (var rule in parsed.OURules)
                    foreach (var err in MappingEngine.ValidateOURule(rule, columns.Count > 0 ? columns : null))
                        p.Errors.Add($"[{SheetOU}] {rule.OUTemplate}: {err}");
            }

            // Lifecycle rules that add or remove groups should name groups the tenant knows.
            var knownGroups = parsed.GroupRules?.Select(g => g.GroupName).ToList()
                ?? await _db.TenantGroupRules.Where(g => g.TenantId == tenantId)
                       .Select(g => g.GroupName).ToListAsync(ct);

            if (parsed.LifecycleRules != null && knownGroups.Count > 0)
            {
                foreach (var rule in parsed.LifecycleRules.Where(r =>
                             r.ActionType is "AddGroups" or "RemoveGroups"
                             && !string.IsNullOrWhiteSpace(r.ActionValue)
                             // {GroupRules} defers to the tenant's group rules; it is not a group name.
                             && !r.ActionValue!.Trim().Equals("{GroupRules}", StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var g in rule.ActionValue!.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0))
                    {
                        if (!knownGroups.Contains(g, StringComparer.OrdinalIgnoreCase))
                            p.Warnings.Add($"[{SheetLifecycle}] ({rule.Name}): المجموعة \"{g}\" غير معرَّفة في قواعد المجموعات — تأكد أنها موجودة في AD");
                    }
                }
            }

            // Two enabled rules at the same priority run in an undefined order.
            if (parsed.LifecycleRules != null)
            {
                var clashes = parsed.LifecycleRules.Where(r => r.Enabled)
                    .GroupBy(r => r.Priority).Where(g => g.Count() > 1);
                foreach (var clash in clashes)
                    p.Warnings.Add($"[{SheetLifecycle}] أكثر من قاعدة بالأولوية {clash.Key} ({string.Join("، ", clash.Select(r => r.Name))}) — ترتيب تنفيذها غير مضمون");
            }
        }
    }
}
