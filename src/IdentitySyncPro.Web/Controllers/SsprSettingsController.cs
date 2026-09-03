using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Connectors;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Web.Controllers
{
    /// <summary>
    /// Admin configuration for the standalone Self-Service Password Reset module:
    /// master toggle, SMS provider, message template, and the AD domains served.
    /// </summary>
    [Authorize(Roles = AppUserRoles.Admin)]
    public class SsprSettingsController : Controller
    {
        private readonly AppDbContext _db;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ExcelExportService _excelExport;

        public SsprSettingsController(AppDbContext db, ILoggerFactory loggerFactory, ExcelExportService excelExport)
        {
            _db = db;
            _loggerFactory = loggerFactory;
            _excelExport = excelExport;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var settings = await _db.SsprSettings.FirstOrDefaultAsync();
            if (settings == null)
            {
                settings = new SsprSettings();
                _db.SsprSettings.Add(settings);
                await _db.SaveChangesAsync();
            }

            ViewBag.Lang = await GetLangAsync();
            ViewBag.Domains = await _db.SsprDomains.AsNoTracking().OrderBy(d => d.Name).ToListAsync();
            ViewBag.SmsProviders = await _db.SmsProviders.AsNoTracking()
                .Select(p => new { p.Id, p.Name, p.IsActive }).ToListAsync();
            // Currently blocked IPs (so an admin can lift a block without touching SQL)
            ViewBag.BlockedIps = await _db.SsprIpBlocks.AsNoTracking()
                .Where(b => b.BlockedUntilUtc != null && b.BlockedUntilUtc > DateTime.UtcNow)
                .OrderByDescending(b => b.BlockedUntilUtc)
                .ToListAsync();
            return View(settings);
        }

        /// <summary>Lift an active IP block immediately and reset its failure counter.</summary>
        [HttpPost]
        public async Task<IActionResult> UnblockIp(int id)
        {
            var row = await _db.SsprIpBlocks.FindAsync(id);
            if (row == null) return Json(new { success = false, message = "غير موجود" });

            row.BlockedUntilUtc = null;
            row.FailedCount = 0;
            await _db.SaveChangesAsync();
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> SaveSettings(bool isEnabled, int? smsProviderId,
            string messageTemplate, string messageTemplateEn, string newPasswordTemplate, string newPasswordTemplateEn,
            int otpLifetimeSeconds, int maxVerifyAttempts, int maxRequestsPerIpPerHour, int maxRequestsPerUserPerHour,
            int maxFailedIdentityAttempts, int ipBlockDurationHours, int maxResetsPerUserPer24h)
        {
            var settings = await _db.SsprSettings.FirstOrDefaultAsync() ?? new SsprSettings();
            if (settings.Id == 0) _db.SsprSettings.Add(settings);

            settings.IsEnabled = isEnabled;
            settings.SmsProviderId = smsProviderId;

            // Tunable limits (clamped to safe bounds)
            settings.OtpLifetimeSeconds = Math.Clamp(otpLifetimeSeconds, 60, 1800);
            settings.MaxVerifyAttempts = Math.Clamp(maxVerifyAttempts, 1, 10);
            settings.MaxRequestsPerIpPerHour = Math.Clamp(maxRequestsPerIpPerHour, 1, 100);
            settings.MaxRequestsPerUserPerHour = Math.Clamp(maxRequestsPerUserPerHour, 1, 50);
            settings.MaxFailedIdentityAttempts = Math.Clamp(maxFailedIdentityAttempts, 1, 20);
            settings.IpBlockDurationHours = Math.Clamp(ipBlockDurationHours, 1, 720);
            settings.MaxResetsPerUserPer24h = Math.Clamp(maxResetsPerUserPer24h, 1, 20);
            settings.MessageTemplate = string.IsNullOrWhiteSpace(messageTemplate) ? settings.MessageTemplate : messageTemplate.Trim();
            settings.MessageTemplateEn = string.IsNullOrWhiteSpace(messageTemplateEn) ? settings.MessageTemplateEn : messageTemplateEn.Trim();
            settings.NewPasswordTemplate = string.IsNullOrWhiteSpace(newPasswordTemplate) ? settings.NewPasswordTemplate : newPasswordTemplate.Trim();
            settings.NewPasswordTemplateEn = string.IsNullOrWhiteSpace(newPasswordTemplateEn) ? settings.NewPasswordTemplateEn : newPasswordTemplateEn.Trim();
            settings.ModifiedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> SaveDomain(int id, string name, string adServer, int adPort,
            int adSecurityMode, bool adAllowUntrustedCertificate,
            string? adUsername, string? adPassword, string adBaseDN, string nationalIdAttribute, string mobileAttribute,
            string? excludedGroups, bool isActive)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(adServer) || string.IsNullOrWhiteSpace(adBaseDN))
                return Json(new { success = false, message = "الاسم والسيرفر وBase DN مطلوبة" });

            var domain = id > 0 ? await _db.SsprDomains.FindAsync(id) : new SsprDomain();
            if (domain == null) return Json(new { success = false, message = "غير موجود" });
            if (id == 0) _db.SsprDomains.Add(domain);

            domain.Name = name.Trim();
            domain.AdServer = adServer.Trim();
            domain.AdPort = adPort <= 0 ? 389 : adPort;
            // Explicit channel choice now wins; keep AdUseSsl in sync so anything still
            // reading the legacy flag sees something sensible.
            domain.AdSecurityMode = Enum.IsDefined(typeof(LdapSecurityMode), adSecurityMode)
                ? (LdapSecurityMode)adSecurityMode
                : LdapSecurityMode.Auto;
            domain.AdSecurityModeSet = true;
            domain.AdAllowUntrustedCertificate = adAllowUntrustedCertificate;
            domain.AdUseSsl = domain.AdSecurityMode == LdapSecurityMode.Ldaps;
            domain.AdUsername = adUsername?.Trim();
            // 🔐 only overwrite the password when a new value is supplied
            if (!string.IsNullOrEmpty(adPassword)) domain.AdPassword = adPassword;
            domain.AdBaseDN = adBaseDN.Trim();
            domain.NationalIdAttribute = string.IsNullOrWhiteSpace(nationalIdAttribute) ? "employeeNumber" : nationalIdAttribute.Trim();
            domain.MobileAttribute = string.IsNullOrWhiteSpace(mobileAttribute) ? "mobile" : mobileAttribute.Trim();
            domain.ExcludedGroups = excludedGroups?.Trim();
            domain.IsActive = isActive;
            await _db.SaveChangesAsync();

            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteDomain(int id)
        {
            var domain = await _db.SsprDomains.FindAsync(id);
            if (domain == null) return Json(new { success = false, message = "غير موجود" });
            _db.SsprDomains.Remove(domain);
            await _db.SaveChangesAsync();
            return Json(new { success = true });
        }

        /// <summary>Test the AD bind for a domain using its stored (or just-entered) credentials.</summary>
        [HttpPost]
        public async Task<IActionResult> TestDomain(int id)
        {
            var domain = await _db.SsprDomains.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
            if (domain == null) return Json(new { success = false, message = "غير موجود" });

            var opts = domain.ToLdapOptions();
            var connector = new ActiveDirectoryConnector(new Core.Models.Connectors.ADConnectionSettings
            {
                Server = domain.AdServer,
                Port = domain.AdPort,
                SecurityMode = opts.SecurityMode,
                SecurityModeSet = true,
                AllowUntrustedCertificate = opts.AllowUntrustedCertificate,
                Username = domain.AdUsername,
                Password = domain.AdPassword,
                BaseDN = domain.AdBaseDN
            }, _loggerFactory.CreateLogger<ActiveDirectoryConnector>());

            // Spell out the channel actually used — the difference between a working and a
            // silently-plaintext connection is invisible until a password write fails.
            var channel = LdapConnectionFactory.Describe(opts.SecurityMode, domain.AdPort);
            var ok = await connector.TestConnectionAsync();
            return Json(new
            {
                success = ok,
                message = ok
                    ? $"الاتصال ناجح — {channel}"
                    : $"فشل الاتصال ({channel}) — تحقق من البيانات والمنفذ ونوع التشفير"
            });
        }

        /// <summary>
        /// Dedicated audit log for self-service password reset operations
        /// (from the PasswordResetRequests table + resolved domain name).
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Log(string? username, string? status, DateTime? dateFrom, DateTime? dateTo, int page = 1)
        {
            const int pageSize = 30;

            var query = _db.PasswordResetRequests.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(username))
                query = query.Where(r => r.Username.Contains(username));
            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(r => r.Status == status);
            if (dateFrom.HasValue)
                query = query.Where(r => r.CreatedAtUtc >= dateFrom.Value);
            if (dateTo.HasValue)
                query = query.Where(r => r.CreatedAtUtc < dateTo.Value.AddDays(1));

            var total = await query.CountAsync();

            // Summary cards over the whole filtered set
            ViewBag.CountCompleted = await query.CountAsync(r => r.Status == "Completed");
            ViewBag.CountBlocked = await query.CountAsync(r => r.Status == "Blocked");
            ViewBag.CountExpired = await query.CountAsync(r => r.Status == "Expired");
            ViewBag.CountPending = await query.CountAsync(r => r.Status == "Pending");

            var rows = await query
                .OrderByDescending(r => r.CreatedAtUtc)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            // Resolve domain names for the shown rows
            var domainIds = rows.Select(r => r.SsprDomainId).Distinct().ToList();
            ViewBag.DomainNames = await _db.SsprDomains.AsNoTracking()
                .Where(d => domainIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.Name);

            ViewBag.Lang = await GetLangAsync();
            ViewBag.Username = username;
            ViewBag.Status = status;
            ViewBag.DateFrom = dateFrom?.ToString("yyyy-MM-dd");
            ViewBag.DateTo = dateTo?.ToString("yyyy-MM-dd");
            ViewBag.CurrentPage = page;
            ViewBag.TotalCount = total;
            ViewBag.TotalPages = (int)Math.Ceiling((double)total / pageSize);

            return View(rows);
        }

        /// <summary>Export the filtered reset log to Excel (same filters as the Log page).</summary>
        [HttpGet]
        public async Task<IActionResult> ExportLog(string? username, string? status, DateTime? dateFrom, DateTime? dateTo)
        {
            var query = _db.PasswordResetRequests.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(username))
                query = query.Where(r => r.Username.Contains(username));
            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(r => r.Status == status);
            if (dateFrom.HasValue)
                query = query.Where(r => r.CreatedAtUtc >= dateFrom.Value);
            if (dateTo.HasValue)
                query = query.Where(r => r.CreatedAtUtc < dateTo.Value.AddDays(1));

            var rows = await query.OrderByDescending(r => r.CreatedAtUtc).Take(10000).ToListAsync();

            var domainIds = rows.Select(r => r.SsprDomainId).Distinct().ToList();
            var domainNames = await _db.SsprDomains.AsNoTracking()
                .Where(d => domainIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.Name);

            var isArabic = (await GetLangAsync()) == "ar";
            var bytes = _excelExport.ExportPasswordResetLog(rows, domainNames, isArabic);
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"PasswordResetLog_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
        }

        private async Task<string> GetLangAsync()
        {
            var setting = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "Language");
            return setting?.Value ?? "ar";
        }
    }
}
