using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentitySyncPro.Web.Controllers
{
    public class TopErrorEntry
    {
        public string? Error { get; set; }
        public int Count { get; set; }
    }

    public class StatusEntry
    {
        public string? Status { get; set; }
        public int Count { get; set; }
    }

    public class ReportsController : Controller
    {
        private readonly AppDbContext _db;
        private readonly ExcelExportService _excelExport;

        public ReportsController(AppDbContext db, ExcelExportService excelExport)
        {
            _db = db;
            _excelExport = excelExport;
        }

        /// <summary>Sync runs, optionally scoped to one tenant.</summary>
        private IQueryable<IdentitySyncPro.Core.Models.Sync.SyncRun> ScopedRuns(int? tenantId)
        {
            var q = _db.SyncRuns.AsNoTracking().AsQueryable();
            return tenantId.HasValue ? q.Where(r => r.TenantId == tenantId.Value) : q;
        }

        /// <summary>
        /// Sync operations, optionally scoped to one tenant. SyncOperation carries no TenantId
        /// of its own — it is scoped through its parent run.
        /// </summary>
        private IQueryable<IdentitySyncPro.Core.Models.Sync.SyncOperation> ScopedOperations(int? tenantId)
        {
            var q = _db.SyncOperations.AsNoTracking().AsQueryable();
            if (!tenantId.HasValue) return q;
            return q.Where(o => _db.SyncRuns.Any(r => r.Id == o.SyncRunId && r.TenantId == tenantId.Value));
        }

        /// <summary>Identity sync states, optionally scoped to one tenant.</summary>
        private IQueryable<IdentitySyncPro.Core.Models.Sync.SyncState> ScopedStates(int? tenantId)
        {
            var q = _db.SyncStates.AsNoTracking().AsQueryable();
            return tenantId.HasValue ? q.Where(s => s.TenantId == tenantId.Value) : q;
        }

        /// <summary>Tenant list for the selector + the selected id.</summary>
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

        [HttpGet]
        public async Task<IActionResult> Index(int? tenantId = null)
        {
            // Tenant list for the selector — populated outside the try so the page still
            // offers the filter when the data queries below degrade.
            await PopulateTenantsAsync(tenantId);

            try
            {
                var now = DateTime.UtcNow;

                // null tenantId = totals across every tenant.
                var runs = ScopedRuns(tenantId);
                var ops = ScopedOperations(tenantId);
                var states = ScopedStates(tenantId);

                // Summary stats
                ViewBag.TotalSyncRuns = await runs.CountAsync();
                ViewBag.SuccessfulRuns = await runs.CountAsync(r => r.Status == SyncRunStatus.Completed);
                ViewBag.FailedRuns = await runs.CountAsync(r => r.Status == SyncRunStatus.Failed);
                ViewBag.TotalOperations = await ops.CountAsync();

                // Monthly trends
                var thirtyDaysAgo = now.AddDays(-30);
                var monthlyData = await ops
                    .Where(o => o.Timestamp >= thirtyDaysAgo)
                    .GroupBy(o => o.Timestamp.Date)
                    .Select(g => new
                    {
                        Date = g.Key,
                        Created = g.Count(o => o.Operation == OperationType.Create && o.Status == SyncOperationStatus.Success),
                        Updated = g.Count(o => o.Operation == OperationType.Update && o.Status == SyncOperationStatus.Success),
                        Failed = g.Count(o => o.Status == SyncOperationStatus.Failed)
                    })
                    .OrderBy(x => x.Date)
                    .ToListAsync();

                ViewBag.MonthLabels = monthlyData.Select(d => d.Date.ToString("MMM dd")).ToList();
                ViewBag.MonthCreated = monthlyData.Select(d => d.Created).ToList();
                ViewBag.MonthUpdated = monthlyData.Select(d => d.Updated).ToList();
                ViewBag.MonthFailed = monthlyData.Select(d => d.Failed).ToList();

                // Status distribution
                var statusDist = await states
                    .GroupBy(s => s.Status)
                    .Select(g => new StatusEntry { Status = g.Key, Count = g.Count() })
                    .ToListAsync();

                ViewBag.StatusLabels = statusDist.Select(s => s.Status).ToList();
                ViewBag.StatusCounts = statusDist.Select(s => s.Count).ToList();

                // Top errors
                ViewBag.TopErrors = await ops
                    .Where(o => o.Status == SyncOperationStatus.Failed && o.ErrorMessage != null)
                    .GroupBy(o => o.ErrorMessage)
                    .Select(g => new TopErrorEntry { Error = g.Key, Count = g.Count() })
                    .OrderByDescending(x => x.Count)
                    .Take(10)
                    .ToListAsync();
            }
            catch (Exception)
            {
                // ✅ Fix #4: Graceful degradation
                ViewBag.TotalSyncRuns = 0; ViewBag.SuccessfulRuns = 0;
                ViewBag.FailedRuns = 0; ViewBag.TotalOperations = 0;
                ViewBag.MonthLabels = new List<string>(); ViewBag.MonthCreated = new List<int>();
                ViewBag.MonthUpdated = new List<int>(); ViewBag.MonthFailed = new List<int>();
                ViewBag.StatusLabels = new List<string>(); ViewBag.StatusCounts = new List<int>();
                ViewBag.TopErrors = new List<TopErrorEntry>();
                ViewBag.DbError = true;
            }

            return View();
        }

        // ═══════════════════════════════════════
        // EXCEL EXPORT
        // ═══════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> ExportExcel(int? tenantId = null)
        {
            var isArabic = (HttpContext.Items["Lang"] as string ?? "ar") == "ar";
            var now = DateTime.UtcNow;
            var thirtyDaysAgo = now.AddDays(-30);

            // Must mirror Index's scoping — otherwise the file contradicts the screen it came from.
            var runs = ScopedRuns(tenantId);
            var ops = ScopedOperations(tenantId);
            var states = ScopedStates(tenantId);

            var totalRuns = await runs.CountAsync();
            var successfulRuns = await runs.CountAsync(r => r.Status == SyncRunStatus.Completed);
            var failedRuns = await runs.CountAsync(r => r.Status == SyncRunStatus.Failed);
            var totalOps = await ops.CountAsync();

            // ✅ Fix #6: Materialize first, then format date on client side
            var monthlyRaw = await ops
                .Where(o => o.Timestamp >= thirtyDaysAgo)
                .GroupBy(o => o.Timestamp.Date)
                .Select(g => new
                {
                    Date = g.Key,
                    Created = g.Count(o => o.Operation == OperationType.Create && o.Status == SyncOperationStatus.Success),
                    Updated = g.Count(o => o.Operation == OperationType.Update && o.Status == SyncOperationStatus.Success),
                    Failed = g.Count(o => o.Status == SyncOperationStatus.Failed)
                })
                .OrderBy(x => x.Date)
                .ToListAsync();

            var monthlyData = monthlyRaw.Select(m => (Date: m.Date.ToString("MMM dd"), m.Created, m.Updated, m.Failed)).ToList();

            var statusDist = await states
                .GroupBy(s => s.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync();

            var topErrors = await ops
                .Where(o => o.Status == SyncOperationStatus.Failed && o.ErrorMessage != null)
                .GroupBy(o => o.ErrorMessage)
                .Select(g => new { Error = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .Take(10)
                .ToListAsync();

            var tenantName = tenantId.HasValue
                ? await _db.TenantSettings.Where(t => t.Id == tenantId.Value)
                    .Select(t => t.TenantName).FirstOrDefaultAsync()
                : null;

            var bytes = _excelExport.ExportReportSummary(
                totalRuns, successfulRuns, failedRuns, totalOps,
                monthlyData,
                statusDist.Select(s => (s.Status, s.Count)).ToList(),
                topErrors.Select(e => (e.Error, e.Count)).ToList(),
                isArabic,
                tenantName
            );

            // Name the file after its scope so downloads don't collide or get mixed up.
            var scopeSuffix = tenantName != null ? $"_{SafeFileName(tenantName)}" : "_AllTenants";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"Reports{scopeSuffix}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
        }

        /// <summary>Strip characters that are invalid in a download filename.</summary>
        private static string SafeFileName(string name)
        {
            var cleaned = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            return cleaned.Replace(' ', '_').Trim('_');
        }
    }
}
