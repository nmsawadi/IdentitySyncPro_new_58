using Hangfire;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Jobs
{
    /// <summary>
    /// Automated data retention job that cleans up old records to prevent
    /// database growth from impacting performance over years of operation.
    /// 
    /// ⛔ SAFETY: This job NEVER touches AD accounts or identity data.
    ///    It only removes old operational logs and completed sync records.
    /// 
    /// Retention periods are configurable from Settings UI (AppSettings table).
    /// </summary>
    public class DataRetentionJob
    {
        private readonly AppDbContext _db;
        private readonly IAuditService _auditService;
        private readonly ILogger<DataRetentionJob> _logger;

        // Default retention periods (used if not configured in DB)
        private const int DEFAULT_SYNC_OPS_DAYS = 90;
        private const int DEFAULT_SYNC_RUNS_DAYS = 180;
        private const int DEFAULT_AUDIT_DAYS = 365;
        private const int DEFAULT_DLQ_DAYS = 30;
        private const int DEFAULT_QUARANTINE_DAYS = 60;

        public DataRetentionJob(AppDbContext db, IAuditService auditService, ILogger<DataRetentionJob> logger)
        {
            _db = db;
            _auditService = auditService;
            _logger = logger;
        }

        [AutomaticRetry(Attempts = 1)]
        [Queue("maintenance")]
        public async Task ExecuteAsync(CancellationToken ct = default)
        {
            _logger.LogInformation("Data Retention Job started");

            // Read configurable periods from AppSettings (or use defaults)
            var syncOpsDays = await GetRetentionDays("Retention.SyncOperationsDays", DEFAULT_SYNC_OPS_DAYS);
            var syncRunsDays = await GetRetentionDays("Retention.SyncRunsDays", DEFAULT_SYNC_RUNS_DAYS);
            var auditDays = await GetRetentionDays("Retention.AuditEntriesDays", DEFAULT_AUDIT_DAYS);
            var dlqDays = await GetRetentionDays("Retention.DeadLetterDays", DEFAULT_DLQ_DAYS);
            var quarDays = await GetRetentionDays("Retention.QuarantineDays", DEFAULT_QUARANTINE_DAYS);

            _logger.LogInformation(
                "Retention periods: SyncOps={SyncOps}d, Runs={Runs}d, Audit={Audit}d, DLQ={DLQ}d, Quarantine={Quar}d",
                syncOpsDays, syncRunsDays, auditDays, dlqDays, quarDays);

            var totalDeleted = 0;

            try
            {
                // 1. Clean old SyncOperations (biggest table)
                var syncOpsDeleted = await _db.SyncOperations
                    .Where(o => o.Timestamp < DateTime.UtcNow.AddDays(-syncOpsDays))
                    .ExecuteDeleteAsync(ct);
                totalDeleted += syncOpsDeleted;
                if (syncOpsDeleted > 0)
                    _logger.LogInformation("Cleaned {Count} SyncOperations older than {Days} days", syncOpsDeleted, syncOpsDays);

                // 2. Clean old SyncRuns (only completed ones)
                var syncRunsDeleted = await _db.SyncRuns
                    .Where(r => r.EndTime != null && r.StartTime < DateTime.UtcNow.AddDays(-syncRunsDays))
                    .ExecuteDeleteAsync(ct);
                totalDeleted += syncRunsDeleted;
                if (syncRunsDeleted > 0)
                    _logger.LogInformation("Cleaned {Count} SyncRuns older than {Days} days", syncRunsDeleted, syncRunsDays);

                // 3. Clean old AuditEntries
                var auditDeleted = await _db.AuditEntries
                    .Where(a => a.Timestamp < DateTime.UtcNow.AddDays(-auditDays))
                    .ExecuteDeleteAsync(ct);
                totalDeleted += auditDeleted;
                if (auditDeleted > 0)
                    _logger.LogInformation("Cleaned {Count} AuditEntries older than {Days} days", auditDeleted, auditDays);

                // 4. Clean replayed Dead Letter entries
                var dlqDeleted = await _db.DeadLetterEntries
                    .Where(d => d.IsReplayed && d.ReplayedDate < DateTime.UtcNow.AddDays(-dlqDays))
                    .ExecuteDeleteAsync(ct);
                totalDeleted += dlqDeleted;
                if (dlqDeleted > 0)
                    _logger.LogInformation("Cleaned {Count} replayed DeadLetterEntries older than {Days} days", dlqDeleted, dlqDays);

                // 5. Clean resolved Quarantine entries
                var quarDeleted = await _db.QuarantinedIdentities
                    .Where(q => q.IsResolved && q.ReviewedDate < DateTime.UtcNow.AddDays(-quarDays))
                    .ExecuteDeleteAsync(ct);
                totalDeleted += quarDeleted;
                if (quarDeleted > 0)
                    _logger.LogInformation("Cleaned {Count} resolved QuarantinedIdentities older than {Days} days", quarDeleted, quarDays);

                // Log summary
                await _auditService.LogAsync(
                    $"Data Retention completed: {totalDeleted} records cleaned",
                    "Maintenance",
                    Core.Enums.AuditSeverity.Info,
                    details: $"SyncOps={syncOpsDeleted}, SyncRuns={syncRunsDeleted}, Audit={auditDeleted}, DLQ={dlqDeleted}, Quarantine={quarDeleted}");

                _logger.LogInformation("Data Retention Job completed: {TotalDeleted} total records cleaned", totalDeleted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Data Retention Job failed");
                await _auditService.LogAsync($"Data Retention Failed: {ex.Message}", "Maintenance", Core.Enums.AuditSeverity.Error);
                throw;
            }
        }

        /// <summary>Read a retention period from AppSettings, or return default.</summary>
        private async Task<int> GetRetentionDays(string key, int defaultValue)
        {
            var setting = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
            return setting != null && int.TryParse(setting.Value, out var days) && days > 0 ? days : defaultValue;
        }
    }
}
