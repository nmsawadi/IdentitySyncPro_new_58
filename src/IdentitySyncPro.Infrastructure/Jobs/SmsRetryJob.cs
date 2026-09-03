using Hangfire;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Jobs
{
    /// <summary>Background job that re-sends failed credentials SMS entries.</summary>
    public class SmsRetryJob
    {
        private readonly SmsRetryService _retryService;
        private readonly IAuditService _auditService;
        private readonly ILogger<SmsRetryJob> _logger;

        public SmsRetryJob(SmsRetryService retryService, IAuditService auditService, ILogger<SmsRetryJob> logger)
        {
            _retryService = retryService;
            _auditService = auditService;
            _logger = logger;
        }

        [AutomaticRetry(Attempts = 0)]
        [Queue("services")]
        public async Task ExecuteAsync(int[]? ids, CancellationToken ct)
        {
            _logger.LogInformation("SMS retry job started");
            try
            {
                var (attempted, succeeded) = await _retryService.RetryFailedAsync(ids, ct);
                await _auditService.LogAsync(
                    $"SMS retry: {succeeded}/{attempted} delivered",
                    "SMS",
                    succeeded < attempted ? Core.Enums.AuditSeverity.Warning : Core.Enums.AuditSeverity.Info);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SMS retry job failed");
                await _auditService.LogAsync($"SMS retry failed: {ex.Message}", "SMS", Core.Enums.AuditSeverity.Error);
                throw;
            }
        }
    }
}
