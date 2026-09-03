using IdentitySyncPro.Core.Models.Services;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Persists audit entries and proves it.
    ///
    /// Why this exists: a run reported "acted on 1 account" while its audit log came up empty, and
    /// nothing in the logs distinguished "the entries were never created" from "they were created
    /// and the save was lost" from "they are there and the screen is not finding them". Each has a
    /// different fix, and the code gave no way to tell them apart.
    ///
    /// So the write is verified against the database rather than assumed: the rows are counted
    /// back after saving, and any disagreement with what was queued is logged as an error. The run
    /// that produced it then carries the answer in the log file.
    /// </summary>
    public static class SvcAuditWriter
    {
        /// <summary>
        /// Saves pending changes and re-reads how many audit rows the run actually has.
        /// </summary>
        /// <returns>The number of audit rows the database holds for this run after saving.</returns>
        public static async Task<int> FlushAndVerifyAsync(
            ServicesDbContext db,
            ILogger logger,
            long runId,
            int serviceId,
            string serviceName,
            CancellationToken ct = default)
        {
            var queued = db.ChangeTracker.Entries<SvcAuditEntry>()
                .Count(e => e.State == EntityState.Added);

            await db.SaveChangesAsync(ct);

            // Counted from the database, not from the change tracker: the tracker would happily
            // report success for rows a trigger, a filter, or the wrong connection discarded.
            var storedForRun = await db.SvcAuditEntries
                .AsNoTracking()
                .CountAsync(a => a.SvcRunLogId == runId, ct);

            // Counted the way the SCREEN counts, on both keys. Verifying the run alone once
            // reported success for rows the audit page could still never show, because the page
            // filters on the service as well — a row with the wrong service id is stored and
            // invisible at the same time.
            var visibleOnPage = await db.SvcAuditEntries
                .AsNoTracking()
                .CountAsync(a => a.SvcRunLogId == runId && a.SvcServiceId == serviceId, ct);

            if (queued > 0 && storedForRun == 0)
            {
                logger.LogError(
                    "SvcAudit['{Service}'] run {RunId}: queued {Queued} audit entr(ies) and the database " +
                    "reports NONE for this run. Check that Svc_AuditEntries is writable and that " +
                    "SvcRunLogId {RunId} exists in Svc_RunLogs.",
                    serviceName, runId, queued, runId);
            }
            else if (storedForRun != visibleOnPage)
            {
                logger.LogError(
                    "SvcAudit['{Service}'] run {RunId}: {Stored} entr(ies) stored but only {Visible} carry " +
                    "SvcServiceId={ServiceId}. The rest exist in the table and can never appear on the " +
                    "audit page, which filters on both keys.",
                    serviceName, runId, storedForRun, visibleOnPage, serviceId);
            }
            else
            {
                logger.LogInformation(
                    "SvcAudit['{Service}'] run {RunId}: queued {Queued}, stored {Stored}, visible on " +
                    "/Services/AuditLog?id={ServiceId}&runId={RunId} = {Visible}.",
                    serviceName, runId, queued, storedForRun, serviceId, runId, visibleOnPage);
            }

            return visibleOnPage;
        }
    }
}
