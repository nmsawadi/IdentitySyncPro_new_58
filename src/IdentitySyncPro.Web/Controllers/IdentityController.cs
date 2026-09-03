using IdentitySyncPro.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentitySyncPro.Web.Controllers
{
    public class IdentityController : Controller
    {
        private readonly AppDbContext _db;

        public IdentityController(AppDbContext db)
        {
            _db = db;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? search, string? status, int? tenantId = null, int page = 1)
        {
            var query = _db.SyncStates.AsQueryable();

            if (!string.IsNullOrEmpty(search))
            {
                if (int.TryParse(search, out var id))
                    query = query.Where(s => s.IdentityId == id);
                else
                    query = query.Where(s => s.Status.Contains(search));
            }

            if (!string.IsNullOrEmpty(status))
                query = query.Where(s => s.Status == status);

            if (tenantId.HasValue)
                query = query.Where(s => s.TenantId == tenantId.Value);

            // Tenant list for the filter (shown only when more than one tenant exists)
            var tenants = await _db.TenantSettings.AsNoTracking()
                .OrderBy(t => t.TenantName)
                .Select(t => new { t.Id, t.TenantName })
                .ToListAsync();
            ViewBag.Tenants = tenants.Select(t => (t.Id, t.TenantName)).ToList();
            ViewBag.TenantId = tenantId;

            var pageSize = 25;
            ViewBag.TotalCount = await query.CountAsync();
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = (int)Math.Ceiling((double)ViewBag.TotalCount / pageSize);
            ViewBag.Search = search;
            ViewBag.Status = status;

            var identities = await query
                .OrderByDescending(s => s.LastSyncDate)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return View(identities);
        }

        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            var syncState = await _db.SyncStates.FirstOrDefaultAsync(s => s.IdentityId == id);
            if (syncState == null) return NotFound();

            ViewBag.Operations = await _db.SyncOperations
                .Where(o => o.IdentityId == id)
                .OrderByDescending(o => o.Timestamp)
                .Take(20)
                .ToListAsync();

            return View(syncState);
        }

        [HttpGet]
        public async Task<IActionResult> Search(string term)
        {
            var results = await _db.SyncStates
                .Where(s => s.IdentityId.ToString().Contains(term))
                .Take(10)
                .Select(s => new { s.IdentityId, s.Status, s.CreatedInAD, s.LastSyncDate })
                .ToListAsync();

            return Json(results);
        }

        [HttpGet]
        public async Task<IActionResult> Export(string? status, int? tenantId = null)
        {
            var query = _db.SyncStates.AsQueryable();
            if (!string.IsNullOrEmpty(status))
                query = query.Where(s => s.Status == status);
            // Must mirror Index's filter — otherwise the file silently covers every tenant.
            if (tenantId.HasValue)
                query = query.Where(s => s.TenantId == tenantId.Value);

            var data = await query.OrderBy(s => s.IdentityId).ToListAsync();

            var tenantNames = await _db.TenantSettings.AsNoTracking()
                .ToDictionaryAsync(t => t.Id, t => t.TenantName);

            // Tenant column so a mixed-tenant file stays interpretable once downloaded.
            var csv = "IdentityId,Tenant,Status,CreatedInAD,LastSyncDate,Hash\n";
            foreach (var s in data)
            {
                var tenant = tenantNames.TryGetValue(s.TenantId, out var n) ? n : s.TenantId.ToString();
                csv += $"{s.IdentityId},{CsvEscape(tenant)},{s.Status},{s.CreatedInAD},{s.LastSyncDate:yyyy-MM-dd HH:mm},{s.CurrentHash}\n";
            }

            return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", "identities_export.csv");
        }

        /// <summary>Quote a CSV field if it contains a comma, quote or newline.</summary>
        private static string CsvEscape(string? value)
        {
            var v = value ?? "";
            return v.Contains(',') || v.Contains('"') || v.Contains('\n')
                ? $"\"{v.Replace("\"", "\"\"")}\""
                : v;
        }
    }
}
