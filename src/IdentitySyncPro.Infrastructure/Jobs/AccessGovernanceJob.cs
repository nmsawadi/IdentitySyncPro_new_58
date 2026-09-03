using Hangfire;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Jobs
{
    /// <summary>
    /// The recurring pass over access requests: close overdue decisions, retry approvals that never
    /// reached Active Directory, and take back access whose period has ended.
    ///
    /// ⛔ SAFETY: it only ever adds or removes the group membership a request names. It never
    /// disables an account, never moves one, and never touches an attribute — the same limits the
    /// rest of the system works under.
    ///
    /// The retry is why this runs on a timer rather than only at the moment somebody approves. An
    /// approval lost to a brief directory outage would otherwise sit as "Approved / Failed" until a
    /// person happened to open the row — an access that a manager granted, that the system recorded
    /// as granted, and that the holder never received.
    /// </summary>
    public class AccessGovernanceJob
    {
        private readonly AccessRequestService _requests;
        private readonly CampaignService _campaigns;
        private readonly ILogger<AccessGovernanceJob> _logger;

        public AccessGovernanceJob(
            AccessRequestService requests, CampaignService campaigns, ILogger<AccessGovernanceJob> logger)
        {
            _requests = requests;
            _campaigns = campaigns;
            _logger = logger;
        }

        /// <summary>
        /// One sweep. <see cref="AutomaticRetryAttribute"/> is set to zero deliberately: the sweep
        /// is already the retry, so Hangfire re-running a failed pass would only stack duplicate
        /// work on top of the next scheduled one.
        /// </summary>
        [AutomaticRetry(Attempts = 0)]
        public async Task ExecuteAsync(CancellationToken ct = default)
        {
            try
            {
                var result = await _requests.SweepAsync(ct);

                // A sweep that changed nothing is the normal case and stays at debug. A sweep that
                // could not complete something is not, and says so at warning — a failure counted
                // and never mentioned is how a stuck request becomes permanent.
                if (result.Failed > 0)
                    _logger.LogWarning(
                        "Access governance sweep: {Expired} expired, {Executed} executed, {Revoked} revoked, {Failed} still failing",
                        result.Expired, result.Executed, result.Revoked, result.Failed);
                else if (result.Expired + result.Executed + result.Revoked > 0)
                    _logger.LogInformation(
                        "Access governance sweep: {Expired} expired, {Executed} executed, {Revoked} revoked",
                        result.Expired, result.Executed, result.Revoked);
                else
                    _logger.LogDebug("Access governance sweep: nothing due");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Access request sweep failed");
            }

            // Deliberately a second try block rather than one covering both. The certification pass
            // closes campaigns and retries revocations; a request sweep that threw must not take it
            // with it, because the campaign side is the one working to a deadline.
            try
            {
                var campaigns = await _campaigns.SweepAsync(ct);

                if (campaigns.Halted > 0)
                    _logger.LogWarning(
                        "Certification sweep: {Closed} closed, {Halted} of them WITHOUT auto-revocation (too little was reviewed), {Retried} revocations executed, {Failed} failing",
                        campaigns.Closed, campaigns.Halted, campaigns.Retried, campaigns.Failed);
                else if (campaigns.Closed + campaigns.Retried + campaigns.Failed > 0)
                    _logger.LogInformation(
                        "Certification sweep: {Closed} closed, {AutoRevoked} auto-revoked, {Retried} executed, {Failed} failing",
                        campaigns.Closed, campaigns.AutoRevoked, campaigns.Retried, campaigns.Failed);
            }
            catch (Exception ex)
            {
                // Swallowed on purpose so one bad pass does not take the recurring job out of the
                // schedule — but logged as an error, because a sweep that never runs means requests
                // that never expire and access that is never taken back.
                _logger.LogError(ex, "Access governance sweep failed");
            }
        }
    }
}
