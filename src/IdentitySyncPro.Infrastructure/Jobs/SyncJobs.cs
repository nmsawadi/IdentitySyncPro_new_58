using Hangfire;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Audit;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Jobs
{
    /// <summary>
    /// Hangfire background job for full identity sync.
    /// Replaces the PowerShell scheduled task approach.
    /// </summary>
    public class FullSyncJob
    {
        private readonly ISyncEngine _syncEngine;
        private readonly IAuditService _auditService;
        private readonly ILogger<FullSyncJob> _logger;

        public FullSyncJob(ISyncEngine syncEngine, IAuditService auditService, ILogger<FullSyncJob> logger)
        {
            _syncEngine = syncEngine;
            _auditService = auditService;
            _logger = logger;
        }

        // ⚠️ No automatic retry: a full sync of 120K+ identities is long-running and NOT resumable.
        // Blindly restarting it from scratch on failure wastes hours and risks overlapping runs.
        // Recovery is handled by the daily schedule + the 30-minute delta sync instead.
        [AutomaticRetry(Attempts = 0)]
        [Queue("sync")]
        public async Task ExecuteAsync(string triggeredBy, CancellationToken ct)
        {
            _logger.LogInformation("Full Sync Job started");
            await _auditService.LogAsync("Full Sync Started", "Sync", Core.Enums.AuditSeverity.Info, performedBy: ActorNames.OrSchedule(triggeredBy));

            try
            {
                // tenantId: null → all active tenants run sequentially
                var result = await _syncEngine.RunFullSyncAsync(1000, false, null, ct, ActorNames.OrSchedule(triggeredBy));

                await _auditService.LogAsync(
                    $"Full Sync Completed: Created={result.TotalCreated}, Updated={result.TotalUpdated}, Failed={result.TotalFailed}",
                    "Sync",
                    result.TotalFailed > 0 ? Core.Enums.AuditSeverity.Warning : Core.Enums.AuditSeverity.Info,
                    performedBy: ActorNames.OrSchedule(triggeredBy), details: $"Duration: {result.Duration}, Status: {result.Status}");
            }
            catch (InvalidOperationException ex)
            {
                // Another sync (this or another instance) already holds the lock — a skip, not a failure.
                _logger.LogWarning("Full Sync skipped: {Reason}", ex.Message);
                await _auditService.LogAsync($"Full Sync skipped: {ex.Message}", "Sync", Core.Enums.AuditSeverity.Info, performedBy: ActorNames.OrSchedule(triggeredBy));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Full Sync Job failed");
                await _auditService.LogAsync($"Full Sync Failed: {ex.Message}", "Sync", Core.Enums.AuditSeverity.Error, performedBy: ActorNames.OrSchedule(triggeredBy));
                throw;
            }
        }

        /// <summary>
        /// Full sync for one specific tenant (multi-source support).
        /// </summary>
        [AutomaticRetry(Attempts = 0)]
        [Queue("sync")]
        public async Task ExecuteTenantAsync(int tenantId, string triggeredBy, CancellationToken ct)
        {
            _logger.LogInformation("Full Sync Job started for tenant {TenantId}", tenantId);
            await _auditService.LogAsync($"Full Sync Started (Tenant {tenantId})", "Sync", Core.Enums.AuditSeverity.Info, performedBy: ActorNames.OrSchedule(triggeredBy));

            try
            {
                var result = await _syncEngine.RunFullSyncAsync(1000, false, tenantId, ct, ActorNames.OrSchedule(triggeredBy));

                await _auditService.LogAsync(
                    $"Full Sync Completed (Tenant {tenantId}): Created={result.TotalCreated}, Updated={result.TotalUpdated}, Failed={result.TotalFailed}",
                    "Sync",
                    result.TotalFailed > 0 ? Core.Enums.AuditSeverity.Warning : Core.Enums.AuditSeverity.Info,
                    performedBy: ActorNames.OrSchedule(triggeredBy), details: $"Duration: {result.Duration}, Status: {result.Status}");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning("Full Sync skipped: {Reason}", ex.Message);
                await _auditService.LogAsync($"Full Sync skipped: {ex.Message}", "Sync", Core.Enums.AuditSeverity.Info, performedBy: ActorNames.OrSchedule(triggeredBy));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Full Sync Job failed for tenant {TenantId}", tenantId);
                await _auditService.LogAsync($"Full Sync Failed: {ex.Message}", "Sync", Core.Enums.AuditSeverity.Error, performedBy: ActorNames.OrSchedule(triggeredBy));
                throw;
            }
        }

        /// <summary>
        /// Dry run variant — runs full sync without making AD changes. (Fix #2)
        /// </summary>
        [AutomaticRetry(Attempts = 0)]
        [Queue("sync")]
        public async Task ExecuteDryRunAsync(string triggeredBy, CancellationToken ct)
        {
            _logger.LogInformation("Dry Run Job started");
            await _auditService.LogAsync("Dry Run Started", "Sync", Core.Enums.AuditSeverity.Info, performedBy: ActorNames.OrSchedule(triggeredBy));

            try
            {
                var result = await _syncEngine.RunFullSyncAsync(1000, true, null, ct, ActorNames.OrSchedule(triggeredBy));

                await _auditService.LogAsync(
                    $"Dry Run Completed: WouldCreate={result.TotalCreated}, WouldUpdate={result.TotalUpdated}, WouldFail={result.TotalFailed}",
                    "Sync", Core.Enums.AuditSeverity.Info, performedBy: ActorNames.OrSchedule(triggeredBy),
                    details: $"Duration: {result.Duration}, Status: {result.Status}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dry Run Job failed");
                await _auditService.LogAsync($"Dry Run Failed: {ex.Message}", "Sync", Core.Enums.AuditSeverity.Error, performedBy: ActorNames.OrSchedule(triggeredBy));
                throw;
            }
        }
    }

    /// <summary>
    /// Hangfire background job for delta (incremental) sync.
    /// </summary>
    public class DeltaSyncJob
    {
        private readonly ISyncEngine _syncEngine;
        private readonly IAuditService _auditService;
        private readonly ILogger<DeltaSyncJob> _logger;

        public DeltaSyncJob(ISyncEngine syncEngine, IAuditService auditService, ILogger<DeltaSyncJob> logger)
        {
            _syncEngine = syncEngine;
            _auditService = auditService;
            _logger = logger;
        }

        [AutomaticRetry(Attempts = 3)]
        [Queue("sync")]
        public async Task ExecuteAsync(string triggeredBy, CancellationToken ct)
        {
            _logger.LogInformation("Delta Sync Job started");

            try
            {
                // tenantId: null → all active tenants run sequentially
                var result = await _syncEngine.RunDeltaSyncAsync(1000, false, null, ct, ActorNames.OrSchedule(triggeredBy));

                await _auditService.LogAsync(
                    $"Delta Sync Completed: Updated={result.TotalUpdated}, NoChange={result.TotalNoChange}",
                    "Sync", Core.Enums.AuditSeverity.Info, performedBy: ActorNames.OrSchedule(triggeredBy));
            }
            catch (InvalidOperationException ex)
            {
                // A full/delta sync is already running — skip this run rather than fail + retry.
                _logger.LogWarning("Delta Sync skipped: {Reason}", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delta Sync Job failed");
                await _auditService.LogAsync($"Delta Sync Failed: {ex.Message}", "Sync", Core.Enums.AuditSeverity.Error, performedBy: ActorNames.OrSchedule(triggeredBy));
                throw;
            }
        }

        /// <summary>
        /// Delta sync for one specific tenant (multi-source support).
        /// </summary>
        [AutomaticRetry(Attempts = 0)]
        [Queue("sync")]
        public async Task ExecuteTenantAsync(int tenantId, string triggeredBy, CancellationToken ct)
        {
            _logger.LogInformation("Delta Sync Job started for tenant {TenantId}", tenantId);

            try
            {
                var result = await _syncEngine.RunDeltaSyncAsync(1000, false, tenantId, ct, ActorNames.OrSchedule(triggeredBy));

                await _auditService.LogAsync(
                    $"Delta Sync Completed (Tenant {tenantId}): Updated={result.TotalUpdated}, NoChange={result.TotalNoChange}",
                    "Sync", Core.Enums.AuditSeverity.Info, performedBy: ActorNames.OrSchedule(triggeredBy));
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning("Delta Sync skipped: {Reason}", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delta Sync Job failed for tenant {TenantId}", tenantId);
                await _auditService.LogAsync($"Delta Sync Failed: {ex.Message}", "Sync", Core.Enums.AuditSeverity.Error, performedBy: ActorNames.OrSchedule(triggeredBy));
                throw;
            }
        }
    }

    /// <summary>
    /// Health check job that periodically tests connector connectivity.
    /// </summary>
    public class HealthCheckJob
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ITenantConnectorFactory _connectorFactory;
        private readonly IAuditService _auditService;
        private readonly ILogger<HealthCheckJob> _logger;

        public HealthCheckJob(
            IServiceScopeFactory scopeFactory,
            ITenantConnectorFactory connectorFactory,
            IAuditService auditService,
            ILogger<HealthCheckJob> logger)
        {
            _scopeFactory = scopeFactory;
            _connectorFactory = connectorFactory;
            _auditService = auditService;
            _logger = logger;
        }

        /// <summary>
        /// Tests each ACTIVE tenant's own source and AD connections.
        ///
        /// It used to test the connectors registered in DI, which — since source/AD settings moved
        /// out of appsettings.json — carry empty configuration. That produced a permanent, useless
        /// "ORA-12154: Cannot find alias  in ." every 10 minutes, and worse, an
        /// "AD connection test successful to " against an empty server: a green light that meant
        /// nothing. Neither result said anything about a tenant that actually syncs.
        /// </summary>
        [AutomaticRetry(Attempts = 1)]
        public async Task ExecuteAsync()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var tenants = await db.TenantSettings
                .AsNoTracking()
                .Where(t => t.IsActive)
                .ToListAsync();

            if (tenants.Count == 0)
            {
                _logger.LogWarning("Health check skipped — no active tenant configured");
                return;
            }

            var allHealthy = true;

            foreach (var tenant in tenants)
            {
                // A tenant with missing connection settings throws here by design
                // (TenantConnectorFactory has no appsettings fallback) — report it as unhealthy
                // for that tenant rather than aborting the checks for the others.
                bool sourceOk, adOk;
                try
                {
                    sourceOk = await _connectorFactory.CreateSourceConnector(tenant).TestConnectionAsync();
                }
                catch (Exception ex)
                {
                    sourceOk = false;
                    _logger.LogError(ex, "Source connector could not be built for tenant '{Tenant}'", tenant.TenantName);
                }

                try
                {
                    adOk = await _connectorFactory.CreateTargetConnector(tenant).TestConnectionAsync();
                }
                catch (Exception ex)
                {
                    adOk = false;
                    _logger.LogError(ex, "AD connector could not be built for tenant '{Tenant}'", tenant.TenantName);
                }

                if (!sourceOk)
                {
                    allHealthy = false;
                    _logger.LogError("Source health check FAILED for tenant '{Tenant}'", tenant.TenantName);
                    await _auditService.LogAsync($"Source connection failed (tenant: {tenant.TenantName})",
                        "HealthCheck", Core.Enums.AuditSeverity.Critical);
                }

                if (!adOk)
                {
                    allHealthy = false;
                    _logger.LogError("Active Directory health check FAILED for tenant '{Tenant}'", tenant.TenantName);
                    await _auditService.LogAsync($"AD connection failed (tenant: {tenant.TenantName})",
                        "HealthCheck", Core.Enums.AuditSeverity.Critical);
                }

                if (sourceOk && adOk)
                    _logger.LogInformation("Connectors healthy for tenant '{Tenant}'", tenant.TenantName);
            }

            if (allHealthy)
                _logger.LogInformation("All connectors healthy across {Count} active tenant(s)", tenants.Count);
        }
    }
}
