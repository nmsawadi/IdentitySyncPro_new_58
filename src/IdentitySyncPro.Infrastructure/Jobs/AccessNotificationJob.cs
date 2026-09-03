using Hangfire;
using IdentitySyncPro.Core.Models.Governance;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Jobs
{
    /// <summary>
    /// Sends the access-request notifications off the request thread.
    ///
    /// They used to be awaited inline, and running it against a lab with no reachable mail server
    /// showed why that is wrong: submitting a request created the row, resolved the account in
    /// Active Directory, and then <b>hung the browser indefinitely on the SMTP connect</b>. The
    /// governance work had already succeeded; the person was left staring at a dead page because a
    /// mail server two systems away was not answering.
    ///
    /// A notification is a consequence of the decision, not part of making it. So it is queued: the
    /// screen returns immediately, and Hangfire owns the retries — which also means a mail server
    /// that comes back an hour later still delivers, instead of the notice being lost with the
    /// request that failed to send it.
    /// </summary>
    public class AccessNotificationJob
    {
        private readonly GovernanceDbContext _gov;
        private readonly AppDbContext _app;
        private readonly AccessRequestNotifier _notifier;
        private readonly ILogger<AccessNotificationJob> _logger;

        public AccessNotificationJob(
            GovernanceDbContext gov, AppDbContext app,
            AccessRequestNotifier notifier, ILogger<AccessNotificationJob> logger)
        {
            _gov = gov;
            _app = app;
            _notifier = notifier;
            _logger = logger;
        }

        public const string Raised = "Raised";
        public const string Decided = "Decided";
        public const string Executed = "Executed";
        public const string Expired = "Expired";
        public const string Revoked = "Revoked";

        /// <summary>
        /// One notification, named by <paramref name="moment"/>.
        ///
        /// Three retries with Hangfire's backoff: a mail server is exactly the kind of dependency
        /// that is briefly unavailable and then fine. Beyond that the notifier has already written
        /// the failure to the audit trail, so giving up is recorded rather than silent.
        /// </summary>
        [AutomaticRetry(Attempts = 3)]
        [Queue("default")]
        public async Task SendAsync(long requestId, string moment, string? approver = null, string? comment = null,
            CancellationToken ct = default)
        {
            var request = await _gov.AccessRequests
                .Include(r => r.CatalogItem)
                .FirstOrDefaultAsync(r => r.Id == requestId, ct);

            if (request?.CatalogItem == null)
            {
                // The request or its catalog entry disappeared between queueing and sending. Worth
                // a line, not worth a retry storm.
                _logger.LogWarning("AccessNotification {Moment}: request {Id} no longer resolves", moment, requestId);
                return;
            }

            var item = request.CatalogItem;
            var tenant = await _app.TenantSettings.FirstOrDefaultAsync(t => t.Id == item.TenantId, ct);

            // Only the moments that read an address out of the directory need the tenant. Expiry and
            // revocation go to the item's own mailbox, so they still work after a tenant is removed.
            if (tenant == null && moment is Raised or Decided or Executed)
            {
                _logger.LogWarning(
                    "AccessNotification {Moment}: tenant {TenantId} for request {Id} no longer exists — cannot resolve recipients",
                    moment, item.TenantId, requestId);
                return;
            }

            switch (moment)
            {
                case Raised:
                    await _notifier.RequestRaisedAsync(request, item, tenant!, ct);
                    break;
                case Decided:
                    await _notifier.RequestDecidedAsync(request, item, tenant!, approver ?? "", comment, ct);
                    break;
                case Executed:
                    await _notifier.RequestExecutedAsync(request, item, tenant!, ct);
                    break;
                case Expired:
                    await _notifier.RequestExpiredAsync(request, item, ct);
                    break;
                case Revoked:
                    await _notifier.AccessRevokedAsync(request, item, ct);
                    break;
                default:
                    _logger.LogError("AccessNotification: unknown moment '{Moment}' for request {Id}", moment, requestId);
                    break;
            }
        }
    }
}
