using IdentitySyncPro.Core.Models.Audit;
using Hangfire;
using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Models.Services;
using IdentitySyncPro.Infrastructure.Connectors;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Jobs;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentitySyncPro.Web.Controllers
{
    /// <summary>
    /// Controller for the Services module — completely independent from IAM.
    /// Manages DB-to-AD sync services with mapping, scheduling, and audit logs.
    /// </summary>
    [Microsoft.AspNetCore.Authorization.Authorize(Roles = IdentitySyncPro.Core.Models.Settings.AppUserRoles.AdminOrOperator)]
    public class ServicesController : Controller
    {
        private readonly ServicesDbContext _db;
        private readonly SvcDatabaseReader _dbReader;
        private readonly SvcSyncExecutor _executor;
        private readonly ILogger<ServicesController> _logger;
        private readonly ExcelExportService _excelExport;

        public ServicesController(
            ServicesDbContext db,
            SvcDatabaseReader dbReader,
            SvcSyncExecutor executor,
            ILogger<ServicesController> logger,
            ExcelExportService excelExport)
        {
            _db = db;
            _dbReader = dbReader;
            _executor = executor;
            _logger = logger;
            _excelExport = excelExport;
        }

        /// <summary>Set by LanguageFilter; decides the header language of exported workbooks.</summary>
        private bool IsArabic => (HttpContext?.Items["Lang"] as string ?? "ar") == "ar";

        // ══════════════════════════════════════
        // INDEX — List all services
        // ══════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var services = await _db.SvcServices
                .Include(s => s.FieldMappings)
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync();

            return View(services);
        }

        // ══════════════════════════════════════
        // CREATE
        // ══════════════════════════════════════
        [HttpGet]
        public IActionResult Create()
        {
            return View(new SvcService());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(SvcService model)
        {
            if (string.IsNullOrWhiteSpace(model.Name))
            {
                ModelState.AddModelError("Name", "اسم الخدمة مطلوب / Service name is required");
                return View(model);
            }

            NormalizeRequiredStrings(model);

            // The form always posts an explicit channel mode; mark it chosen so the legacy
            // ADUseSsl fallback no longer applies, and keep that flag coherent.
            model.ADSecurityModeSet = true;
            model.ADUseSsl = model.ADSecurityMode == LdapSecurityMode.Ldaps;

            model.CreatedAt = DateTime.UtcNow;
            model.UpdatedAt = DateTime.UtcNow;

            // Build cron expression from schedule settings. A custom expression that cannot be used
            // now refuses the save instead of silently becoming the daily default — the form is
            // returned with the reason, which is the only moment the author can still fix it.
            try
            {
                model.ScheduleCron = BuildCron(model);
            }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError("ScheduleCustomCron", ex.Message);
                return View(model);
            }

            _db.SvcServices.Add(model);
            await _db.SaveChangesAsync();

            // Register Hangfire recurring job if enabled
            if (model.IsEnabled && !string.IsNullOrEmpty(model.ScheduleCron))
            {
                RegisterRecurringJob(model);
            }

            TempData["Success"] = "تم إنشاء الخدمة بنجاح / Service created successfully";
            return RedirectToAction("Edit", new { id = model.Id });
        }

        // ══════════════════════════════════════
        // EDIT
        // ══════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var service = await _db.SvcServices
                .Include(s => s.FieldMappings.OrderBy(m => m.SortOrder))
                .FirstOrDefaultAsync(s => s.Id == id);

            if (service == null) return NotFound();
            return View(service);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, SvcService model)
        {
            var service = await _db.SvcServices.FindAsync(id);
            if (service == null) return NotFound();

            NormalizeRequiredStrings(model);

            // Update properties
            service.Name = model.Name;
            service.Description = model.Description;
            service.IsEnabled = model.IsEnabled;

            // Source
            service.SourceProvider = model.SourceProvider;
            service.SourceHost = model.SourceHost;
            service.SourcePort = model.SourcePort;
            service.SourceDatabase = model.SourceDatabase;
            service.SourceUsername = model.SourceUsername;
            if (!string.IsNullOrEmpty(model.SourcePassword))
                service.SourcePassword = model.SourcePassword;
            service.SourceTableOrView = model.SourceTableOrView;
            service.SourceIntegratedSecurity = model.SourceIntegratedSecurity;

            // AD
            service.ADServer = model.ADServer;
            service.ADPort = model.ADPort;
            // Explicit channel mode replaces the old SSL switch; keep ADUseSsl coherent.
            service.ADSecurityMode = model.ADSecurityMode;
            service.ADSecurityModeSet = true;
            service.ADAllowUntrustedCertificate = model.ADAllowUntrustedCertificate;
            service.ADUseSsl = model.ADSecurityMode == LdapSecurityMode.Ldaps;
            service.ADUsername = model.ADUsername;
            if (!string.IsNullOrEmpty(model.ADPassword))
                service.ADPassword = model.ADPassword;
            service.ADBaseDN = model.ADBaseDN;
            service.ADSearchAttribute = model.ADSearchAttribute;

            // Key
            service.KeySourceColumn = model.KeySourceColumn;

            // Schedule
            service.ScheduleMode = model.ScheduleMode;
            service.ScheduleTime = model.ScheduleTime;
            service.ScheduleDays = model.ScheduleDays;
            service.ScheduleIntervalMinutes = model.ScheduleIntervalMinutes;
            service.ScheduleDayOfMonth = model.ScheduleDayOfMonth;
            service.ScheduleCustomCron = model.ScheduleCustomCron;
            try
            {
                service.ScheduleCron = BuildCron(model);
            }
            catch (InvalidOperationException ex)
            {
                // Nothing is saved: the tracked entity keeps whatever it already had, and the
                // service goes on running the schedule it ran before the bad edit.
                TempData["Error"] = ex.Message;
                return RedirectToAction("Edit", new { id });
            }

            // Service Type & Offboarding settings
            service.ServiceType = model.ServiceType;
            service.StatusColumn = model.StatusColumn;
            service.StatusValue = model.StatusValue;
            service.TargetOU = model.TargetOU;
            service.OffboardingSearchOU = model.OffboardingSearchOU;
            service.OffboardingExclusionGroup = model.OffboardingExclusionGroup;
            service.EmptyCheckAttributes = model.EmptyCheckAttributes;

            // Per-service-type settings. These were missing here entirely: Create binds the whole
            // model, so a new service kept them, but Edit copies field by field and never copied
            // these — so changing the inactivity threshold, the report type, or the orphan action
            // on an existing service saved successfully and changed nothing at all.
            service.ReportType = model.ReportType;
            service.AuditGroups = model.AuditGroups;
            service.DuplicateAttribute = model.DuplicateAttribute;
            service.PwdNeverExpiresAction = model.PwdNeverExpiresAction;
            service.NhiNamePatterns = model.NhiNamePatterns;
            service.NhiOUs = model.NhiOUs;
            service.NhiGroups = model.NhiGroups;
            service.NhiAttributeRules = model.NhiAttributeRules;
            service.NhiFlagNoKeyAttribute = model.NhiFlagNoKeyAttribute;
            service.NhiFlagPwdNeverExpires = model.NhiFlagPwdNeverExpires;
            service.NhiFlagHasSpn = model.NhiFlagHasSpn;
            service.NhiIncludeManagedServiceAccounts = model.NhiIncludeManagedServiceAccounts;
            service.NhiMatchMode = model.NhiMatchMode;
            service.NhiCredentialMaxAgeDays = model.NhiCredentialMaxAgeDays;
            service.NhiDormantDays = model.NhiDormantDays;
            // The lifecycle settings. Copied one by one like everything else here — a field the form
            // posts and this method does not mention is accepted by the browser, saved by nothing,
            // and reported to nobody.
            service.NhiLifecycleEnabled = model.NhiLifecycleEnabled;
            service.NhiClaimDays = model.NhiClaimDays;
            service.NhiAttestationDays = model.NhiAttestationDays;
            service.NhiAttestationGraceDays = model.NhiAttestationGraceDays;
            service.NhiQuarantineMode = model.NhiQuarantineMode;
            service.NhiMaxQuarantinePercent = model.NhiMaxQuarantinePercent;
            service.NhiOwnerNotificationEmail = model.NhiOwnerNotificationEmail;
            service.InactivityMonths = model.InactivityMonths;
            service.LastLogonAttribute = model.LastLogonAttribute;
            service.ExpiryAttribute = model.ExpiryAttribute;
            service.ExpiryWarnDays = model.ExpiryWarnDays;
            service.OrphanAction = model.OrphanAction;
            service.MinSourceRecords = model.MinSourceRecords;
            service.SmsProviderId = model.SmsProviderId;
            service.PhoneColumn = model.PhoneColumn;
            service.EmployeeNameColumn = model.EmployeeNameColumn;
            service.EnableSms = model.EnableSms;
            service.SmsTemplate = model.SmsTemplate;
            service.EnableEmailNotification = model.EnableEmailNotification;
            service.NotificationEmail = model.NotificationEmail;
            service.EmailSubject = model.EmailSubject;
            service.EmailBodyTemplate = model.EmailBodyTemplate;

            service.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            // Update Hangfire job
            var jobId = $"svc-sync-{service.Id}";
            if (service.IsEnabled && !string.IsNullOrEmpty(service.ScheduleCron))
            {
                RegisterRecurringJob(service);
            }
            else
            {
                RecurringJob.RemoveIfExists(jobId);
            }

            TempData["Success"] = "تم تحديث الخدمة بنجاح / Service updated successfully";
            return RedirectToAction("Edit", new { id });
        }

        // ══════════════════════════════════════
        // DELETE
        // ══════════════════════════════════════
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var service = await _db.SvcServices.FindAsync(id);
            if (service == null) return NotFound();

            // Remove Hangfire job
            RecurringJob.RemoveIfExists($"svc-sync-{service.Id}");

            _db.SvcServices.Remove(service);
            await _db.SaveChangesAsync();

            TempData["Success"] = "تم حذف الخدمة / Service deleted";
            return RedirectToAction("Index");
        }

        // ══════════════════════════════════════
        // FIELD MAPPINGS
        // ══════════════════════════════════════
        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> SaveMappings([FromQuery] int serviceId, [FromBody] List<SvcFieldMappingDto> mappings)
        {
            var service = await _db.SvcServices
                .Include(s => s.FieldMappings)
                .FirstOrDefaultAsync(s => s.Id == serviceId);

            if (service == null)
                return Json(new { success = false, message = "Service not found" });

            try
            {
                if (mappings == null || mappings.Count == 0)
                    return Json(new { success = false, message = "No mappings provided" });

                // Remove existing mappings
                _db.SvcFieldMappings.RemoveRange(service.FieldMappings);
                await _db.SaveChangesAsync();

                // Add new mappings
                int order = 0;
                foreach (var m in mappings)
                {
                    if (string.IsNullOrWhiteSpace(m.SourceColumn) || string.IsNullOrWhiteSpace(m.TargetAttribute))
                        continue;

                    _db.SvcFieldMappings.Add(new SvcFieldMapping
                    {
                        SvcServiceId = serviceId,
                        SourceColumn = m.SourceColumn,
                        TargetAttribute = m.TargetAttribute,
                        IsKeyMapping = m.IsKeyMapping,
                        SortOrder = order++
                    });
                }

                service.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();

                _logger.LogInformation("Saved {Count} field mappings for service {ServiceId}", order, serviceId);
                return Json(new { success = true, message = $"تم حفظ {order} ربط / Saved {order} mappings" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save mappings for service {ServiceId}", serviceId);
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ══════════════════════════════════════
        // RUN NOW (Manual Execution)
        // ══════════════════════════════════════
        [HttpPost]
        public async Task<IActionResult> RunNow(int id)
        {
            // ✅ Check if service is already running
            var isRunning = await _db.SvcRunLogs
                .AnyAsync(l => l.SvcServiceId == id && l.Status == "Running");

            if (isRunning)
            {
                TempData["Error"] = "الخدمة قيد التشغيل حالياً — انتظر حتى تنتهي / Service is currently running — please wait";
                return RedirectToAction("Logs", new { id });
            }

            // Enqueue immediate execution via Hangfire
            // The username is captured here, in the request; the job runs later in Hangfire
            // where there is no session to read it from.
            var actor = ActorNames.Clamp(User.Identity?.Name);
            BackgroundJob.Enqueue<SvcSyncJob>(job => job.ExecuteManualAsync(id, actor, CancellationToken.None));

            TempData["Success"] = "تم بدء تشغيل الخدمة / Service execution started";
            return RedirectToAction("Logs", new { id });
        }

        // ══════════════════════════════════════
        // CANCEL RUNNING SERVICE
        // ══════════════════════════════════════
        [HttpPost]
        public async Task<IActionResult> CancelRun(int id)
        {
            // Check if the service is actually running
            var isRunning = await _db.SvcRunLogs
                .AnyAsync(l => l.SvcServiceId == id && l.Status == "Running");

            if (!isRunning)
            {
                return Json(new { success = false, message = "الخدمة ليست قيد التشغيل / Service is not running" });
            }

            // Request cancellation via the registry
            var cancelled = SvcCancellationRegistry.Cancel(id);

            if (cancelled)
            {
                _logger.LogWarning("ServicesController: Cancel requested for service ID {ServiceId}", id);
                return Json(new { success = true, message = "تم طلب إلغاء الخدمة / Cancel requested" });
            }

            // The registry lives in memory, so it is empty after a restart. A run marked Running
            // with no token behind it is not running at all — it was interrupted by a process that
            // has since gone. Returning "no running operation found" left the service permanently
            // unrunnable, because RunNow refuses while the row says Running and nothing else ever
            // changes it. Startup now closes these out; this handles the case without a restart.
            var stale = await _db.SvcRunLogs
                .Where(l => l.SvcServiceId == id && l.Status == "Running")
                .ToListAsync();

            foreach (var run in stale)
            {
                run.Status = "Interrupted";
                run.EndTime ??= DateTime.UtcNow;
                run.ErrorMessage = string.IsNullOrWhiteSpace(run.ErrorMessage)
                    ? "No live execution was found for this run — it was left behind by a stopped process and closed here."
                    : run.ErrorMessage;
            }
            await _db.SaveChangesAsync();

            _logger.LogWarning(
                "ServicesController: service {ServiceId} had {Count} run(s) marked Running with no live execution — " +
                "closed as Interrupted by {User}. The service can run again.",
                id, stale.Count, User.Identity?.Name ?? "unknown");

            return Json(new
            {
                success = true,
                message = "لم تكن هناك عملية فعلية — أُغلق السجل المعلّق ويمكن تشغيل الخدمة الآن / " +
                          "No live execution — the stuck run was closed and the service can run again"
            });
        }

        // ══════════════════════════════════════
        // LOGS
        // ══════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> Logs(int id, string? status = null, DateTime? dateFrom = null, DateTime? dateTo = null, int page = 1)
        {
            var service = await _db.SvcServices.FindAsync(id);
            if (service == null) return NotFound();

            var query = _db.SvcRunLogs
                .Where(l => l.SvcServiceId == id)
                .AsQueryable();

            if (!string.IsNullOrEmpty(status))
                query = query.Where(l => l.Status == status);
            if (dateFrom.HasValue)
                query = query.Where(l => l.StartTime >= dateFrom.Value);
            if (dateTo.HasValue)
                query = query.Where(l => l.StartTime < dateTo.Value.AddDays(1));

            var pageSize = 20;
            var totalCount = await query.CountAsync();
            var logs = await query
                .OrderByDescending(l => l.StartTime)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.Service = service;
            ViewBag.StatusFilter = status;
            ViewBag.DateFrom = dateFrom?.ToString("yyyy-MM-dd");
            ViewBag.DateTo = dateTo?.ToString("yyyy-MM-dd");
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = (int)Math.Ceiling((double)totalCount / pageSize);
            ViewBag.TotalCount = totalCount;

            return View(logs);
        }

        // ══════════════════════════════════════
        // AUDIT LOG
        // ══════════════════════════════════════
        /// <summary>
        /// Everything the service has done, newest first.
        ///
        /// <paramref name="runId"/> is accepted only so older links and bookmarks do not 404; it
        /// no longer scopes the query. See BuildAuditQuery for why.
        ///
        /// ⚠️ <paramref name="actionType"/> must NOT be named <c>action</c>. The default route is
        /// <c>{controller}/{action}/{id?}</c>, so a parameter called <c>action</c> binds from the
        /// ROUTE — it silently receives the method name, "AuditLog". Every request then filtered on
        /// <c>Action = 'AuditLog'</c>, a value no audit row ever has, so this screen and its Excel
        /// export returned nothing for every service while the data sat in the table. It read as a
        /// write failure and cost several rounds of looking in the wrong place.
        /// The query-string name stays <c>action</c> via [FromQuery] so existing links keep working.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> AuditLog(
            int id,
            long? runId = null,
            [FromQuery(Name = "action")] string? actionType = null,
            string? q = null,
            DateTime? dateFrom = null,
            DateTime? dateTo = null,
            int page = 1)
        {
            var service = await _db.SvcServices.FindAsync(id);
            if (service == null) return NotFound();

            var query = BuildAuditQuery(id, actionType, q, dateFrom, dateTo);

            var pageSize = 50;
            var totalCount = await query.CountAsync();
            var entries = await query
                .OrderByDescending(a => a.Timestamp)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            // Summary counts over the whole filtered set (not just the current page)
            ViewBag.ActionCounts = await query
                .GroupBy(a => a.Action)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.Key, g => g.Count);

            ViewBag.Service = service;
            ViewBag.ActionFilter = actionType;
            ViewBag.SearchTerm = q;
            ViewBag.DateFrom = dateFrom?.ToString("yyyy-MM-dd");
            ViewBag.DateTo = dateTo?.ToString("yyyy-MM-dd");
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = (int)Math.Ceiling((double)totalCount / pageSize);
            ViewBag.TotalCount = totalCount;

            return View(entries);
        }

        /// <summary>
        /// The audit-log filter, shared by the screen and its Excel export.
        ///
        /// Extracted rather than repeated: an export that quietly applies a different filter than
        /// the screen above it produces a file that disagrees with what the operator just read, and
        /// nothing about the file says so.
        /// </summary>
        private IQueryable<SvcAuditEntry> BuildAuditQuery(
            int serviceId, string? action, string? q, DateTime? dateFrom, DateTime? dateTo)
        {
            // Deliberately NOT filtered by run.
            //
            // The results screen used to be scoped to a single run id, and when that scoping
            // produced nothing the page was blank with no way to tell a run that did nothing from
            // a filter that matched nothing from a fault. Results are a property of the service,
            // not of a run number: showing the service's history newest-first means the screen
            // always has something to show, and the most recent run is simply at the top.
            var query = _db.SvcAuditEntries.Where(a => a.SvcServiceId == serviceId);

            if (!string.IsNullOrEmpty(action))
                query = query.Where(a => a.Action == action);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                query = query.Where(a => a.KeyValue.Contains(term) || (a.ADIdentity != null && a.ADIdentity.Contains(term)));
            }

            if (dateFrom.HasValue)
                query = query.Where(a => a.Timestamp >= dateFrom.Value);
            if (dateTo.HasValue)
                query = query.Where(a => a.Timestamp < dateTo.Value.AddDays(1));

            return query;
        }

        /// <summary>
        /// Excel export of the audit log, honouring the filters currently applied on screen.
        ///
        /// Exports the whole filtered set, not the visible page — the point of the file is the
        /// records the screen is paging through, and a 50-row file from a 4,000-row result would
        /// look complete.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> ExportAuditLog(
            int id,
            [FromQuery(Name = "action")] string? actionType = null,
            string? q = null,
            DateTime? dateFrom = null, DateTime? dateTo = null)
        {
            var service = await _db.SvcServices.FindAsync(id);
            if (service == null) return NotFound();

            var entries = await BuildAuditQuery(id, actionType, q, dateFrom, dateTo)
                .OrderByDescending(a => a.Timestamp)
                .ToListAsync();

            var bytes = _excelExport.ExportServiceAudit(entries, service.Name, IsArabic);
            return File(bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"ServiceAudit-{Sanitize(service.Name)}-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
        }

        /// <summary>Excel export of the run history, honouring the filters applied on screen.</summary>
        [HttpGet]
        public async Task<IActionResult> ExportLogs(
            int id, string? status = null, DateTime? dateFrom = null, DateTime? dateTo = null)
        {
            var service = await _db.SvcServices.FindAsync(id);
            if (service == null) return NotFound();

            var query = _db.SvcRunLogs.Where(l => l.SvcServiceId == id);

            if (!string.IsNullOrEmpty(status))
                query = query.Where(l => l.Status == status);
            if (dateFrom.HasValue)
                query = query.Where(l => l.StartTime >= dateFrom.Value);
            if (dateTo.HasValue)
                query = query.Where(l => l.StartTime < dateTo.Value.AddDays(1));

            var logs = await query.OrderByDescending(l => l.StartTime).ToListAsync();

            var bytes = _excelExport.ExportServiceLogs(logs, service.Name, IsArabic);
            return File(bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"ServiceRuns-{Sanitize(service.Name)}-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
        }

        /// <summary>Service names reach a filename here, and a name may hold characters Windows rejects.</summary>
        private static string Sanitize(string name)
        {
            var clean = new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != ' ').ToArray());
            return string.IsNullOrWhiteSpace(clean) ? "service" : clean;
        }

        // ══════════════════════════════════════
        // AJAX: Test Source Connection
        // ══════════════════════════════════════
        [HttpPost]
        public async Task<IActionResult> TestConnection(string provider, string host, int port,
            string? database, string? username, string? password, bool integratedSecurity)
        {
            var connectionString = SvcDatabaseReader.BuildConnectionString(
                provider, host, port, database, username, password, integratedSecurity);

            var (success, message) = await _dbReader.TestConnectionAsync(provider, connectionString);
            return Json(new { success, message });
        }

        // ══════════════════════════════════════
        // AJAX: Test AD Connection
        // ══════════════════════════════════════
        [HttpPost]
        public IActionResult TestAdConnection(string server, int port, int securityMode,
            bool allowUntrustedCertificate, string? username, string? password, string baseDN)
        {
            var mode = Enum.IsDefined(typeof(LdapSecurityMode), securityMode)
                ? (LdapSecurityMode)securityMode
                : LdapSecurityMode.Auto;

            var svc = new SvcService
            {
                ADServer = server,
                ADPort = port,
                ADSecurityMode = mode,
                ADSecurityModeSet = true,
                ADAllowUntrustedCertificate = allowUntrustedCertificate,
                ADUseSsl = mode == LdapSecurityMode.Ldaps,
                ADUsername = username,
                ADPassword = password,
                ADBaseDN = baseDN
            };

            // Name the channel actually used — a silently-plaintext connection looks identical
            // to a good one until a password write fails.
            var channel = LdapConnectionFactory.Describe(mode, port);
            var (success, message) = _executor.TestAdConnection(svc);
            return Json(new { success, message = $"{message} — {channel}" });
        }

        // ══════════════════════════════════════
        // AJAX: Get Source Columns
        // ══════════════════════════════════════
        [HttpPost]
        public async Task<IActionResult> GetSourceColumns(string provider, string host, int port,
            string? database, string? username, string? password, bool integratedSecurity, string tableOrView)
        {
            try
            {
                var connectionString = SvcDatabaseReader.BuildConnectionString(
                    provider, host, port, database, username, password, integratedSecurity);

                var columns = await _dbReader.GetColumnsAsync(provider, connectionString, tableOrView);
                return Json(new { success = true, columns });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ══════════════════════════════════════
        // AJAX: Get Service Run Status (Fallback for SignalR)
        // ══════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> GetRunStatus(int id)
        {
            var runLog = await _db.SvcRunLogs
                .Where(l => l.SvcServiceId == id)
                .OrderByDescending(l => l.StartTime)
                .FirstOrDefaultAsync();

            if (runLog == null)
                return Json(new { running = false });

            return Json(new
            {
                running = runLog.Status == "Running",
                runLogId = runLog.Id,
                status = runLog.Status,
                total = runLog.TotalRecords,
                updated = runLog.UpdatedRecords,
                failed = runLog.FailedRecords,
                skipped = runLog.SkippedRecords,
                notFound = runLog.NotFoundRecords,
                startTime = runLog.StartTime,
                endTime = runLog.EndTime
            });
        }

        // ══════════════════════════════════════
        // HELPERS
        // ══════════════════════════════════════

        /// <summary>
        /// MVC binds empty (or disabled/absent) form inputs as null, but these columns
        /// are NOT NULL in Svc_Services — e.g. EmptyAttrDisable services skip the whole
        /// source-database step, leaving KeySourceColumn etc. null.
        /// </summary>
        private static void NormalizeRequiredStrings(SvcService m)
        {
            m.ServiceType = string.IsNullOrWhiteSpace(m.ServiceType) ? "Sync" : m.ServiceType;
            m.SourceProvider = string.IsNullOrWhiteSpace(m.SourceProvider) ? "SqlServer" : m.SourceProvider;
            m.SourceHost ??= string.Empty;
            m.SourceTableOrView ??= string.Empty;
            m.KeySourceColumn ??= string.Empty;
            m.ADServer ??= string.Empty;
            m.ADBaseDN ??= string.Empty;
            m.ADSearchAttribute ??= string.Empty;
            m.ScheduleMode = string.IsNullOrWhiteSpace(m.ScheduleMode) ? "daily" : m.ScheduleMode;
        }

        /// <summary>
        /// Suspends or resumes a service's schedule in one click, without opening the edit form.
        ///
        /// It flips the same IsEnabled the edit form writes — deliberately, rather than introducing
        /// a second "paused" concept beside it. Two switches meaning almost the same thing is how an
        /// operator ends up with a service that is enabled, unpaused, and still not running.
        ///
        /// Manual runs are unaffected: this only governs the recurring job.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleSchedule(int id)
        {
            var service = await _db.SvcServices.FindAsync(id);
            if (service == null) return NotFound();

            service.IsEnabled = !service.IsEnabled;
            service.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            var jobId = $"svc-sync-{service.Id}";
            var hasCron = !string.IsNullOrWhiteSpace(service.ScheduleCron);

            if (service.IsEnabled && hasCron)
            {
                RegisterRecurringJob(service);
                _logger.LogInformation(
                    "Service '{Name}' (id {Id}) schedule RESUMED by {User} — recurring job {JobId} registered with [{Cron}]",
                    service.Name, service.Id, User.Identity?.Name ?? "unknown", jobId, service.ScheduleCron);
            }
            else
            {
                RecurringJob.RemoveIfExists(jobId);

                // Suspending removes the recurring job, so nothing will fire and nothing would
                // otherwise mark its absence. Said plainly here, and shown as a badge on the list,
                // because a schedule silently not running is the failure this feature exists to
                // prevent — not to reproduce.
                _logger.LogWarning(
                    "Service '{Name}' (id {Id}) schedule SUSPENDED by {User} — recurring job {JobId} removed; " +
                    "it will NOT run on a timer until resumed. Manual runs still work.",
                    service.Name, service.Id, User.Identity?.Name ?? "unknown", jobId);
            }

            return Json(new
            {
                success = true,
                enabled = service.IsEnabled,
                scheduled = service.IsEnabled && hasCron
            });
        }

        /// <summary>
        /// The cron for a posted schedule. One place, so the create and edit paths cannot drift into
        /// passing different arguments — which is how the custom expression came to be dropped on
        /// both of them: the call simply stopped short of the parameter that carried it.
        /// </summary>
        private static string BuildCron(SvcService model) => CronBuilder.Build(
            model.ScheduleMode,
            model.ScheduleTime,
            model.ScheduleDays,
            model.ScheduleIntervalMinutes,
            model.ScheduleCustomCron,
            model.ScheduleDayOfMonth);

        private void RegisterRecurringJob(SvcService service)
        {
            var jobId = $"svc-sync-{service.Id}";
            RecurringJob.AddOrUpdate<SvcSyncJob>(
                jobId,
                job => job.ExecuteAsync(service.Id, CancellationToken.None),
                service.ScheduleCron,
                new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });
        }

        // DTO for mapping deserialization
        public class SvcFieldMappingDto
        {
            public string SourceColumn { get; set; } = "";
            public string TargetAttribute { get; set; } = "";
            public bool IsKeyMapping { get; set; }
        }
    }
}
