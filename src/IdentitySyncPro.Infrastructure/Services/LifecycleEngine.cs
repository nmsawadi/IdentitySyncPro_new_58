using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Identity;
using IdentitySyncPro.Core.Models.Metaverse;
using IdentitySyncPro.Core.Models.Rules;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Lifecycle Engine — orchestrates identity lifecycle through the Metaverse pipeline.
    /// 
    /// Pipeline: Source (Oracle) → Import → Metaverse → Rules Engine → Export → Target (AD)
    /// 
    /// State Transitions (all driven by Lifecycle Rules; OU targets come from each rule's ActionValue):
    ///   Pending → Active:          New identity provisioned to AD
    ///   Active → Suspended:        Identity status changed / deprovision rule matched
    ///   Suspended → Active:        Identity reactivated
    ///   Suspended → Deprovisioned: Grace period expired
    /// </summary>
    public class LifecycleEngine : ILifecycleEngine
    {
        private readonly AppDbContext _db;

        // Per-tenant connectors: reassigned by SetTenantContext for each processed tenant.
        private ISourceConnector _sourceConnector;
        private ITargetConnector _targetConnector;
        private readonly ITenantConnectorFactory? _connectorFactory;
        private int _currentTenantContextId; // avoids rebuilding connectors for the same tenant

        private readonly ILogger<LifecycleEngine> _logger;

        public LifecycleEngine(
            AppDbContext db,
            ISourceConnector sourceConnector,
            ITargetConnector targetConnector,
            ILogger<LifecycleEngine> logger,
            ITenantConnectorFactory? connectorFactory = null)
        {
            _db = db;
            _sourceConnector = sourceConnector;
            _targetConnector = targetConnector;
            _logger = logger;
            _connectorFactory = connectorFactory;
        }

        /// <summary>
        /// Resolve the tenant an operation should run under: the requested one, the entry's
        /// own tenant, or the first active tenant (legacy single-tenant behavior).
        /// </summary>
        private async Task<Core.Models.Settings.TenantSettings?> ResolveTenantAsync(int? tenantId, CancellationToken ct)
        {
            return tenantId.HasValue && tenantId.Value > 0
                ? await _db.TenantSettings.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId.Value, ct)
                : await _db.TenantSettings.AsNoTracking().FirstOrDefaultAsync(t => t.IsActive, ct);
        }

        /// <summary>
        /// Point the engine's connectors at the given tenant (no-op when unchanged or no factory).
        /// </summary>
        private void SetTenantContext(Core.Models.Settings.TenantSettings? tenant)
        {
            if (tenant == null || _connectorFactory == null) return;
            if (_currentTenantContextId == tenant.Id) return;

            _sourceConnector = _connectorFactory.CreateSourceConnector(tenant);
            _targetConnector = _connectorFactory.CreateTargetConnector(tenant);
            _currentTenantContextId = tenant.Id;
        }

        /// <summary>
        /// Import a identity identity into the Metaverse.
        /// Creates or updates the MetaverseEntry with latest data from Oracle.
        /// </summary>
        public async Task<MetaverseEntry> ImportToMetaverseAsync(int identityId, int? tenantId = null, CancellationToken ct = default)
        {
            var db = _db;

            var tenant = await ResolveTenantAsync(tenantId, ct);
            SetTenantContext(tenant);
            var scopeTenantId = tenant?.Id ?? 0;

            var identities = await _sourceConnector.ReadBatchAsync(new[] { identityId }, ct);
            var identity = identities.FirstOrDefault();

            if (identity == null)
                throw new InvalidOperationException($"Identity {identityId} not found in source system");

            // Find or create MetaverseEntry
            var entry = await db.MetaverseEntries
                .FirstOrDefaultAsync(e => e.TenantId == scopeTenantId && e.ExternalId == identityId.ToString(), ct);

            var isNew = entry == null;
            if (isNew)
            {
                entry = new MetaverseEntry
                {
                    TenantId = scopeTenantId,
                    ExternalId = identityId.ToString(),
                    IdentityType = "User",
                    LifecycleState = "Pending",
                    FirstSeenDate = DateTime.UtcNow,
                    CreatedDate = DateTime.UtcNow
                };
                db.MetaverseEntries.Add(entry);
            }

            // Update consolidated attributes
            var attrs = identity.ToDictionary();
            entry!.SetAttributes(attrs);
            entry.SourceStatusCode = identity.StatusCode;
            entry.SourceStatusDesc = identity.StatusDesc;

            // Update hash for change detection
            var newHash = identity.ComputeHash();
            if (entry.CurrentHash != newHash)
            {
                entry.PreviousHash = entry.CurrentHash;
                entry.CurrentHash = newHash;
            }

            entry.LastImportDate = DateTime.UtcNow;
            entry.ModifiedDate = DateTime.UtcNow;
            entry.SourceSystemsJson = JsonSerializer.Serialize(new[] { "Oracle" });

            // Log history — use navigation property so EF Core resolves FK after insert
            db.MetaverseHistory.Add(new MetaverseHistory
            {
                MetaverseEntry = entry,
                ChangeType = isNew ? "Import" : "Import",
                Details = isNew ? "First import from Oracle" : $"Updated from Oracle (StatusCode={identity.StatusCode})",
                TriggeredBy = "System",
                Timestamp = DateTime.UtcNow
            });

            await db.SaveChangesAsync(ct);
            return entry;
        }

        /// <summary>
        /// Apply all active lifecycle rules to a Metaverse entry.
        /// Evaluates conditions and determines state transitions.
        /// </summary>
        public async Task<LifecycleActionResult> ApplyLifecycleRulesAsync(MetaverseEntry entry, bool dryRun = false, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var db = _db;

            // The entry knows which tenant it belongs to — use that tenant's rules and connectors
            var tenant = await ResolveTenantAsync(entry.TenantId, ct);
            SetTenantContext(tenant);

            // Get active lifecycle rules for this tenant, ordered by priority
            var rules = tenant != null
                ? await db.LifecycleRules
                    .Where(r => r.TenantId == tenant.Id && r.Enabled)
                    .OrderBy(r => r.Priority)
                    .ToListAsync(ct)
                : new List<LifecycleRule>();

            var previousState = entry.LifecycleState;
            var actionsTaken = new List<string>();
            var failedActions = new List<string>();
            var attrs = BuildRuleAttributes(entry);

            foreach (var rule in rules)
            {
                // Only process OnImport rules here
                if (rule.TriggerType != "OnImport") continue;

                // Evaluate condition
                if (!EvaluateRuleCondition(rule, attrs))
                    continue;

                // Check grace period
                if (rule.GracePeriodDays.HasValue && rule.GracePeriodDays > 0)
                {
                    var daysSinceStateChange = (DateTime.UtcNow - entry.StateChangedDate).TotalDays;
                    if (daysSinceStateChange < rule.GracePeriodDays.Value)
                    {
                        _logger.LogDebug(
                            "Rule '{RuleName}' for {ExternalId}: grace period ({Days} days) not yet expired",
                            rule.Name, entry.ExternalId, rule.GracePeriodDays);
                        continue;
                    }
                }

                // Execute action (async to support AD operations like MoveOU, RemoveGroups)
                var newState = await ExecuteRuleActionAsync(entry, rule, tenant.ADBaseDN, dryRun, ct, failedActions);
                if (newState != null)
                {
                    actionsTaken.Add($"{rule.Name}: {rule.ActionType}={rule.ActionValue ?? newState}");
                }
            }

            // If no rule changed the lifecycle state, apply default status-based transitions
            if (entry.LifecycleState == previousState)
            {
                var defaultTransition = ApplyDefaultTransition(entry);
                if (defaultTransition != null)
                    actionsTaken.Add(defaultTransition);
            }

            // No LastExportDate is stamped here, so a failure cannot strand the identity the way
            // it can in the export path — but it must still be visible rather than absorbed into
            // a result that reports only what succeeded.
            if (failedActions.Count > 0)
            {
                _logger.LogError("LifecycleEngine: {ExternalId} — {Count} rule action(s) failed: {Failed}",
                    entry.ExternalId, failedActions.Count, string.Join("; ", failedActions));
                actionsTaken.Add("FAILED: " + string.Join("; ", failedActions));
            }

            // Fail only if no tenant AND no actions were taken
            if (tenant == null && actionsTaken.Count == 0)
            {
                return new LifecycleActionResult
                {
                    Success = false,
                    Error = "No active tenant found and no default transition applied"
                };
            }

            // Record state change if it happened
            if (entry.LifecycleState != previousState)
            {
                entry.PreviousState = previousState;
                entry.StateChangedDate = DateTime.UtcNow;

                db.MetaverseHistory.Add(new MetaverseHistory
                {
                    MetaverseEntry = entry,
                    ChangeType = "StateChange",
                    OldState = previousState,
                    NewState = entry.LifecycleState,
                    Details = string.Join("; ", actionsTaken),
                    TriggeredBy = "LifecycleEngine",
                    Timestamp = DateTime.UtcNow
                });
            }

            entry.ModifiedDate = DateTime.UtcNow;

            // A dry run reports what WOULD happen and leaves no trace — in AD (guarded inside
            // ExecuteRuleActionAsync) and here in the database. Discarding the tracked changes
            // keeps the state transition out of MetaverseEntries and its MetaverseHistory row
            // out of the audit trail, so a preview cannot be mistaken later for something that
            // actually ran.
            if (dryRun)
            {
                db.ChangeTracker.Clear();
            }
            else
            {
                await db.SaveChangesAsync(ct);
            }

            sw.Stop();
            return new LifecycleActionResult
            {
                Success = true,
                PreviousState = previousState,
                NewState = entry.LifecycleState,
                ActionsTaken = string.Join("; ", actionsTaken),
                DurationMs = (int)sw.ElapsedMilliseconds
            };
        }

        /// <summary>
        /// Export changes from Metaverse to AD based on lifecycle state.
        /// </summary>
        public async Task<LifecycleActionResult> ExportFromMetaverseAsync(MetaverseEntry entry, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var db = _db;

            // The entry knows which tenant it belongs to — use that tenant's rules and connectors
            var tenant = await ResolveTenantAsync(entry.TenantId, ct);
            SetTenantContext(tenant);

            if (tenant == null || string.IsNullOrWhiteSpace(tenant.ADBaseDN))
            {
                return new LifecycleActionResult { Success = false, Error = "No active tenant or BaseDN" };
            }

            var identity = entry.ExternalId;
            var actionsTaken = new List<string>();
            var failedActions = new List<string>();

            try
            {
                // Verifying that an Active account exists is an ADDITIONAL step, not an
                // alternative to running its rules — the same either/or that stopped every rule
                // firing for Active identities in the bulk path.
                if (entry.LifecycleState == "Active")
                {
                    var existsInAD = await _targetConnector.ExistsAsync(identity, ct);
                    if (existsInAD)
                    {
                        entry.ADAccountEnabled = true;
                        var provisionedTargets = new[] { "ActiveDirectory" };
                        entry.ProvisionedTargetsJson = JsonSerializer.Serialize(provisionedTargets);
                        actionsTaken.Add("Verified: Active in AD");
                    }
                }

                // OnExport rules run for every state, Active included.
                //
                // OnImport rules are deliberately NOT loaded here, unlike the bulk path.
                // The two pipelines divide the work differently: BulkApplyRules only records an
                // intended MoveOU and leaves BulkExport to carry it out, whereas
                // ApplyLifecycleRulesAsync has already executed every OnImport rule inline for
                // this identity. Running them again here would repeat every move and every group
                // change.
                var exportRules = await db.LifecycleRules
                    .Where(r => r.TenantId == tenant.Id && r.Enabled && r.TriggerType == "OnExport")
                    .OrderBy(r => r.Priority)
                    .ToListAsync(ct);

                if (exportRules.Count > 0)
                {
                    var attrs = BuildRuleAttributes(entry);

                    foreach (var rule in exportRules)
                    {
                        if (!EvaluateRuleCondition(rule, attrs))
                            continue;

                        var result = await ExecuteRuleActionAsync(entry, rule, tenant.ADBaseDN, false, ct, failedActions);
                        if (result != null)
                        {
                            actionsTaken.Add($"{rule.Name}: {result}");
                        }
                    }
                }

                // Same rule as the bulk path: LastExportDate is the "this entry is finished"
                // marker, and the re-export query only revisits an entry whose lifecycle state
                // changed after it. Stamping it over a failed action strands the identity for
                // good. See the matching comment in BulkExportAsync.
                if (failedActions.Count == 0)
                {
                    entry.LastExportDate = DateTime.UtcNow;
                }
                else
                {
                    _logger.LogError(
                        "LifecycleEngine: {ExternalId} was NOT marked exported — {Count} action(s) failed: {Failed}. " +
                        "It will be retried on the next run.",
                        entry.ExternalId, failedActions.Count, string.Join("; ", failedActions));
                }

                if (actionsTaken.Count > 0 || failedActions.Count > 0)
                {
                    var details = string.Join("; ", actionsTaken);
                    if (failedActions.Count > 0)
                        details = string.IsNullOrEmpty(details)
                            ? "FAILED: " + string.Join("; ", failedActions)
                            : details + " | FAILED: " + string.Join("; ", failedActions);

                    db.MetaverseHistory.Add(new MetaverseHistory
                    {
                        MetaverseEntry = entry,
                        ChangeType = "Export",
                        Details = details,
                        TriggeredBy = "LifecycleEngine",
                        Timestamp = DateTime.UtcNow
                    });
                }

                await db.SaveChangesAsync(ct);

                sw.Stop();
                return new LifecycleActionResult
                {
                    Success = true,
                    ActionsTaken = string.Join("; ", actionsTaken),
                    DurationMs = (int)sw.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "Export failed for identity {ExternalId}", entry.ExternalId);
                return new LifecycleActionResult
                {
                    Success = false,
                    Error = ex.Message,
                    DurationMs = (int)sw.ElapsedMilliseconds
                };
            }
        }

        /// <summary>
        /// Full pipeline: Import → Rules → Export for a single identity.
        /// </summary>
        public async Task<LifecycleActionResult> ProcessIdentityAsync(int identityId, bool dryRun = false, int? tenantId = null, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                // Stage 1: Import
                var entry = await ImportToMetaverseAsync(identityId, tenantId, ct);

                // Stage 2: Apply Rules
                var rulesResult = await ApplyLifecycleRulesAsync(entry, dryRun, ct);

                if (!rulesResult.Success)
                {
                    sw.Stop();
                    return new LifecycleActionResult
                    {
                        Success = false,
                        Error = rulesResult.Error ?? "Lifecycle rules failed",
                        DurationMs = (int)sw.ElapsedMilliseconds
                    };
                }

                // Stage 3: Export (skip in dry run)
                LifecycleActionResult? exportResult = null;
                if (!dryRun)
                {
                    exportResult = await ExportFromMetaverseAsync(entry, ct);
                }

                sw.Stop();
                return new LifecycleActionResult
                {
                    Success = true,
                    PreviousState = rulesResult.PreviousState,
                    NewState = entry.LifecycleState,
                    ActionsTaken = $"Import → {rulesResult.ActionsTaken ?? "NoChange"} → {(dryRun ? "DryRun" : exportResult?.ActionsTaken ?? "NoExport")}",
                    DurationMs = (int)sw.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "Pipeline failed for identity {IdentityId}", identityId);
                
                // ✅ Clear poisoned entities from change tracker to prevent corrupting shared DbContext
                _db.ChangeTracker.Clear();
                
                return new LifecycleActionResult
                {
                    Success = false,
                    Error = ex.Message,
                    DurationMs = (int)sw.ElapsedMilliseconds
                };
            }
        }

        /// <summary>
        /// Get lifecycle statistics for dashboard.
        /// </summary>
        public async Task<LifecycleStats> GetStatsAsync(int? tenantId = null, CancellationToken ct = default)
        {
            var db = _db;
            var sevenDaysAgo = DateTime.UtcNow.AddDays(-7);

            // null tenantId = totals across every tenant; a value scopes every count to it.
            var entries = db.MetaverseEntries.AsQueryable();
            var history = db.MetaverseHistory.AsQueryable();
            var rules = db.LifecycleRules.AsQueryable();

            if (tenantId.HasValue)
            {
                entries = entries.Where(e => e.TenantId == tenantId.Value);
                // History has no TenantId of its own — scope it through its parent entry.
                history = history.Where(h => db.MetaverseEntries
                    .Any(e => e.Id == h.MetaverseEntryId && e.TenantId == tenantId.Value));
                rules = rules.Where(r => r.TenantId == tenantId.Value);
            }

            return new LifecycleStats
            {
                TotalIdentities = await entries.CountAsync(ct),
                PendingCount = await entries.CountAsync(e => e.LifecycleState == "Pending", ct),
                ActiveCount = await entries.CountAsync(e => e.LifecycleState == "Active", ct),
                SuspendedCount = await entries.CountAsync(e => e.LifecycleState == "Suspended", ct),
                DeprovisionedCount = await entries.CountAsync(e => e.LifecycleState == "Deprovisioned", ct),
                RecentTransitions = await history
                    .CountAsync(h => h.ChangeType == "StateChange" && h.Timestamp >= sevenDaysAgo, ct),
                RulesCount = await rules.CountAsync(ct),
                EnabledRulesCount = await rules.CountAsync(r => r.Enabled, ct)
            };
        }

        // ═══════════════════════════════════════
        // PRIVATE HELPERS
        // ═══════════════════════════════════════

        /// <summary>
        /// Attributes for rule evaluation: the raw source row, plus canonical aliases, plus
        /// case-insensitive lookup.
        ///
        /// Source column names belong to each tenant's own view — one production tenant's status
        /// column is literally named STATUSE_CODE — while lifecycle rules are written against the
        /// canonical STATUS_CODE that the seed data and the UI both use. A rule naming a column
        /// that does not exist evaluates its field as null, and an empty field never equals the
        /// condition value, so the rule silently never fires.
        ///
        /// That is exactly what happened: all ten of a tenant's lifecycle rules matched nothing
        /// across 111,465 identities, leaving graduates (STATUSE_CODE 7) sitting Active in their
        /// original OU with no error anywhere.
        ///
        /// The alias is only added when the row does not already carry that key, so a tenant
        /// whose column really is called STATUS_CODE keeps its own value.
        /// </summary>
        /// <summary>
        /// Reports source status codes that no enabled rule reacts to.
        ///
        /// Organisations define rules for the statuses they have in mind; a rare or newly added
        /// code then arrives with nothing to handle it. Those identities keep whatever lifecycle
        /// state they already had and are quietly skipped — no error, no counter, nothing to
        /// notice. One tenant carried 57 such identities across four unmapped codes and only
        /// found out because a single account failed to move.
        ///
        /// This is deliberately a warning, not an error: an unmapped code is often intentional.
        /// The point is that it should be visible rather than silent.
        /// </summary>
        private void WarnAboutUncoveredStatusCodes(List<MetaverseEntry> entries, List<LifecycleRule> rules)
        {
            var uncovered = entries
                .GroupBy(e => e.SourceStatusCode)
                .Where(g => !rules.Any(r => EvaluateRuleCondition(r, BuildRuleAttributes(g.First()))))
                .OrderByDescending(g => g.Count())
                .ToList();

            foreach (var group in uncovered)
            {
                _logger.LogWarning(
                    "BulkRules: source status code {StatusCode} ({Desc}) matches no enabled lifecycle rule — " +
                    "{Count} identities will keep their current state and be skipped",
                    group.Key, group.First().SourceStatusDesc ?? "—", group.Count());
            }
        }

        /// <summary>
        /// Names a rule may condition on that are NOT source columns — <see cref="BuildRuleAttributes"/>
        /// synthesises them for every identity, whatever the tenant's view calls its own columns.
        ///
        /// Declared here, and consumed both by the synthesis below and by the run-start check that
        /// looks for rules naming a column that does not exist. If the two lists ever drifted apart,
        /// that check would report a working rule as broken.
        /// </summary>
        public static readonly IReadOnlyList<string> SyntheticRuleFields = new[] { "STATUS_CODE", "IDENTITY_ID" };

        private static Dictionary<string, object?> BuildRuleAttributes(MetaverseEntry entry)
        {
            // JsonSerializer hands back a case-sensitive dictionary; rules should not have to
            // match the source view's capitalisation.
            var attrs = new Dictionary<string, object?>(entry.GetAttributes(), StringComparer.OrdinalIgnoreCase);

            if (!attrs.ContainsKey("STATUS_CODE"))
                attrs["STATUS_CODE"] = entry.SourceStatusCode;

            if (!attrs.ContainsKey("IDENTITY_ID"))
                attrs["IDENTITY_ID"] = entry.ExternalId;

            return attrs;
        }

        private bool EvaluateRuleCondition(LifecycleRule rule, Dictionary<string, object?> attrs)
        {
            if (string.IsNullOrEmpty(rule.ConditionField)) return true;

            string? fieldValue = null;
            if (attrs.TryGetValue(rule.ConditionField, out var val) && val != null)
                fieldValue = val.ToString();

            return EvaluateCondition(fieldValue, rule.ConditionOperator, rule.ConditionValue);
        }

        private static bool EvaluateCondition(string? fieldValue, string? op, string? conditionValue)
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
                _ => true
            };
        }

        /// <summary>
        /// The ActionValue that means "work out the groups from this tenant's group rules"
        /// instead of naming them literally.
        /// </summary>
        private const string GroupRulesToken = "{GroupRules}";

        private static bool IsGroupRulesToken(string actionValue) =>
            actionValue.Trim().Equals(GroupRulesToken, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The ActionValue that means "work out the OU from this tenant's OU rules" instead of
        /// naming one literally.
        /// </summary>
        private const string OuRulesToken = "{OURules}";

        private static bool IsOuRulesToken(string? actionValue) =>
            actionValue?.Trim().Equals(OuRulesToken, StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>
        /// Turns a lifecycle rule's ActionValue into a target DN.
        ///
        /// A literal ActionValue only ever had {BaseDN} substituted, so it can express one fixed OU
        /// for everybody. That is enough to archive graduates, and not enough to send a returning
        /// identity home: the home OU is per-identity, built by the tenant's OU rules from source
        /// data (one tenant nests by gender and city). Reactivation could therefore restore the
        /// state and the groups but never the location — the account stayed in the OU it was parked
        /// in, with the database reporting Active.
        ///
        /// <c>{OURules}</c> resolves through the same <see cref="MappingEngine.ResolveOU"/> that
        /// account creation uses, so a reactivated identity lands exactly where a newly created one
        /// would. It mirrors <c>{GroupRules}</c>, which exists for the same reason on the group side.
        /// </summary>
        /// <returns>The DN, or null if the token cannot be resolved — the caller treats that as a
        /// failed action rather than moving the account somewhere arbitrary.</returns>
        private async Task<string?> ResolveActionOuAsync(
            MetaverseEntry entry, LifecycleRule rule, string baseDN, CancellationToken ct)
        {
            var actionValue = rule.ActionValue ?? "";

            // Skip the query entirely when the value needs nothing from it.
            if (!IsOuRulesToken(actionValue) && !HasFieldPlaceholder(actionValue))
                return actionValue.Replace("{BaseDN}", baseDN);

            var ouRules = await _db.TenantOURules
                .AsNoTracking()
                .Where(o => o.TenantId == entry.TenantId)
                .ToListAsync(ct);

            return ResolveActionOu(entry, rule, baseDN, ouRules);
        }

        /// <summary>
        /// The resolution itself, with the tenant's OU rules already in hand. The bulk path loads
        /// them once for the whole batch rather than once per identity.
        /// </summary>
        private string? ResolveActionOu(
            MetaverseEntry entry, LifecycleRule rule, string baseDN, List<TenantOURule> ouRules)
        {
            var actionValue = rule.ActionValue ?? "";

            // ── 1. A literal DN with nothing to substitute but {BaseDN} ──────────────
            // The overwhelmingly common case, and the one every existing tenant uses. It takes the
            // exact path it always took: one Replace, no rules loaded, no behaviour to regress.
            if (!IsOuRulesToken(actionValue) && !HasFieldPlaceholder(actionValue))
                return actionValue.Replace("{BaseDN}", baseDN);

            if (ouRules.Count == 0)
            {
                _logger.LogError(
                    "Rule '{RuleName}' needs the tenant's OU rules to resolve \"{ActionValue}\", but tenant " +
                    "{TenantId} has none — cannot place {ExternalId}",
                    rule.Name, actionValue, entry.TenantId, entry.ExternalId);
                return null;
            }

            var attrs = BuildRuleAttributes(entry);

            // ── 2. {OURules}: the identity's home OU, exactly as account creation builds it ──
            if (IsOuRulesToken(actionValue))
            {
                var resolved = MappingEngine.ResolveOU(attrs, ouRules, baseDN, _logger);

                // ResolveOU falls back to the bare baseDN when nothing matches. Moving an identity
                // to the domain root is never the intent, and not a failure this should absorb.
                if (string.IsNullOrWhiteSpace(resolved) ||
                    resolved.Equals(baseDN, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError(
                        "Rule '{RuleName}': {Token} resolved to no OU for {ExternalId} — the tenant's OU rules " +
                        "matched nothing, so the account is left where it is rather than moved to the domain root",
                        rule.Name, OuRulesToken, entry.ExternalId);
                    return null;
                }

                return resolved;
            }

            // ── 3. A template of its own: a fixed root, subdivided per identity ──────
            // {OURules} gives the whole home OU and a literal gives one OU for everybody. Neither
            // expresses the shape an archive actually has: a fixed root with the same subdivision
            // underneath it — OU=MALE and OU=FEMALE inside OU=GRADUATES. Writing
            // "OU=GRADUATES,{BaseDN}" therefore put every graduate, male and female, in the root of
            // the archive, because {GENDER_CODE} was never substituted anywhere but the OU rules.
            //
            // The ActionValue is treated as a template in its own right, translated through the
            // tenant's existing ValueMappings so GENDER_CODE 1 becomes MALE here exactly as it does
            // at creation — one declaration, not a second copy to keep in step.
            var synthetic = new TenantOURule
            {
                OUTemplate = actionValue,
                ValueMappings = MappingEngine.MergeValueMappings(ouRules.OrderBy(o => o.Priority))
            };

            return MappingEngine.ResolveOU(attrs, new List<TenantOURule> { synthetic }, baseDN, _logger);
        }

        /// <summary>
        /// Does this ActionValue carry a placeholder that needs source data — anything but
        /// {BaseDN}, which is substituted without consulting the identity at all?
        /// </summary>
        private static bool HasFieldPlaceholder(string actionValue) =>
            Regex.Matches(actionValue, @"\{(\w+)\}")
                 .Select(m => m.Groups[1].Value)
                 .Any(p => !p.Equals("BaseDN", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Resolves the groups the tenant's own group rules assign to this identity — the same
        /// rules, and the same MappingEngine call, that decide group membership when an account
        /// is first created.
        ///
        /// Why this exists: group rules are conditional on source data (one tenant assigns an
        /// Office licence group per campus via CITY_NO), but a lifecycle rule carries a single
        /// condition, so it cannot express "active AND in this city". Naming the groups literally
        /// therefore meant one AddGroups rule per city with no status condition, which added
        /// every identity — including graduates and withdrawn students — to a licence group that
        /// a later rule then removed.
        ///
        /// On 91,504 accounts that is not merely wasted work: if directory sync runs inside that
        /// window, a licence is assigned and withdrawn for real, which drives mailbox
        /// provisioning and deprovisioning in Exchange Online.
        /// </summary>
        private async Task<IEnumerable<string>> ResolveGroupsFromTenantRulesAsync(MetaverseEntry entry, CancellationToken ct)
        {
            var groupRules = await _db.TenantGroupRules
                .AsNoTracking()
                .Where(g => g.TenantId == entry.TenantId)
                .ToListAsync(ct);

            if (groupRules.Count == 0)
            {
                _logger.LogWarning(
                    "AddGroups rule uses {Token} but tenant {TenantId} has no group rules — no groups added for {ExternalId}",
                    GroupRulesToken, entry.TenantId, entry.ExternalId);
                return Enumerable.Empty<string>();
            }

            return MappingEngine.ResolveGroups(BuildRuleAttributes(entry), groupRules);
        }

        /// <param name="failedActions">
        /// Optional. A returned null is ambiguous on its own — it means both "the rule had nothing
        /// to do" and "the connector refused". Callers that must tell those apart (anything that
        /// stamps LastExportDate) pass a list here and read it after the call.
        /// </param>
        private async Task<string?> ExecuteRuleActionAsync(MetaverseEntry entry, LifecycleRule rule, string baseDN,
            bool dryRun = false, CancellationToken ct = default, List<string>? failedActions = null)
        {
            // Every action below this line except SetState touches AD. Reporting the intended
            // action and returning is what makes a dry run a preview rather than a rehearsal —
            // ProcessIdentityAsync used to guard only its export stage, so a "dry run" from the
            // lifecycle page moved the account for real.
            if (dryRun && rule.ActionType != "SetState")
                return $"DryRun: would {rule.ActionType}" +
                       (string.IsNullOrWhiteSpace(rule.ActionValue) ? "" : $" → {rule.ActionValue}");

            switch (rule.ActionType)
            {
                case "SetState":
                    if (!string.IsNullOrEmpty(rule.ActionValue))
                    {
                        entry.LifecycleState = rule.ActionValue;
                        return rule.ActionValue;
                    }
                    break;

                case "MoveOU":
                    // Move account to specified OU — supports {BaseDN} placeholder
                    if (!string.IsNullOrEmpty(rule.ActionValue))
                    {
                        var moveTargetOU = await ResolveActionOuAsync(entry, rule, baseDN, ct);
                        if (moveTargetOU == null)
                        {
                            failedActions?.Add($"{rule.Name}: MoveOU target could not be resolved");
                            break;
                        }

                        var moved = await _targetConnector.MoveToOUAsync(entry.ExternalId, moveTargetOU, ct);
                        if (moved)
                        {
                            entry.ADCurrentOU = moveTargetOU;
                            _logger.LogInformation("LifecycleEngine: Moved {ExternalId} to {OU} via rule '{RuleName}'",
                                entry.ExternalId, moveTargetOU, rule.Name);
                            return $"Moved to {moveTargetOU}";
                        }

                        failedActions?.Add($"{rule.Name}: MoveOU to {moveTargetOU} failed");
                    }
                    break;

                case "RemoveGroups":
                    // If ActionValue specifies group names (comma-separated), remove only from those groups.
                    // If ActionValue is empty/null, remove from ALL groups (except primary group).
                    if (!string.IsNullOrWhiteSpace(rule.ActionValue))
                    {
                        var specificGroups = rule.ActionValue.Split(',').Select(g => g.Trim()).Where(g => !string.IsNullOrEmpty(g));
                        var specificResult = await _targetConnector.RemoveFromSpecificGroupsAsync(entry.ExternalId, specificGroups, ct);
                        if (specificResult.Success && specificResult.RemovedCount > 0)
                        {
                            _logger.LogInformation("LifecycleEngine: Removed {ExternalId} from {Count} specific groups via rule '{RuleName}': {Groups}",
                                entry.ExternalId, specificResult.RemovedCount, rule.Name, string.Join(", ", specificResult.GroupNames));
                            return $"Removed from {specificResult.RemovedCount} groups: {string.Join(", ", specificResult.GroupNames)}";
                        }
                        if (specificResult.Success) return "No matching groups to remove";
                        failedActions?.Add($"{rule.Name}: RemoveGroups failed");
                        return null;
                    }
                    else
                    {
                        var removeResult = await _targetConnector.RemoveFromAllGroupsAsync(entry.ExternalId, ct);
                        if (removeResult.Success && removeResult.RemovedCount > 0)
                        {
                            _logger.LogInformation("LifecycleEngine: Removed {ExternalId} from {Count} groups via rule '{RuleName}'",
                                entry.ExternalId, removeResult.RemovedCount, rule.Name);
                            return $"Removed from {removeResult.RemovedCount} groups: {string.Join(", ", removeResult.GroupNames)}";
                        }
                        if (removeResult.Success) return "No groups to remove";
                        failedActions?.Add($"{rule.Name}: RemoveGroups (all) failed");
                        return null;
                    }

                case "Deprovision":
                    // Update Metaverse state + optional OU move + Remove from groups
                    // ⛔ SAFETY: AD account is NOT disabled (Safe Sync mode)
                    entry.LifecycleState = "Suspended";
                    _logger.LogWarning("⛔ Deprovision rule matched for {ExternalId} — AD account NOT disabled (Safe Sync)", entry.ExternalId);

                    // Move to the OU named by the rule (ActionValue), if any. No org-specific default.
                    if (!string.IsNullOrWhiteSpace(rule.ActionValue))
                    {
                        var deprovOU = await ResolveActionOuAsync(entry, rule, baseDN, ct);
                        if (deprovOU == null)
                        {
                            failedActions?.Add($"{rule.Name}: Deprovision OU could not be resolved");
                        }
                        else
                        {
                            var mvResult = await _targetConnector.MoveToOUAsync(entry.ExternalId, deprovOU, ct);
                            if (mvResult) entry.ADCurrentOU = deprovOU;
                            else failedActions?.Add($"{rule.Name}: Deprovision move to {deprovOU} failed");
                        }
                    }

                    // Remove from all groups. The result used to be discarded outright, so a
                    // deprovisioned identity could keep every group it had and still report
                    // "Suspended".
                    var deprovRemove = await _targetConnector.RemoveFromAllGroupsAsync(entry.ExternalId, ct);
                    if (!deprovRemove.Success)
                        failedActions?.Add($"{rule.Name}: Deprovision group removal failed");

                    return "Suspended";

                case "Reactivate":
                    // Full reactivation: change state + move to active OU + optionally add groups
                    entry.LifecycleState = "Active";
                    entry.ADAccountEnabled = true;

                    // Move to active identities OU if specified in ActionValue
                    // ActionValue format: "OU=Identities,{BaseDN}" or "OU=DEPT-A,OU=Identities,{BaseDN}"
                    if (!string.IsNullOrEmpty(rule.ActionValue))
                    {
                        var reactivateOU = await ResolveActionOuAsync(entry, rule, baseDN, ct);
                        if (reactivateOU == null)
                        {
                            failedActions?.Add($"{rule.Name}: Reactivate OU could not be resolved");
                            return "Active";
                        }

                        var reactivateMoved = await _targetConnector.MoveToOUAsync(entry.ExternalId, reactivateOU, ct);
                        if (reactivateMoved)
                        {
                            entry.ADCurrentOU = reactivateOU;
                            _logger.LogInformation("LifecycleEngine: Reactivated and moved {ExternalId} to {OU} via rule '{RuleName}'",
                                entry.ExternalId, reactivateOU, rule.Name);
                            return $"Reactivated → Moved to {reactivateOU}";
                        }

                        failedActions?.Add($"{rule.Name}: Reactivate move to {reactivateOU} failed");
                    }
                    return "Active";

                case "AddGroups":
                    // Add user to specified AD groups (comma-separated in ActionValue), or to the
                    // groups the tenant's own group rules resolve for this identity.
                    if (!string.IsNullOrWhiteSpace(rule.ActionValue))
                    {
                        var groupsToAdd = IsGroupRulesToken(rule.ActionValue)
                            ? await ResolveGroupsFromTenantRulesAsync(entry, ct)
                            : rule.ActionValue.Split(',').Select(g => g.Trim()).Where(g => !string.IsNullOrEmpty(g));

                        if (!groupsToAdd.Any()) return "No groups resolved for this identity";

                        var addResult = await _targetConnector.AddToGroupsAsync(entry.ExternalId, groupsToAdd, ct);
                        if (addResult.Success && addResult.AddedCount > 0)
                        {
                            _logger.LogInformation("LifecycleEngine: Added {ExternalId} to {Count} groups via rule '{RuleName}': {Groups}",
                                entry.ExternalId, addResult.AddedCount, rule.Name, string.Join(", ", addResult.GroupNames));
                            return $"Added to {addResult.AddedCount} groups: {string.Join(", ", addResult.GroupNames)}";
                        }
                        if (addResult.Success) return "No groups to add";
                        failedActions?.Add($"{rule.Name}: AddGroups failed");
                        return null;
                    }
                    break;
            }

            return null;
        }

        /// <summary>
        /// Generic default transition when no Lifecycle Rule matches: a freshly imported
        /// identity (Pending) is marked Active. Everything else is left untouched —
        /// organizations drive all other transitions and OU moves via Lifecycle Rules.
        /// (No org-specific status semantics or OU moves are assumed here.)
        /// </summary>
        private static string? ApplyDefaultTransition(MetaverseEntry entry)
        {
            if (entry.LifecycleState == "Pending")
            {
                entry.LifecycleState = "Active";
                return "Default: new identity → Active";
            }

            return null;
        }

        // ═══════════════════════════════════════
        // BULK 3-STAGE PIPELINE
        // ═══════════════════════════════════════

        public async Task<int> BulkImportPendingAsync(int? tenantId = null, Action<int, int>? onProgress = null, CancellationToken ct = default)
        {
            var db = _db;
            const int fetchSize = 5000;

            var tenant = await ResolveTenantAsync(tenantId, ct);
            SetTenantContext(tenant);
            var scopeTenantId = tenant?.Id ?? 0;

            // Get ALL identity IDs from the source (not just Pending in MetaverseEntries)
            var oracleIds = (await _sourceConnector.ReadAllIdsAsync(ct)).ToList();
            if (oracleIds.Count == 0)
            {
                _logger.LogWarning("BulkImport Stage 1: No identities found in the source");
                return 0;
            }

            _logger.LogInformation("BulkImport Stage 1: Found {Total} identities in the source, importing all...", oracleIds.Count);

            // ✅ Fix #5: Single dictionary load instead of loading IDs + full entries separately
            var existingEntries = await db.MetaverseEntries
                .Where(e => e.TenantId == scopeTenantId)
                .ToDictionaryAsync(e => e.ExternalId, ct);

            // Filter to only process identities NOT yet in MetaverseEntries
            var newIds = oracleIds.Where(id => !existingEntries.ContainsKey(id.ToString())).ToList();
            var total = newIds.Count;

            if (total == 0)
            {
                _logger.LogInformation("BulkImport Stage 1: All {Total} identities already imported, checking for status changes...", oracleIds.Count);

                // Update SourceStatusCode for all existing entries (for Stage 2 re-evaluation)
                int statusUpdated = 0;
                for (int chunk = 0; chunk < oracleIds.Count; chunk += fetchSize)
                {
                    var batchIds = oracleIds.Skip(chunk).Take(fetchSize).ToArray();
                    var identities = await _sourceConnector.ReadBatchAsync(batchIds, ct);
                    foreach (var identity in identities)
                    {
                        var extId = identity.Key.ToString();
                        if (existingEntries.TryGetValue(extId, out var entry))
                        {
                            // Mark for rule re-evaluation only if status actually changed
                            if (entry.SourceStatusCode != identity.StatusCode)
                            {
                                entry.NeedsRuleEval = true;
                                statusUpdated++;
                            }
                            entry.SourceStatusCode = identity.StatusCode;
                            entry.SourceStatusDesc = identity.StatusDesc;
                        }
                    }
                    await db.SaveChangesAsync(ct);
                    onProgress?.Invoke(Math.Min(chunk + fetchSize, oracleIds.Count), oracleIds.Count);
                }

                _logger.LogInformation("BulkImport Stage 1: Updated status for {Count} existing identities, {Changed} need rule re-evaluation", oracleIds.Count, statusUpdated);
                return 0;
            }

            _logger.LogInformation("BulkImport Stage 1: Loading {Total} NEW identities from Oracle", total);

            var allIdentities = new Dictionary<int, SourceRecord>();
            for (int chunk = 0; chunk < newIds.Count; chunk += fetchSize)
            {
                var batchIds = newIds.Skip(chunk).Take(fetchSize).ToArray();
                var identities = await _sourceConnector.ReadBatchAsync(batchIds, ct);
                foreach (var s in identities)
                    allIdentities[s.Key] = s;

                onProgress?.Invoke(Math.Min(chunk + fetchSize, total), total);
            }

            _logger.LogInformation("BulkImport Stage 1: Loaded {Count} identities from Oracle", allIdentities.Count);

            int imported = 0;
            var now = DateTime.UtcNow;
            var entryHistory = new List<(MetaverseEntry entry, string details)>();

            foreach (var identity in allIdentities.Values)
            {
                var externalId = identity.Key.ToString();
                if (!existingEntries.TryGetValue(externalId, out var entry))
                {
                    entry = new MetaverseEntry
                    {
                        TenantId = scopeTenantId,
                        ExternalId = externalId,
                        IdentityType = "User",
                        LifecycleState = "Pending",
                        NeedsRuleEval = true,
                        FirstSeenDate = now,
                        CreatedDate = now
                    };
                    db.MetaverseEntries.Add(entry);
                    existingEntries[externalId] = entry;
                }

                var attrs = identity.ToDictionary();
                entry.SetAttributes(attrs);
                entry.SourceStatusCode = identity.StatusCode;
                entry.SourceStatusDesc = identity.StatusDesc;

                var newHash = identity.ComputeHash();
                if (entry.CurrentHash != newHash)
                {
                    entry.PreviousHash = entry.CurrentHash;
                    entry.CurrentHash = newHash;
                    entry.NeedsRuleEval = true;
                }

                entry.LastImportDate = now;
                entry.ModifiedDate = now;
                entry.SourceSystemsJson = JsonSerializer.Serialize(new[] { "Oracle" });

                entryHistory.Add((entry, entry.CurrentHash == newHash ? "Updated from Oracle" : "First import from Oracle"));
                imported++;

                if (entryHistory.Count >= 1000)
                {
                    await db.SaveChangesAsync(ct);

                    var historyToAdd = entryHistory.Select(h => new MetaverseHistory
                    {
                        MetaverseEntryId = h.entry.Id,
                        ChangeType = "Import",
                        Details = h.details,
                        TriggeredBy = "BulkPipeline",
                        Timestamp = now
                    }).ToList();

                    db.MetaverseHistory.AddRange(historyToAdd);
                    await db.SaveChangesAsync(ct);

                    entryHistory.Clear();
                    _logger.LogDebug("BulkImport: saved batch of 1000");
                }
            }

            if (entryHistory.Count > 0)
            {
                await db.SaveChangesAsync(ct);

                var historyToAdd = entryHistory.Select(h => new MetaverseHistory
                {
                    MetaverseEntryId = h.entry.Id,
                    ChangeType = "Import",
                    Details = h.details,
                    TriggeredBy = "BulkPipeline",
                    Timestamp = now
                }).ToList();

                db.MetaverseHistory.AddRange(historyToAdd);
                await db.SaveChangesAsync(ct);
            }

            _logger.LogInformation("BulkImport Stage 1 complete: {Imported} imported, {Total} total identities in Oracle", imported, oracleIds.Count);
            return imported;
        }

        public async Task<int> BulkApplyRulesAsync(int? tenantId = null, Action<int, int>? onProgress = null, CancellationToken ct = default)
        {
            var db = _db;

            var tenant = await ResolveTenantAsync(tenantId, ct);
            SetTenantContext(tenant);
            var scopeTenantId = tenant?.Id ?? 0;

            var rules = tenant != null
                ? await db.LifecycleRules
                    .Where(r => r.TenantId == tenant.Id && r.Enabled && r.TriggerType == "OnImport")
                    .OrderBy(r => r.Priority)
                    .ToListAsync(ct)
                : new List<LifecycleRule>();

            var entries = await db.MetaverseEntries
                .Where(e => e.TenantId == scopeTenantId && e.NeedsRuleEval)
                .ToListAsync(ct);

            var total = entries.Count;
            if (total == 0)
            {
                _logger.LogInformation("BulkRules Stage 2: No entries need rule re-evaluation — skipping");
                onProgress?.Invoke(1, 1);
                return 0;
            }

            _logger.LogInformation("BulkRules Stage 2: Evaluating {Total} changed entries (delta-based)", total);

            WarnAboutUncoveredStatusCodes(entries, rules);

            int changed = 0;
            int processedCount = 0;
            var now = DateTime.UtcNow;
            var stateChanges = new List<(int entryId, string prevState, string newState, string details)>();

            foreach (var entry in entries)
            {
                var previousState = entry.LifecycleState;
                var attrs = BuildRuleAttributes(entry);
                var actionsTaken = new List<string>();

                foreach (var rule in rules)
                {
                    if (!EvaluateRuleCondition(rule, attrs)) continue;

                    if (rule.GracePeriodDays.HasValue && rule.GracePeriodDays > 0)
                    {
                        var daysSince = (now - entry.StateChangedDate).TotalDays;
                        if (daysSince < rule.GracePeriodDays.Value) continue;
                    }

                    if (rule.ActionType == "SetState" && !string.IsNullOrEmpty(rule.ActionValue))
                    {
                        entry.LifecycleState = rule.ActionValue;
                        actionsTaken.Add($"{rule.Name}: SetState={rule.ActionValue}");
                    }
                    else if (rule.ActionType == "MoveOU" && !string.IsNullOrEmpty(rule.ActionValue))
                    {
                        actionsTaken.Add($"{rule.Name}: MoveOU={rule.ActionValue}");
                    }
                    else if (rule.ActionType == "Reactivate")
                    {
                        entry.LifecycleState = "Active";
                        entry.ADAccountEnabled = true;
                        actionsTaken.Add($"{rule.Name}: Reactivate=Active");
                    }
                    else if (rule.ActionType == "Deprovision")
                    {
                        entry.LifecycleState = "Suspended";
                        actionsTaken.Add($"{rule.Name}: Deprovision=Suspended");
                    }
                }

                if (entry.LifecycleState == previousState)
                {
                    var defaultTransition = ApplyDefaultTransition(entry);
                    if (defaultTransition != null)
                        actionsTaken.Add(defaultTransition);
                }

                if (entry.LifecycleState != previousState)
                {
                    entry.PreviousState = previousState;
                    entry.StateChangedDate = now;
                    changed++;

                    stateChanges.Add((entry.Id, previousState, entry.LifecycleState, string.Join("; ", actionsTaken)));
                }

                entry.NeedsRuleEval = false;
                entry.ModifiedDate = now;

                if (stateChanges.Count >= 1000)
                {
                    await db.SaveChangesAsync(ct);

                    var historyToAdd = stateChanges.Select(sc => new MetaverseHistory
                    {
                        MetaverseEntryId = sc.entryId,
                        ChangeType = "StateChange",
                        OldState = sc.prevState,
                        NewState = sc.newState,
                        Details = sc.details,
                        TriggeredBy = "BulkPipeline",
                        Timestamp = now
                    }).ToList();

                    db.MetaverseHistory.AddRange(historyToAdd);
                    await db.SaveChangesAsync(ct);

                    stateChanges.Clear();
                    _logger.LogDebug("BulkRules: saved batch of 1000");
                }

                onProgress?.Invoke(++processedCount, total);
            }

            if (stateChanges.Count > 0)
            {
                await db.SaveChangesAsync(ct);

                var historyToAdd = stateChanges.Select(sc => new MetaverseHistory
                {
                    MetaverseEntryId = sc.entryId,
                    ChangeType = "StateChange",
                    OldState = sc.prevState,
                    NewState = sc.newState,
                    Details = sc.details,
                    TriggeredBy = "BulkPipeline",
                    Timestamp = now
                }).ToList();

                db.MetaverseHistory.AddRange(historyToAdd);
                await db.SaveChangesAsync(ct);
            }

            _logger.LogInformation("BulkRules Stage 2 complete: {Processed} evaluated, {Changed} state transitions", total, changed);
            return changed;
        }

        public async Task<int> BulkExportAsync(object scopeFactory, int? tenantId = null, Action<int, int>? onProgress = null, CancellationToken ct = default)
        {
            var db = _db;

            var tenant = await ResolveTenantAsync(tenantId, ct);
            SetTenantContext(tenant);
            var scopeTenantId = tenant?.Id ?? 0;

            if (tenant == null || string.IsNullOrWhiteSpace(tenant.ADBaseDN))
            {
                _logger.LogWarning("BulkExport: No active tenant or BaseDN — skipping export");
                return 0;
            }

            var exportRules = await db.LifecycleRules
                .Where(r => r.TenantId == tenant.Id && r.Enabled && (r.TriggerType == "OnExport" || r.TriggerType == "OnImport"))
                .OrderBy(r => r.Priority)
                .ToListAsync(ct);

            // Loaded once for the whole batch: any rule whose ActionValue is {OURules} resolves its
            // target through these, per identity, and a query per identity would be pure waste.
            var tenantOuRules = await db.TenantOURules
                .AsNoTracking()
                .Where(o => o.TenantId == tenant.Id)
                .ToListAsync(ct);

            // ✅ Delta export — only entries that actually need an AD lifecycle action:
            // 1. Never exported before (LastExportDate is null), OR
            // 2. Lifecycle STATE changed after the last export (StateChangedDate > LastExportDate).
            //
            // Pending is excluded because those identities have no AD account yet — the main sync
            // creates them.
            //
            // Deprovisioned used to be excluded too, on the assumption that a finished identity
            // needs nothing further in AD. That assumption broke the one action such identities
            // most need: a tenant whose rules send graduates (status 7) to an archive OU had those
            // identities correctly set to Deprovisioned and then filtered straight back out, so the
            // move could never run. Their state said archived while the account sat in its original
            // OU, and nothing anywhere reported a problem.
            //
            // A state with no matching rules simply finds none in the loop below and does nothing,
            // so including it costs a no-op iteration once per state change.
            //
            // NOTE: We intentionally do NOT trigger on ModifiedDate. ModifiedDate is bumped on every
            // import for every entry (UpsertMetaverseEntry), so including it re-exported all ~111K
            // entries on every full sync (~51 min). Attribute changes are already pushed to AD by the
            // main sync (UpdateDynamicAsync); lifecycle export only needs to react to state changes.
            var entryIds = await db.MetaverseEntries
                .Where(e =>
                    e.TenantId == scopeTenantId &&
                    e.LifecycleState != "Pending" &&
                    (e.LastExportDate == null ||
                     e.StateChangedDate > e.LastExportDate))
                .Select(e => e.Id)
                .ToListAsync(ct);

            var total = entryIds.Count;
            var pendingCount = await db.MetaverseEntries.CountAsync(e => e.TenantId == scopeTenantId && e.LifecycleState == "Pending", ct);
            var exportedBefore = await db.MetaverseEntries.CountAsync(e => e.TenantId == scopeTenantId && e.LastExportDate != null, ct);
            var activeCount = await db.MetaverseEntries.CountAsync(e => e.TenantId == scopeTenantId && e.LifecycleState == "Active", ct);
            var suspendedCount = await db.MetaverseEntries.CountAsync(e => e.TenantId == scopeTenantId && e.LifecycleState == "Suspended", ct);
            var deprovisionedCount = await db.MetaverseEntries.CountAsync(e => e.TenantId == scopeTenantId && e.LifecycleState == "Deprovisioned", ct);

            _logger.LogInformation("[BulkExport] Query result: total={Total}, Pending={Pending}, Active={Active}, Suspended={Suspended}, Deprovisioned={Deprovisioned}, ExportedBefore={ExportedBefore}",
                total, pendingCount, activeCount, suspendedCount, deprovisionedCount, exportedBefore);

            if (total == 0) return 0;

            _logger.LogInformation("BulkExport Stage 3: Exporting {Total} entries to AD (batched, sequential)", total);

            int exported = 0;
            int failed = 0;
            // Counted rather than logged per identity: at semester start a whole cohort returns at
            // once, and a line each would bury the run. It is not an error — a tenant may want
            // Reactivate without a move — but it is invisible otherwise, so it is stated once.
            int reactivatedWithoutMove = 0;
            const int exportBatchSize = 50; // Small batches to avoid AD timeout

            for (int i = 0; i < entryIds.Count; i += exportBatchSize)
            {
                ct.ThrowIfCancellationRequested();

                var batchIds = entryIds.Skip(i).Take(exportBatchSize).ToList();

                using var scope = ((IServiceScopeFactory)scopeFactory).CreateScope();
                // Per-tenant target connector (set by SetTenantContext); falls back to the DI singleton
                var target = _targetConnector ?? scope.ServiceProvider.GetRequiredService<ITargetConnector>();
                var innerDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var batchEntries = await innerDb.MetaverseEntries
                    .Where(e => batchIds.Contains(e.Id))
                    .ToListAsync(ct);

                foreach (var entry in batchEntries)
                {
                    ct.ThrowIfCancellationRequested();

                    // Retry up to 2 times on LDAP/network failure
                    for (int attempt = 1; attempt <= 2; attempt++)
                    {
                        try
                        {
                            var actionsTaken = new List<string>();
                            // Actions the connector reported as failed. Kept apart from
                            // actionsTaken because the two drive different decisions: what to
                            // write in the history, and whether this entry may be marked exported.
                            var failedActions = new List<string>();
                            var now = DateTime.UtcNow;

                            // Verifying an Active account exists is an ADDITIONAL step, not an
                            // alternative to running its rules. It used to be an either/or, which
                            // meant no rule ever ran for an Active identity:
                            //
                            //  * an AddGroups rule targeting active identities never fired at all;
                            //  * and an identity whose source status has no SetState rule stays
                            //    Active, so a rule like "status not in (1,7) -> move" skipped it —
                            //    the export branches on lifecycle STATE while rules condition on
                            //    source STATUS, and the two diverge exactly where no rule maps.
                            if (entry.LifecycleState == "Active")
                            {
                                var exists = await target.ExistsAsync(entry.ExternalId, ct);
                                if (exists)
                                {
                                    entry.ADAccountEnabled = true;
                                    entry.ProvisionedTargetsJson = JsonSerializer.Serialize(new[] { "ActiveDirectory" });
                                    actionsTaken.Add("Verified: Active in AD");
                                }
                            }

                            {
                                var attrs = BuildRuleAttributes(entry);

                                foreach (var rule in exportRules)
                                {
                                    if (!EvaluateRuleCondition(rule, attrs)) continue;

                                    switch (rule.ActionType)
                                    {
                                        // Every branch below records a failure as well as a success.
                                        // A connector returning false used to be discarded, and the
                                        // entry was still stamped as exported — see the guard after
                                        // this loop for why that stranded the identity for good.
                                        //
                                        // "Succeeded but changed nothing" is NOT a failure:
                                        // RemovedCount == 0 means the user was not in those groups,
                                        // and AddedCount == 0 means they already were. Only an
                                        // explicit Success == false counts.
                                        case "MoveOU" when !string.IsNullOrEmpty(rule.ActionValue):
                                            var moveTargetOU = ResolveActionOu(entry, rule, tenant.ADBaseDN, tenantOuRules);
                                            if (moveTargetOU == null)
                                            {
                                                failedActions.Add("MoveOU target could not be resolved");
                                                break;
                                            }
                                            var moved = await target.MoveToOUAsync(entry.ExternalId, moveTargetOU, ct);
                                            if (moved)
                                            {
                                                entry.ADCurrentOU = moveTargetOU;
                                                actionsTaken.Add($"Moved to {moveTargetOU}");
                                            }
                                            else
                                            {
                                                failedActions.Add($"MoveOU → {moveTargetOU}");
                                            }
                                            break;

                                        case "RemoveGroups" when !string.IsNullOrWhiteSpace(rule.ActionValue):
                                            var specificGroups = rule.ActionValue.Split(',').Select(g => g.Trim()).Where(g => !string.IsNullOrEmpty(g));
                                            var specResult = await target.RemoveFromSpecificGroupsAsync(entry.ExternalId, specificGroups, ct);
                                            if (!specResult.Success)
                                                failedActions.Add($"RemoveGroups → {rule.ActionValue}");
                                            else if (specResult.RemovedCount > 0)
                                                actionsTaken.Add($"Removed from {specResult.RemovedCount} groups");
                                            break;

                                        case "RemoveGroups":
                                            var allResult = await target.RemoveFromAllGroupsAsync(entry.ExternalId, ct);
                                            if (!allResult.Success)
                                                failedActions.Add("RemoveGroups → all");
                                            else if (allResult.RemovedCount > 0)
                                                actionsTaken.Add($"Removed from {allResult.RemovedCount} groups");
                                            break;

                                        case "Deprovision":
                                            // Move to the OU named by the rule (ActionValue), if any.
                                            if (!string.IsNullOrWhiteSpace(rule.ActionValue))
                                            {
                                                var deprovOU = ResolveActionOu(entry, rule, tenant.ADBaseDN, tenantOuRules);
                                                if (deprovOU == null)
                                                {
                                                    failedActions.Add("Deprovision OU could not be resolved");
                                                }
                                                else
                                                {
                                                    var mvResult = await target.MoveToOUAsync(entry.ExternalId, deprovOU, ct);
                                                    if (mvResult) entry.ADCurrentOU = deprovOU;
                                                    else failedActions.Add($"Deprovision move → {deprovOU}");
                                                }
                                            }
                                            // This result used to be thrown away entirely, so an
                                            // identity could be recorded "Deprovisioned" while still
                                            // holding every group it had.
                                            var deprovGroups = await target.RemoveFromAllGroupsAsync(entry.ExternalId, ct);
                                            if (!deprovGroups.Success)
                                                failedActions.Add("Deprovision group removal");
                                            actionsTaken.Add("Deprovisioned");
                                            break;

                                        case "Reactivate":
                                            entry.ADAccountEnabled = true;
                                            if (!string.IsNullOrEmpty(rule.ActionValue))
                                            {
                                                var reactivateOU = ResolveActionOu(entry, rule, tenant.ADBaseDN, tenantOuRules);
                                                if (reactivateOU == null)
                                                {
                                                    actionsTaken.Add("Reactivated");
                                                    failedActions.Add("Reactivate OU could not be resolved");
                                                    break;
                                                }
                                                var reactivateMoved = await target.MoveToOUAsync(entry.ExternalId, reactivateOU, ct);
                                                if (reactivateMoved)
                                                {
                                                    entry.ADCurrentOU = reactivateOU;
                                                    actionsTaken.Add($"Reactivated → Moved to {reactivateOU}");
                                                }
                                                else
                                                {
                                                    // Enabled but left in the wrong OU: half done,
                                                    // and the half that is missing must be retried.
                                                    actionsTaken.Add("Reactivated");
                                                    failedActions.Add($"Reactivate move → {reactivateOU}");
                                                }
                                            }
                                            else
                                            {
                                                // No ActionValue means no move, which is a valid
                                                // configuration and reports as a clean success. It is
                                                // also how an identity ends up Active in the OU it was
                                                // parked in when it left: the rules that moved it out
                                                // are conditioned on the old status and never fire
                                                // again. Counted so the run can say so.
                                                actionsTaken.Add("Reactivated (no OU configured — not moved)");
                                                reactivatedWithoutMove++;
                                            }
                                            break;

                                        case "AddGroups" when !string.IsNullOrWhiteSpace(rule.ActionValue):
                                            // Honour {GroupRules} here too — the bulk path used to
                                            // split it as a literal name and ask AD for a group
                                            // called "{GroupRules}".
                                            var addGroups = IsGroupRulesToken(rule.ActionValue)
                                                ? await ResolveGroupsFromTenantRulesAsync(entry, ct)
                                                : rule.ActionValue.Split(',').Select(g => g.Trim()).Where(g => !string.IsNullOrEmpty(g));
                                            if (!addGroups.Any()) break;
                                            var addResult = await target.AddToGroupsAsync(entry.ExternalId, addGroups, ct);
                                            if (!addResult.Success)
                                                failedActions.Add($"AddGroups → {string.Join(", ", addGroups)}");
                                            else if (addResult.AddedCount > 0)
                                                actionsTaken.Add($"Added to {addResult.AddedCount} groups: {string.Join(", ", addResult.GroupNames)}");
                                            break;
                                    }
                                }

                                // OU placement is driven entirely by the rules above (ActionValue),
                                // not by any org-specific status/gender defaults.
                            }

                            // LastExportDate is what tells the next run "this entry is finished":
                            // the re-export query only reconsiders an entry whose lifecycle STATE
                            // changed after this stamp. Stamping it after a failed action therefore
                            // strands the identity permanently — the action never happened, the
                            // state will not change again, and no error is ever raised again.
                            //
                            // Seen live: a MoveOU to an OU that did not exist was logged as an
                            // error, counted as "1 exported, 0 failed", and the account was never
                            // moved even after the OU was created.
                            //
                            // So a failed action leaves the stamp alone. The entry is retried on
                            // every run until it succeeds. That is noisy while the cause persists,
                            // which is the intent: a missing OU should keep complaining rather than
                            // be quietly forgotten.
                            if (failedActions.Count == 0)
                            {
                                entry.LastExportDate = now;
                            }
                            else
                            {
                                _logger.LogError(
                                    "BulkExport: {ExternalId} was NOT marked exported — {Count} action(s) failed: {Failed}. " +
                                    "It will be retried on the next run.",
                                    entry.ExternalId, failedActions.Count, string.Join("; ", failedActions));
                            }

                            if (actionsTaken.Count > 0 || failedActions.Count > 0)
                            {
                                var details = string.Join("; ", actionsTaken);
                                if (failedActions.Count > 0)
                                    details = string.IsNullOrEmpty(details)
                                        ? "FAILED: " + string.Join("; ", failedActions)
                                        : details + " | FAILED: " + string.Join("; ", failedActions);

                                innerDb.MetaverseHistory.Add(new MetaverseHistory
                                {
                                    MetaverseEntryId = entry.Id,
                                    ChangeType = "Export",
                                    Details = details,
                                    TriggeredBy = "BulkPipeline",
                                    Timestamp = now
                                });
                            }

                            if (failedActions.Count == 0) exported++; else failed++;

                            // Leave the retry loop either way: the retry here is for exceptions,
                            // and a connector that returned false will return false again on an
                            // immediate second attempt. The next scheduled run is the retry.
                            break;
                        }
                        catch (Exception ex) when (attempt < 2)
                        {
                            _logger.LogWarning(ex, "BulkExport retry {Attempt}/2 for entry {EntryId}", attempt, entry.Id);
                            await Task.Delay(1000 * attempt, ct); // Exponential backoff
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "BulkExport failed for entry {EntryId} after retries", entry.Id);
                            failed++;
                        }
                    }

                    onProgress?.Invoke(exported + failed, total);
                }

                await innerDb.SaveChangesAsync(ct);

                // Rate limiting: pause between batches to protect AD
                if (i + exportBatchSize < entryIds.Count)
                {
                    await Task.Delay(200, ct);
                }
            }

            _logger.LogInformation("BulkExport Stage 3 complete: {Exported} exported, {Failed} failed", exported, failed);

            if (reactivatedWithoutMove > 0)
            {
                _logger.LogWarning(
                    "BulkExport: {Count} identity/identities were reactivated but not moved — the Reactivate rule has no " +
                    "ActionValue. They are Active in whichever OU they were placed while inactive, because the rules that " +
                    "moved them there are conditioned on the old status and will not fire again. " +
                    "Set the rule's ActionValue to {Token} to return each one to the OU its own data resolves.",
                    reactivatedWithoutMove, OuRulesToken);
            }

            return exported;
        }

        public async Task ProcessAllPendingPipelineAsync(object scopeFactory, int? tenantId = null, Action<string, int, int>? onStageProgress = null, CancellationToken ct = default)
        {
            // When no tenant is specified, run the pipeline for every active tenant sequentially
            List<int> tenantIds;
            if (tenantId.HasValue)
            {
                tenantIds = new List<int> { tenantId.Value };
            }
            else
            {
                tenantIds = await _db.TenantSettings.AsNoTracking()
                    .Where(t => t.IsActive)
                    .OrderBy(t => t.Id)
                    .Select(t => t.Id)
                    .ToListAsync(ct);
                if (tenantIds.Count == 0) tenantIds.Add(0); // legacy: no tenant configured
            }

            foreach (var tid in tenantIds)
            {
                ct.ThrowIfCancellationRequested();
                _logger.LogInformation("=== 3-Stage Bulk Pipeline START (TenantId={TenantId}) ===", tid);

                _logger.LogInformation("[Stage 1/3] Bulk Import from the source");
                var imported = await BulkImportPendingAsync(tid,
                    (p, t) => onStageProgress?.Invoke("Import", p, t), ct);

                _logger.LogInformation("[Stage 2/3] Bulk Apply Rules");
                var changed = await BulkApplyRulesAsync(tid,
                    (p, t) => onStageProgress?.Invoke("Rules", p, t), ct);

                _logger.LogInformation("[Stage 3/3] Bulk Export to AD");
                var exported = await BulkExportAsync(scopeFactory, tid,
                    (p, t) => onStageProgress?.Invoke("Export", p, t), ct);

                _logger.LogInformation("=== 3-Stage Bulk Pipeline COMPLETE (TenantId={TenantId}): Import={Imported}, Rules={Changed}, Export={Exported} ===",
                    tid, imported, changed, exported);
            }
        }

        private readonly object _exportLock = new();
    }
}
