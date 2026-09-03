using IdentitySyncPro.Core.Models.Audit;
using System.Collections.Concurrent;
using Hangfire;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Rules;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentitySyncPro.Web.Controllers
{
    [Microsoft.AspNetCore.Authorization.Authorize(Roles = IdentitySyncPro.Core.Models.Settings.AppUserRoles.AdminOrOperator)]
    public class LifecycleController : Controller
    {
        private readonly AppDbContext _db;
        private readonly ILifecycleEngine _lifecycleEngine;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ExcelExportService _excelExport;

        // ✅ Fix #1: ConcurrentDictionary keyed by jobId instead of static single instance
        private static readonly ConcurrentDictionary<string, LifecycleBatchProgress> _progressMap = new();

        public LifecycleController(AppDbContext db, ILifecycleEngine lifecycleEngine, IServiceScopeFactory scopeFactory, ExcelExportService excelExport)
        {
            _db = db;
            _lifecycleEngine = lifecycleEngine;
            _scopeFactory = scopeFactory;
            _excelExport = excelExport;
        }

        /// <summary>
        /// Main lifecycle management page — shows identity states, rules, and recent transitions.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Index(string? state, string? search, int? tenantId = null, int page = 1)
        {
            // tenantId null = every tenant (default view), with a Tenant column in the table.
            var stats = await _lifecycleEngine.GetStatsAsync(tenantId);
            ViewBag.Stats = stats;

            var rulesQuery = _db.LifecycleRules.AsQueryable();
            if (tenantId.HasValue)
                rulesQuery = rulesQuery.Where(r => r.TenantId == tenantId.Value);
            ViewBag.Rules = await rulesQuery
                .OrderBy(r => r.TenantId).ThenBy(r => r.Priority)
                .ToListAsync();

            var historyQuery = _db.MetaverseHistory.Where(h => h.ChangeType == "StateChange");
            if (tenantId.HasValue)
            {
                // History has no TenantId of its own — scope it through its parent entry.
                historyQuery = historyQuery.Where(h => _db.MetaverseEntries
                    .Any(e => e.Id == h.MetaverseEntryId && e.TenantId == tenantId.Value));
            }
            ViewBag.RecentTransitions = await historyQuery
                .OrderByDescending(h => h.Timestamp)
                .Take(20)
                .ToListAsync();

            await PopulateTenantsAsync(tenantId);

            var query = _db.MetaverseEntries.AsQueryable();

            if (tenantId.HasValue)
                query = query.Where(e => e.TenantId == tenantId.Value);

            if (!string.IsNullOrEmpty(state))
                query = query.Where(e => e.LifecycleState == state);

            if (!string.IsNullOrEmpty(search))
            {
                search = search.Trim();
                query = query.Where(e =>
                    e.ExternalId.Contains(search) ||
                    (e.AttributesJson != null && e.AttributesJson.Contains(search)));
            }

            ViewBag.Search = search;
            var pageSize = 25;
            ViewBag.TotalCount = await query.CountAsync();
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = (int)Math.Ceiling((double)ViewBag.TotalCount / pageSize);
            ViewBag.State = state;
            ViewBag.TenantId = tenantId;

            var entries = await query
                .OrderByDescending(e => e.ModifiedDate)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return View(entries);
        }

        /// <summary>
        /// Tenant list for the selector + id→name lookup for the Tenant column.
        /// </summary>
        private async Task PopulateTenantsAsync(int? tenantId)
        {
            var tenants = await _db.TenantSettings.AsNoTracking()
                .OrderBy(t => t.TenantName)
                .Select(t => new { t.Id, t.TenantName, t.IsActive })
                .ToListAsync();

            ViewBag.Tenants = tenants.Select(t => (t.Id, t.TenantName, t.IsActive)).ToList();
            ViewBag.TenantNames = tenants.ToDictionary(t => t.Id, t => t.TenantName);
            ViewBag.TenantId = tenantId;
        }

        /// <summary>
        /// Details page for a single identity in the Metaverse.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Details(string id)
        {
            var entry = await _db.MetaverseEntries
                .Include(e => e.History.OrderByDescending(h => h.Timestamp).Take(50))
                .FirstOrDefaultAsync(e => e.ExternalId == id);

            if (entry == null) return NotFound();

            return View(entry);
        }

        /// <summary>
        /// Process a single identity through the full lifecycle pipeline.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> ProcessIdentity(int identityId, bool dryRun = false)
        {
            var result = await _lifecycleEngine.ProcessIdentityAsync(identityId, dryRun);
            return Json(result);
        }

        // ═══════════════════════════════════════
        // LIFECYCLE RULES CRUD
        // ═══════════════════════════════════════

        [HttpPost]
        public async Task<IActionResult> CreateRule([FromBody] LifecycleRule rule)
        {
            // The rule belongs to the tenant chosen in the UI; fall back to the first active
            // tenant only when the client didn't specify one (single-tenant installs).
            if (rule.TenantId > 0)
            {
                var exists = await _db.TenantSettings.AnyAsync(t => t.Id == rule.TenantId);
                if (!exists) return BadRequest(new { error = "Unknown tenant" });
            }
            else
            {
                var tenant = await _db.TenantSettings.FirstOrDefaultAsync(t => t.IsActive);
                if (tenant == null) return BadRequest(new { error = "No active tenant" });
                rule.TenantId = tenant.Id;
            }

            rule.CreatedDate = DateTime.UtcNow;
            rule.ModifiedDate = DateTime.UtcNow;

            _db.LifecycleRules.Add(rule);
            await _db.SaveChangesAsync();

            return Json(new { success = true, id = rule.Id });
        }

        [HttpPost]
        public async Task<IActionResult> UpdateRule([FromBody] LifecycleRule rule)
        {
            var existing = await _db.LifecycleRules.FindAsync(rule.Id);
            if (existing == null) return NotFound();

            existing.Name = rule.Name;
            existing.Description = rule.Description;
            existing.Enabled = rule.Enabled;
            existing.Priority = rule.Priority;
            existing.TriggerType = rule.TriggerType;
            existing.ConditionField = rule.ConditionField;
            existing.ConditionOperator = rule.ConditionOperator;
            existing.ConditionValue = rule.ConditionValue;
            existing.ActionType = rule.ActionType;
            existing.ActionValue = rule.ActionValue;
            existing.GracePeriodDays = rule.GracePeriodDays;
            existing.ModifiedDate = DateTime.UtcNow;

            await _db.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteRule(int id)
        {
            var rule = await _db.LifecycleRules.FindAsync(id);
            if (rule == null) return NotFound();

            _db.LifecycleRules.Remove(rule);
            await _db.SaveChangesAsync();

            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> ToggleRule(int id)
        {
            var rule = await _db.LifecycleRules.FindAsync(id);
            if (rule == null) return NotFound();

            rule.Enabled = !rule.Enabled;
            rule.ModifiedDate = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            return Json(new { success = true, enabled = rule.Enabled });
        }

        /// <summary>
        /// Get lifecycle stats as JSON (for AJAX dashboard updates).
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Stats(int? tenantId = null)
        {
            var stats = await _lifecycleEngine.GetStatsAsync(tenantId);
            return Json(stats);
        }

        [HttpPost]
        public IActionResult ProcessAll(int? tenantId = null)
        {
            var progress = new LifecycleBatchProgress { Total = -1, Processed = 0, IsRunning = true, Stage = "Initializing", CurrentStage = 0, StageLabel = "جاري التهيئة..." };
            // tenantId null = every active tenant sequentially (engine's own default).
            // The actor is captured in the request; the job runs in Hangfire with no session.
            var actor = ActorNames.Clamp(User.Identity?.Name);
            var jobId = BackgroundJob.Enqueue(() => ProcessAllPendingJob(string.Empty, tenantId, actor));
            _progressMap[jobId] = progress;
            return Json(new { success = true, jobId });
        }

        [HttpGet]
        public IActionResult GetProgress(string? jobId = null)
        {
            // ✅ Backward compatible: if no jobId, return the latest running or most recent progress
            if (string.IsNullOrEmpty(jobId))
            {
                var latest = _progressMap.Values
                    .OrderByDescending(p => p.IsRunning)
                    .FirstOrDefault();
                return Json(latest ?? new LifecycleBatchProgress());
            }

            return _progressMap.TryGetValue(jobId, out var progress)
                ? Json(progress)
                : Json(new LifecycleBatchProgress());
        }

        /// <summary>
        /// The Hangfire body behind the bulk pipeline. See the note on
        /// HealthController.ReplayAllDeadLettersJob: a public instance method on a controller is a
        /// routable action, so this was reachable as GET /Lifecycle/ProcessAllPendingJob and would
        /// run the whole three-stage pipeline against AD inside the request, outside antiforgery.
        ///
        /// [NonAction] takes it out of routing without affecting Hangfire, which calls it directly.
        /// </summary>
        [NonAction]
        [AutomaticRetry(Attempts = 0)]
        public async Task ProcessAllPendingJob(string jobId, int? tenantId = null, string? triggeredBy = null)
        {
            // Resolve or create progress tracker for this job
            var progress = _progressMap.GetOrAdd(jobId, _ => new LifecycleBatchProgress { IsRunning = true, Stage = "Initializing" });

            using var scope = _scopeFactory.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<ILifecycleEngine>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<LifecycleController>>();

            logger.LogInformation("Starting 3-stage bulk pipeline for Pending identities (Job: {JobId}, Tenant: {TenantId}, By: {By})",
                jobId, tenantId?.ToString() ?? "all", ActorNames.OrSchedule(triggeredBy));

            try
            {
                await engine.ProcessAllPendingPipelineAsync(_scopeFactory, tenantId,
                    onStageProgress: (stage, current, total) =>
                    {
                        lock (progress)
                        {
                            progress.Stage = stage;
                            progress.Processed = current;
                            progress.Total = total;

                            switch (stage)
                            {
                                case "Import":
                                    progress.CurrentStage = 1;
                                    progress.StageLabel = "الاستيراد من Oracle";
                                    break;
                                case "Rules":
                                    progress.CurrentStage = 2;
                                    progress.StageLabel = "تطبيق القواعد";
                                    break;
                                case "Export":
                                    progress.CurrentStage = 3;
                                    progress.StageLabel = "التصدير إلى AD";
                                    break;
                            }
                        }
                    });

                lock (progress)
                {
                    progress.Stage = "Completed";
                    progress.CurrentStage = 3;
                    progress.StageLabel = "اكتملت المعالجة";
                    progress.IsRunning = false;
                }

                logger.LogInformation("3-stage bulk pipeline completed successfully (Job: {JobId})", jobId);
            }
            catch (Exception ex)
            {
                var innerMsg = ex.InnerException?.Message ?? ex.Message;
                logger.LogError(ex, "3-stage bulk pipeline failed: {Error}", innerMsg);
                lock (progress)
                {
                    progress.Stage = "Failed";
                    progress.IsRunning = false;
                    progress.Error = innerMsg;
                }
            }
            finally
            {
                // Clean up old completed progress entries (keep max 10)
                if (_progressMap.Count > 10)
                {
                    var oldKeys = _progressMap
                        .Where(kv => !kv.Value.IsRunning)
                        .Select(kv => kv.Key)
                        .Take(_progressMap.Count - 5)
                        .ToList();
                    foreach (var key in oldKeys)
                        _progressMap.TryRemove(key, out _);
                }
            }
        }

        // ═══════════════════════════════════════
        // EXCEL EXPORT (Fix #7)
        // ═══════════════════════════════════════

        [HttpGet]
        public async Task<IActionResult> ExportExcel(string? state = null, int? tenantId = null)
        {
            var isArabic = (HttpContext.Items["Lang"] as string ?? "ar") == "ar";
            var query = _db.MetaverseEntries.AsNoTracking().AsQueryable();

            if (tenantId.HasValue)
                query = query.Where(e => e.TenantId == tenantId.Value);

            if (!string.IsNullOrEmpty(state))
                query = query.Where(e => e.LifecycleState == state);

            var entries = await query
                .OrderByDescending(e => e.ModifiedDate)
                .Take(10000)
                .ToListAsync();

            var tenantNames = await _db.TenantSettings.AsNoTracking()
                .ToDictionaryAsync(t => t.Id, t => t.TenantName);

            var bytes = _excelExport.ExportMetaverseEntries(entries, isArabic, tenantNames);
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"Lifecycle_{state ?? "All"}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
        }

        private class LifecycleBatchProgress
        {
            public int Total { get; set; }
            public int Processed { get; set; }
            public bool IsRunning { get; set; }
            public string Stage { get; set; } = "";
            public string? Error { get; set; }
            public int CurrentStage { get; set; }
            public int TotalStages { get; set; } = 3;
            public string StageLabel { get; set; } = "";
        }
    }
}
