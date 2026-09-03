using System.Text;
using System.Text.Json;
using Hangfire;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Jobs;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentitySyncPro.Web.Controllers
{
    [Microsoft.AspNetCore.Authorization.Authorize(Roles = IdentitySyncPro.Core.Models.Settings.AppUserRoles.AdminOrOperator)]
    public class SmsCenterController : Controller
    {
        private readonly AppDbContext _db;
        private readonly ISmsService _smsService;
        private readonly IBackgroundJobClient _backgroundJobs;
        private readonly ExcelExportService _excelExport;
        private readonly ILogger<SmsCenterController> _logger;

        public SmsCenterController(
            AppDbContext db,
            ISmsService smsService,
            IBackgroundJobClient backgroundJobs,
            ExcelExportService excelExport,
            ILogger<SmsCenterController> logger)
        {
            _db = db;
            _smsService = smsService;
            _backgroundJobs = backgroundJobs;
            _excelExport = excelExport;
            _logger = logger;
        }

        private string Lang => HttpContext?.Items["Lang"] as string ?? "ar";

        // ══════════════════════════════════════
        // INDEX — Cards view
        // ══════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var providers = await _db.SmsProviders
                .OrderByDescending(p => p.IsActive)
                .ThenBy(p => p.Name)
                .ToListAsync();

            ViewBag.Lang = Lang;
            return View(providers);
        }

        // ══════════════════════════════════════
        // CREATE
        // ══════════════════════════════════════
        [HttpGet]
        public IActionResult Create()
        {
            ViewBag.Lang = Lang;
            return View(new SmsProvider());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(SmsProvider model)
        {
            if (string.IsNullOrWhiteSpace(model.Name))
            {
                ModelState.AddModelError("Name", "اسم المزود مطلوب / Provider name is required");
                ViewBag.Lang = Lang;
                return View(model);
            }

            if (string.IsNullOrWhiteSpace(model.ApiUrl))
            {
                ModelState.AddModelError("ApiUrl", "رابط API مطلوب / API URL is required");
                ViewBag.Lang = Lang;
                return View(model);
            }

            model.CreatedAt = DateTime.UtcNow;
            _db.SmsProviders.Add(model);
            await _db.SaveChangesAsync();

            _logger.LogInformation("SMS provider '{Name}' created with ID {Id}", model.Name, model.Id);
            TempData["Success"] = $"تم إضافة مزود \"{model.Name}\" بنجاح / Provider \"{model.Name}\" created successfully";
            return RedirectToAction("Index");
        }

        // ══════════════════════════════════════
        // EDIT
        // ══════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var provider = await _db.SmsProviders.FindAsync(id);
            if (provider == null) return NotFound();

            ViewBag.Lang = Lang;
            return View(provider);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, SmsProvider model)
        {
            var provider = await _db.SmsProviders.FindAsync(id);
            if (provider == null) return NotFound();

            if (string.IsNullOrWhiteSpace(model.Name))
            {
                ModelState.AddModelError("Name", "اسم المزود مطلوب / Provider name is required");
                ViewBag.Lang = Lang;
                return View(model);
            }

            if (string.IsNullOrWhiteSpace(model.ApiUrl))
            {
                ModelState.AddModelError("ApiUrl", "رابط API مطلوب / API URL is required");
                ViewBag.Lang = Lang;
                return View(model);
            }

            provider.Name = model.Name;
            provider.ApiUrl = model.ApiUrl;
            provider.ApiUsername = model.ApiUsername;
            // Secrets: only overwrite when a new value is supplied (blank = keep existing)
            if (!string.IsNullOrEmpty(model.ApiPassword))
                provider.ApiPassword = model.ApiPassword;
            if (!string.IsNullOrEmpty(model.ApiKey))
                provider.ApiKey = model.ApiKey;
            provider.SenderName = model.SenderName;
            provider.HttpMethod = string.IsNullOrWhiteSpace(model.HttpMethod) ? "POST" : model.HttpMethod;
            provider.BodyFormat = string.IsNullOrWhiteSpace(model.BodyFormat) ? "Json" : model.BodyFormat;
            provider.RequestTemplate = model.RequestTemplate;
            provider.HeadersJson = model.HeadersJson;
            provider.SuccessBodyContains = model.SuccessBodyContains;
            provider.IsActive = model.IsActive;
            provider.Notes = model.Notes;

            await _db.SaveChangesAsync();

            _logger.LogInformation("SMS provider '{Name}' (ID {Id}) updated", provider.Name, provider.Id);
            TempData["Success"] = $"تم تحديث مزود \"{provider.Name}\" / Provider \"{provider.Name}\" updated";
            return RedirectToAction("Index");
        }

        // ══════════════════════════════════════
        // DELETE
        // ══════════════════════════════════════
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var provider = await _db.SmsProviders.FindAsync(id);
            if (provider == null) return NotFound();

            // Check if provider is in use by any tenant
            var tenantUsing = await _db.TenantSettings
                .AnyAsync(t => t.SmsProviderId == id);
            if (tenantUsing)
            {
                TempData["Error"] = "لا يمكن حذف هذا المزود — مستخدم من قبل جهة واحدة على الأقل / Cannot delete — in use by at least one tenant";
                return RedirectToAction("Index");
            }

            var name = provider.Name;
            _db.SmsProviders.Remove(provider);
            await _db.SaveChangesAsync();

            _logger.LogInformation("SMS provider '{Name}' (ID {Id}) deleted", name, id);
            TempData["Success"] = $"تم حذف مزود \"{name}\" / Provider \"{name}\" deleted";
            return RedirectToAction("Index");
        }

        // ══════════════════════════════════════
        // TOGGLE ACTIVE
        // ══════════════════════════════════════
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleActive(int id)
        {
            var provider = await _db.SmsProviders.FindAsync(id);
            if (provider == null) return Json(new { success = false });

            provider.IsActive = !provider.IsActive;
            await _db.SaveChangesAsync();
            return Json(new { success = true, isActive = provider.IsActive });
        }

        // ══════════════════════════════════════
        // API: Get active providers (for dropdowns)
        // ══════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> GetProviders()
        {
            var providers = await _db.SmsProviders
                .Where(p => p.IsActive)
                .OrderBy(p => p.Name)
                .Select(p => new { p.Id, p.Name, p.SenderName })
                .ToListAsync();

            return Json(providers);
        }

        // ══════════════════════════════════════
        // SEND TEST SMS
        // ══════════════════════════════════════
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendTest(int providerId, string phoneNumber, string message)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber))
                return Json(new { success = false, error = "رقم الجوال مطلوب / Phone number is required" });
            if (string.IsNullOrWhiteSpace(message))
                return Json(new { success = false, error = "الرسالة مطلوبة / Message is required" });

            var provider = await _db.SmsProviders.FindAsync(providerId);
            if (provider == null)
                return Json(new { success = false, error = "المزود غير موجود / Provider not found" });

            try
            {
                // Same generic engine the real sends use — so the test exercises the exact
                // method/format/template/headers/success-rule the provider is configured with.
                var result = await _smsService.SendCredentialsAsync(new SmsRequest
                {
                    PhoneNumber = phoneNumber,
                    MessageTemplate = message // literal text, no tokens to resolve
                }.WithProvider(provider));

                if (result.Success)
                {
                    _logger.LogInformation("Test SMS sent successfully via provider '{Provider}' to {Phone}",
                        provider.Name, MaskPhone(PhoneHelper.NormalizePhone(phoneNumber)));
                    return Json(new { success = true, response = result.Response });
                }

                _logger.LogWarning("Test SMS via '{Provider}' failed: {Error}", provider.Name, result.Error);
                return Json(new { success = false, error = result.Error });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Test SMS failed via provider '{Provider}'", provider.Name);
                return Json(new { success = false, error = ex.Message });
            }
        }

        private static string MaskPhone(string phone) => PhoneHelper.MaskPhone(phone);

        // ══════════════════════════════════════
        // SMS SEND LOG — review who received the credentials SMS
        // ══════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> Log(string? status, string? account, DateTime? dateFrom, DateTime? dateTo, int page = 1)
        {
            var query = BuildLogQuery(status, account, dateFrom, dateTo);

            const int pageSize = 50;
            var total = await query.CountAsync();
            var logs = await query
                .OrderByDescending(l => l.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.SuccessCount = await _db.SmsSendLogs.CountAsync(l => l.Status == "Success");
            ViewBag.FailedCount = await _db.SmsSendLogs.CountAsync(l => l.Status == "Failed");
            ViewBag.SkippedCount = await _db.SmsSendLogs.CountAsync(l => l.Status == "Skipped");
            ViewBag.RetryableCount = await _db.SmsSendLogs.CountAsync(l => l.Status == "Failed" && l.SentMessage != null);

            ViewBag.Status = status;
            ViewBag.Account = account;
            ViewBag.DateFrom = dateFrom?.ToString("yyyy-MM-dd");
            ViewBag.DateTo = dateTo?.ToString("yyyy-MM-dd");
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = (int)Math.Ceiling((double)total / pageSize);
            ViewBag.TotalCount = total;
            ViewBag.Lang = Lang;
            return View(logs);
        }

        [HttpGet]
        public async Task<IActionResult> ExportLog(string? status, string? account, DateTime? dateFrom, DateTime? dateTo)
        {
            var logs = await BuildLogQuery(status, account, dateFrom, dateTo)
                .OrderByDescending(l => l.Id)
                .Take(10000)
                .ToListAsync();

            var bytes = _excelExport.ExportSmsLogs(logs, Lang == "ar");
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"SmsLog_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
        }

        /// <summary>Retry all failed sends, or a specific set of ids — runs in the background.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult RetryFailed(int[]? ids)
        {
            var jobId = _backgroundJobs.Enqueue<SmsRetryJob>(j => j.ExecuteAsync(ids, CancellationToken.None));
            return Json(new
            {
                success = true,
                message = "بدأت إعادة إرسال الرسائل الفاشلة في الخلفية / Retry started in background",
                jobId
            });
        }

        private IQueryable<SmsSendLog> BuildLogQuery(string? status, string? account, DateTime? dateFrom, DateTime? dateTo)
        {
            var query = _db.SmsSendLogs.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(status)) query = query.Where(l => l.Status == status);
            if (!string.IsNullOrEmpty(account)) query = query.Where(l => l.Account != null && l.Account.Contains(account));
            if (dateFrom.HasValue) query = query.Where(l => l.CreatedAt >= dateFrom.Value);
            if (dateTo.HasValue) query = query.Where(l => l.CreatedAt < dateTo.Value.AddDays(1));
            return query;
        }
    }
}
