using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Rules;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentitySyncPro.Web.Controllers
{
    [Microsoft.AspNetCore.Authorization.Authorize(Roles = IdentitySyncPro.Core.Models.Settings.AppUserRoles.Admin)]
    public class RulesController : Controller
    {
        private readonly AppDbContext _db;
        private readonly IRulesEngine _rulesEngine;
        private readonly RuleVersioningService _versioning;
        private readonly RuleValidationService _validation;

        public RulesController(AppDbContext db, IRulesEngine rulesEngine, RuleVersioningService versioning, RuleValidationService validation)
        {
            _db = db;
            _rulesEngine = rulesEngine;
            _versioning = versioning;
            _validation = validation;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? type, int? tenantId = null)
        {
            // tenantId null = every tenant (default view), with a Tenant column in the table.
            var stats = await _rulesEngine.GetStatsAsync(tenantId);
            ViewBag.Stats = stats;
            ViewBag.SelectedType = type;

            await PopulateTenantsAsync(tenantId);

            var rules = await _rulesEngine.GetRulesAsync(type, tenantId);
            return View(rules);
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

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] SyncRuleV2 rule)
        {
            // Validate before saving
            var errors = _validation.Validate(rule);
            if (errors.Count > 0)
                return BadRequest(new { error = "Validation failed", errors });

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

            _db.SyncRulesV2.Add(rule);
            await _db.SaveChangesAsync();

            await _versioning.SaveVersionAsync(rule.Id, "Initial creation");

            return Json(new { success = true, id = rule.Id });
        }

        [HttpPost]
        public async Task<IActionResult> Update([FromBody] SyncRuleV2 rule)
        {
            // Validate before saving
            var errors = _validation.Validate(rule);
            if (errors.Count > 0)
                return BadRequest(new { error = "Validation failed", errors });

            var existing = await _db.SyncRulesV2
                .Include(r => r.FlowMappings)
                .FirstOrDefaultAsync(r => r.Id == rule.Id);

            if (existing == null) return NotFound();

            existing.Name = rule.Name;
            existing.Description = rule.Description;
            existing.Enabled = rule.Enabled;
            existing.Priority = rule.Priority;
            existing.RuleType = rule.RuleType;
            existing.Direction = rule.Direction;
            existing.SourceSystem = rule.SourceSystem;
            existing.TargetSystem = rule.TargetSystem;
            existing.ScopeFilter = rule.ScopeFilter;
            existing.ConditionJson = rule.ConditionJson;
            existing.ConfigurationJson = rule.ConfigurationJson;
            existing.ModifiedDate = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            await _versioning.SaveVersionAsync(rule.Id, "Updated via UI");

            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            var rule = await _db.SyncRulesV2.FindAsync(id);
            if (rule == null) return NotFound();

            _db.SyncRulesV2.Remove(rule);
            await _db.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> Toggle(int id)
        {
            var rule = await _db.SyncRulesV2.FindAsync(id);
            if (rule == null) return NotFound();

            rule.Enabled = !rule.Enabled;
            rule.ModifiedDate = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Json(new { success = true, enabled = rule.Enabled });
        }

        [HttpPost]
        public async Task<IActionResult> UpdatePriority([FromBody] List<PriorityUpdate> updates)
        {
            foreach (var update in updates)
            {
                var rule = await _db.SyncRulesV2.FindAsync(update.Id);
                if (rule != null)
                {
                    rule.Priority = update.Priority;
                    rule.ModifiedDate = DateTime.UtcNow;
                }
            }
            await _db.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> Preview(int ruleId, int identityId)
        {
            var result = await _rulesEngine.PreviewRuleAsync(ruleId, identityId);
            return Json(result);
        }

        // Flow Mappings CRUD
        [HttpPost]
        public async Task<IActionResult> AddMapping([FromBody] SyncRuleFlowMapping mapping)
        {
            _db.SyncRuleFlowMappings.Add(mapping);
            await _db.SaveChangesAsync();
            return Json(new { success = true, id = mapping.Id });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteMapping(int id)
        {
            var mapping = await _db.SyncRuleFlowMappings.FindAsync(id);
            if (mapping == null) return NotFound();
            _db.SyncRuleFlowMappings.Remove(mapping);
            await _db.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpGet]
        public async Task<IActionResult> Stats(int? tenantId = null)
        {
            var stats = await _rulesEngine.GetStatsAsync(tenantId);
            return Json(stats);
        }

        // ═══════════════════════════════════════
        // VERSIONING
        // ═══════════════════════════════════════

        [HttpGet]
        public async Task<IActionResult> Versions(int ruleId)
        {
            var versions = await _versioning.GetVersionsAsync(ruleId);
            return Json(versions.Select(v => new {
                v.VersionNumber, v.ChangeNotes, v.ChangedBy, v.CreatedDate, v.IsCurrent
            }));
        }

        [HttpPost]
        public async Task<IActionResult> Rollback(int ruleId, int version)
        {
            var success = await _versioning.RollbackAsync(ruleId, version);
            return Json(new { success });
        }

        [HttpGet]
        public async Task<IActionResult> CompareVersions(int ruleId, int v1, int v2)
        {
            var result = await _versioning.CompareVersionsAsync(ruleId, v1, v2);
            return Json(result);
        }
    }

    public class PriorityUpdate
    {
        public int Id { get; set; }
        public int Priority { get; set; }
    }
}
