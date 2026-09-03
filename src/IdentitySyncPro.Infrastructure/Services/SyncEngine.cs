using System.Diagnostics;
using System.Text.Json;
using Hangfire;
using Hangfire.Storage;
using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Metaverse;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Core.Models.Audit;
using IdentitySyncPro.Core.Models.Sync;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Main sync engine orchestrator.
    /// Port of Start-IdentitySync PowerShell function with full async support.
    /// NO DELETE/DISABLE mode — Safe Sync Only.
    /// 
    /// Performance optimizations for 120,000+ identities:
    /// - DbContext scope per batch to prevent memory leaks
    /// - Batch-load SyncStates to eliminate N+1 queries (120K → ~120 queries instead of 120K)
    /// - Pre-computed hash passed to UpsertSyncState (avoid double computation)
    /// - Rate limiting between batches to protect Domain Controller
    /// </summary>
    public class SyncEngine : ISyncEngine
    {
        private readonly IServiceScopeFactory _scopeFactory;

        // Per-run connectors: reassigned for each tenant from ITenantConnectorFactory.
        // Only one run executes at a time (in-memory + distributed lock), so this is safe.
        private ISourceConnector _sourceConnector;
        private ITargetConnector _targetConnector;
        private readonly ITenantConnectorFactory? _connectorFactory;

        // Tenant scope for all SyncStates/MetaverseEntries reads/writes of the current run.
        private int _runTenantId;

        /// <summary>
        /// True while a dry run is in progress. A dry run reports what WOULD happen; it must not
        /// leave a trace that makes the system believe work was done.
        ///
        /// This is enforced centrally in <see cref="UpsertSyncState"/> rather than at each call
        /// site: under dry run the connectors return a synthetic success, so every "if (success)"
        /// branch reaches a state write, and guarding them one by one is how the bug below got in.
        ///
        /// Bug it prevents: a dry run used to stamp the current hash into SyncStates. The next
        /// REAL sync then compared hashes, found them identical, reported NoChange for every
        /// identity and pushed nothing to AD — silently losing a full sync's worth of updates.
        /// Invisible on an established install (the hashes written matched what was already
        /// there), catastrophic on a fresh database where the dry run ran first.
        /// </summary>
        private bool _isDryRun;

        // Provisioning policy for the tenant currently being synced (see SetRunContext).
        private string? _runCreationMode;
        private string? _runCreationConditionField;
        private string? _runCreationConditionOperator;
        private string? _runCreationConditionValue;

        // Withheld provisioning is counted, not just logged per record. A tenant set to Never
        // produces one skip line per identity, which is exactly the shape of output an operator
        // scrolls past; the end-of-run total is what makes "3,412 people have no account" land.
        private int _creationSkipped;
        private readonly Dictionary<string, int> _creationSkipReasons = new();

        private readonly ISmsService _smsService;
        private readonly ISyncProgressNotifier? _progressNotifier;
        private readonly ILogger<SyncEngine> _logger;
        private readonly ResilienceService _resilience;
        private readonly object _runLock = new();
        private CancellationTokenSource? _cts;
        private bool _isRunning;

        // Cross-instance guard: a distributed lock held for the duration of a run so two servers
        // (or a run started after an ungraceful restart) can never sync concurrently.
        private IDisposable? _distributedSyncLock;
        private const string SyncLockResource = "IdentitySyncPro:GlobalSync";
        private static readonly TimeSpan SyncLockTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Single circuit-breaker component key for all Active Directory operations
        /// (create + update). Using one key means sustained AD failures aggregate and
        /// trip the breaker instead of being split across separate create/update counters.
        /// </summary>
        private const string AdComponent = "ActiveDirectory";

        // Phase tracking for progress reporting
        private int _currentPhase;
        private int _totalPhases;
        private string _phaseDescription = "";
        private double _phaseProgress;

        // SignalR broadcast throttling — a 120K-record run calls ReportProgress per record; without
        // throttling that spawns tens of thousands of fire-and-forget tasks. Broadcast at most once
        // per interval (terminal/forced updates always go through).
        private DateTime _lastProgressBroadcastUtc = DateTime.MinValue;
        private static readonly TimeSpan ProgressBroadcastInterval = TimeSpan.FromMilliseconds(750);

        public bool IsRunning { get { lock (_runLock) return _isRunning; } }
        public event Action<SyncProgressInfo>? OnProgress;

        public SyncEngine(
            IServiceScopeFactory scopeFactory,
            ISourceConnector sourceConnector,
            ITargetConnector targetConnector,
            ISmsService smsService,
            ILogger<SyncEngine> logger,
            ISyncProgressNotifier? progressNotifier = null,
            ResilienceService? resilience = null,
            ILoggerFactory? loggerFactory = null,
            ITenantConnectorFactory? connectorFactory = null)
        {
            _scopeFactory = scopeFactory;
            _sourceConnector = sourceConnector;
            _targetConnector = targetConnector;
            _connectorFactory = connectorFactory;
            _smsService = smsService;
            _logger = logger;
            _progressNotifier = progressNotifier;
            
            if (resilience != null)
            {
                _resilience = resilience;
            }
            else if (loggerFactory != null)
            {
                _resilience = new ResilienceService(scopeFactory, loggerFactory.CreateLogger<ResilienceService>());
            }
            else
            {
                _resilience = new ResilienceService(scopeFactory, logger as ILogger<ResilienceService> ?? throw new ArgumentException("Cannot resolve ILogger<ResilienceService>"));
            }
        }

        public void CancelCurrentSync()
        {
            CancellationTokenSource? cts;
            lock (_runLock) { cts = _cts; }
            cts?.Cancel();
            _logger.LogWarning("Sync cancellation requested");
        }

        /// <summary>
        /// Execute a full sync run — processes all records from the source.
        /// When tenantId is null, every active tenant runs sequentially, each with its own
        /// source connection, mappings, and rules (multi-source support).
        /// </summary>
        /// <param name="triggeredBy">
        /// Username when a person started this run, <see cref="ActorNames.Schedule"/> when the
        /// recurring job did. It must be supplied by the caller: a run reaching here has already
        /// left the request that started it, so nothing at this level can tell the two apart.
        /// </param>
        public async Task<SyncRun> RunFullSyncAsync(int batchSize = 1000, bool dryRun = false, int? tenantId = null, CancellationToken ct = default, string? triggeredBy = null)
        {
            _runTriggeredBy = triggeredBy;
            lock (_runLock)
            {
                if (_isRunning) throw new InvalidOperationException("A sync is already running");
                _isRunning = true;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            }

            // Cross-instance/restart guard — released in the finally below.
            try { _distributedSyncLock = TryAcquireDistributedSyncLock(); }
            catch { ReleaseRunLock(); throw; }

            try
            {
                var tenants = await LoadRunTenantsAsync(tenantId, _cts.Token);
                SyncRun? last = null;

                if (tenants.Count == 0)
                {
                    // No active tenant — run the core once so the failure is recorded as a SyncRun
                    last = await RunFullSyncCoreAsync(null, batchSize, dryRun);
                }
                else
                {
                    foreach (var tenant in tenants)
                    {
                        if (_cts.Token.IsCancellationRequested) break;
                        last = await RunFullSyncCoreAsync(tenant, batchSize, dryRun);
                    }
                }

                return last!;
            }
            finally
            {
                ReleaseRunLock();
            }
        }

        /// <summary>
        /// Full sync for a single tenant. Assumes the run lock is already held by the caller.
        /// </summary>
        private async Task<SyncRun> RunFullSyncCoreAsync(TenantSettings? tenant, int batchSize, bool dryRun)
        {
            SetRunContext(tenant, dryRun);

            // Create SyncRun in its own scope (long-lived — updated across batches)
            int runId;
            var run = new SyncRun
            {
                TenantId = tenant?.Id,
                RunType = dryRun ? "DryRun" : "Full",
                Status = SyncRunStatus.Running,
                StartTime = DateTime.UtcNow,
                BatchSize = batchSize,
                TriggeredBy = _runTriggeredBy ?? ActorNames.System
            };

            using (var initScope = _scopeFactory.CreateScope())
            {
                var initDb = initScope.ServiceProvider.GetRequiredService<AppDbContext>();
                initDb.SyncRuns.Add(run);
                await initDb.SaveChangesAsync(_cts!.Token);
                runId = run.Id;
            }

            var overallSw = Stopwatch.StartNew();

            // ✅ Tenant configuration (mappings + OU/group rules + lifecycle setting) for this run
            var tenantMappings = tenant?.AttributeMappings?.ToList() ?? new List<TenantAttributeMapping>();
            var tenantOURules = tenant?.OURules?.ToList() ?? new List<TenantOURule>();
            var tenantGroupRules = tenant?.GroupRules?.ToList() ?? new List<TenantGroupRule>();
            var tenantBaseDN = tenant?.ADBaseDN ?? "";
            // EffectiveGlobalDefault, not GlobalDefaultValue: the placeholder is an Active Directory
            // workaround for its refusal of empty attribute writes, and sending it to a target that
            // has no such constraint writes nonsense — a source row with no email produced
            // emails[0].value = "." in a SCIM service.
            var globalDefaultValue = tenant?.EffectiveGlobalDefault ?? ".";
            var useGlobalDefaultForEmptyFields = tenant?.UseGlobalDefaultForEmptyFields ?? false;
            var enableLifecycleDuringSync = tenant?.EnableLifecycleDuringSync ?? false;

            // STATUS_CODE is a synthetic alias, not a source column: LifecycleEngine injects it from
            // the status column the tenant names, so a rule can be written against STATUS_CODE
            // whatever the view actually calls its column. The alias exists because one tenant's
            // column is STATUSE_CODE and every rule written against STATUS_CODE therefore read null
            // — ten dead rules across 111,465 identities, with nothing logged.
            //
            // The alias only works if the tenant names its status column. Without it StatusCode is 0
            // for everybody and the rules die again the same way, so say so before reading a row.
            if (tenant != null && string.IsNullOrWhiteSpace(tenant.SourceStatusColumn))
            {
                using var statusScope = _scopeFactory.CreateScope();
                var statusDb = statusScope.ServiceProvider.GetRequiredService<AppDbContext>();

                var rulesOnAlias = (await statusDb.LifecycleRules
                        .Where(r => r.TenantId == tenant.Id && r.Enabled && r.ConditionField != null)
                        .Select(r => r.ConditionField!)
                        .ToListAsync(_cts!.Token))
                    .Count(f => f.Trim().Equals("STATUS_CODE", StringComparison.OrdinalIgnoreCase));

                if (rulesOnAlias > 0)
                {
                    _logger.LogError(
                        "Tenant '{Tenant}' has no source status column configured, but {Count} enabled lifecycle " +
                        "rule(s) are conditioned on STATUS_CODE. STATUS_CODE is derived from that column, so it is 0 " +
                        "for every identity and those rules will never match. Set the status column in " +
                        "Settings → Source, or condition the rules on the real column name.",
                        tenant.TenantName, rulesOnAlias);
                }
                else
                {
                    _logger.LogWarning(
                        "Tenant '{Tenant}' has no source status column configured — StatusCode will be 0 for every " +
                        "identity. Harmless while no rule uses STATUS_CODE, which is the case now.",
                        tenant.TenantName);
                }
            }

            // A lifecycle rule conditioned on a column that does not exist reads null and never
            // matches — the quietest failure in the system, and the one that cost ten dead rules
            // across 111,465 identities with nothing logged. Retyping a ConditionField is all it
            // takes to reintroduce, so it is checked on every run.
            //
            // Deliberately outside the OU-rule block below: a tenant with no OU rules still has
            // lifecycle rules, and this check has nothing to do with OU placement.
            if (tenant != null)
            {
                try
                {
                    using var ruleScope = _scopeFactory.CreateScope();
                    var ruleDb = ruleScope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var deadRules = await OuRulePrecheck.FindRulesNamingUnknownColumnsAsync(
                        ruleDb, tenant.Id, _cts!.Token);

                    foreach (var p in deadRules)
                        _logger.LogError("Lifecycle rule precheck: {Problem}", p);

                    if (deadRules.Count == 0)
                        _logger.LogInformation(
                            "Lifecycle rule precheck: every enabled rule conditions on a field that exists");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Lifecycle rule precheck could not run — rule condition fields were NOT verified for this run");
                }
            }

            // Check the OU rules against the data before touching AD rather than after. A
            // ValueMappings map that covers some values and not others produces a syntactically
            // fine DN naming an OU that does not exist, and the only symptom is one failed create
            // per identity — the run is well under way by the time that shows up.
            //
            // This warns and continues. The map may legitimately be about to change, and the rest
            // of the run is unaffected; the point is that the cause is stated up front instead of
            // being reconstructed later from a pile of create failures.
            if (tenant != null && tenantOURules.Count > 0)
            {
                try
                {
                    using var checkScope = _scopeFactory.CreateScope();
                    var checkDb = checkScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var (ouErrors, ouWarnings) = await OuRulePrecheck.ValidateAsync(
                        checkDb, tenant.Id, tenantOURules, null, _cts!.Token);

                    foreach (var e in ouErrors)
                        _logger.LogError("OU rule precheck: {Problem}", e);
                    foreach (var w in ouWarnings)
                        _logger.LogWarning("OU rule precheck: {Problem}", w);

                    // A check that says nothing when healthy cannot be told apart from a check that
                    // did not run, so it states that it ran either way.
                    if (ouErrors.Count == 0 && ouWarnings.Count == 0)
                        _logger.LogInformation(
                            "OU rule precheck: {RuleCount} rule(s) clean — every mapped placeholder value in the staged data has a ValueMappings entry",
                            tenantOURules.Count);
                }
                catch (Exception ex)
                {
                    // A diagnostic must never be the reason a working sync does not run — but it
                    // must not disappear either, or its silence would read as a clean result.
                    _logger.LogWarning(ex, "OU rule precheck could not run — OU rules were NOT verified for this run");
                }
            }

            if (tenantMappings.Count == 0)
            {
                _logger.LogError("⚠️ No attribute mappings configured for tenant {Tenant}! Please configure mappings in Settings → Attribute Mapping", tenant?.TenantName ?? "(none)");
                run.Status = SyncRunStatus.Failed;
                run.ErrorMessage = "لا يوجد إعدادات ربط الحقول (Attribute Mapping). يرجى إعداد ربط الحقول من صفحة الإعدادات أولاً.";
                run.EndTime = DateTime.UtcNow;
                using (var failScope = _scopeFactory.CreateScope())
                {
                    var failDb = failScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var dbRun = await failDb.SyncRuns.FindAsync(runId);
                    if (dbRun != null)
                    {
                        dbRun.Status = run.Status;
                        dbRun.EndTime = run.EndTime;
                        dbRun.ErrorMessage = run.ErrorMessage;
                        await failDb.SaveChangesAsync(CancellationToken.None);
                    }
                }
                return run;
            }
            _logger.LogInformation("[FullSync] Tenant '{Tenant}': using dynamic mapping with {Count} attribute mappings", tenant?.TenantName, tenantMappings.Count);

            try
            {
                _logger.LogInformation("[{CorrelationId}] Starting Full Sync — NO DELETE/DISABLE Mode", run.CorrelationId);

                // Step 1: Get all IDs from Oracle
                _currentPhase = 1; _totalPhases = enableLifecycleDuringSync ? 5 : 3;
                _phaseDescription = "التحضير — قراءة البيانات";
                _phaseProgress = 0;
                ReportProgress("Reading identity IDs from Oracle...", run);
                var oracleIds = (await _sourceConnector.ReadAllIdsAsync(_cts.Token)).ToArray();
                _logger.LogInformation("[{CorrelationId}] Oracle: {Count} identities", run.CorrelationId, oracleIds.Length);

                // Step 2: Get synced IDs from local DB (use separate scope, read-only)
                HashSet<int> localIds;
                using (var readScope = _scopeFactory.CreateScope())
                {
                    var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    _phaseProgress = 50;
                    ReportProgress("Getting synced IDs from database...", run);
                    localIds = (await readDb.SyncStates
                        .AsNoTracking()
                        .Where(s => s.TenantId == _runTenantId && s.CreatedInAD)
                        .Select(s => s.IdentityId)
                        .ToListAsync(_cts.Token))
                        .ToHashSet();
                }
                _logger.LogInformation("[{CorrelationId}] Local: {Count} identities", run.CorrelationId, localIds.Count);

                // Step 3: Identify new vs existing
                var newIds = oracleIds.Where(id => !localIds.Contains(id)).ToArray();
                var existingIds = oracleIds.Where(id => localIds.Contains(id)).ToArray();

                // Quarantined identities are held back from this run.
                //
                // Quarantine used to be a record and nothing more: an identity that had failed
                // five times was still retried on every subsequent run, forever. With a batch
                // intake that matters — a handful of malformed records would be re-attempted
                // every cycle, each attempt creating and deleting an AD account, and nothing
                // would ever draw attention to them.
                //
                // They are released from /Health once the underlying data is fixed.
                using (var qScope = _scopeFactory.CreateScope())
                {
                    var qDb = qScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var quarantined = (await qDb.QuarantinedIdentities
                        .Where(q => !q.IsResolved)
                        .Select(q => q.IdentityId)
                        .ToListAsync(_cts.Token))
                        .ToHashSet();

                    if (quarantined.Count > 0)
                    {
                        var newBefore = newIds.Length;
                        var existingBefore = existingIds.Length;
                        newIds = newIds.Where(id => !quarantined.Contains(id)).ToArray();
                        existingIds = existingIds.Where(id => !quarantined.Contains(id)).ToArray();

                        var held = (newBefore - newIds.Length) + (existingBefore - existingIds.Length);
                        if (held > 0)
                        {
                            _logger.LogWarning(
                                "[{CorrelationId}] Holding back {Held} quarantined identities " +
                                "({NewHeld} new, {ExistingHeld} existing) — review and release them at /Health",
                                run.CorrelationId, held,
                                newBefore - newIds.Length, existingBefore - existingIds.Length);
                        }
                    }
                }

                _logger.LogInformation("[{CorrelationId}] New: {NewCount}, Existing: {ExistingCount}", run.CorrelationId, newIds.Length, existingIds.Length);

                var totalToProcess = newIds.Length + existingIds.Length;
                var processed = 0;

                _phaseProgress = 100;
                ReportProgress($"Preparation complete: {newIds.Length} new, {existingIds.Length} existing", run, 0, totalToProcess);

                // Step 4: Process NEW identities
                if (newIds.Length > 0)
                {
                    _currentPhase = 2;
                    _phaseDescription = "معالجة الهويات الجديدة";
                    _phaseProgress = 0;
                    _logger.LogInformation("[{CorrelationId}] Processing {Count} NEW identities...", run.CorrelationId, newIds.Length);

                    for (int i = 0; i < newIds.Length; i += batchSize)
                    {
                        _cts.Token.ThrowIfCancellationRequested();
                        ThrowIfAdCircuitOpen();

                        var batchIds = newIds.Skip(i).Take(batchSize).ToArray();
                        _phaseProgress = newIds.Length > 0 ? (double)i / newIds.Length * 100 : 0;
                        ReportProgress($"New identities batch {i / batchSize + 1}: {batchIds.Length} records", run, processed, totalToProcess);

                        var identities = await _sourceConnector.ReadBatchAsync(batchIds, _cts.Token);

                        // ✅ New scope per batch — prevents memory leak
                        using var batchScope = _scopeFactory.CreateScope();
                        var db = batchScope.ServiceProvider.GetRequiredService<AppDbContext>();

                        // ✅ Batch-load existing SyncStates for this batch (for identities that may already exist in DB but not in AD)
                        var batchSyncStates = await db.SyncStates
                            .Where(s => s.TenantId == _runTenantId && batchIds.Contains(s.IdentityId))
                            .ToDictionaryAsync(s => s.IdentityId, _cts.Token);

                        // ✅ Lifecycle: Pre-load MetaverseEntries for this batch if lifecycle is enabled
                        Dictionary<string, MetaverseEntry>? batchMetaverseEntries = null;
                        if (enableLifecycleDuringSync)
                        {
                            var batchExternalIds = batchIds.Select(id => id.ToString()).ToList();
                            batchMetaverseEntries = await db.MetaverseEntries
                                .Where(e => e.TenantId == _runTenantId && batchExternalIds.Contains(e.ExternalId))
                                .ToDictionaryAsync(e => e.ExternalId, _cts.Token);
                        }

                        foreach (var identity in identities)
                        {
                            _cts.Token.ThrowIfCancellationRequested();

                            // ✅ Apply Global Default for empty fields FIRST (before any validation)
                            if (useGlobalDefaultForEmptyFields && !string.IsNullOrEmpty(globalDefaultValue))
                            {
                                identity.ApplyGlobalDefaults(globalDefaultValue);
                            }

                            var identityIdStr = identity.Key.ToString();
                            // ✅ Compute hash once
                            var currentHash = identity.ComputeHash();

                            // ✅ Account identifier comes from the tenant's identifier mapping (falls back to source key)
                            var sourceRow = identity.ToDictionary();
                            string accountId;
                            bool existsInAD;
                            try
                            {
                                (accountId, existsInAD) = await ResolveAccountAsync(
                                    sourceRow, tenantMappings, identityIdStr, _cts.Token);
                            }
                            catch (InvalidOperationException resolveEx)
                            {
                                // Ambiguous match, unreadable directory, or no free name. Skipping is
                                // the only safe outcome — the alternative is a duplicate account.
                                run.TotalSkipped++;
                                LogOperation(db, runId, identity.Key, OperationType.Skip,
                                    SyncOperationStatus.Skipped, null, 0, resolveEx.Message);
                                processed++;
                                run.TotalProcessed = processed;
                                continue;
                            }

                            if (existsInAD)
                            {
                                // Already in AD — update using dynamic mappings
                                Core.Interfaces.SyncResult result;
                                if (dryRun)
                                    result = new Core.Interfaces.SyncResult { Success = true, ChangedFields = "DryRun" };
                                else
                                {
                                    Dictionary<string, string> mappedAttrs;
                                    try
                                    {
                                        mappedAttrs = MappingEngine.ApplyMappings(sourceRow, tenantMappings,
                                            useGlobalDefaultForEmptyFields ? globalDefaultValue : null);
                                    }
                                    catch (InvalidOperationException mapEx)
                                    {
                                        // Required source field empty → skip this record (matches legacy skip semantics)
                                        run.TotalSkipped++;
                                        LogOperation(db, runId, identity.Key, OperationType.Skip,
                                            SyncOperationStatus.Skipped, null, 0, mapEx.Message);
                                        processed++;
                                        run.TotalProcessed = processed;
                                        continue;
                                    }
                                    result = await SafeWriteAsync(accountId, "Update", () =>
                                        _targetConnector.UpdateDynamicAsync(accountId, mappedAttrs, _cts.Token));
                                }

                                if (result.Success)
                                {
                                    // ✅ Pass pre-loaded state + pre-computed hash
                                    batchSyncStates.TryGetValue(identity.Key, out var existingState);
                                    UpsertSyncState(db, identity, "Synced", currentHash, existingState);

                                    if (result.ChangedFields != "NoChanges")
                                    {
                                        run.TotalUpdated++;
                                        _resilience.RecordSuccess(AdComponent);
                                        LogOperation(db, runId, identity.Key, OperationType.Update,
                                            SyncOperationStatus.Success, result.ChangedFields, result.DurationMs);
                                    }
                                    else
                                    {
                                        run.TotalAlreadyExisted++;
                                        LogOperation(db, runId, identity.Key, OperationType.Sync,
                                            SyncOperationStatus.Success, "AlreadyExisted", result.DurationMs);
                                    }

                                    // ✅ Lifecycle: individual processing only when lifecycle integration is DISABLED
                                    if (!enableLifecycleDuringSync)
                                    {
                                        var previousStatusCode1 = existingState?.LastStatusCode;
                                        var statusChanged1 = previousStatusCode1 == null || previousStatusCode1 != identity.StatusCode;
                                        if (!dryRun && statusChanged1)
                                        {
                                            try
                                            {
                                                var lifecycleEngine = batchScope.ServiceProvider.GetRequiredService<ILifecycleEngine>();
                                                var lcResult = await lifecycleEngine.ProcessIdentityAsync(identity.Key, false, _runTenantId, _cts.Token);
                                                if (lcResult.Success && !string.IsNullOrEmpty(lcResult.ActionsTaken))
                                                {
                                                    _logger.LogInformation("[{CorrelationId}] Lifecycle processed for {IdentityId} (StatusCode {OldStatus}→{NewStatus}): {Actions}",
                                                        run.CorrelationId, identity.Key, previousStatusCode1, identity.StatusCode, lcResult.ActionsTaken);
                                                    LogOperation(db, runId, identity.Key, OperationType.Move,
                                                        SyncOperationStatus.Success, lcResult.ActionsTaken, lcResult.DurationMs);
                                                }
                                                else if (!lcResult.Success)
                                                {
                                                    _logger.LogWarning("Lifecycle failed for {IdentityId}: {Error}, clearing DbContext", identity.Key, lcResult.Error);
                                                    db.ChangeTracker.Clear();
                                                    batchSyncStates = await db.SyncStates
                                                        .Where(s => s.TenantId == _runTenantId && batchIds.Contains(s.IdentityId))
                                                        .ToDictionaryAsync(s => s.IdentityId, _cts.Token);
                                                    batchSyncStates.TryGetValue(identity.Key, out var reloaded);
                                                    UpsertSyncState(db, identity, "Synced", currentHash, reloaded);
                                                }
                                            }
                                            catch (Exception lcEx)
                                            {
                                                _logger.LogWarning(lcEx, "Lifecycle processing failed for {IdentityId}, sync continues", identity.Key);
                                                db.ChangeTracker.Clear();
                                                batchSyncStates = await db.SyncStates
                                                    .Where(s => s.TenantId == _runTenantId && batchIds.Contains(s.IdentityId))
                                                    .ToDictionaryAsync(s => s.IdentityId, _cts.Token);
                                                batchSyncStates.TryGetValue(identity.Key, out var reloaded);
                                                UpsertSyncState(db, identity, "Synced", currentHash, reloaded);
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    run.TotalFailed++;
                                    RecordWriteFailure(result);
                                    await _resilience.CheckAndQuarantineAsync(identity.Key, result.Error ?? "Update failed", "Update");
                                    if (!dryRun)
                                    {
                                        await _resilience.AddToDeadLetterAsync(identity.Key, "Update", result.Error ?? "Unknown error", identity.ToDictionary());
                                    }
                                    LogOperation(db, runId, identity.Key, OperationType.Update,
                                        SyncOperationStatus.Failed, null, result.DurationMs, result.Error);
                                }
                            }
                            else
                            {
                                // Not in AD. Whether that means "provision this person" is the
                                // tenant's policy, not a foregone conclusion — a tenant that only
                                // maintains accounts created elsewhere must not populate AD from
                                // its source view.
                                var gate = EvaluateCreationGate(sourceRow);
                                if (!gate.Allowed)
                                {
                                    run.TotalSkipped++;
                                    LogOperation(db, runId, identity.Key, OperationType.Skip,
                                        SyncOperationStatus.Skipped, null, 0, gate.Reason);
                                    processed++;
                                    run.TotalProcessed = processed;
                                    continue;
                                }

                                // Not in AD — create with random password using dynamic mappings
                                var randomPassword = PasswordGenerator.Generate();
                                identity.Password = randomPassword;

                                Core.Interfaces.SyncResult result;
                                if (dryRun)
                                    result = new Core.Interfaces.SyncResult { Success = true, ChangedFields = "DryRun" };
                                else
                                {
                                    Dictionary<string, string> mappedAttrs;
                                    try
                                    {
                                        mappedAttrs = MappingEngine.ApplyMappings(sourceRow, tenantMappings,
                                            useGlobalDefaultForEmptyFields ? globalDefaultValue : null);
                                    }
                                    catch (InvalidOperationException mapEx)
                                    {
                                        // Required source field empty → skip this record (matches legacy skip semantics)
                                        run.TotalSkipped++;
                                        LogOperation(db, runId, identity.Key, OperationType.Skip,
                                            SyncOperationStatus.Skipped, null, 0, mapEx.Message);
                                        processed++;
                                        run.TotalProcessed = processed;
                                        continue;
                                    }
                                    StampMatchAttribute(mappedAttrs, sourceRow, identityIdStr);
                                    var targetOU = MappingEngine.ResolveOU(sourceRow, tenantOURules, tenantBaseDN, _logger);
                                    var targetGroups = MappingEngine.ResolveGroups(sourceRow, tenantGroupRules);

                                    result = await SafeWriteAsync(accountId, "Create", () =>
                                        _targetConnector.CreateDynamicAsync(accountId, mappedAttrs,
                                            targetOU, targetGroups, randomPassword, _cts.Token));
                                }

                                if (result.Success)
                                {
                                    run.TotalCreated++;
                                    _resilience.RecordSuccess(AdComponent);
                                    batchSyncStates.TryGetValue(identity.Key, out var existingState);
                                    UpsertSyncState(db, identity, "Synced", currentHash, existingState);
                                    LogOperation(db, runId, identity.Key, OperationType.Create,
                                        SyncOperationStatus.Success, null, result.DurationMs);

                                    // Send SMS with credentials
                                    await SendCredentialsSmsAsync(db, identity, accountId, randomPassword, runId);
                                }
                                else
                                {
                                    run.TotalFailed++;
                                    RecordWriteFailure(result);
                                    await _resilience.CheckAndQuarantineAsync(identity.Key, result.Error ?? "Create failed", "Create");
                                    if (!dryRun)
                                    {
                                        await _resilience.AddToDeadLetterAsync(identity.Key, "Create", result.Error ?? "Unknown error", identity.ToDictionary());
                                    }
                                    batchSyncStates.TryGetValue(identity.Key, out var existingState);
                                    UpsertSyncState(db, identity, "Failed", currentHash, existingState, result.Error);
                                    LogOperation(db, runId, identity.Key, OperationType.Create,
                                        SyncOperationStatus.Failed, null, result.DurationMs, result.Error);
                                }
                            }

                            // ✅ Lifecycle: Upsert MetaverseEntry during sync (avoids second Oracle fetch)
                            if (enableLifecycleDuringSync && !dryRun)
                            {
                                UpsertMetaverseEntry(db, identity, currentHash, batchMetaverseEntries!);
                            }

                            processed++;
                            run.TotalProcessed = processed;
                            _phaseProgress = newIds.Length > 0 ? (double)processed / newIds.Length * 100 : 100;
                            ReportProgress($"Processing: {identityIdStr}", run, processed, totalToProcess, identityIdStr);
                        }

                        await db.SaveChangesAsync(_cts.Token);
                        // Batch scope disposed here — all tracked entities released from memory ✅

                        await Task.Delay(500, _cts.Token); // Rate limiting
                    }
                }

                // Step 5: Check updates for existing identities
                _currentPhase = 3;
                _phaseDescription = "فحص وتحديث الهويات الموجودة";
                _phaseProgress = 0;
                if (existingIds.Length > 0)
                {
                    _logger.LogInformation("[{CorrelationId}] Checking {Count} existing identities for updates...", run.CorrelationId, existingIds.Length);

                    for (int i = 0; i < existingIds.Length; i += batchSize)
                    {
                        _cts.Token.ThrowIfCancellationRequested();
                        ThrowIfAdCircuitOpen();

                        var batchIds = existingIds.Skip(i).Take(batchSize).ToArray();
                        _phaseProgress = existingIds.Length > 0 ? (double)i / existingIds.Length * 100 : 0;
                        ReportProgress($"Existing identities batch {i / batchSize + 1}: {batchIds.Length} records", run, processed, totalToProcess);

                        var identities = await _sourceConnector.ReadBatchAsync(batchIds, _cts.Token);

                        // ✅ New scope per batch
                        using var batchScope = _scopeFactory.CreateScope();
                        var db = batchScope.ServiceProvider.GetRequiredService<AppDbContext>();

                        // ✅ Batch-load all SyncStates for this batch in one query
                        var batchSyncStates = await db.SyncStates
                            .Where(s => s.TenantId == _runTenantId && batchIds.Contains(s.IdentityId))
                            .ToDictionaryAsync(s => s.IdentityId, _cts.Token);

                        // ✅ Lifecycle: Pre-load MetaverseEntries for this batch if lifecycle is enabled
                        Dictionary<string, MetaverseEntry>? batchMetaverseEntries2 = null;
                        if (enableLifecycleDuringSync)
                        {
                            var batchExternalIds2 = batchIds.Select(id => id.ToString()).ToList();
                            batchMetaverseEntries2 = await db.MetaverseEntries
                                .Where(e => e.TenantId == _runTenantId && batchExternalIds2.Contains(e.ExternalId))
                                .ToDictionaryAsync(e => e.ExternalId, _cts.Token);
                        }

                        foreach (var identity in identities)
                        {
                            _cts.Token.ThrowIfCancellationRequested();

                            // ✅ Apply Global Default for empty fields before validation
                            if (useGlobalDefaultForEmptyFields && !string.IsNullOrEmpty(globalDefaultValue))
                            {
                                identity.ApplyGlobalDefaults(globalDefaultValue);
                            }

                            // ✅ Compute hash once
                            var currentHash = identity.ComputeHash();

                            // ✅ Lookup from pre-loaded dictionary (no DB query)
                            batchSyncStates.TryGetValue(identity.Key, out var syncState);

                            if (syncState != null && syncState.CurrentHash != currentHash)
                            {
                                Core.Interfaces.SyncResult result;
                                if (dryRun)
                                    result = new Core.Interfaces.SyncResult { Success = true, ChangedFields = "DryRun" };
                                else
                                {
                                    var sourceRow = identity.ToDictionary();
                                    Dictionary<string, string> mappedAttrs;
                                    string accountId;
                                    try
                                    {
                                        // Resolve rather than regenerate: for a match-attribute tenant the
                                        // account's real name may differ from what the pattern produces now
                                        // (a discriminator, or a source spelling change since creation).
                                        (accountId, _) = await ResolveAccountAsync(
                                            sourceRow, tenantMappings, identity.Key.ToString(), _cts.Token);

                                        mappedAttrs = MappingEngine.ApplyMappings(sourceRow, tenantMappings,
                                            useGlobalDefaultForEmptyFields ? globalDefaultValue : null);
                                    }
                                    catch (InvalidOperationException mapEx)
                                    {
                                        // Required source field empty, or the account could not be matched
                                        // → skip this record (matches legacy skip semantics)
                                        run.TotalSkipped++;
                                        LogOperation(db, runId, identity.Key, OperationType.Skip,
                                            SyncOperationStatus.Skipped, null, 0, mapEx.Message);
                                        processed++;
                                        run.TotalProcessed = processed;
                                        continue;
                                    }
                                    result = await SafeWriteAsync(accountId, "Update", () =>
                                        _targetConnector.UpdateDynamicAsync(accountId, mappedAttrs, _cts.Token));
                                }

                                // ✅ Feed the circuit breaker so sustained AD update failures can trip it
                                if (!dryRun)
                                {
                                    if (result.Success) _resilience.RecordSuccess(AdComponent);
                                    else RecordWriteFailure(result);
                                }

                                if (result.Success && result.ChangedFields != "NoChanges")
                                {
                                    run.TotalUpdated++;
                                    // Dry run must not stamp the hash — doing so tells the next real
                                    // sync this identity is already up to date. See _isDryRun.
                                    if (!dryRun)
                                    {
                                        syncState.CurrentHash = currentHash;
                                        syncState.LastSyncDate = DateTime.UtcNow;
                                        syncState.Status = "Synced";
                                    }
                                    LogOperation(db, runId, identity.Key, OperationType.Update,
                                        SyncOperationStatus.Success, result.ChangedFields, result.DurationMs);

                                    // ✅ Lifecycle: individual processing only when lifecycle integration is DISABLED
                                    if (!enableLifecycleDuringSync)
                                    {
                                        var previousStatusCode2 = syncState?.LastStatusCode;
                                        // Treat an unknown previous status (null — e.g. rows migrated before LastStatusCode
                                        // existed) as "changed" so lifecycle runs once to establish the baseline, instead of
                                        // silently skipping the identity until their status changes twice.
                                        var statusChanged2 = previousStatusCode2 == null || previousStatusCode2 != identity.StatusCode;
                                        if (!dryRun && statusChanged2)
                                        {
                                            try
                                            {
                                                var lifecycleEngine = batchScope.ServiceProvider.GetRequiredService<ILifecycleEngine>();
                                                var lcResult = await lifecycleEngine.ProcessIdentityAsync(identity.Key, false, _runTenantId, _cts.Token);
                                                if (lcResult.Success && !string.IsNullOrEmpty(lcResult.ActionsTaken))
                                                {
                                                    _logger.LogInformation("[{CorrelationId}] Lifecycle processed for {IdentityId} (StatusCode {OldStatus}→{NewStatus}): {Actions}",
                                                        run.CorrelationId, identity.Key, previousStatusCode2, identity.StatusCode, lcResult.ActionsTaken);
                                                    LogOperation(db, runId, identity.Key, OperationType.Move,
                                                        SyncOperationStatus.Success, lcResult.ActionsTaken, lcResult.DurationMs);
                                                }
                                                else if (!lcResult.Success)
                                                {
                                                    _logger.LogWarning("Lifecycle failed for {IdentityId}: {Error}, clearing DbContext", identity.Key, lcResult.Error);
                                                    db.ChangeTracker.Clear();
                                                    batchSyncStates = await db.SyncStates
                                                        .Where(s => s.TenantId == _runTenantId && batchIds.Contains(s.IdentityId))
                                                        .ToDictionaryAsync(s => s.IdentityId, _cts.Token);
                                                    if (batchSyncStates.TryGetValue(identity.Key, out var reloaded))
                                                    {
                                                        reloaded.CurrentHash = currentHash;
                                                        reloaded.LastSyncDate = DateTime.UtcNow;
                                                        reloaded.Status = "Synced";
                                                    }
                                                }
                                            }
                                            catch (Exception lcEx)
                                            {
                                                _logger.LogWarning(lcEx, "Lifecycle processing failed for {IdentityId}, sync continues", identity.Key);
                                                db.ChangeTracker.Clear();
                                                batchSyncStates = await db.SyncStates
                                                    .Where(s => s.TenantId == _runTenantId && batchIds.Contains(s.IdentityId))
                                                    .ToDictionaryAsync(s => s.IdentityId, _cts.Token);
                                                if (batchSyncStates.TryGetValue(identity.Key, out var reloaded))
                                                {
                                                    reloaded.CurrentHash = currentHash;
                                                    reloaded.LastSyncDate = DateTime.UtcNow;
                                                    reloaded.Status = "Synced";
                                                }
                                            }
                                        }
                                    }

                                    // ✅ Store current StatusCode for future change detection
                                    if (syncState != null) syncState.LastStatusCode = identity.StatusCode;
                                }
                                else if (!result.Success)
                                {
                                    run.TotalFailed++;
                                    LogOperation(db, runId, identity.Key, OperationType.Update,
                                        SyncOperationStatus.Failed, null, result.DurationMs, result.Error);
                                }
                                else
                                {
                                    run.TotalNoChange++;
                                }
                            }
                            else
                            {
                                run.TotalNoChange++;
                            }

                            // ✅ Lifecycle: Upsert MetaverseEntry during sync (avoids second Oracle fetch)
                            if (enableLifecycleDuringSync && !dryRun)
                            {
                                UpsertMetaverseEntry(db, identity, currentHash, batchMetaverseEntries2!);
                            }

                            processed++;
                            run.TotalProcessed = processed;
                        }

                        await db.SaveChangesAsync(_cts.Token);
                        // Batch scope disposed here — memory released ✅

                        await Task.Delay(500, _cts.Token);
                    }
                }

                // ✅ Lifecycle: Run bulk rules + export after sync completes (uses local Metaverse data, no Oracle fetch)
                if (enableLifecycleDuringSync && !dryRun)
                {
                    _logger.LogInformation("[{CorrelationId}] Lifecycle integration: Starting bulk Rules + Export", run.CorrelationId);

                    // Phase 4: Apply lifecycle rules
                    _currentPhase = 4;
                    _phaseDescription = "تطبيق قواعد دورة الحياة";
                    _phaseProgress = 0;
                    ReportProgress("Applying lifecycle rules...", run, processed, totalToProcess);

                    using (var rulesScope = _scopeFactory.CreateScope())
                    {
                        var lifecycleEngine = rulesScope.ServiceProvider.GetRequiredService<ILifecycleEngine>();
                        var rulesChanged = await lifecycleEngine.BulkApplyRulesAsync(
                            _runTenantId,
                            (p, t) =>
                            {
                                _phaseProgress = t > 0 ? (double)p / t * 100 : 100;
                                ReportProgress($"Rules: {p}/{t}", run, processed, totalToProcess);
                            }, _cts.Token);
                        _logger.LogInformation("[{CorrelationId}] Lifecycle Rules: {Changed} state transitions", run.CorrelationId, rulesChanged);
                    }

                    // Phase 5: Export to AD
                    _currentPhase = 5;
                    _phaseDescription = "تصدير دورة الحياة إلى AD";
                    _phaseProgress = 0;
                    ReportProgress("Exporting lifecycle changes to AD...", run, processed, totalToProcess);

                    using (var exportScope = _scopeFactory.CreateScope())
                    {
                        var lifecycleEngine = exportScope.ServiceProvider.GetRequiredService<ILifecycleEngine>();
                        var exported = await lifecycleEngine.BulkExportAsync(_scopeFactory,
                            _runTenantId,
                            (p, t) =>
                            {
                                _phaseProgress = t > 0 ? (double)p / t * 100 : 100;
                                ReportProgress($"Export: {p}/{t}", run, processed, totalToProcess);
                            }, _cts.Token);
                        _logger.LogInformation("[{CorrelationId}] Lifecycle Export: {Exported} entries exported", run.CorrelationId, exported);
                    }
                }

                run.Status = run.TotalFailed > 0 ? SyncRunStatus.CompletedWithErrors : SyncRunStatus.Completed;
            }
            catch (OperationCanceledException)
            {
                run.Status = SyncRunStatus.Cancelled;
                _logger.LogWarning("Sync was cancelled");
            }
            catch (CircuitBreakerOpenException cbEx)
            {
                run.Status = SyncRunStatus.Failed;
                run.ErrorMessage = Truncate(cbEx.Message, 2000);
                _logger.LogWarning("[{CorrelationId}] Full Sync aborted — Active Directory circuit breaker is open. Progress so far has been saved.", run.CorrelationId);
            }
            catch (Exception ex)
            {
                run.Status = SyncRunStatus.Failed;
                run.ErrorMessage = Truncate(ex.Message, 2000);
                _logger.LogError(ex, "Sync failed with critical error");
            }
            finally
            {
                overallSw.Stop();
                run.EndTime = DateTime.UtcNow;

                // ✅ Save final SyncRun status in its own scope
                using (var finalScope = _scopeFactory.CreateScope())
                {
                    var finalDb = finalScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var dbRun = await finalDb.SyncRuns.FindAsync(runId);
                    if (dbRun != null)
                    {
                        dbRun.Status = run.Status;
                        dbRun.EndTime = run.EndTime;
                        dbRun.ErrorMessage = run.ErrorMessage;
                        dbRun.TotalCreated = run.TotalCreated;
                        dbRun.TotalUpdated = run.TotalUpdated;
                        dbRun.TotalFailed = run.TotalFailed;
                        dbRun.TotalNoChange = run.TotalNoChange;
                        dbRun.TotalSkipped = run.TotalSkipped;
                        dbRun.TotalProcessed = run.TotalProcessed;
                        dbRun.TotalAlreadyExisted = run.TotalAlreadyExisted;
                        await finalDb.SaveChangesAsync(CancellationToken.None);
                    }
                }

                // Report the real outcome. This used to say "completed" unconditionally, so a run
                // that aborted on a SQL timeout logged "Sync completed" on the line after
                // "Sync failed with critical error" — the counters then read as a finished run
                // when most identities had never been looked at.
                if (run.Status == SyncRunStatus.Completed)
                {
                    _logger.LogInformation(
                        "[{CorrelationId}] Sync completed: Created={Created}, Updated={Updated}, Failed={Failed}, NoChange={NoChange}, Duration={Duration}",
                        run.CorrelationId, run.TotalCreated, run.TotalUpdated, run.TotalFailed, run.TotalNoChange, overallSw.Elapsed);
                }
                else
                {
                    _logger.LogError(
                        "[{CorrelationId}] Sync ended as {Status} — PARTIAL, only {Processed} identities were processed: " +
                        "Created={Created}, Updated={Updated}, Failed={Failed}, NoChange={NoChange}, Duration={Duration}. Error: {Error}",
                        run.CorrelationId, run.Status, run.TotalProcessed,
                        run.TotalCreated, run.TotalUpdated, run.TotalFailed, run.TotalNoChange,
                        overallSw.Elapsed, run.ErrorMessage ?? "(none)");
                }

                // Reported after the outcome line, and for a partial run too: identities the
                // policy withheld are just as real when the run aborted early.
                LogCreationGateSummary();

                _phaseProgress = 100;
                _phaseDescription = "اكتملت المزامنة";
                ReportProgress("Sync completed", run, run.TotalProcessed, run.TotalProcessed, force: true);
            }

            return run;
        }

        /// <summary>
        /// Execute a delta sync — only processes identities whose data has changed since last sync.
        /// When tenantId is null, every active tenant runs sequentially.
        /// </summary>
        /// <param name="triggeredBy">Username, or <see cref="ActorNames.Schedule"/> — see RunFullSyncAsync.</param>
        public async Task<SyncRun> RunDeltaSyncAsync(int batchSize = 1000, bool dryRun = false, int? tenantId = null, CancellationToken ct = default, string? triggeredBy = null)
        {
            _runTriggeredBy = triggeredBy;
            lock (_runLock)
            {
                if (_isRunning) throw new InvalidOperationException("A sync is already running");
                _isRunning = true;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            }

            // Cross-instance/restart guard — released in the finally below.
            try { _distributedSyncLock = TryAcquireDistributedSyncLock(); }
            catch { ReleaseRunLock(); throw; }

            try
            {
                var tenants = await LoadRunTenantsAsync(tenantId, _cts.Token);
                SyncRun? last = null;

                if (tenants.Count == 0)
                {
                    last = await RunDeltaSyncCoreAsync(null, batchSize, dryRun);
                }
                else
                {
                    foreach (var tenant in tenants)
                    {
                        if (_cts.Token.IsCancellationRequested) break;
                        last = await RunDeltaSyncCoreAsync(tenant, batchSize, dryRun);
                    }
                }

                return last!;
            }
            finally
            {
                ReleaseRunLock();
            }
        }

        /// <summary>
        /// Delta sync for a single tenant. Assumes the run lock is already held by the caller.
        /// </summary>
        private async Task<SyncRun> RunDeltaSyncCoreAsync(TenantSettings? tenant, int batchSize, bool dryRun)
        {
            SetRunContext(tenant, dryRun);

            int runId;
            var run = new SyncRun
            {
                TenantId = tenant?.Id,
                RunType = dryRun ? "DryRun-Delta" : "Delta",
                Status = SyncRunStatus.Running,
                StartTime = DateTime.UtcNow,
                BatchSize = batchSize,
                TriggeredBy = _runTriggeredBy ?? ActorNames.System
            };

            using (var initScope = _scopeFactory.CreateScope())
            {
                var initDb = initScope.ServiceProvider.GetRequiredService<AppDbContext>();
                initDb.SyncRuns.Add(run);
                await initDb.SaveChangesAsync(_cts!.Token);
                runId = run.Id;
            }

            var overallSw = Stopwatch.StartNew();

            // ✅ Tenant configuration (mappings + lifecycle setting) for this run
            var tenantMappings = tenant?.AttributeMappings?.ToList() ?? new List<TenantAttributeMapping>();
            // EffectiveGlobalDefault, not GlobalDefaultValue: the placeholder is an Active Directory
            // workaround for its refusal of empty attribute writes, and sending it to a target that
            // has no such constraint writes nonsense — a source row with no email produced
            // emails[0].value = "." in a SCIM service.
            var globalDefaultValue = tenant?.EffectiveGlobalDefault ?? ".";
            var useGlobalDefaultForEmptyFields = tenant?.UseGlobalDefaultForEmptyFields ?? false;
            var enableLifecycleDuringSync = tenant?.EnableLifecycleDuringSync ?? false;

            if (tenantMappings.Count == 0)
            {
                _logger.LogError("⚠️ No attribute mappings configured for Delta Sync (tenant {Tenant})!", tenant?.TenantName ?? "(none)");
                run.Status = SyncRunStatus.Failed;
                run.ErrorMessage = "لا يوجد إعدادات ربط الحقول (Attribute Mapping). يرجى إعداد ربط الحقول من صفحة الإعدادات أولاً.";
                run.EndTime = DateTime.UtcNow;
                using (var failScope = _scopeFactory.CreateScope())
                {
                    var failDb = failScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var dbRun = await failDb.SyncRuns.FindAsync(runId);
                    if (dbRun != null)
                    {
                        dbRun.Status = run.Status;
                        dbRun.EndTime = run.EndTime;
                        dbRun.ErrorMessage = run.ErrorMessage;
                        await failDb.SaveChangesAsync(CancellationToken.None);
                    }
                }
                return run;
            }

            try
            {
                _logger.LogInformation("[{CorrelationId}] Starting Delta Sync — checking only existing synced identities", run.CorrelationId);

                // Step 1: Get all synced identity IDs from local DB
                int[] syncedStates;
                using (var readScope = _scopeFactory.CreateScope())
                {
                    var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    _currentPhase = 1; _totalPhases = 2;
                    _phaseDescription = "التحضير — قراءة البيانات";
                    _phaseProgress = 0;
                    ReportProgress("Reading synced identity IDs from database...", run);
                    syncedStates = await readDb.SyncStates
                        .AsNoTracking()
                        .Where(s => s.TenantId == _runTenantId && s.CreatedInAD)
                        .Select(s => s.IdentityId)
                        .ToArrayAsync(_cts.Token);
                }

                _logger.LogInformation("[{CorrelationId}] Delta: {Count} synced identities to check", run.CorrelationId, syncedStates.Length);

                if (syncedStates.Length == 0)
                {
                    run.Status = SyncRunStatus.Completed;
                    run.EndTime = DateTime.UtcNow;
                    using (var emptyScope = _scopeFactory.CreateScope())
                    {
                        var emptyDb = emptyScope.ServiceProvider.GetRequiredService<AppDbContext>();
                        var dbRun = await emptyDb.SyncRuns.FindAsync(runId);
                        if (dbRun != null)
                        {
                            dbRun.Status = run.Status;
                            dbRun.EndTime = run.EndTime;
                            await emptyDb.SaveChangesAsync(_cts.Token);
                        }
                    }
                    return run;
                }

                var totalToProcess = syncedStates.Length;
                var processed = 0;

                _phaseProgress = 100;
                ReportProgress($"Preparation complete: {totalToProcess} identities to check", run, 0, totalToProcess);

                // Step 2: Process in batches — read from Oracle and compare hashes
                _currentPhase = 2;
                _phaseDescription = "فحص وتحديث التغييرات";
                _phaseProgress = 0;
                for (int i = 0; i < syncedStates.Length; i += batchSize)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    ThrowIfAdCircuitOpen();

                    var batchIds = syncedStates.Skip(i).Take(batchSize).ToArray();
                    _phaseProgress = syncedStates.Length > 0 ? (double)i / syncedStates.Length * 100 : 0;
                    ReportProgress($"Delta batch {i / batchSize + 1}: {batchIds.Length} records", run, processed, totalToProcess);

                    var identities = await _sourceConnector.ReadBatchAsync(batchIds, _cts.Token);

                    // ✅ New scope per batch
                    using var batchScope = _scopeFactory.CreateScope();
                    var db = batchScope.ServiceProvider.GetRequiredService<AppDbContext>();

                    // ✅ Batch-load all SyncStates for this batch
                    var batchSyncStates = await db.SyncStates
                        .Where(s => s.TenantId == _runTenantId && batchIds.Contains(s.IdentityId))
                        .ToDictionaryAsync(s => s.IdentityId, _cts.Token);

foreach (var identity in identities)
                        {
                            _cts.Token.ThrowIfCancellationRequested();

                            // ✅ Apply Global Default for empty fields before validation
                            if (useGlobalDefaultForEmptyFields && !string.IsNullOrEmpty(globalDefaultValue))
                            {
                                identity.ApplyGlobalDefaults(globalDefaultValue);
                            }

                        // ✅ Compute hash once
                        var currentHash = identity.ComputeHash();

                        // ✅ Lookup from pre-loaded dictionary
                        batchSyncStates.TryGetValue(identity.Key, out var syncState);

                        // Only process if hash has changed
                        if (syncState != null && syncState.CurrentHash != currentHash)
                        {
                            Core.Interfaces.SyncResult result;
                            if (dryRun)
                                result = new Core.Interfaces.SyncResult { Success = true, ChangedFields = "DryRun" };
                            else
                            {
                                var sourceRow = identity.ToDictionary();
                                Dictionary<string, string> mappedAttrs;
                                string accountId;
                                try
                                {
                                    // See the delta path above: resolve, never regenerate, for update.
                                    (accountId, _) = await ResolveAccountAsync(
                                        sourceRow, tenantMappings, identity.Key.ToString(), _cts.Token);

                                    mappedAttrs = MappingEngine.ApplyMappings(sourceRow, tenantMappings,
                                        useGlobalDefaultForEmptyFields ? globalDefaultValue : null);
                                }
                                catch (InvalidOperationException mapEx)
                                {
                                    // Required source field empty, or unmatchable → skip this record
                                    run.TotalSkipped++;
                                    LogOperation(db, runId, identity.Key, OperationType.Skip,
                                        SyncOperationStatus.Skipped, null, 0, mapEx.Message);
                                    processed++;
                                    run.TotalProcessed = processed;
                                    continue;
                                }
                                result = await SafeWriteAsync(accountId, "Update", () =>
                                        _targetConnector.UpdateDynamicAsync(accountId, mappedAttrs, _cts.Token));
                            }

                            // ✅ Feed the circuit breaker so sustained AD update failures can trip it
                            if (!dryRun)
                            {
                                if (result.Success) _resilience.RecordSuccess(AdComponent);
                                else RecordWriteFailure(result);
                            }

                            if (result.Success)
                            {
                                // Dry run must not stamp the hash — see _isDryRun.
                                // Stamped on any success, including "NoChanges": the source did
                                // change (we only get here when the hash differs), it simply
                                // produced no AD difference. Leaving the hash alone made every
                                // such identity be re-read and re-compared on every delta run.
                                if (!dryRun)
                                {
                                    syncState.CurrentHash = currentHash;
                                    syncState.LastSyncDate = DateTime.UtcNow;
                                    syncState.Status = "Synced";
                                }

                                if (result.ChangedFields != "NoChanges")
                                {
                                    run.TotalUpdated++;
                                    LogOperation(db, runId, identity.Key, OperationType.Update,
                                        SyncOperationStatus.Success, result.ChangedFields, result.DurationMs);
                                }
                                else
                                {
                                    run.TotalNoChange++;
                                }

                                // ✅ Lifecycle: driven by the STATUS changing, not by whether an
                                // AD attribute happened to differ. These are independent: a
                                // lifecycle rule reacts to the source status, while the attribute
                                // update depends on what the tenant chose to map.
                                //
                                // This block used to sit inside "ChangedFields != NoChanges", so
                                // an identity that graduated produced no lifecycle action unless
                                // some mapped attribute also changed. It worked here only because
                                // this tenant maps its status description to two AD attributes —
                                // an optional mapping. Drop it, or add a status whose description
                                // does not change, and graduates would stop being archived with
                                // nothing reported anywhere.
                                var previousStatusCode3 = syncState?.LastStatusCode;
                                // Null previous status (migrated rows) counts as "changed" — see RunFullSyncAsync note.
                                var statusChanged3 = previousStatusCode3 == null || previousStatusCode3 != identity.StatusCode;
                                if (enableLifecycleDuringSync && !dryRun && statusChanged3)
                                {
                                    try
                                    {
                                        var lcEngine = batchScope.ServiceProvider.GetRequiredService<ILifecycleEngine>();
                                        var lcResult = await lcEngine.ProcessIdentityAsync(identity.Key, false, _runTenantId, _cts.Token);
                                        if (lcResult.Success && !string.IsNullOrEmpty(lcResult.ActionsTaken))
                                        {
                                            _logger.LogInformation("[{CorrelationId}] Delta Lifecycle for {IdentityId} (StatusCode {OldStatus}→{NewStatus}): {Actions}",
                                                run.CorrelationId, identity.Key, previousStatusCode3, identity.StatusCode, lcResult.ActionsTaken);
                                            LogOperation(db, runId, identity.Key, OperationType.Move,
                                                SyncOperationStatus.Success, lcResult.ActionsTaken, lcResult.DurationMs);
                                        }
                                        else if (!lcResult.Success)
                                        {
                                            _logger.LogWarning("Delta lifecycle failed for {IdentityId}: {Error}", identity.Key, lcResult.Error);
                                            db.ChangeTracker.Clear();
                                            batchSyncStates = await db.SyncStates
                                                .Where(s => s.TenantId == _runTenantId && batchIds.Contains(s.IdentityId))
                                                .ToDictionaryAsync(s => s.IdentityId, _cts.Token);
                                            if (batchSyncStates.TryGetValue(identity.Key, out var reloaded))
                                            {
                                                reloaded.CurrentHash = currentHash;
                                                reloaded.LastSyncDate = DateTime.UtcNow;
                                                reloaded.Status = "Synced";
                                            }
                                        }
                                    }
                                    catch (Exception lcEx)
                                    {
                                        _logger.LogWarning(lcEx, "Delta lifecycle exception for {IdentityId}, sync continues", identity.Key);
                                        db.ChangeTracker.Clear();
                                        batchSyncStates = await db.SyncStates
                                            .Where(s => s.TenantId == _runTenantId && batchIds.Contains(s.IdentityId))
                                            .ToDictionaryAsync(s => s.IdentityId, _cts.Token);
                                        if (batchSyncStates.TryGetValue(identity.Key, out var reloaded))
                                        {
                                            reloaded.CurrentHash = currentHash;
                                            reloaded.LastSyncDate = DateTime.UtcNow;
                                            reloaded.Status = "Synced";
                                        }
                                    }
                                }

                                // ✅ Store current StatusCode for future change detection
                                if (syncState != null) syncState.LastStatusCode = identity.StatusCode;
                            }
                            else
                            {
                                run.TotalFailed++;
                                LogOperation(db, runId, identity.Key, OperationType.Update,
                                    SyncOperationStatus.Failed, null, result.DurationMs, result.Error);
                            }
                        }
                        else
                        {
                            run.TotalNoChange++;
                        }

                        processed++;
                        run.TotalProcessed = processed;
                    }

                    await db.SaveChangesAsync(_cts.Token);
                    await Task.Delay(500, _cts.Token); // Rate limiting
                }

                run.Status = run.TotalFailed > 0 ? SyncRunStatus.CompletedWithErrors : SyncRunStatus.Completed;
            }
            catch (OperationCanceledException)
            {
                run.Status = SyncRunStatus.Cancelled;
                _logger.LogWarning("Delta Sync was cancelled");
            }
            catch (CircuitBreakerOpenException cbEx)
            {
                run.Status = SyncRunStatus.Failed;
                run.ErrorMessage = Truncate(cbEx.Message, 2000);
                _logger.LogWarning("[{CorrelationId}] Delta Sync aborted — Active Directory circuit breaker is open. Progress so far has been saved.", run.CorrelationId);
            }
            catch (Exception ex)
            {
                run.Status = SyncRunStatus.Failed;
                run.ErrorMessage = Truncate(ex.Message, 2000);
                _logger.LogError(ex, "Delta Sync failed with critical error");
            }
            finally
            {
                overallSw.Stop();
                run.EndTime = DateTime.UtcNow;

                // ✅ Save final SyncRun status
                using (var finalScope = _scopeFactory.CreateScope())
                {
                    var finalDb = finalScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var dbRun = await finalDb.SyncRuns.FindAsync(runId);
                    if (dbRun != null)
                    {
                        dbRun.Status = run.Status;
                        dbRun.EndTime = run.EndTime;
                        dbRun.ErrorMessage = run.ErrorMessage;
                        dbRun.TotalCreated = run.TotalCreated;
                        dbRun.TotalUpdated = run.TotalUpdated;
                        dbRun.TotalFailed = run.TotalFailed;
                        dbRun.TotalNoChange = run.TotalNoChange;
                        dbRun.TotalSkipped = run.TotalSkipped;
                        dbRun.TotalProcessed = run.TotalProcessed;
                        dbRun.TotalAlreadyExisted = run.TotalAlreadyExisted;
                        await finalDb.SaveChangesAsync(CancellationToken.None);
                    }
                }

                _logger.LogInformation(
                    "[{CorrelationId}] Delta Sync completed: Updated={Updated}, Failed={Failed}, NoChange={NoChange}, Duration={Duration}",
                    run.CorrelationId, run.TotalUpdated, run.TotalFailed, run.TotalNoChange, overallSw.Elapsed);

                _phaseProgress = 100;
                _phaseDescription = "اكتملت المزامنة";
                ReportProgress("Delta Sync completed", run, run.TotalProcessed, run.TotalProcessed, force: true);
            }

            return run;
        }


        /// <param name="triggeredBy">Username, or an <see cref="ActorNames"/> token — see RunFullSyncAsync.</param>
        public async Task<SyncOperation> SyncSingleAsync(int identityId, bool dryRun = false, int? tenantId = null, CancellationToken ct = default, string? triggeredBy = null)
        {
            _runTriggeredBy = triggeredBy;
            if (IsRunning)
                return new SyncOperation
                {
                    IdentityId = identityId,
                    Operation = OperationType.Sync,
                    Status = SyncOperationStatus.Failed,
                    ErrorMessage = "لا يمكن المزامنة الفردية أثناء تشغيل مزامنة كاملة"
                };

            // Cross-instance guard: don't modify AD for a single identity while a bulk sync runs elsewhere.
            IDisposable? singleSyncLock;
            try { singleSyncLock = TryAcquireDistributedSyncLock(); }
            catch (InvalidOperationException)
            {
                return new SyncOperation
                {
                    IdentityId = identityId,
                    Operation = OperationType.Sync,
                    Status = SyncOperationStatus.Failed,
                    ErrorMessage = "لا يمكن المزامنة الفردية أثناء تشغيل مزامنة أخرى (على هذا الخادم أو خادم آخر)"
                };
            }
            using var _singleSyncLockScope = singleSyncLock; // released on every return path (null-safe)

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // ✅ Load tenant mappings + OU/group rules for single sync (specific tenant, or first active)
            var tenantQuery = db.TenantSettings
                .AsNoTracking()
                .Include(t => t.AttributeMappings)
                .Include(t => t.OURules)
                .Include(t => t.GroupRules);
            var tenant = tenantId.HasValue
                ? await tenantQuery.FirstOrDefaultAsync(t => t.Id == tenantId.Value, ct)
                : await tenantQuery.FirstOrDefaultAsync(t => t.IsActive, ct);
            var tenantMappings = tenant?.AttributeMappings?.ToList() ?? new List<TenantAttributeMapping>();

            // ✅ Per-tenant source/target connectors + tenant scope for state queries
            SetRunContext(tenant, dryRun);

            var identities = await _sourceConnector.ReadBatchAsync(new[] { identityId }, ct);
            var identity = identities.FirstOrDefault();

            if (identity == null)
            {
                return new SyncOperation
                {
                    IdentityId = identityId,
                    Operation = OperationType.Sync,
                    Status = SyncOperationStatus.Failed,
                    ErrorMessage = "Identity not found in the source database"
                };
            }

            var runStart = DateTime.UtcNow;
            Core.Interfaces.SyncResult result;

            if (tenantMappings.Count == 0)
            {
                return new SyncOperation
                {
                    IdentityId = identityId,
                    Operation = OperationType.Sync,
                    Status = SyncOperationStatus.Failed,
                    ErrorMessage = "لا يوجد إعدادات ربط الحقول (Attribute Mapping). يرجى إعداد ربط الحقول من صفحة الإعدادات أولاً."
                };
            }

            // ✅ Fix #9: Null-safe tenant access
            var globalDefault = tenant?.UseGlobalDefaultForEmptyFields == true ? tenant.GlobalDefaultValue : null;

            // ✅ Apply Global Default for empty fields before mapping
            if (!string.IsNullOrEmpty(globalDefault))
            {
                identity.ApplyGlobalDefaults(globalDefault);
            }

            // ✅ Account identifier comes from the tenant's identifier mapping (falls back to source key)
            var sourceRow = identity.ToDictionary();
            var (accountId, existsInAD) = await ResolveAccountAsync(
                sourceRow, tenantMappings, identityId.ToString(), ct);

            if (existsInAD)
            {
                if (dryRun)
                    result = new Core.Interfaces.SyncResult { Success = true, ChangedFields = "DryRun" };
                else
                {
                    try
                    {
                        var mappedAttrs = MappingEngine.ApplyMappings(sourceRow, tenantMappings, globalDefault);
                        result = await SafeWriteAsync(accountId, "Update", () =>
                            _targetConnector.UpdateDynamicAsync(accountId, mappedAttrs, ct));
                    }
                    catch (InvalidOperationException mapEx)
                    {
                        // Required source field empty — surface as a failed single sync
                        result = new Core.Interfaces.SyncResult { Success = false, Error = mapEx.Message };
                    }
                }
            }
            else if (EvaluateCreationGate(sourceRow) is { Allowed: false } gate)
            {
                // Refusing to provision is a policy outcome, not a fault: reported as a clear
                // message rather than an error so a manual sync of an identity the tenant does
                // not provision reads as "not created, and here is why".
                result = new Core.Interfaces.SyncResult { Success = false, Error = gate.Reason };
            }
            else
            {
                var randomPassword = PasswordGenerator.Generate();
                identity.Password = randomPassword;

                if (dryRun)
                    result = new Core.Interfaces.SyncResult { Success = true };
                else
                {
                    try
                    {
                        var mappedAttrs = MappingEngine.ApplyMappings(sourceRow, tenantMappings, globalDefault);
                        StampMatchAttribute(mappedAttrs, sourceRow, identityId.ToString());
                        var targetOU = MappingEngine.ResolveOU(sourceRow, tenant?.OURules?.ToList() ?? new List<TenantOURule>(), tenant?.ADBaseDN ?? "", _logger);
                        var targetGroups = MappingEngine.ResolveGroups(sourceRow, tenant?.GroupRules?.ToList() ?? new List<TenantGroupRule>());
                        result = await SafeWriteAsync(accountId, "Create", () =>
                            _targetConnector.CreateDynamicAsync(accountId, mappedAttrs,
                                targetOU, targetGroups, randomPassword, ct));
                    }
                    catch (InvalidOperationException mapEx)
                    {
                        result = new Core.Interfaces.SyncResult { Success = false, Error = mapEx.Message };
                    }
                }
            }

            // Persist a run + operation record so single syncs show up in the
            // identity's sync history (SyncOperations requires a parent SyncRun).
            var singleRun = new SyncRun
            {
                TenantId = tenant?.Id,
                RunType = dryRun ? "DryRun-Single" : "Single",
                Status = result.Success ? SyncRunStatus.Completed : SyncRunStatus.Failed,
                StartTime = runStart,
                EndTime = DateTime.UtcNow,
                TotalProcessed = 1,
                TotalCreated = result.Success && !existsInAD ? 1 : 0,
                TotalUpdated = result.Success && existsInAD ? 1 : 0,
                TotalFailed = result.Success ? 0 : 1,
                ErrorMessage = Truncate(result.Error, 2000),
                TriggeredBy = _runTriggeredBy ?? ActorNames.System,
                BatchSize = 1
            };
            db.SyncRuns.Add(singleRun);
            db.SyncOperations.Add(new SyncOperation
            {
                SyncRun = singleRun,
                IdentityId = identity.Key,
                Operation = existsInAD ? OperationType.Update : OperationType.Create,
                Status = result.Success ? SyncOperationStatus.Success : SyncOperationStatus.Failed,
                ChangedFields = Truncate(result.ChangedFields, 500),
                DurationMs = result.DurationMs,
                ErrorMessage = Truncate(result.Error, 2000),
                Timestamp = DateTime.UtcNow
            });

            string? lifecycleAction = null;

            if (result.Success)
            {
                var existingState = await db.SyncStates.FirstOrDefaultAsync(s => s.TenantId == _runTenantId && s.IdentityId == identity.Key, ct);
                var hash = identity.ComputeHash();

                // ✅ Detect status change for lifecycle processing
                var previousStatusCode = existingState?.LastStatusCode;
                // Null previous status (new/migrated rows) counts as "changed" — see RunFullSyncAsync note.
                var statusChanged = previousStatusCode == null || previousStatusCode != identity.StatusCode;

                UpsertSyncState(db, identity, "Synced", hash, existingState);
                await db.SaveChangesAsync(ct);

                // ✅ Lifecycle: Process identity if status changed (e.g., Active→Suspended, Suspended→Active)
                if (statusChanged && !dryRun)
                {
                    try
                    {
                        var lifecycleEngine = scope.ServiceProvider.GetRequiredService<ILifecycleEngine>();
                        var lcResult = await lifecycleEngine.ProcessIdentityAsync(identity.Key, false, tenant?.Id, ct);
                        if (lcResult.Success && !string.IsNullOrEmpty(lcResult.ActionsTaken))
                        {
                            lifecycleAction = lcResult.ActionsTaken;
                            _logger.LogInformation("Single sync lifecycle for {IdentityId} (StatusCode {OldStatus}→{NewStatus}): {Actions}",
                                identity.Key, previousStatusCode, identity.StatusCode, lcResult.ActionsTaken);
                        }
                    }
                    catch (Exception lcEx)
                    {
                        _logger.LogWarning(lcEx, "Single sync lifecycle failed for {IdentityId}, sync result still success", identity.Key);
                    }
                }
            }
            else
            {
                // Failed AD write: still persist the run + operation for the history
                await db.SaveChangesAsync(ct);
            }

            return new SyncOperation
            {
                IdentityId = identityId,
                Operation = existsInAD ? OperationType.Update : OperationType.Create,
                Status = result.Success ? SyncOperationStatus.Success : SyncOperationStatus.Failed,
                ChangedFields = result.ChangedFields,
                ErrorMessage = result.Error,
                DurationMs = result.DurationMs,
                LifecycleAction = lifecycleAction
            };
        }

        // === Private Helpers ===

        /// <summary>
        /// Load the tenants a run should process: the requested one, or all active tenants.
        /// Includes attribute mappings + OU/group rules so the run needs no further config queries.
        /// </summary>
        private async Task<List<TenantSettings>> LoadRunTenantsAsync(int? tenantId, CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var query = db.TenantSettings
                .AsNoTracking()
                .Include(t => t.AttributeMappings)
                .Include(t => t.OURules)
                .Include(t => t.GroupRules);

            return tenantId.HasValue
                ? await query.Where(t => t.Id == tenantId.Value).ToListAsync(ct)
                : await query.Where(t => t.IsActive).OrderBy(t => t.Id).ToListAsync(ct);
        }

        /// <summary>
        /// Point the engine at one tenant: its connectors (from the factory) and its
        /// TenantId scope for all state reads/writes. Safe because only one run executes
        /// at a time (in-memory + distributed lock).
        /// </summary>
        private void SetRunContext(TenantSettings? tenant, bool dryRun = false)
        {
            _runTenantId = tenant?.Id ?? 0;
            _isDryRun = dryRun;

            _runCreationMode = tenant?.AccountCreationMode;
            _runCreationConditionField = tenant?.AccountCreationConditionField;
            _runCreationConditionOperator = tenant?.AccountCreationConditionOperator;
            _runCreationConditionValue = tenant?.AccountCreationConditionValue;
            _creationSkipped = 0;
            _creationSkipReasons.Clear();

            _runMatchAttribute = tenant?.ADMatchAttribute;
            _runMatchSourceColumn = tenant?.ADMatchSourceColumn;
            _runCollisionFormat = tenant?.UsernameCollisionFormat;
            _runCollisionStart = tenant?.UsernameCollisionStart ?? 2;
            _runCollisionMaxAttempts = tenant?.UsernameCollisionMaxAttempts ?? 20;

            if (tenant != null && _connectorFactory != null)
            {
                _sourceConnector = _connectorFactory.CreateSourceConnector(tenant);
                _targetConnector = _connectorFactory.CreateTargetConnector(tenant);
            }
            // else: keep the appsettings-configured default connectors (legacy behavior / tests)
        }

        // ═══════════════════════════════════════
        // ACCOUNT RESOLUTION
        // ═══════════════════════════════════════
        // Set from tenant settings by SetRunContext, like _isDryRun — one choke point rather than
        // a parameter threaded through every call site, because the number of call sites is what
        // let the dry-run guard be missed in one of them.

        /// <summary>
        /// Who started the run in progress: a username, or an <see cref="ActorNames"/> token.
        /// Set once by the public entry point rather than passed down, because the run splits into
        /// one core call per tenant and the actor is a property of the run, not of the tenant.
        /// Safe as a field for the same reason <c>_isDryRun</c> is: the run lock allows one at a time.
        /// </summary>
        private string? _runTriggeredBy;

        private string? _runMatchAttribute;
        private string? _runMatchSourceColumn;
        private string? _runCollisionFormat;
        private int _runCollisionStart = 2;
        private int _runCollisionMaxAttempts = 20;

        /// <summary>
        /// Decides which AD account this source row belongs to, and whether it exists yet.
        ///
        /// Two modes, chosen by whether the tenant named a match attribute:
        ///
        /// Without one (the original behaviour, and what the numeric-identity tenant uses) the
        /// account name IS the key — it is derived from an immutable source number, so looking the
        /// account up by name is reliable.
        ///
        /// With one, the name is treated as a display choice rather than a key. The account is
        /// found by the immutable value (employee number in extensionAttribute2) and its EXISTING
        /// sAMAccountName is returned untouched, so a later spelling correction in the source
        /// renames nothing and — the actual risk — does not read as a missing account and create a
        /// duplicate. Only genuinely new identities get a generated name, and that name is checked
        /// for availability first.
        /// </summary>
        private async Task<(string accountId, bool exists)> ResolveAccountAsync(
            Dictionary<string, object?> sourceRow,
            List<TenantAttributeMapping> mappings,
            string fallbackId,
            CancellationToken ct)
        {
            var generatedName = MappingEngine.GetIdentifier(sourceRow, mappings) ?? fallbackId;

            if (string.IsNullOrWhiteSpace(_runMatchAttribute))
            {
                var exists = await _targetConnector.ExistsAsync(generatedName, ct);
                return (generatedName, exists);
            }

            var matchValue = ResolveMatchValue(sourceRow, fallbackId);
            if (string.IsNullOrWhiteSpace(matchValue))
            {
                // Without the join value there is no safe way to tell "new person" from "person
                // whose name changed", and guessing creates duplicates. Fail the record instead.
                throw new InvalidOperationException(
                    $"Match column '{_runMatchSourceColumn ?? "(source key)"}' is empty — cannot match this record " +
                    $"against AD attribute '{_runMatchAttribute}'");
            }

            var existingSam = await _targetConnector.FindAccountByAttributeAsync(_runMatchAttribute!, matchValue, ct);
            if (!string.IsNullOrWhiteSpace(existingSam))
                return (existingSam!, true);

            var availableName = await FindAvailableAccountNameAsync(generatedName, ct);
            return (availableName, false);
        }

        /// <summary>
        /// The single point where provisioning is allowed or refused.
        ///
        /// Both creation paths (bulk and single) route through here rather than testing the policy
        /// themselves — a guard duplicated per call site is one that eventually exists at only
        /// some of them, which is how the dry-run guard was missed the first time.
        ///
        /// Refusals are tallied so the run can report the total; per-record skip lines alone are
        /// too easy to scroll past.
        /// </summary>
        private MappingEngine.CreationGateResult EvaluateCreationGate(Dictionary<string, object?> sourceRow)
        {
            var decision = MappingEngine.ShouldCreateAccount(
                _runCreationMode,
                _runCreationConditionField,
                _runCreationConditionOperator,
                _runCreationConditionValue,
                sourceRow);

            if (!decision.Allowed)
            {
                _creationSkipped++;
                _creationSkipReasons[decision.Reason] =
                    _creationSkipReasons.TryGetValue(decision.Reason, out var n) ? n + 1 : 1;
            }

            return decision;
        }

        /// <summary>
        /// Reports withheld provisioning at the end of a run.
        ///
        /// Deliberately a warning even though refusing is correct behaviour: "creation is off and
        /// 3,412 identities therefore have no account" is a fact an operator must see, especially
        /// after switching a tenant to Never expecting it to affect a handful of people.
        /// </summary>
        private void LogCreationGateSummary()
        {
            if (_creationSkipped == 0) return;

            _logger.LogWarning(
                "Provisioning withheld for {Count} identities in this run (mode: {Mode}) — " +
                "these identities have no AD account and none was created",
                _creationSkipped, string.IsNullOrWhiteSpace(_runCreationMode) ? "Always" : _runCreationMode);

            foreach (var (reason, count) in _creationSkipReasons.OrderByDescending(r => r.Value))
                _logger.LogWarning("  · {Count} × {Reason}", count, reason);
        }

        /// <summary>
        /// Writes the join value into the match attribute on the attributes about to be created.
        ///
        /// This is what closes the loop. The account is found on later syncs by
        /// <c>extensionAttribute2 = employee number</c>, so if creation does not stamp that value,
        /// the very next sync finds nothing, generates the name again, and creates a second
        /// account — repeating every run, silently, for every identity.
        ///
        /// An explicit mapping for the same attribute wins: an organisation that already maps the
        /// employee number there keeps its own mapping (and its own transform).
        /// </summary>
        private void StampMatchAttribute(
            Dictionary<string, string> mappedAttrs,
            Dictionary<string, object?> sourceRow,
            string fallbackId)
        {
            if (string.IsNullOrWhiteSpace(_runMatchAttribute)) return;
            if (mappedAttrs.ContainsKey(_runMatchAttribute!)) return;

            var matchValue = ResolveMatchValue(sourceRow, fallbackId);
            if (string.IsNullOrWhiteSpace(matchValue)) return;

            mappedAttrs[_runMatchAttribute!] = matchValue!;
        }

        /// <summary>The source value written to and matched against the tenant's match attribute.</summary>
        private string? ResolveMatchValue(Dictionary<string, object?> sourceRow, string fallbackId)
        {
            if (string.IsNullOrWhiteSpace(_runMatchSourceColumn))
                return fallbackId;

            return sourceRow.TryGetValue(_runMatchSourceColumn, out var v) && v != null && v != DBNull.Value
                ? v.ToString()?.Trim()
                : null;
        }

        /// <summary>
        /// Returns the generated name, or the first free discriminated variant when it is taken.
        ///
        /// Needed because a name built from a person's name is not unique: two people called
        /// Mohammed ali al hareth both resolve to maalhareth. Creating the second one would fail on
        /// a duplicate sAMAccountName, so the second becomes maalhareth2.
        ///
        /// Bounded on purpose — a pattern that maps everyone to the same name (a mistyped column,
        /// say) would otherwise probe AD indefinitely for every record in the run.
        /// </summary>
        private async Task<string> FindAvailableAccountNameAsync(string generatedName, CancellationToken ct)
        {
            if (!await _targetConnector.ExistsAsync(generatedName, ct))
                return generatedName;

            for (var n = _runCollisionStart; n < _runCollisionStart + _runCollisionMaxAttempts; n++)
            {
                var candidate = MappingEngine.ApplyCollisionSuffix(generatedName, n, _runCollisionFormat);

                if (!await _targetConnector.ExistsAsync(candidate, ct))
                {
                    _logger.LogInformation(
                        "Account name '{Generated}' is taken — using '{Candidate}' for the new account",
                        generatedName, candidate);
                    return candidate;
                }
            }

            throw new InvalidOperationException(
                $"Account name '{generatedName}' and {_runCollisionMaxAttempts} discriminated variants are all taken — " +
                $"check the username pattern for this tenant");
        }

        /// <summary>
        /// Upsert SyncState with pre-loaded entity and pre-computed hash.
        /// Eliminates N+1 queries by accepting already-loaded SyncState from batch dictionary.
        /// </summary>
        private void UpsertSyncState(AppDbContext db, Core.Models.Identity.SourceRecord identity,
            string status, string computedHash, SyncState? existing, string? error = null)
        {
            // A dry run must leave no trace. See _isDryRun for why this guard lives here.
            if (_isDryRun) return;

            // Truncate to column limits
            var safeHash = Truncate(computedHash, 100);
            var safeStatus = Truncate(status, 50);
            var safeError = Truncate(error, 1000);

            if (existing != null)
            {
                existing.CurrentHash = safeHash;
                existing.CreatedInAD = true;
                existing.Status = safeStatus;
                existing.ErrorMessage = safeError;
                existing.LastStatusCode = identity.StatusCode;
                existing.LastSyncDate = DateTime.UtcNow;
                existing.LastModified = DateTime.UtcNow;
            }
            else
            {
                db.SyncStates.Add(new SyncState
                {
                    TenantId = _runTenantId,
                    IdentityId = identity.Key,
                    CurrentHash = safeHash,
                    CreatedInAD = status == "Synced",
                    Status = safeStatus,
                    ErrorMessage = safeError,
                    LastStatusCode = identity.StatusCode,
                    LastSyncDate = DateTime.UtcNow,
                    LastModified = DateTime.UtcNow,
                    CreatedDate = DateTime.UtcNow
                });
            }
        }

        /// <summary>
        /// Log a sync operation (synchronous — no DB query, just adds to context).
        /// </summary>
        private void LogOperation(AppDbContext db, int runId, int identityId, OperationType op,
            SyncOperationStatus status, string? changedFields = null, int duration = 0, string? error = null)
        {
            db.SyncOperations.Add(new SyncOperation
            {
                SyncRunId = runId,
                IdentityId = identityId,
                Operation = op,
                Status = status,
                ChangedFields = Truncate(changedFields, 500),
                DurationMs = duration,
                ErrorMessage = Truncate(error, 2000),
                Timestamp = DateTime.UtcNow
            });
        }

        /// <summary>Truncate a string to fit within a database column's max length.</summary>
        private static string? Truncate(string? value, int maxLength)
            => value != null && value.Length > maxLength ? value[..maxLength] : value;

        /// <summary>
        /// Performs one identity's write against the target connector, turning a thrown exception
        /// into a reported failure so the run continues.
        /// </summary>
        /// <remarks>
        /// ⛔ The single choke point for every connector write. There are six call sites across
        /// four paths (full, delta, bulk, single); guarding them individually is exactly how the
        /// original defect survived — the full-sync path was reasoned about and the others were
        /// not.
        ///
        /// The defect, from production on 2026-08-05: a connector reported a failed account
        /// creation by throwing rather than returning <c>Success = false</c>. Every caller already
        /// handles a reported failure — mark the identity Failed, quarantine it, dead-letter it,
        /// carry on — but a throw unwinds past all of that to the top-level handler and ends the
        /// run. One student whose password write timed out stopped a sync covering 111,464
        /// identities, of which zero were processed, and every retry repeated it.
        ///
        /// Cancellation is deliberately re-thrown: an operator stopping the run is not an
        /// identity's fault, and swallowing it would turn "cancel" into "mark everyone failed".
        /// </remarks>
        private async Task<Core.Interfaces.SyncResult> SafeWriteAsync(
            string accountId, string operation, Func<Task<Core.Interfaces.SyncResult>> write)
        {
            try
            {
                return await write();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var kind = Core.Helpers.SyncFailureClassifier.Classify(ex);

                _logger.LogError(ex,
                    "{Operation} failed for {Identity} ({Kind}) — recorded as failed, the run continues",
                    operation, accountId, kind);

                return new Core.Interfaces.SyncResult
                {
                    Success = false,
                    Error = ex.Message,
                    FailureKind = kind
                };
            }
        }

        /// <summary>
        /// Records a failed write against the circuit breaker, unless it was this record's own
        /// fault.
        /// </summary>
        /// <remarks>
        /// The breaker opens after three consecutive failures so a sync stops hammering a dead
        /// directory. Malformed records must not feed it: an intake batch is ordered by identity
        /// number, so a new college or city arrives as a contiguous block, and three neighbouring
        /// records sharing one defect would halt every run at the same place — leaving everyone
        /// behind them without an account.
        ///
        /// Unclassified failures still count, so the breaker only ever loses sensitivity to faults
        /// positively identified as belonging to a single record.
        /// </remarks>
        private void RecordWriteFailure(Core.Interfaces.SyncResult result)
        {
            var kind = result.FailureKind != Core.Helpers.SyncFailureKind.Unknown
                ? result.FailureKind
                : Core.Helpers.SyncFailureClassifier.Classify(result.Error);

            if (Core.Helpers.SyncFailureClassifier.CountsTowardsCircuitBreaker(kind))
            {
                _resilience.RecordFailure(AdComponent, result.Error);
                return;
            }

            _logger.LogWarning(
                "Record-level failure not counted towards the AD circuit breaker: {Error}",
                result.Error);
        }

        // ═══ Distributed sync lock (cross-instance / restart safety) ═══

        /// <summary>
        /// Acquire a Hangfire-backed distributed lock shared by full + delta sync, so two servers —
        /// or a new run started after an ungraceful restart — can never sync concurrently. The lock is
        /// session-scoped in SQL Server, so it auto-releases if this process dies (no stale locks).
        /// Returns null (falls back to the in-memory guard) if Hangfire storage isn't configured
        /// (e.g. unit tests). Throws <see cref="InvalidOperationException"/> if another run holds it.
        /// </summary>
        private IDisposable? TryAcquireDistributedSyncLock()
        {
            JobStorage storage;
            try { storage = JobStorage.Current; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hangfire storage unavailable — distributed sync lock skipped (in-memory guard only)");
                return null;
            }

            var connection = storage.GetConnection();
            try
            {
                var handle = connection.AcquireDistributedLock(SyncLockResource, SyncLockTimeout);
                return new SyncLockHandle(handle, connection);
            }
            catch (DistributedLockTimeoutException)
            {
                connection.Dispose();
                throw new InvalidOperationException("A sync is already running (on this or another instance)");
            }
            catch (Exception ex)
            {
                connection.Dispose();
                _logger.LogWarning(ex, "Could not acquire distributed sync lock — proceeding with in-memory guard only");
                return null;
            }
        }

        /// <summary>Release the in-memory flag and the distributed lock together. Safe to call multiple times.</summary>
        private void ReleaseRunLock()
        {
            IDisposable? toDispose;
            lock (_runLock)
            {
                toDispose = _distributedSyncLock;
                _distributedSyncLock = null;
                _isRunning = false;
            }
            try { toDispose?.Dispose(); } catch { /* releasing the lock must never throw */ }
        }

        /// <summary>Disposes the distributed lock handle and its underlying storage connection together.</summary>
        private sealed class SyncLockHandle : IDisposable
        {
            private readonly IDisposable _lock;
            private readonly IDisposable _connection;
            public SyncLockHandle(IDisposable lockHandle, IDisposable connection)
            {
                _lock = lockHandle;
                _connection = connection;
            }
            public void Dispose()
            {
                try { _lock.Dispose(); } finally { _connection.Dispose(); }
            }
        }

        /// <summary>
        /// Aborts the run if the Active Directory circuit breaker is open — i.e. AD has
        /// failed repeatedly (e.g. the Domain Controller is unreachable). This prevents the
        /// engine from hammering a dead AD with tens of thousands of doomed operations.
        /// The breaker auto-closes after its cooldown, so the next scheduled run resumes.
        /// Called at each batch boundary so any in-flight batch saves its progress first.
        /// </summary>
        /// <summary>
        /// Stops the run when the AD circuit is open, naming the failure that opened it.
        /// </summary>
        /// <remarks>
        /// The message used to assert the cause was connectivity ("check the domain controller is
        /// available"). Repeated failures also open the breaker when the service account has lost
        /// its delegated Create/Reset-Password rights — and that advice then points at the one
        /// place the operator will not find the problem. Carrying the last error avoids sending
        /// anyone to the wrong system, which is the expensive part of an outage during an intake.
        /// </remarks>
        private void ThrowIfAdCircuitOpen()
        {
            if (!_resilience.IsCircuitOpen(AdComponent)) return;

            var lastError = _resilience.GetLastError(AdComponent);
            var cause = string.IsNullOrWhiteSpace(lastError)
                ? ""
                : $" آخر خطأ: {Truncate(lastError, 500)}";

            throw new CircuitBreakerOpenException(
                "تم إيقاف المزامنة مؤقتاً: تكرر فشل العمليات على Active Directory (Circuit Breaker مفتوح). " +
                "تحقّق من توفر خادم الدومين ومن صلاحيات حساب الخدمة (إنشاء الحسابات وإعادة تعيين كلمة المرور)، " +
                "وسيُعاد المحاولة تلقائياً في التشغيل المجدول القادم بعد فترة التهدئة." + cause);
        }

        private void ReportProgress(string operation, SyncRun run, int processed = 0, int total = 0, string? currentId = null, bool force = false)
        {
            var info = new SyncProgressInfo
            {
                TotalRecords = total,
                ProcessedRecords = processed,
                Created = run.TotalCreated,
                Updated = run.TotalUpdated,
                Failed = run.TotalFailed,
                Skipped = run.TotalSkipped,
                CurrentOperation = operation,
                CurrentIdentityId = currentId,
                Elapsed = DateTime.UtcNow - run.StartTime,
                CurrentPhase = _currentPhase,
                TotalPhases = _totalPhases,
                PhaseDescription = _phaseDescription,
                PhaseProgress = _phaseProgress
            };

            // Notify local subscribers (in-process — cheap, always fire)
            OnProgress?.Invoke(info);

            // ✅ Broadcast via SignalR to Live Monitor page — throttled to avoid flooding the thread
            // pool with fire-and-forget tasks during large runs. Forced (terminal) updates always send.
            if (_progressNotifier != null)
            {
                var now = DateTime.UtcNow;
                if (force || now - _lastProgressBroadcastUtc >= ProgressBroadcastInterval)
                {
                    _lastProgressBroadcastUtc = now;
                    _ = Task.Run(async () =>
                    {
                        try { await _progressNotifier.NotifyProgressAsync(info).ConfigureAwait(false); }
                        catch { /* SignalR failure should not break the sync */ }
                    });
                }
            }
        }

        /// <summary>
        /// Send SMS with username and password for a newly created account.
        /// Phone/name come from the tenant's configured source columns
        /// (SourcePhoneColumn / SourceDisplayNameColumn) with legacy fallbacks.
        /// </summary>
        private async Task SendCredentialsSmsAsync(AppDbContext db, Core.Models.Identity.SourceRecord identity, string username, string password, int runId)
        {
            // Find the run's tenant with SMS enabled (falls back to any active tenant for legacy runs)
            var tenant = _runTenantId > 0
                ? await db.TenantSettings.FirstOrDefaultAsync(t => t.Id == _runTenantId && t.EnableSmsNotification)
                : await db.TenantSettings.FirstOrDefaultAsync(t => t.IsActive && t.EnableSmsNotification);

            // Dynamic schema: which columns hold the phone/display name (legacy defaults)
            var phoneColumn = string.IsNullOrWhiteSpace(tenant?.SourcePhoneColumn) ? "MOBILE_PHONE" : tenant!.SourcePhoneColumn!;
            var phone = identity.GetString(phoneColumn);
            var displayName = identity.GetString(tenant?.SourceDisplayNameColumn)
                ?? $"{identity.GetString("FIRST_NAME")} {identity.GetString("LAST_NAME")}".Trim();

            // Every attempt is recorded so operators can review who got the SMS and retry failures.
            var log = new SmsSendLog
            {
                Source = "Sync",
                IdentityId = identity.Key,
                Account = username,
                DisplayName = displayName,
                PhoneNumber = phone,
                SyncRunId = runId,
                CreatedAt = DateTime.UtcNow,
                LastAttemptAt = DateTime.UtcNow
            };
            db.SmsSendLogs.Add(log);

            try
            {
                if (tenant == null)
                {
                    log.Status = "Skipped";
                    log.GatewayResponse = "SMS notifications are disabled";
                    return;
                }

                if (string.IsNullOrWhiteSpace(phone))
                {
                    log.Status = "Skipped";
                    log.GatewayResponse = "No mobile phone on file";
                    _logger.LogWarning("No mobile phone for identity {Key}, skipping SMS", identity.Key);
                    return;
                }

                string apiUrl, apiUsername, apiPassword, senderName, providerName;
                IdentitySyncPro.Core.Models.Settings.SmsProvider? resolvedProvider = null;

                // Resolve from SmsProvider if configured
                if (tenant.SmsProviderId.HasValue)
                {
                    var provider = await db.SmsProviders.FindAsync(tenant.SmsProviderId.Value);
                    if (provider == null || !provider.IsActive)
                    {
                        log.Status = "Skipped";
                        log.GatewayResponse = "Selected SMS provider is missing or inactive";
                        return;
                    }
                    resolvedProvider = provider;
                    apiUrl = provider.ApiUrl;
                    apiUsername = provider.ApiUsername;
                    apiPassword = provider.ApiPassword;
                    senderName = provider.SenderName;
                    providerName = provider.Name;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(tenant.SmsApiUrl))
                    {
                        log.Status = "Skipped";
                        log.GatewayResponse = "No SMS provider configured";
                        return;
                    }
                    apiUrl = tenant.SmsApiUrl;
                    apiUsername = tenant.SmsApiUsername;
                    apiPassword = tenant.SmsApiPassword;
                    senderName = tenant.SmsSenderName;
                    providerName = "(inline)";
                }

                log.ProviderName = providerName;

                var smsRequest = new SmsRequest
                {
                    ApiUrl = apiUrl,
                    ApiUsername = apiUsername,
                    ApiPassword = apiPassword,
                    SenderName = senderName,
                    PhoneNumber = phone,
                    Username = username,
                    Password = password,
                    DisplayName = log.DisplayName,
                    IdentityId = identity.Key.ToString(),
                    MessageTemplate = tenant.SmsMessageTemplate
                };
                // Carry the provider's generic gateway config (method/format/template/headers).
                if (resolvedProvider != null) smsRequest.WithProvider(resolvedProvider);

                // Capture the exact rendered text (contains the password) so a failure can be retried verbatim.
                var renderedMessage = SmsService.RenderMessage(smsRequest);

                var smsResult = await _smsService.SendCredentialsAsync(smsRequest);

                if (smsResult.Success)
                {
                    log.Status = "Success";
                    log.GatewayResponse = Truncate(smsResult.Response, 2000);
                    log.SentMessage = null; // delivered — drop the stored message
                }
                else
                {
                    log.Status = "Failed";
                    log.GatewayResponse = Truncate(smsResult.Error, 2000);
                    log.SentMessage = renderedMessage; // keep (encrypted) so the send can be retried
                    _logger.LogWarning("SMS failed for identity {IdentityId}: {Error}", identity.Key, smsResult.Error);
                }
            }
            catch (Exception ex)
            {
                // SMS failure should not break the sync
                log.Status = "Failed";
                log.GatewayResponse = Truncate(ex.Message, 2000);
                _logger.LogWarning(ex, "SMS notification failed for identity {IdentityId}", identity.Key);
            }
        }

        /// <summary>
        /// Upsert a MetaverseEntry during sync — reuses already-fetched Oracle data.
        /// Sets NeedsRuleEval = true when hash or StatusCode changes so that
        /// BulkApplyRulesAsync will process it after sync completes.
        /// </summary>
        private void UpsertMetaverseEntry(
            AppDbContext db,
            Core.Models.Identity.SourceRecord identity,
            string currentHash,
            Dictionary<string, MetaverseEntry> batchEntries)
        {
            var externalId = identity.Key.ToString();
            var now = DateTime.UtcNow;

            if (!batchEntries.TryGetValue(externalId, out var entry))
            {
                // New entry — create
                entry = new MetaverseEntry
                {
                    TenantId = _runTenantId,
                    ExternalId = externalId,
                    IdentityType = "User",
                    LifecycleState = "Pending",
                    NeedsRuleEval = true,
                    FirstSeenDate = now,
                    CreatedDate = now
                };
                db.MetaverseEntries.Add(entry);
                batchEntries[externalId] = entry;
            }
            else
            {
                // Existing entry — mark for rule re-evaluation if status changed
                if (entry.SourceStatusCode != identity.StatusCode || entry.CurrentHash != currentHash)
                {
                    entry.NeedsRuleEval = true;
                }
            }

            // Update attributes from Oracle data
            var attrs = identity.ToDictionary();
            entry.SetAttributes(attrs);
            entry.SourceStatusCode = identity.StatusCode;
            entry.SourceStatusDesc = identity.StatusDesc;

            // Update hash for change detection
            if (entry.CurrentHash != currentHash)
            {
                entry.PreviousHash = entry.CurrentHash;
                entry.CurrentHash = currentHash;
            }

            entry.LastImportDate = now;
            entry.ModifiedDate = now;
            entry.SourceSystemsJson = JsonSerializer.Serialize(new[] { "Oracle" });
        }
    }
}
