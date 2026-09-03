using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Audit;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Jobs
{
    /// <summary>
    /// Finds who is currently holding two duties that must not meet.
    ///
    /// The approval check catches conflicts as they are about to be created. This catches the ones
    /// that arrived some other way — a group added by hand, a policy written after the fact, an
    /// account that changed roles — which in practice is most of them.
    /// </summary>
    public class SodScanJob
    {
        private readonly AppDbContext _app;
        private readonly SodEvaluator _evaluator;
        private readonly IAuditService _audit;
        private readonly ILogger<SodScanJob> _logger;

        private const string AuditCategory = "SeparationOfDuties";

        public SodScanJob(AppDbContext app, SodEvaluator evaluator, IAuditService audit, ILogger<SodScanJob> logger)
        {
            _app = app;
            _evaluator = evaluator;
            _audit = audit;
            _logger = logger;
        }

        public async Task ExecuteAsync(CancellationToken ct = default)
        {
            var tenants = await _app.TenantSettings.Where(t => t.IsActive).ToListAsync(ct);
            var now = DateTime.UtcNow;

            foreach (var tenant in tenants)
            {
                ct.ThrowIfCancellationRequested();

                var policies = await _evaluator.EnabledPoliciesAsync(tenant.Id, ct);
                if (policies.Count == 0) continue;

                try
                {
                    var result = await _evaluator.ScanTenantAsync(tenant, now, ct);

                    if (!result.Trustworthy)
                    {
                        // Raised as an error, not logged and forgotten. An operator who never hears
                        // this reads an empty violations screen as a clean institution.
                        await _audit.LogAsync("SodScanUnreliable", AuditCategory, AuditSeverity.Error,
                            details: $"'{tenant.TenantName}': {result.Problem}", performedBy: ActorNames.System);
                        continue;
                    }

                    if (result.Opened > 0)
                        await _audit.LogAsync("SodViolationsFound", AuditCategory, AuditSeverity.Warning,
                            details: $"'{tenant.TenantName}': {result.Opened} new conflict(s), " +
                                     $"{result.Continuing} continuing, {result.Cleared} cleared",
                            performedBy: ActorNames.System);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // One tenant's directory being down must not stop the others being scanned.
                    _logger.LogError(ex, "SoD scan failed for tenant '{Tenant}'", tenant.TenantName);

                    await _audit.LogAsync("SodScanFailed", AuditCategory, AuditSeverity.Error,
                        details: $"'{tenant.TenantName}': {ex.Message}", performedBy: ActorNames.System);
                }
            }
        }
    }
}
