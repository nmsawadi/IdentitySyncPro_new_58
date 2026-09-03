using Hangfire;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentitySyncPro.Web.Controllers
{
    [Microsoft.AspNetCore.Authorization.Authorize(Roles = IdentitySyncPro.Core.Models.Settings.AppUserRoles.AdminOrOperator)]
    public class HealthController : Controller
    {
        private readonly AppDbContext _db;
        private readonly ResilienceService _resilience;
        private readonly ISyncEngine _syncEngine;
        private readonly IServiceScopeFactory _scopeFactory;

        public HealthController(AppDbContext db, ResilienceService resilience, ISyncEngine syncEngine, IServiceScopeFactory scopeFactory)
        {
            _db = db;
            _resilience = resilience;
            _syncEngine = syncEngine;
            _scopeFactory = scopeFactory;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            // Component health
            ViewBag.Components = _resilience.GetComponentsHealth();

            // Quarantined identities
            ViewBag.Quarantined = await _db.QuarantinedIdentities
                .Where(q => !q.IsResolved)
                .OrderByDescending(q => q.QuarantinedDate)
                .Take(50)
                .ToListAsync();

            ViewBag.QuarantinedCount = await _db.QuarantinedIdentities.CountAsync(q => !q.IsResolved);
            ViewBag.ResolvedCount = await _db.QuarantinedIdentities.CountAsync(q => q.IsResolved);

            // Dead letter queue
            ViewBag.DeadLetters = await _db.DeadLetterEntries
                .Where(d => !d.IsReplayed)
                .OrderByDescending(d => d.FailedDate)
                .Take(50)
                .ToListAsync();

            var dlStats = await _resilience.GetDeadLetterStatsAsync();
            ViewBag.DeadLetterPending = dlStats.Pending;
            ViewBag.DeadLetterReplayed = dlStats.Replayed;

            return View();
        }

        [HttpPost]
        public async Task<IActionResult> ResolveQuarantine(int id, string? notes)
        {
            var item = await _db.QuarantinedIdentities.FindAsync(id);
            if (item == null) return NotFound();

            item.IsResolved = true;
            item.ReviewedDate = DateTime.UtcNow;
            item.ReviewedBy = "Admin";
            item.ResolutionNotes = notes;
            await _db.SaveChangesAsync();

            return Json(new { success = true });
        }

        /// <summary>
        /// Replay a dead letter entry by actually re-executing the sync operation.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> ReplayDeadLetter(long id)
        {
            var entry = await _db.DeadLetterEntries.FindAsync(id);
            if (entry == null) return NotFound();

            try
            {
                // Actually re-execute the sync for this identity
                var result = await _syncEngine.SyncSingleAsync(entry.IdentityId, false);

                entry.IsReplayed = true;
                entry.ReplayedDate = DateTime.UtcNow;
                entry.ReplayResult = result.Status == Core.Enums.SyncOperationStatus.Success
                    ? $"Success: {result.Operation} completed in {result.DurationMs}ms"
                    : $"Failed: {result.ErrorMessage}";

                await _db.SaveChangesAsync();
                return Json(new { success = result.Status == Core.Enums.SyncOperationStatus.Success, result = entry.ReplayResult });
            }
            catch (Exception ex)
            {
                entry.ReplayResult = $"Replay error: {ex.Message}";
                await _db.SaveChangesAsync();
                return Json(new { success = false, result = entry.ReplayResult });
            }
        }

        /// <summary>
        /// ✅ Fix #8: Replay all dead letters via background job to avoid HTTP timeout.
        /// </summary>
        [HttpPost]
        public IActionResult ReplayAllDeadLetters()
        {
            var jobId = BackgroundJob.Enqueue(() => ReplayAllDeadLettersJob());
            return Json(new { success = true, message = "Replay started in background", jobId });
        }

        /// <summary>
        /// The Hangfire body behind ReplayAllDeadLetters. It lives on the controller so the enqueue
        /// expression can name it, which also made it a routable action: any public instance method
        /// on a controller is one unless marked otherwise.
        ///
        /// That meant GET /Health/ReplayAllDeadLettersJob replayed every dead letter inside the
        /// request — and a GET is not covered by the global antiforgery filter, so an image tag
        /// pointed at it from anywhere would fire it in an operator's session. Found by CA5395,
        /// which asks only for an explicit verb; the verb was never the point.
        ///
        /// [NonAction] removes it from routing. Hangfire is unaffected: it invokes the method
        /// directly and never goes through MVC.
        /// </summary>
        [NonAction]
        [AutomaticRetry(Attempts = 0)]
        public async Task ReplayAllDeadLettersJob()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var syncEngine = scope.ServiceProvider.GetRequiredService<ISyncEngine>();

            var entries = await db.DeadLetterEntries
                .Where(d => !d.IsReplayed)
                .ToListAsync();

            foreach (var entry in entries)
            {
                try
                {
                    var result = await syncEngine.SyncSingleAsync(entry.IdentityId, false);
                    entry.IsReplayed = true;
                    entry.ReplayedDate = DateTime.UtcNow;
                    entry.ReplayResult = result.Status == Core.Enums.SyncOperationStatus.Success
                        ? "Success" : $"Failed: {result.ErrorMessage}";
                }
                catch (Exception ex)
                {
                    entry.ReplayResult = $"Error: {ex.Message}";
                }
            }

            await db.SaveChangesAsync();
        }

        [HttpGet]
        public IActionResult Status()
        {
            var components = _resilience.GetComponentsHealth();
            var overall = components.Any(c => c.CircuitOpen) ? "Degraded"
                : components.All(c => c.Status == "Healthy") ? "Healthy" : "Warning";

            return Json(new { overall, components });
        }
    }
}
