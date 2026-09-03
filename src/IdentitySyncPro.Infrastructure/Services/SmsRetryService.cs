using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Re-sends failed credentials SMS entries (from <c>SmsSendLogs</c>) using the currently active
    /// provider and the stored (encrypted) credentials. Updates each log with the new outcome and
    /// clears the stored password once delivery succeeds.
    /// </summary>
    public class SmsRetryService
    {
        private readonly AppDbContext _db;
        private readonly ISmsService _smsService;
        private readonly ILogger<SmsRetryService> _logger;

        public SmsRetryService(AppDbContext db, ISmsService smsService, ILogger<SmsRetryService> logger)
        {
            _db = db;
            _smsService = smsService;
            _logger = logger;
        }

        /// <summary>
        /// Retries failed, retryable sends. Pass specific log ids, or null to retry all failures.
        /// Returns (attempted, succeeded).
        /// </summary>
        public async Task<(int Attempted, int Succeeded)> RetryFailedAsync(int[]? ids = null, CancellationToken ct = default)
        {
            var query = _db.SmsSendLogs.Where(l => l.Status == "Failed" && l.SentMessage != null);
            if (ids != null && ids.Length > 0)
                query = query.Where(l => ids.Contains(l.Id));

            var failed = await query.OrderBy(l => l.Id).ToListAsync(ct);
            if (failed.Count == 0) return (0, 0);

            // Resolve the active provider once for the whole batch.
            var provider = await ResolveProviderAsync(ct);
            if (provider == null)
            {
                _logger.LogWarning("SMS retry aborted — no active SMS provider configured");
                return (0, 0);
            }

            int succeeded = 0;
            foreach (var log in failed)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(log.PhoneNumber) || string.IsNullOrEmpty(log.SentMessage))
                    continue;

                // Resend the exact original message (already rendered) via the active provider.
                var result = await _smsService.SendCredentialsAsync(new SmsRequest
                {
                    PhoneNumber = log.PhoneNumber,
                    MessageTemplate = log.SentMessage // decrypted on read; contains no unresolved tokens
                }.WithProvider(provider.Value.Provider));

                log.RetryCount++;
                log.LastAttemptAt = DateTime.UtcNow;
                log.ProviderName = provider.Value.ProviderName;

                if (result.Success)
                {
                    log.Status = "Success";
                    log.GatewayResponse = Truncate(result.Response, 2000);
                    log.SentMessage = null; // delivered — drop the stored message
                    succeeded++;
                }
                else
                {
                    log.GatewayResponse = Truncate(result.Error, 2000);
                }

                await _db.SaveChangesAsync(ct);
            }

            _logger.LogInformation("SMS retry complete: {Succeeded}/{Attempted} delivered", succeeded, failed.Count);
            return (failed.Count, succeeded);
        }

        private async Task<(IdentitySyncPro.Core.Models.Settings.SmsProvider Provider, string ProviderName)?> ResolveProviderAsync(CancellationToken ct)
        {
            var tenant = await _db.TenantSettings.FirstOrDefaultAsync(t => t.IsActive, ct);
            if (tenant == null) return null;

            if (tenant.SmsProviderId.HasValue)
            {
                var p = await _db.SmsProviders.FindAsync(new object?[] { tenant.SmsProviderId.Value }, ct);
                if (p == null || !p.IsActive) return null;
                return (p, p.Name);
            }

            if (string.IsNullOrWhiteSpace(tenant.SmsApiUrl)) return null;
            // Inline tenant SMS config → synthesize a provider (empty template = legacy JSON payload).
            return (new IdentitySyncPro.Core.Models.Settings.SmsProvider
            {
                ApiUrl = tenant.SmsApiUrl,
                ApiUsername = tenant.SmsApiUsername,
                ApiPassword = tenant.SmsApiPassword,
                SenderName = tenant.SmsSenderName
            }, "(inline)");
        }

        private static string? Truncate(string? value, int maxLength)
            => value != null && value.Length > maxLength ? value[..maxLength] : value;
    }
}
