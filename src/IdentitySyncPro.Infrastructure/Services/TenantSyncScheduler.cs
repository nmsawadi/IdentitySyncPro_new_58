using IdentitySyncPro.Core.Models.Audit;
using Hangfire;
using Hangfire.Storage;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Jobs;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Registers per-tenant Hangfire recurring jobs from each active tenant's
    /// own cron schedule (FullSyncSchedule / DeltaSyncSchedule + EnableAutoSync).
    /// Example: identities nightly at 02:00, employees hourly — independently.
    ///
    /// When at least one tenant has auto-sync enabled, the legacy global
    /// "full-sync"/"delta-sync" jobs (appsettings-driven) are removed so runs
    /// are not duplicated. With no tenant schedules the legacy behavior stays.
    /// Called at startup and whenever tenant settings change.
    /// </summary>
    public static class TenantSyncScheduler
    {
        private const string FullPrefix = "full-sync-tenant-";
        private const string DeltaPrefix = "delta-sync-tenant-";

        /// <summary>
        /// Three states, not two — "suspended" and "never configured" both mean nothing runs, but
        /// only one of them is something an operator turned off and can turn back on.
        /// </summary>
        private static string DescribeType(bool enabled, string? cron) =>
            string.IsNullOrWhiteSpace(cron) ? "(no schedule)"
            : enabled ? "(registered)"
            : "(SUSPENDED)";

        public static void RefreshTenantJobs(AppDbContext db, ILogger? logger = null)
        {
            List<(int Id, string Name, bool Scheduled, bool IsActive, bool FullOn, bool DeltaOn,
                  string? FullCron, string? DeltaCron)> tenants;
            try
            {
                tenants = db.TenantSettings
                    .Select(t => new
                    {
                        t.Id, t.TenantName, t.IsActive, t.EnableAutoSync,
                        t.EnableFullSyncSchedule, t.EnableDeltaSyncSchedule,
                        t.FullSyncSchedule, t.DeltaSyncSchedule
                    })
                    .AsEnumerable()
                    .Select(t => (t.Id, t.TenantName, t.IsActive && t.EnableAutoSync, t.IsActive,
                                  t.EnableFullSyncSchedule, t.EnableDeltaSyncSchedule,
                                  t.FullSyncSchedule, t.DeltaSyncSchedule))
                    .ToList();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "TenantSyncScheduler: could not read tenants — skipping refresh");
                return;
            }

            var options = new RecurringJobOptions { TimeZone = TimeZoneInfo.Local };
            var validIds = new HashSet<string>();
            var anyScheduled = false;

            foreach (var t in tenants)
            {
                var tid = t.Id; // capture per iteration for the Hangfire expression
                var fullId = FullPrefix + tid;
                var deltaId = DeltaPrefix + tid;

                // Each type is gated independently, so a delta can be suspended while the full pass
                // keeps running — they answer different needs and used to share one switch.
                if (t.Scheduled && t.FullOn && !string.IsNullOrWhiteSpace(t.FullCron))
                {
                    RecurringJob.AddOrUpdate<FullSyncJob>(fullId,
                        job => job.ExecuteTenantAsync(tid, ActorNames.Schedule, CancellationToken.None), t.FullCron, options);
                    validIds.Add(fullId);
                    anyScheduled = true;
                }

                if (t.Scheduled && t.DeltaOn && !string.IsNullOrWhiteSpace(t.DeltaCron))
                {
                    RecurringJob.AddOrUpdate<DeltaSyncJob>(deltaId,
                        job => job.ExecuteTenantAsync(tid, ActorNames.Schedule, CancellationToken.None), t.DeltaCron, options);
                    validIds.Add(deltaId);
                    anyScheduled = true;
                }

                // A saved schedule that registers nothing is the quietest way for a tenant to be
                // configured and inert: the settings page shows a cron and an interval, the save
                // reports success, and no job exists. Seen live — a full sync set to every 300
                // minutes never ran for a day because EnableAutoSync was off, and nothing said so.
                //
                // Now that full and delta are gated separately, the message has to name WHICH one is
                // idle and why; "scheduling is off" would hide a half-suspended tenant.
                if (!t.Scheduled)
                {
                    if (!string.IsNullOrWhiteSpace(t.FullCron) || !string.IsNullOrWhiteSpace(t.DeltaCron))
                        logger?.LogWarning(
                            "TenantSyncScheduler: tenant '{Tenant}' has a saved schedule (full [{Full}], delta [{Delta}]) " +
                            "but {Reason} — no recurring job is registered and NOTHING will run on a timer. " +
                            "Manual syncs are unaffected.",
                            t.Name, t.FullCron ?? "-", t.DeltaCron ?? "-",
                            t.IsActive ? "auto-sync is disabled for it" : "the tenant is inactive");
                }
                else
                {
                    logger?.LogInformation(
                        "TenantSyncScheduler: tenant '{Tenant}' → full [{Full}] {FullState}, delta [{Delta}] {DeltaState}",
                        t.Name,
                        t.FullCron ?? "-", DescribeType(t.FullOn, t.FullCron),
                        t.DeltaCron ?? "-", DescribeType(t.DeltaOn, t.DeltaCron));

                    if (!t.FullOn && !string.IsNullOrWhiteSpace(t.FullCron))
                        logger?.LogWarning(
                            "TenantSyncScheduler: tenant '{Tenant}' — the FULL sync schedule [{Cron}] is suspended; " +
                            "only the delta runs on a timer.", t.Name, t.FullCron);

                    if (!t.DeltaOn && !string.IsNullOrWhiteSpace(t.DeltaCron))
                        logger?.LogWarning(
                            "TenantSyncScheduler: tenant '{Tenant}' — the DELTA sync schedule [{Cron}] is suspended; " +
                            "only the full sync runs on a timer.", t.Name, t.DeltaCron);
                }
            }

            // Drop stale per-tenant jobs (deactivated or deleted tenants)
            try
            {
                using var connection = JobStorage.Current.GetConnection();
                foreach (var job in connection.GetRecurringJobs())
                {
                    var isTenantJob = job.Id.StartsWith(FullPrefix) || job.Id.StartsWith(DeltaPrefix);
                    if (isTenantJob && !validIds.Contains(job.Id))
                        RecurringJob.RemoveIfExists(job.Id);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "TenantSyncScheduler: stale-job cleanup skipped");
            }

            // Per-tenant schedules replace the legacy global jobs (avoid double runs)
            if (anyScheduled)
            {
                RecurringJob.RemoveIfExists("full-sync");
                RecurringJob.RemoveIfExists("delta-sync");
            }

            // Say what the refresh concluded, including when it concluded nothing. A scheduler that
            // logs only on success cannot be told apart from one that never ran — and this method is
            // called from settings saves, where "no output" is exactly what an operator reads as
            // "the schedule is now in place".
            if (validIds.Count > 0)
                logger?.LogInformation("TenantSyncScheduler: {Count} recurring sync job(s) registered", validIds.Count);
            else
                logger?.LogWarning("TenantSyncScheduler: no tenant has an active schedule — no sync will run on a timer");
        }
    }
}
