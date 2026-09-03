using IdentitySyncPro.Core.Models.Audit;
using Hangfire;
using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Jobs;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentitySyncPro.Web.Controllers
{
    [Microsoft.AspNetCore.Authorization.Authorize(Roles = IdentitySyncPro.Core.Models.Settings.AppUserRoles.AdminOrOperator)]
    public class SyncController : Controller
    {
        private readonly AppDbContext _db;
        private readonly ISyncEngine _syncEngine;
        private readonly IBackgroundJobClient _backgroundJobs;
        private readonly ExcelExportService _excelExport;

        public SyncController(AppDbContext db, ISyncEngine syncEngine, IBackgroundJobClient backgroundJobs, ExcelExportService excelExport)
        {
            _db = db;
            _syncEngine = syncEngine;
            _backgroundJobs = backgroundJobs;
            _excelExport = excelExport;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? runType = null, DateTime? dateFrom = null, DateTime? dateTo = null, int? tenantId = null, int page = 1)
        {
            ViewBag.IsRunning = _syncEngine.IsRunning;

            var query = _db.SyncRuns.AsNoTracking().AsQueryable();

            // Filter by run type
            if (!string.IsNullOrEmpty(runType))
                query = query.Where(r => r.RunType == runType);

            // Filter by tenant
            if (tenantId.HasValue)
                query = query.Where(r => r.TenantId == tenantId.Value);

            // Filter by date range
            if (dateFrom.HasValue)
                query = query.Where(r => r.StartTime >= dateFrom.Value);
            if (dateTo.HasValue)
                query = query.Where(r => r.StartTime < dateTo.Value.AddDays(1));

            var pageSize = 20;
            var totalCount = await query.CountAsync();
            var runs = await query
                .OrderByDescending(r => r.StartTime)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            // Tenant list for the selector + name lookup (multi-source support)
            var tenants = await _db.TenantSettings.AsNoTracking()
                .OrderBy(t => t.TenantName)
                .Select(t => new { t.Id, t.TenantName, t.IsActive })
                .ToListAsync();
            ViewBag.ActiveTenants = tenants.Where(t => t.IsActive).Select(t => (t.Id, t.TenantName)).ToList();
            // All tenants (with their active flag) so the selector can be shown whenever more than
            // one tenant is defined, listing inactive ones as unselectable rather than hiding them.
            ViewBag.AllTenants = tenants.Select(t => (t.Id, t.TenantName, t.IsActive)).ToList();
            ViewBag.TenantNames = tenants.ToDictionary(t => t.Id, t => t.TenantName);
            ViewBag.TenantId = tenantId;

            ViewBag.RunType = runType;
            ViewBag.DateFrom = dateFrom?.ToString("yyyy-MM-dd");
            ViewBag.DateTo = dateTo?.ToString("yyyy-MM-dd");
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = (int)Math.Ceiling((double)totalCount / pageSize);
            ViewBag.TotalCount = totalCount;

            return View(runs);
        }

        [HttpGet]
        public async Task<IActionResult> Details(int id, string? opType = null, int? identityId = null, int page = 1, int pageSize = 50)
        {
            // Retry up to 3 times on deadlock
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    // Load SyncRun header only (no operations — they could be 120K+)
                    var run = await _db.SyncRuns
                        .AsNoTracking()
                        .FirstOrDefaultAsync(r => r.Id == id);

                    if (run == null) return NotFound();

                    // Build paginated operations query with optional filter
                    var opsQuery = _db.SyncOperations
                        .AsNoTracking()
                        .Where(o => o.SyncRunId == id);

                    if (!string.IsNullOrEmpty(opType))
                    {
                        if (Enum.TryParse<IdentitySyncPro.Core.Enums.OperationType>(opType, out var opEnum))
                            opsQuery = opsQuery.Where(o => o.Operation == opEnum);
                    }

                    // Filter by identity ID
                    if (identityId.HasValue)
                        opsQuery = opsQuery.Where(o => o.IdentityId == identityId.Value);

                    var totalOps = await opsQuery.CountAsync();
                    var operations = await opsQuery
                        .OrderByDescending(o => o.Timestamp)
                        .Skip((page - 1) * pageSize)
                        .Take(pageSize)
                        .ToListAsync();

                    ViewBag.Operations = operations;
                    ViewBag.OpType = opType;
                    ViewBag.IdentityId = identityId;
                    ViewBag.CurrentPage = page;
                    ViewBag.PageSize = pageSize;
                    ViewBag.TotalOps = totalOps;
                    ViewBag.TotalPages = (int)Math.Ceiling((double)totalOps / pageSize);

                    return View(run);
                }
                catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 1205 && attempt < 2)
                {
                    // Deadlock victim — wait briefly then retry
                    await Task.Delay(500 * (attempt + 1));
                }
            }
            return StatusCode(503, "Database busy, please try again");
        }

        [HttpGet]
        public IActionResult Live()
        {
            ViewBag.IsRunning = _syncEngine.IsRunning;
            return View();
        }

        [HttpPost]
        public IActionResult StartFullSync(int? tenantId = null)
        {
            if (_syncEngine.IsRunning)
                return Json(new { success = false, message = "A sync is already running" });

            var actor = CurrentActor();

            // tenantId: null → all active tenants run sequentially
            var jobId = tenantId.HasValue
                ? _backgroundJobs.Enqueue<FullSyncJob>(job => job.ExecuteTenantAsync(tenantId.Value, actor, CancellationToken.None))
                : _backgroundJobs.Enqueue<FullSyncJob>(job => job.ExecuteAsync(actor, CancellationToken.None));
            return Json(new { success = true, message = "Full sync started", jobId });
        }

        [HttpPost]
        public IActionResult StartDeltaSync(int? tenantId = null)
        {
            if (_syncEngine.IsRunning)
                return Json(new { success = false, message = "A sync is already running" });

            var actor = CurrentActor();

            var jobId = tenantId.HasValue
                ? _backgroundJobs.Enqueue<DeltaSyncJob>(job => job.ExecuteTenantAsync(tenantId.Value, actor, CancellationToken.None))
                : _backgroundJobs.Enqueue<DeltaSyncJob>(job => job.ExecuteAsync(actor, CancellationToken.None));
            return Json(new { success = true, message = "Delta sync started", jobId });
        }

        /// <summary>
        /// The signed-in user, captured in the request. The job runs later inside Hangfire where
        /// there is no session, so this has to travel as part of the job payload.
        /// </summary>
        private string CurrentActor() =>
            ActorNames.Clamp(User.Identity?.Name);

        [HttpPost]
        public IActionResult CancelSync()
        {
            _syncEngine.CancelCurrentSync();
            return Json(new { success = true, message = "Cancellation requested" });
        }

        [HttpPost]
        public async Task<IActionResult> SyncSingle(int identityId, bool dryRun = false, int? tenantId = null)
        {
            var result = await _syncEngine.SyncSingleAsync(identityId, dryRun, tenantId, triggeredBy: CurrentActor());

            return Json(new
            {
                success = result.Status == SyncOperationStatus.Success,
                operation = result.Operation.ToString(),
                changedFields = result.ChangedFields,
                error = result.ErrorMessage,
                durationMs = result.DurationMs,
                isDryRun = dryRun,
                lifecycleAction = result.LifecycleAction
            });
        }

        [HttpGet]
        public async Task<IActionResult> GetRunHistory(int page = 1)
        {
            var pageSize = 10;
            // ✅ Fix #11: AsNoTracking for read-only query
            var runs = await _db.SyncRuns
                .AsNoTracking()
                .OrderByDescending(r => r.StartTime)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Json(runs);
        }

        [HttpGet]
        public IActionResult GetStatus()
        {
            return Json(new { isRunning = _syncEngine.IsRunning });
        }

        // ✅ Fix #2: DryRun now runs as background job to avoid HTTP timeout
        [HttpPost]
        public IActionResult StartDryRun()
        {
            if (_syncEngine.IsRunning)
                return Json(new { success = false, message = "A sync is already running" });

            var actor = CurrentActor();

            var jobId = _backgroundJobs.Enqueue<FullSyncJob>(job => job.ExecuteDryRunAsync(actor, CancellationToken.None));
            return Json(new { success = true, message = "Dry run started in background", jobId });
        }

        // ═══════════════════════════════════════
        // EXCEL EXPORT
        // ═══════════════════════════════════════

        [HttpGet]
        public async Task<IActionResult> ExportRunsExcel(string? runType = null, DateTime? dateFrom = null, DateTime? dateTo = null, int? tenantId = null)
        {
            var isArabic = (HttpContext.Items["Lang"] as string ?? "ar") == "ar";
            var query = _db.SyncRuns.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(runType))
                query = query.Where(r => r.RunType == runType);
            // Must mirror Index's filter — otherwise the file silently covers every tenant.
            if (tenantId.HasValue)
                query = query.Where(r => r.TenantId == tenantId.Value);
            if (dateFrom.HasValue)
                query = query.Where(r => r.StartTime >= dateFrom.Value);
            if (dateTo.HasValue)
                query = query.Where(r => r.StartTime < dateTo.Value.AddDays(1));

            var runs = await query.OrderByDescending(r => r.StartTime).Take(5000).ToListAsync();

            // Tenant names for the Tenant column so a mixed-tenant file is readable.
            var tenantNames = await _db.TenantSettings.AsNoTracking()
                .ToDictionaryAsync(t => t.Id, t => t.TenantName);

            var bytes = _excelExport.ExportSyncRuns(runs, isArabic, tenantNames);
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"SyncRuns_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
        }

        [HttpGet]
        public async Task<IActionResult> ExportDetailsExcel(int id, string? opType = null)
        {
            var isArabic = (HttpContext.Items["Lang"] as string ?? "ar") == "ar";
            var opsQuery = _db.SyncOperations.AsNoTracking().Where(o => o.SyncRunId == id);

            if (!string.IsNullOrEmpty(opType) && Enum.TryParse<OperationType>(opType, out var opEnum))
                opsQuery = opsQuery.Where(o => o.Operation == opEnum);

            // ✅ Fix #3: Reduced from 50K to 10K to prevent OutOfMemoryException
            var ops = await opsQuery.OrderByDescending(o => o.Timestamp).Take(10000).ToListAsync();
            var bytes = _excelExport.ExportSyncOperations(ops, id, isArabic);
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"SyncRun_{id}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
        }
    }
}

