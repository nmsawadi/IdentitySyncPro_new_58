using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentitySyncPro.Web.Controllers
{
    public class DashboardController : Controller
    {
        private readonly AppDbContext _db;

        public DashboardController(AppDbContext db)
        {
            _db = db;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            try
            {
                var now = DateTime.UtcNow;
                var sevenDaysAgo = now.AddDays(-7);

                // Main stats
                ViewBag.TotalIdentities = await _db.SyncStates.CountAsync();
                ViewBag.ActiveIdentities = await _db.SyncStates.CountAsync(s => s.CreatedInAD);
                ViewBag.FailedIdentities = await _db.SyncStates.CountAsync(s => s.Status == "Failed");
                ViewBag.PendingIdentities = await _db.SyncStates.CountAsync(s => !s.CreatedInAD && s.Status != "Failed");

                // Last sync run
                ViewBag.LastSync = await _db.SyncRuns
                    .OrderByDescending(r => r.StartTime)
                    .FirstOrDefaultAsync();

                // Last 5 sync runs for the mini table
                ViewBag.RecentRuns = await _db.SyncRuns
                    .OrderByDescending(r => r.StartTime)
                    .Take(5)
                    .ToListAsync();

                // 7-day chart data
                var dailyStats = await _db.SyncOperations
                    .Where(o => o.Timestamp >= sevenDaysAgo)
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

                ViewBag.ChartLabels = dailyStats.Select(d => d.Date.ToString("MMM dd")).ToList();
                ViewBag.ChartCreated = dailyStats.Select(d => d.Created).ToList();
                ViewBag.ChartUpdated = dailyStats.Select(d => d.Updated).ToList();
                ViewBag.ChartFailed = dailyStats.Select(d => d.Failed).ToList();

                // Recent activity
                ViewBag.RecentActivity = await _db.SyncOperations
                    .OrderByDescending(o => o.Timestamp)
                    .Take(10)
                    .ToListAsync();

                // Total operations today
                var today = DateTime.UtcNow.Date;
                ViewBag.TodayCreated = await _db.SyncOperations.CountAsync(o => o.Timestamp >= today && o.Operation == OperationType.Create && o.Status == SyncOperationStatus.Success);
                ViewBag.TodayUpdated = await _db.SyncOperations.CountAsync(o => o.Timestamp >= today && o.Operation == OperationType.Update && o.Status == SyncOperationStatus.Success);
                ViewBag.TodayFailed = await _db.SyncOperations.CountAsync(o => o.Timestamp >= today && o.Status == SyncOperationStatus.Failed);
            }
            catch (Exception)
            {
                // ✅ Fix #4: Graceful degradation — show dashboard with empty data instead of 500
                ViewBag.TotalIdentities = 0; ViewBag.ActiveIdentities = 0;
                ViewBag.FailedIdentities = 0; ViewBag.PendingIdentities = 0;
                ViewBag.LastSync = null; ViewBag.RecentRuns = new List<object>();
                ViewBag.ChartLabels = new List<string>(); ViewBag.ChartCreated = new List<int>();
                ViewBag.ChartUpdated = new List<int>(); ViewBag.ChartFailed = new List<int>();
                ViewBag.RecentActivity = new List<object>();
                ViewBag.TodayCreated = 0; ViewBag.TodayUpdated = 0; ViewBag.TodayFailed = 0;
                ViewBag.DbError = true;
            }

            return View();
        }

        [HttpGet]
        public async Task<IActionResult> GetStats()
        {
            return Json(new
            {
                totalIdentities = await _db.SyncStates.CountAsync(),
                activeIdentities = await _db.SyncStates.CountAsync(s => s.CreatedInAD),
                failedIdentities = await _db.SyncStates.CountAsync(s => s.Status == "Failed"),
                lastSync = await _db.SyncRuns.OrderByDescending(r => r.StartTime).FirstOrDefaultAsync()
            });
        }
    }
}
