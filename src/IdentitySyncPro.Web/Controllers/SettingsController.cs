using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using IdentitySyncPro.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentitySyncPro.Web.Controllers
{
    [Microsoft.AspNetCore.Authorization.Authorize(Roles = IdentitySyncPro.Core.Models.Settings.AppUserRoles.Admin)]
    public class SettingsController : Controller
    {
        private readonly AppDbContext _db;
        private readonly IConfiguration _configuration;
        private readonly SettingsTransferService _transfer;
        private readonly ILogger<SettingsController> _logger;

        public SettingsController(AppDbContext db, IConfiguration configuration, SettingsTransferService transfer,
            ILogger<SettingsController> logger)
        {
            _db = db;
            _configuration = configuration;
            _transfer = transfer;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Index(int? editId = null)
        {
            var tenants = await _db.TenantSettings.OrderBy(t => t.TenantName).ToListAsync();
            var langSetting = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "Language");

            var vm = new SettingsViewModel
            {
                Tenants = tenants,
                CurrentLanguage = langSetting?.Value ?? "ar",
                IsEditing = editId.HasValue
            };

            if (editId.HasValue)
            {
                vm.CurrentTenant = tenants.FirstOrDefault(t => t.Id == editId.Value);
            }

            ViewBag.Lang = vm.CurrentLanguage;
            return View(vm);
        }

        [HttpGet]
        public IActionResult Create()
        {
            var tenant = new TenantSettings();
            var syncSection = _configuration.GetSection("SyncSettings");

            // Blank starter values — connections are entered here per tenant (no appsettings source).
            tenant.SourceProvider = "Oracle";
            tenant.SourcePort = 1521;
            tenant.SourceTableOrView = "V_IDENTITY_DATA";
            tenant.SourceCommandTimeout = 300;

            // Active Directory
            tenant.ADPort = 389;
            tenant.ADDefaultPassword = "ChangeMe@2026";

            // Application Database — structured fields (parse from the system connection string)
            var connStr = _configuration.GetConnectionString("DefaultConnection") ?? "";
            ParseConnectionStringToTenant(tenant, connStr);

            // Sync schedule defaults
            tenant.DefaultBatchSize = int.TryParse(syncSection["DefaultBatchSize"], out var bs) ? bs : 1000;
            tenant.FullSyncMode = "daily";
            tenant.FullSyncTime = "02:00";
            tenant.FullSyncSchedule = syncSection["FullSyncSchedule"] ?? "0 2 * * *";
            tenant.DeltaSyncMode = "interval";
            tenant.DeltaSyncIntervalMinutes = 30;
            tenant.DeltaSyncSchedule = syncSection["DeltaSyncSchedule"] ?? "*/30 * * * *";
            tenant.HealthCheckMode = "interval";
            tenant.HealthCheckIntervalMinutes = 10;
            tenant.HealthCheckSchedule = syncSection["HealthCheckSchedule"] ?? "*/10 * * * *";

            ViewBag.Lang = GetCurrentLang();
            return View("Edit", tenant);
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var tenant = await _db.TenantSettings.FindAsync(id);
            if (tenant == null) return NotFound();

            ViewBag.Lang = GetCurrentLang();
            return View(tenant);
        }

        [HttpPost]
        public async Task<IActionResult> Save(TenantSettings model)
        {
            if (string.IsNullOrWhiteSpace(model.TenantName))
            {
                return Json(new { success = false, message = "Tenant name is required" });
            }

            try
            {
                // Build cron expressions from human-friendly schedule settings
                model.FullSyncSchedule = CronBuilder.Build(
                    model.FullSyncMode, model.FullSyncTime, model.FullSyncDays,
                    model.FullSyncIntervalMinutes, model.FullSyncSchedule);

                model.DeltaSyncSchedule = CronBuilder.Build(
                    model.DeltaSyncMode, model.DeltaSyncTime, model.DeltaSyncDays,
                    model.DeltaSyncIntervalMinutes, model.DeltaSyncSchedule);

                model.HealthCheckSchedule = CronBuilder.Build(
                    model.HealthCheckMode, model.HealthCheckTime, null,
                    model.HealthCheckIntervalMinutes, model.HealthCheckSchedule);

                if (model.Id == 0)
                {
                    model.CreatedDate = DateTime.UtcNow;
                    model.ModifiedDate = DateTime.UtcNow;
                    _db.TenantSettings.Add(model);
                }
                else
                {
                    var existing = await _db.TenantSettings.FindAsync(model.Id);
                    if (existing == null) return Json(new { success = false, message = "Tenant not found" });

                    // Tenant Info
                    existing.TenantName = model.TenantName;
                    existing.Description = model.Description;
                    existing.IsActive = model.IsActive;

                    // Data Source — generic fields
                    existing.SourceProvider = model.SourceProvider;
                    existing.SourceHost = model.SourceHost;
                    existing.SourcePort = model.SourcePort;
                    existing.SourceDatabase = model.SourceDatabase;
                    existing.SourceUsername = model.SourceUsername;
                    existing.SourceIntegratedSecurity = model.SourceIntegratedSecurity;
                    // 🔐 Password fields: only overwrite when a new value is supplied (blank = keep existing)
                    if (!string.IsNullOrEmpty(model.SourcePassword)) existing.SourcePassword = model.SourcePassword;
                    existing.SourceTableOrView = model.SourceTableOrView;
                    existing.SourceCommandTimeout = model.SourceCommandTimeout;

                    // Dynamic source schema — key/status/phone/name columns per tenant
                    existing.SourceKeyColumn = model.SourceKeyColumn;
                    existing.SourceStatusColumn = model.SourceStatusColumn;
                    existing.SourceStatusDescColumn = model.SourceStatusDescColumn;
                    existing.SourcePhoneColumn = model.SourcePhoneColumn;
                    existing.SourceDisplayNameColumn = model.SourceDisplayNameColumn;

                    // Target selection. An unknown value is refused rather than stored: a
                    // tenant carrying a provider nothing can build would fail at the next sync
                    // with an error about the connector rather than about the setting.
                    if (!TargetProviders.IsKnown(model.TargetProvider))
                    {
                        TempData["Error"] = $"نوع هدف غير معروف '{model.TargetProvider}' / Unknown target provider.";
                        return RedirectToAction(nameof(Edit), new { id = model.Id });
                    }
                    existing.TargetProvider = TargetProviders.Normalise(model.TargetProvider);
                    existing.ScimBaseUrl = model.ScimBaseUrl?.Trim();
                    // Blank means "keep the stored one", the same rule every other secret here
                    // follows — the field is never populated on the way out.
                    if (!string.IsNullOrEmpty(model.ScimBearerToken)) existing.ScimBearerToken = model.ScimBearerToken;
                    existing.ScimAllowUntrustedCertificate = model.ScimAllowUntrustedCertificate;
                    existing.ScimTimeoutSeconds = model.ScimTimeoutSeconds <= 0 ? 30 : model.ScimTimeoutSeconds;

                    // Active Directory
                    existing.ADServer = model.ADServer;
                    existing.ADPort = model.ADPort;
                    // The explicit channel mode replaces the old SSL switch; ADUseSsl is kept in
                    // sync so anything still reading the legacy flag stays coherent.
                    existing.ADSecurityMode = model.ADSecurityMode;
                    existing.ADSecurityModeSet = true;
                    existing.ADAllowUntrustedCertificate = model.ADAllowUntrustedCertificate;
                    existing.ADUseSsl = model.ADSecurityMode == LdapSecurityMode.Ldaps;
                    existing.ADUsername = model.ADUsername;
                    if (!string.IsNullOrEmpty(model.ADPassword)) existing.ADPassword = model.ADPassword;
                    existing.ADBaseDN = model.ADBaseDN;
                    if (!string.IsNullOrEmpty(model.ADDefaultPassword)) existing.ADDefaultPassword = model.ADDefaultPassword;

                    // Provisioning policy — empty mode reads as "Always" downstream, preserving
                    // the behaviour of every tenant configured before this setting existed.
                    existing.AccountCreationMode = string.IsNullOrWhiteSpace(model.AccountCreationMode)
                        ? null : model.AccountCreationMode.Trim();
                    existing.AccountCreationConditionField = string.IsNullOrWhiteSpace(model.AccountCreationConditionField)
                        ? null : model.AccountCreationConditionField.Trim();
                    existing.AccountCreationConditionOperator = string.IsNullOrWhiteSpace(model.AccountCreationConditionOperator)
                        ? null : model.AccountCreationConditionOperator.Trim();
                    existing.AccountCreationConditionValue = string.IsNullOrWhiteSpace(model.AccountCreationConditionValue)
                        ? null : model.AccountCreationConditionValue.Trim();

                    // Account matching — empty ADMatchAttribute keeps matching by sAMAccountName,
                    // which is what every tenant configured before this existed does.
                    existing.ADMatchAttribute = string.IsNullOrWhiteSpace(model.ADMatchAttribute)
                        ? null : model.ADMatchAttribute.Trim();
                    existing.ADMatchSourceColumn = string.IsNullOrWhiteSpace(model.ADMatchSourceColumn)
                        ? null : model.ADMatchSourceColumn.Trim();

                    existing.UsernameCollisionFormat = string.IsNullOrWhiteSpace(model.UsernameCollisionFormat)
                        ? null : model.UsernameCollisionFormat.Trim();

                    // Clamped rather than trusted: a start of 0 would try the undiscriminated name
                    // twice, and an attempt count of 0 would make every collision a hard failure.
                    existing.UsernameCollisionStart = Math.Clamp(model.UsernameCollisionStart, 1, 999);
                    existing.UsernameCollisionMaxAttempts = Math.Clamp(model.UsernameCollisionMaxAttempts, 1, 200);

                    // Application Database fields are deliberately NOT bound. The system's own
                    // database comes from ConnectionStrings:DefaultConnection at startup and is
                    // not per-tenant; the editor for these fields was removed because saving them
                    // changed nothing. Left unbound rather than deleted so an existing row keeps
                    // whatever it holds instead of being silently blanked on the next save.

                    // Sync Settings
                    existing.DefaultBatchSize = model.DefaultBatchSize;
                    existing.FullSyncMode = model.FullSyncMode;
                    existing.FullSyncTime = model.FullSyncTime;
                    existing.FullSyncDays = model.FullSyncDays;
                    existing.FullSyncIntervalMinutes = model.FullSyncIntervalMinutes;
                    existing.FullSyncSchedule = model.FullSyncSchedule;
                    existing.DeltaSyncMode = model.DeltaSyncMode;
                    existing.DeltaSyncTime = model.DeltaSyncTime;
                    existing.DeltaSyncDays = model.DeltaSyncDays;
                    existing.DeltaSyncIntervalMinutes = model.DeltaSyncIntervalMinutes;
                    existing.DeltaSyncSchedule = model.DeltaSyncSchedule;
                    existing.HealthCheckMode = model.HealthCheckMode;
                    existing.HealthCheckTime = model.HealthCheckTime;
                    existing.HealthCheckIntervalMinutes = model.HealthCheckIntervalMinutes;
                    existing.HealthCheckSchedule = model.HealthCheckSchedule;
                    existing.EnableAutoSync = model.EnableAutoSync;
                    existing.EnableLifecycleDuringSync = model.EnableLifecycleDuringSync;

                    // EnableFullSyncSchedule / EnableDeltaSyncSchedule are deliberately NOT written
                    // here. This form does not render them — the per-type toggles own them — and a
                    // save that overwrites a switch it never showed would silently undo a suspension
                    // the operator made elsewhere. If they are ever added to this form, assign them
                    // here at the same time.

                    // SMS Settings
                    existing.EnableSmsNotification = model.EnableSmsNotification;
                    existing.SmsProviderId = model.SmsProviderId;
                    // Legacy fallback fields — only overwrite if new value is non-empty
                    if (!string.IsNullOrEmpty(model.SmsApiUrl)) existing.SmsApiUrl = model.SmsApiUrl;
                    if (!string.IsNullOrEmpty(model.SmsSenderName)) existing.SmsSenderName = model.SmsSenderName;
                    if (!string.IsNullOrEmpty(model.SmsApiUsername)) existing.SmsApiUsername = model.SmsApiUsername;
                    if (!string.IsNullOrEmpty(model.SmsApiPassword)) existing.SmsApiPassword = model.SmsApiPassword;
                    existing.SmsMessageTemplate = model.SmsMessageTemplate;

                    // Global Default Value for Empty Fields
                    existing.UseGlobalDefaultForEmptyFields = model.UseGlobalDefaultForEmptyFields;
                    existing.GlobalDefaultValue = string.IsNullOrEmpty(model.GlobalDefaultValue) ? "." : model.GlobalDefaultValue;

                    existing.ModifiedDate = DateTime.UtcNow;
                }

                await _db.SaveChangesAsync();

                // Re-register per-tenant recurring jobs to reflect the new schedules.
                // The logger is passed deliberately: this is where schedules actually change, so it
                // is the one place that must say what the refresh concluded. It used to be the only
                // call site that passed none, which is how a saved schedule could register nothing
                // and leave no trace of having done so.
                try { TenantSyncScheduler.RefreshTenantJobs(_db, _logger); }
                catch (Exception schedEx) { _logger.LogWarning(schedEx, "Schedule refresh failed after saving tenant {TenantId}", model.Id); }

                return Json(new { success = true, message = "Settings saved successfully" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            var tenant = await _db.TenantSettings.FindAsync(id);
            if (tenant == null) return Json(new { success = false, message = "Tenant not found" });

            _db.TenantSettings.Remove(tenant);
            await _db.SaveChangesAsync();
            try { TenantSyncScheduler.RefreshTenantJobs(_db, _logger); }
            catch (Exception schedEx) { _logger.LogWarning(schedEx, "Schedule refresh failed"); }
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> ToggleActive(int id)
        {
            var tenant = await _db.TenantSettings.FindAsync(id);
            if (tenant == null) return Json(new { success = false });

            tenant.IsActive = !tenant.IsActive;
            tenant.ModifiedDate = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            try { TenantSyncScheduler.RefreshTenantJobs(_db, _logger); }
            catch (Exception schedEx) { _logger.LogWarning(schedEx, "Schedule refresh failed"); }
            return Json(new { success = true, isActive = tenant.IsActive });
        }

        /// <summary>
        /// Suspends or resumes a tenant's scheduled syncs in one click, without opening the settings
        /// form. It flips EnableAutoSync — the same switch the form writes — rather than adding a
        /// second concept beside it.
        ///
        /// Manual syncs are unaffected. Only the recurring jobs are governed here.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> ToggleAutoSync(int id)
        {
            var tenant = await _db.TenantSettings.FindAsync(id);
            if (tenant == null) return Json(new { success = false, message = "Tenant not found" });

            tenant.EnableAutoSync = !tenant.EnableAutoSync;
            tenant.ModifiedDate = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            // RefreshTenantJobs states the outcome either way — including the warning when a saved
            // schedule now registers nothing. That warning is the whole point of routing through it
            // rather than adding or removing the job here.
            try { TenantSyncScheduler.RefreshTenantJobs(_db, _logger); }
            catch (Exception schedEx) { _logger.LogWarning(schedEx, "Schedule refresh failed"); }

            _logger.LogInformation(
                "Tenant '{Tenant}' (id {Id}) scheduled sync {State} by {User}",
                tenant.TenantName, tenant.Id, tenant.EnableAutoSync ? "RESUMED" : "SUSPENDED",
                User.Identity?.Name ?? "unknown");

            return Json(new
            {
                success = true,
                enableAutoSync = tenant.EnableAutoSync,
                scheduled = tenant.IsActive && tenant.EnableAutoSync
                            && !string.IsNullOrWhiteSpace(tenant.FullSyncSchedule)
            });
        }

        /// <summary>
        /// Suspends or resumes one sync type for a tenant, leaving the other alone. A delta every
        /// half hour and a full pass every few hours answer different needs, and one switch for both
        /// meant pausing the noisy one also stopped the thorough one.
        /// </summary>
        /// <param name="type">"full" or "delta".</param>
        [HttpPost]
        public async Task<IActionResult> ToggleSyncType(int id, string type)
        {
            var tenant = await _db.TenantSettings.FindAsync(id);
            if (tenant == null) return Json(new { success = false, message = "Tenant not found" });

            bool isFull;
            switch ((type ?? "").Trim().ToLowerInvariant())
            {
                case "full": isFull = true; break;
                case "delta": isFull = false; break;
                default:
                    // Named rather than silently defaulted: guessing here would suspend the wrong
                    // schedule and report success.
                    return Json(new { success = false, message = $"Unknown sync type '{type}' — expected 'full' or 'delta'" });
            }

            if (isFull) tenant.EnableFullSyncSchedule = !tenant.EnableFullSyncSchedule;
            else tenant.EnableDeltaSyncSchedule = !tenant.EnableDeltaSyncSchedule;

            tenant.ModifiedDate = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            try { TenantSyncScheduler.RefreshTenantJobs(_db, _logger); }
            catch (Exception schedEx) { _logger.LogWarning(schedEx, "Schedule refresh failed"); }

            var nowOn = isFull ? tenant.EnableFullSyncSchedule : tenant.EnableDeltaSyncSchedule;
            _logger.LogInformation(
                "Tenant '{Tenant}' (id {Id}) {Type} sync schedule {State} by {User}",
                tenant.TenantName, tenant.Id, isFull ? "FULL" : "DELTA",
                nowOn ? "RESUMED" : "SUSPENDED", User.Identity?.Name ?? "unknown");

            return Json(new
            {
                success = true,
                type = isFull ? "full" : "delta",
                enabled = nowOn,
                // Whether a job actually exists now — the master switch and the cron still apply.
                scheduled = tenant.IsActive && tenant.EnableAutoSync && nowOn
                            && !string.IsNullOrWhiteSpace(isFull ? tenant.FullSyncSchedule : tenant.DeltaSyncSchedule),
                masterOn = tenant.EnableAutoSync
            });
        }

        [HttpPost]
        public async Task<IActionResult> SetLanguage(string lang)
        {
            var setting = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "Language");
            if (setting != null)
            {
                setting.Value = lang;
                setting.ModifiedDate = DateTime.UtcNow;
            }
            else
            {
                _db.AppSettings.Add(new AppSettings { Key = "Language", Value = lang });
            }
            await _db.SaveChangesAsync();
            return Json(new { success = true });
        }

        private string GetCurrentLang()
        {
            var setting = _db.AppSettings.FirstOrDefault(s => s.Key == "Language");
            return setting?.Value ?? "ar";
        }

        // ══════════════════════════════════════
        // DATA RETENTION SETTINGS
        // ══════════════════════════════════════

        [HttpGet]
        public async Task<IActionResult> GetRetentionSettings()
        {
            var keys = new[] {
                "Retention.SyncOperationsDays",
                "Retention.SyncRunsDays",
                "Retention.AuditEntriesDays",
                "Retention.DeadLetterDays",
                "Retention.QuarantineDays"
            };

            var settings = await _db.AppSettings
                .Where(s => keys.Contains(s.Key))
                .ToDictionaryAsync(s => s.Key, s => s.Value);

            return Json(new
            {
                syncOperationsDays = GetSetting(settings, "Retention.SyncOperationsDays", 90),
                syncRunsDays = GetSetting(settings, "Retention.SyncRunsDays", 180),
                auditEntriesDays = GetSetting(settings, "Retention.AuditEntriesDays", 365),
                deadLetterDays = GetSetting(settings, "Retention.DeadLetterDays", 30),
                quarantineDays = GetSetting(settings, "Retention.QuarantineDays", 60)
            });
        }

        [HttpPost]
        public async Task<IActionResult> SaveRetentionSettings([FromBody] RetentionSettingsDto dto)
        {
            try
            {
                await UpsertAppSetting("Retention.SyncOperationsDays", dto.SyncOperationsDays.ToString());
                await UpsertAppSetting("Retention.SyncRunsDays", dto.SyncRunsDays.ToString());
                await UpsertAppSetting("Retention.AuditEntriesDays", dto.AuditEntriesDays.ToString());
                await UpsertAppSetting("Retention.DeadLetterDays", dto.DeadLetterDays.ToString());
                await UpsertAppSetting("Retention.QuarantineDays", dto.QuarantineDays.ToString());
                await _db.SaveChangesAsync();
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        private async Task UpsertAppSetting(string key, string value)
        {
            var setting = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
            if (setting != null)
            {
                setting.Value = value;
                setting.ModifiedDate = DateTime.UtcNow;
            }
            else
            {
                _db.AppSettings.Add(new AppSettings { Key = key, Value = value });
            }
        }

        private static int GetSetting(Dictionary<string, string?> settings, string key, int defaultValue)
        {
            return settings.TryGetValue(key, out var val) && int.TryParse(val, out var num) ? num : defaultValue;
        }

        // ══════════════════════════════════════
        // MAPPING PAGE & APIs
        // ══════════════════════════════════════

        [HttpGet]
        public async Task<IActionResult> Mapping(int id)
        {
            var tenant = await _db.TenantSettings
                .Include(t => t.AttributeMappings.OrderBy(m => m.SortOrder))
                .Include(t => t.GroupRules)
                .Include(t => t.OURules.OrderBy(o => o.Priority))
                .FirstOrDefaultAsync(t => t.Id == id);

            if (tenant == null) return NotFound();
            ViewBag.Lang = GetCurrentLang();
            return View(tenant);
        }

        [HttpPost]
        public async Task<IActionResult> SaveMappings(int tenantId, [FromBody] List<TenantAttributeMapping> mappings)
        {
            try
            {
                var existing = await _db.TenantAttributeMappings.Where(m => m.TenantId == tenantId).ToListAsync();
                _db.TenantAttributeMappings.RemoveRange(existing);

                foreach (var m in mappings)
                {
                    m.Id = 0;
                    m.TenantId = tenantId;
                    _db.TenantAttributeMappings.Add(m);
                }

                await _db.SaveChangesAsync();
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> SaveGroups(int tenantId, [FromBody] List<TenantGroupRule> groups)
        {
            try
            {
                var existing = await _db.TenantGroupRules.Where(g => g.TenantId == tenantId).ToListAsync();
                _db.TenantGroupRules.RemoveRange(existing);

                foreach (var g in groups)
                {
                    g.Id = 0;
                    g.TenantId = tenantId;
                    _db.TenantGroupRules.Add(g);
                }

                await _db.SaveChangesAsync();
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> SaveOURules(int tenantId, [FromBody] List<TenantOURule> rules)
        {
            try
            {
                // Reject rules that would fail silently at sync time — malformed ValueMappings
                // JSON, or placeholders naming no real source column (they resolve to the literal
                // "DEFAULT" and every account creation into that OU fails).
                // Mapped columns alone are not the full picture: a column can be used purely as
                // a rule condition and never mapped to an AD attribute (a real tenant drives its
                // OU rules off CITY_NO while mapping CITY_DESC). Fold in condition fields too,
                // including the ones being saved right now.
                var knownColumns = (await _db.TenantAttributeMappings
                        .Where(m => m.TenantId == tenantId).Select(m => m.SourceColumn).ToListAsync())
                    .Concat(await _db.TenantGroupRules
                        .Where(g => g.TenantId == tenantId).Select(g => g.ConditionField).ToListAsync())
                    .Concat(await _db.LifecycleRules
                        .Where(l => l.TenantId == tenantId).Select(l => l.ConditionField).ToListAsync())
                    .Concat(rules.Select(r => r.ConditionField))
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => c!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // The coverage check needs data, so it runs here against the tenant's own staged
                // identities: a map that knows CITY_NO 14 but not 20 is correct-looking JSON over
                // real columns, and nothing else in this method can see the gap. It is returned as
                // a warning — the save still goes through.
                var (validationErrors, warnings) = await OuRulePrecheck.ValidateAsync(
                    _db, tenantId, rules, knownColumns.Count > 0 ? knownColumns : null);

                if (validationErrors.Count > 0)
                    return Json(new { success = false, message = string.Join(" | ", validationErrors) });

                var existing = await _db.TenantOURules.Where(o => o.TenantId == tenantId).ToListAsync();
                _db.TenantOURules.RemoveRange(existing);

                foreach (var r in rules)
                {
                    r.Id = 0;
                    r.TenantId = tenantId;
                    _db.TenantOURules.Add(r);
                }

                await _db.SaveChangesAsync();
                return Json(new { success = true, warning = warnings.Count > 0 ? string.Join(" | ", warnings) : null });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ═══════════════════════════════════════
        // SETTINGS EXPORT / IMPORT (rules only — no credentials)
        // ═══════════════════════════════════════

        [HttpGet]
        public async Task<IActionResult> ExportSettings(int tenantId)
        {
            var tenant = await _db.TenantSettings.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId);
            if (tenant == null) return NotFound();

            var bytes = await _transfer.ExportAsync(tenantId);
            var safeName = string.Concat(tenant.TenantName.Select(c =>
                Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Replace(' ', '_');

            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"Settings_{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
        }

        /// <summary>
        /// Validates an uploaded workbook and returns what WOULD change. Writes nothing —
        /// applying is a separate, explicit call.
        /// </summary>
        [HttpPost]
        [RequestSizeLimit(10 * 1024 * 1024)]
        public async Task<IActionResult> PreviewSettingsImport(int tenantId, IFormFile? file)
        {
            if (file == null || file.Length == 0)
                return Json(new { success = false, message = "لم يتم اختيار ملف / No file selected" });

            if (!await _db.TenantSettings.AnyAsync(t => t.Id == tenantId))
                return Json(new { success = false, message = "جهة غير معروفة / Unknown tenant" });

            await using var stream = file.OpenReadStream();
            var (preview, parsed) = await _transfer.PreviewAsync(stream, tenantId);

            return Json(new
            {
                success = true,
                canApply = preview.CanApply,
                errors = preview.Errors,
                warnings = preview.Warnings,
                sections = preview.Sections.Select(s => new
                {
                    name = s.Name,
                    present = s.Present,
                    incoming = s.IncomingRows,
                    existing = s.ExistingRows,
                    sample = s.Sample
                })
            });
        }

        /// <summary>
        /// Applies an import. The file is re-uploaded and re-validated here rather than trusting
        /// anything carried over from the preview call — the client must not be able to apply
        /// rows that were never validated on the server.
        /// </summary>
        [HttpPost]
        [RequestSizeLimit(10 * 1024 * 1024)]
        public async Task<IActionResult> ApplySettingsImport(int tenantId, IFormFile? file)
        {
            if (file == null || file.Length == 0)
                return Json(new { success = false, message = "لم يتم اختيار ملف / No file selected" });

            if (!await _db.TenantSettings.AnyAsync(t => t.Id == tenantId))
                return Json(new { success = false, message = "جهة غير معروفة / Unknown tenant" });

            await using var stream = file.OpenReadStream();
            var (preview, parsed) = await _transfer.PreviewAsync(stream, tenantId);

            if (!preview.CanApply || parsed == null)
                return Json(new { success = false, message = "الملف لم يجتز التحقق / Validation failed", errors = preview.Errors });

            try
            {
                var count = await _transfer.ApplyAsync(parsed, tenantId);
                return Json(new
                {
                    success = true,
                    count,
                    message = $"تم استيراد {count} صفاً. ⚠️ لتطبيق التغييرات على الحسابات القائمة: فرّغ البصمات ثم شغّل مزامنة كاملة."
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> FetchSourceColumns([FromBody] SourceConnectionRequest req)
        {
            try
            {
                // 🔐 The password is no longer sent from the browser. Load the stored (decrypted) value by tenant.
                if (string.IsNullOrEmpty(req.Password) && req.TenantId > 0)
                {
                    var tenant = await _db.TenantSettings.FindAsync(req.TenantId);
                    if (tenant != null) req.Password = tenant.SourcePassword ?? "";
                }

                // An object name cannot be a parameter, so this statement is built as text — which
                // made req.TableOrView a direct injection point. Validated against a strict
                // identifier whitelist before it goes anywhere near the command.
                if (!IdentitySyncPro.Core.Helpers.SqlIdentifierGuard.IsValidObjectName(req.TableOrView))
                {
                    _logger.LogWarning(
                        "FetchSourceColumns: rejected table/view name {Name} from {User} — not a plain identifier",
                        req.TableOrView, User.Identity?.Name ?? "unknown");

                    return Json(new
                    {
                        success = false,
                        message = "اسم الجدول أو العرض غير صالح — يُقبل حرف أو شرطة سفلية ثم حروف وأرقام، مع مخطط اختياري / " +
                                  "Invalid table or view name — letters, digits and underscores only, optionally schema-qualified"
                    });
                }

                var columns = new List<string>();
                var connStr = BuildSourceConnStr(req);

                // CA3001 still reports these two lines, and it is right that the value originates in
                // the request — its taint analysis has no way to recognise SqlIdentifierGuard as a
                // sanitiser, so the flow from req to CommandText looks unbroken to it.
                //
                // What breaks it: the name has already been checked against a strict identifier
                // whitelist above, and the quoting helpers throw rather than clean, so no value
                // reaching here can contain a bracket, quote, semicolon, comment marker or space.
                // SqlIdentifierGuardTests covers the injection strings that used to work.
                //
                // Suppressed narrowly, at the two statements, so any NEW tainted SQL elsewhere in
                // this file is still reported.
                if (req.Provider == "SqlServer")
                {
                    var obj = IdentitySyncPro.Core.Helpers.SqlIdentifierGuard.QuoteSqlServer(req.TableOrView);
                    using var conn = new Microsoft.Data.SqlClient.SqlConnection(connStr);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
#pragma warning disable CA3001 // object name validated by SqlIdentifierGuard; see note above
                    cmd.CommandText = $"SELECT TOP 0 * FROM {obj}";
#pragma warning restore CA3001
                    using var reader = cmd.ExecuteReader();
                    for (int i = 0; i < reader.FieldCount; i++)
                        columns.Add(reader.GetName(i));
                }
                else if (req.Provider == "Oracle")
                {
                    var obj = IdentitySyncPro.Core.Helpers.SqlIdentifierGuard.ForOracle(req.TableOrView);
                    using var conn = new Oracle.ManagedDataAccess.Client.OracleConnection(connStr);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
#pragma warning disable CA3001 // object name validated by SqlIdentifierGuard; see note above
                    cmd.CommandText = $"SELECT * FROM {obj} WHERE ROWNUM = 0";
#pragma warning restore CA3001
                    using var reader = cmd.ExecuteReader();
                    for (int i = 0; i < reader.FieldCount; i++)
                        columns.Add(reader.GetName(i));
                }
                else
                {
                    return Json(new { success = true, columns = new List<string>(), message = $"{req.Provider} column discovery is not yet supported." });
                }

                return Json(new { success = true, columns });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Pre-populates default attribute mappings, group rules, and OU rules for a tenant.
        /// Based on the standard V_IDENTITY_DATA Oracle view columns.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> LoadDefaultMappings(int tenantId)
        {
            try
            {
                var tenant = await _db.TenantSettings.FindAsync(tenantId);
                if (tenant == null) return Json(new { success = false, message = "Tenant not found" });

                // ═══ Clear existing mappings ═══
                var existingMappings = await _db.TenantAttributeMappings.Where(m => m.TenantId == tenantId).ToListAsync();
                var existingGroups = await _db.TenantGroupRules.Where(g => g.TenantId == tenantId).ToListAsync();
                var existingOU = await _db.TenantOURules.Where(o => o.TenantId == tenantId).ToListAsync();
                _db.TenantAttributeMappings.RemoveRange(existingMappings);
                _db.TenantGroupRules.RemoveRange(existingGroups);
                _db.TenantOURules.RemoveRange(existingOU);

                // ═══ Default Attribute Mappings ═══
                var mappings = new List<TenantAttributeMapping>
                {
                    // === Core Identity ===
                    new() { TenantId = tenantId, SourceColumn = "IDENTITY_ID", TargetAttribute = "sAMAccountName", IsRequired = true, IsIdentifier = true, SortOrder = 0 },
                    new() { TenantId = tenantId, SourceColumn = "IDENTITY_ID", TargetAttribute = "employeeID", IsRequired = true, SortOrder = 1 },
                    new() { TenantId = tenantId, SourceColumn = "FIRST_NAME", TargetAttribute = "givenName", IsRequired = true, SortOrder = 2 },
                    new() { TenantId = tenantId, SourceColumn = "LAST_NAME", TargetAttribute = "sn", IsRequired = true, SortOrder = 3 },
                    new() { TenantId = tenantId, SourceColumn = "MIDDLE_NAME", TargetAttribute = "initials", Transform = "GetInitials", SortOrder = 4 },
                    new() { TenantId = tenantId, SourceColumn = "FIRST_NAME", TargetAttribute = "displayName", Transform = "Concat:{FIRST_NAME} {MIDDLE_NAME} {LAST_NAME}", SortOrder = 5 },
                    new() { TenantId = tenantId, SourceColumn = "DISPLAY_NAME", TargetAttribute = "description", SortOrder = 6 },

                    // === Email & Proxy (عدّل النطاق ليطابق نطاق جهتك) ===
                    new() { TenantId = tenantId, SourceColumn = "IDENTITY_ID", TargetAttribute = "mail", Transform = "Format:{0}@example.com", SortOrder = 7 },
                    new() { TenantId = tenantId, SourceColumn = "IDENTITY_ID", TargetAttribute = "userPrincipalName", Transform = "Format:{0}@example.com", SortOrder = 8 },
                    new() { TenantId = tenantId, SourceColumn = "IDENTITY_ID", TargetAttribute = "mailNickname", SortOrder = 9 },
                    new() { TenantId = tenantId, SourceColumn = "IDENTITY_ID", TargetAttribute = "proxyAddresses", Transform = "Format:SMTP:{0}@example.com", SortOrder = 10 },
                    new() { TenantId = tenantId, SourceColumn = "IDENTITY_ID", TargetAttribute = "proxyAddresses", Transform = "Format:smtp:{0}@example.mail.onmicrosoft.com", SortOrder = 11 },
                    new() { TenantId = tenantId, SourceColumn = "IDENTITY_ID", TargetAttribute = "targetAddress", Transform = "Format:SMTP:{0}@example.mail.onmicrosoft.com", SortOrder = 12 },

                    // === Contact & Department ===
                    new() { TenantId = tenantId, SourceColumn = "MOBILE_PHONE", TargetAttribute = "mobile", SortOrder = 12 },
                    new() { TenantId = tenantId, SourceColumn = "MOBILE_PHONE", TargetAttribute = "telephoneNumber", SortOrder = 13 },
                    new() { TenantId = tenantId, SourceColumn = "DEPARTMENT", TargetAttribute = "department", SortOrder = 14 },
                    new() { TenantId = tenantId, SourceColumn = "JOB_TITLE", TargetAttribute = "title", SortOrder = 15 },

                    // === Location & Nationality ===
                    new() { TenantId = tenantId, SourceColumn = "NATIONALITY", TargetAttribute = "co", SortOrder = 16 },
                    new() { TenantId = tenantId, SourceColumn = "LOCATION_DESC", TargetAttribute = "l", SortOrder = 17 },

                    // === Extension Attributes (matching PowerShell script) ===
                    new() { TenantId = tenantId, SourceColumn = "NATIONAL_ID", TargetAttribute = "extensionAttribute1", SortOrder = 20 },
                    new() { TenantId = tenantId, SourceColumn = "IDENTITY_ID", TargetAttribute = "extensionAttribute2", SortOrder = 21 },
                    new() { TenantId = tenantId, SourceColumn = "IDENTITY_ID", TargetAttribute = "extensionAttribute3", SortOrder = 22 },
                    new() { TenantId = tenantId, SourceColumn = "STATUS_DESC", TargetAttribute = "extensionAttribute4", SortOrder = 23 },
                    new() { TenantId = tenantId, SourceColumn = "CATEGORY_DESC", TargetAttribute = "extensionAttribute5", SortOrder = 24 },
                    new() { TenantId = tenantId, SourceColumn = "IDENTITY_ID", TargetAttribute = "extensionAttribute6", Transform = "Static:User", SortOrder = 25 },
                    new() { TenantId = tenantId, SourceColumn = "NATIONALITY", TargetAttribute = "extensionAttribute11", SortOrder = 26 },
                    new() { TenantId = tenantId, SourceColumn = "MOBILE_PHONE", TargetAttribute = "extensionAttribute13", SortOrder = 27 },
                    new() { TenantId = tenantId, SourceColumn = "MOBILE_PHONE", TargetAttribute = "extensionAttribute14", SortOrder = 28 },
                    new() { TenantId = tenantId, SourceColumn = "JOB_TITLE", TargetAttribute = "extensionAttribute15", SortOrder = 29 },

                    // === Standard HR ===
                    new() { TenantId = tenantId, SourceColumn = "NATIONAL_ID", TargetAttribute = "employeeNumber", SortOrder = 30 },
                    new() { TenantId = tenantId, SourceColumn = "STATUS_DESC", TargetAttribute = "employeeType", SortOrder = 31 },
                    new() { TenantId = tenantId, SourceColumn = "CATEGORY_DESC", TargetAttribute = "company", SortOrder = 32 },
                };
                _db.TenantAttributeMappings.AddRange(mappings);

                // ═══ Default Group Rules ═══
                var groups = new List<TenantGroupRule>
                {
                    new() { TenantId = tenantId, GroupName = "All-Users-Group", IsDefault = true, Description = "جميع المستخدمين / All Users" },
                    new() { TenantId = tenantId, GroupName = "Site1-Users-Group", ConditionField = "LOCATION_CODE", ConditionOperator = "==", ConditionValue = "1", Description = "مثال: مجموعة حسب الموقع / Example: site-based group" },
                };
                _db.TenantGroupRules.AddRange(groups);

                // ═══ Default OU Rules ═══
                var ouRules = new List<TenantOURule>
                {
                    new() { TenantId = tenantId, OUTemplate = "OU=Users,{BaseDN}", Priority = 1, Description = "القاعدة الافتراضية / Default Rule — يمكن استخدام قوالب مثل OU={DEPARTMENT},OU={LOCATION},{BaseDN}" },
                };
                _db.TenantOURules.AddRange(ouRules);

                await _db.SaveChangesAsync();

                return Json(new { success = true, mappingCount = mappings.Count, groupCount = groups.Count, ouCount = ouRules.Count });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        private static string BuildSourceConnStr(SourceConnectionRequest req)
        {
            return req.Provider switch
            {
                "SqlServer" => $"Server={req.Host},{req.Port};Database={req.Database};User Id={req.Username};Password={req.Password};TrustServerCertificate=True;",
                "Oracle" => $"Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={req.Host})(PORT={req.Port}))(CONNECT_DATA=(SERVICE_NAME={req.Database})));User Id={req.Username};Password={req.Password};",
                "PostgreSQL" => $"Host={req.Host};Port={req.Port};Database={req.Database};Username={req.Username};Password={req.Password};",
                "MySQL" => $"Server={req.Host};Port={req.Port};Database={req.Database};Uid={req.Username};Pwd={req.Password};",
                _ => ""
            };
        }

        /// <summary>
        /// Best-effort parse of a SQL Server connection string into structured fields.
        /// </summary>
        private static void ParseConnectionStringToTenant(TenantSettings tenant, string connStr)
        {
            tenant.DatabaseProvider = "SqlServer";
            tenant.DbIntegratedSecurity = true;
            tenant.DbTrustServerCertificate = true;

            if (string.IsNullOrEmpty(connStr)) return;

            var parts = connStr.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2) continue;

                var key = kv[0].Trim().ToLower();
                var value = kv[1].Trim();

                switch (key)
                {
                    case "server":
                    case "data source":
                        if (value.Contains(','))
                        {
                            var serverParts = value.Split(',');
                            tenant.DbHost = serverParts[0];
                            if (int.TryParse(serverParts[1], out var p)) tenant.DbPort = p;
                        }
                        else
                        {
                            tenant.DbHost = value;
                        }
                        break;
                    case "database":
                    case "initial catalog":
                        tenant.DbName = value;
                        break;
                    case "user id":
                    case "uid":
                        tenant.DbUsername = value;
                        tenant.DbIntegratedSecurity = false;
                        break;
                    case "password":
                    case "pwd":
                        tenant.DbPassword = value;
                        break;
                    case "integrated security":
                        tenant.DbIntegratedSecurity = value.Equals("true", StringComparison.OrdinalIgnoreCase)
                                                      || value.Equals("sspi", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "trustservercertificate":
                        tenant.DbTrustServerCertificate = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                }
            }
        }
    }

    /// <summary>DTO for FetchSourceColumns request</summary>
    public class SourceConnectionRequest
    {
        /// <summary>When set, the server loads the stored source password for this tenant instead of trusting the client.</summary>
        public int TenantId { get; set; }
        public string Provider { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string Database { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string TableOrView { get; set; } = "";
    }

    /// <summary>DTO for Data Retention settings</summary>
    public class RetentionSettingsDto
    {
        public int SyncOperationsDays { get; set; } = 90;
        public int SyncRunsDays { get; set; } = 180;
        public int AuditEntriesDays { get; set; } = 365;
        public int DeadLetterDays { get; set; } = 30;
        public int QuarantineDays { get; set; } = 60;
    }
}
