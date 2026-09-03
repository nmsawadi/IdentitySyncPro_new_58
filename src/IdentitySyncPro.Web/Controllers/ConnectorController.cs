using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentitySyncPro.Web.Controllers
{
    [Microsoft.AspNetCore.Authorization.Authorize(Roles = IdentitySyncPro.Core.Models.Settings.AppUserRoles.Admin)]
    public class ConnectorController : Controller
    {
        private readonly AppDbContext _db;
        private readonly ITenantConnectorFactory _connectorFactory;

        public ConnectorController(AppDbContext db, ITenantConnectorFactory connectorFactory)
        {
            _db = db;
            _connectorFactory = connectorFactory;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var connectors = await _db.ConnectorConfigs.ToListAsync();
            return View(connectors);
        }

        /// <summary>Resolve the active tenant whose DB-stored connection the diagnostic tests.</summary>
        private async Task<TenantSettings?> GetActiveTenantAsync() =>
            await _db.TenantSettings.AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.Id)
                .FirstOrDefaultAsync();

        [HttpPost]
        public async Task<IActionResult> TestOracle()
        {
            var tenant = await GetActiveTenantAsync();
            if (tenant == null)
                return Json(new { success = false, message = "No active tenant configured" });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var source = _connectorFactory.CreateSourceConnector(tenant);
                var success = await source.TestConnectionAsync();
                sw.Stop();
                var info = success ? await source.GetConnectionInfoAsync() : "";
                return Json(new
                {
                    success,
                    message = success ? $"Source connection OK ({sw.ElapsedMilliseconds}ms)" : "Source connection FAILED",
                    info,
                    durationMs = sw.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                return Json(new { success = false, message = ex.Message, durationMs = sw.ElapsedMilliseconds });
            }
        }

        [HttpPost]
        public async Task<IActionResult> TestAD()
        {
            var tenant = await GetActiveTenantAsync();
            if (tenant == null)
                return Json(new { success = false, message = "No active tenant configured" });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var target = _connectorFactory.CreateTargetConnector(tenant);
                var success = await target.TestConnectionAsync();
                sw.Stop();
                var info = success ? await target.GetConnectionInfoAsync() : "";
                return Json(new
                {
                    success,
                    message = success ? $"AD connection OK ({sw.ElapsedMilliseconds}ms)" : "AD connection FAILED",
                    info,
                    durationMs = sw.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                return Json(new { success = false, message = ex.Message, durationMs = sw.ElapsedMilliseconds });
            }
        }

        [HttpPost]
        public async Task<IActionResult> GetOracleCount()
        {
            var tenant = await GetActiveTenantAsync();
            if (tenant == null)
                return Json(new { success = false, error = "No active tenant configured" });

            try
            {
                var source = _connectorFactory.CreateSourceConnector(tenant);
                var count = await source.GetTotalCountAsync();
                return Json(new { success = true, count });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = ex.Message });
            }
        }
    }
}
