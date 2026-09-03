using IdentitySyncPro.Core.Models.Audit;
using Hangfire;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Jobs
{
    /// <summary>
    /// Hangfire background job for executing service operations.
    /// Routes to the correct executor based on ServiceType (Sync vs Offboarding).
    /// Runs on a dedicated "services" queue, separate from IAM sync jobs.
    /// </summary>
    public class SvcSyncJob
    {
        private readonly Services.SvcSyncExecutor _syncExecutor;
        private readonly Services.SvcOffboardingExecutor _offboardingExecutor;
        private readonly Services.SvcEmptyAttrDisableExecutor _emptyAttrExecutor;
        private readonly Services.SvcInactiveDisableExecutor _inactiveExecutor;
        private readonly Services.SvcAdAuditExecutor _auditExecutor;
        private readonly Services.SvcExpiryExecutor _expiryExecutor;
        private readonly Services.SvcOrphanExecutor _orphanExecutor;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<SvcSyncJob> _logger;

        public SvcSyncJob(
            Services.SvcSyncExecutor syncExecutor,
            Services.SvcOffboardingExecutor offboardingExecutor,
            Services.SvcEmptyAttrDisableExecutor emptyAttrExecutor,
            Services.SvcInactiveDisableExecutor inactiveExecutor,
            Services.SvcAdAuditExecutor auditExecutor,
            Services.SvcExpiryExecutor expiryExecutor,
            Services.SvcOrphanExecutor orphanExecutor,
            IServiceScopeFactory scopeFactory,
            ILogger<SvcSyncJob> logger)
        {
            _syncExecutor = syncExecutor;
            _offboardingExecutor = offboardingExecutor;
            _emptyAttrExecutor = emptyAttrExecutor;
            _inactiveExecutor = inactiveExecutor;
            _auditExecutor = auditExecutor;
            _expiryExecutor = expiryExecutor;
            _orphanExecutor = orphanExecutor;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        /// <summary>
        /// Execute a scheduled service (recurring).
        /// </summary>
        [AutomaticRetry(Attempts = 1)]
        [Queue("services")]
        public async Task ExecuteAsync(int serviceId, CancellationToken ct)
        {
            _logger.LogInformation("SvcSyncJob: Starting scheduled execution for service ID {ServiceId}", serviceId);

            try
            {
                var result = await RouteAndExecuteAsync(serviceId, ActorNames.Schedule, ct);

                _logger.LogInformation(
                    "SvcSyncJob: Service {ServiceId} completed — Status={Status}, Updated={Updated}, Failed={Failed}",
                    serviceId, result.Status, result.UpdatedRecords, result.FailedRecords);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SvcSyncJob: Service {ServiceId} failed", serviceId);
                throw;
            }
        }

        /// <summary>
        /// Execute a manual (on-demand) service. Enqueued immediately, not recurring.
        /// </summary>
        [AutomaticRetry(Attempts = 0)]
        [Queue("services")]
        public async Task ExecuteManualAsync(int serviceId, string triggeredBy, CancellationToken ct)
        {
            _logger.LogInformation("SvcSyncJob: Starting manual execution for service ID {ServiceId}", serviceId);

            try
            {
                var result = await RouteAndExecuteAsync(serviceId, ActorNames.OrSchedule(triggeredBy), ct);

                _logger.LogInformation(
                    "SvcSyncJob: Manual execution for service {ServiceId} completed — Status={Status}, Updated={Updated}, Failed={Failed}",
                    serviceId, result.Status, result.UpdatedRecords, result.FailedRecords);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SvcSyncJob: Manual execution for service {ServiceId} failed", serviceId);
                throw;
            }
        }

        /// <summary>
        /// Routes execution to the correct executor based on ServiceType.
        /// Registers a linked CancellationTokenSource so the service can be cancelled from the UI.
        /// </summary>
        private async Task<Core.Models.Services.SvcRunLog> RouteAndExecuteAsync(int serviceId, string triggeredBy, CancellationToken ct)
        {
            // Register a linked CancellationTokenSource for manual cancel support
            using var linkedCts = Services.SvcCancellationRegistry.Register(serviceId, ct);
            var linkedToken = linkedCts.Token;

            try
            {
                // Determine service type
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();
                var serviceType = await db.SvcServices
                    .Where(s => s.Id == serviceId)
                    .Select(s => s.ServiceType)
                    .FirstOrDefaultAsync(linkedToken);

                return serviceType switch
                {
                    "Offboarding" => await _offboardingExecutor.ExecuteAsync(serviceId, triggeredBy, linkedToken),
                    "EmptyAttrDisable" => await _emptyAttrExecutor.ExecuteAsync(serviceId, triggeredBy, linkedToken),
                    "InactiveDisable" => await _inactiveExecutor.ExecuteAsync(serviceId, triggeredBy, linkedToken),
                    "AdAudit" => await _auditExecutor.ExecuteAsync(serviceId, triggeredBy, linkedToken),
                    "ExpiryDisable" => await _expiryExecutor.ExecuteAsync(serviceId, triggeredBy, linkedToken),
                    "OrphanCleanup" => await _orphanExecutor.ExecuteAsync(serviceId, triggeredBy, linkedToken),
                    _ => await _syncExecutor.ExecuteAsync(serviceId, triggeredBy, linkedToken)
                };
            }
            finally
            {
                // Always remove the registration when done
                Services.SvcCancellationRegistry.Remove(serviceId);
            }
        }
    }
}
