using System.Text.Json.Serialization;
using IdentitySyncPro.Core.Models.AccountStatus;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentitySyncPro.Web.Controllers
{
    /// <summary>
    /// Controller for Account Enable/Disable page.
    /// Completely independent from IAM and Services modules.
    /// </summary>
    [Microsoft.AspNetCore.Authorization.Authorize(Roles = IdentitySyncPro.Core.Models.Settings.AppUserRoles.AdminOrOperator)]
    public class AccountStatusController : Controller
    {
        private readonly AccountStatusDbContext _db;
        private readonly AppDbContext _appDb;
        private readonly AccountStatusService _accountService;
        private readonly ISmsService _smsService;
        private readonly ILogger<AccountStatusController> _logger;

        public AccountStatusController(
            AccountStatusDbContext db,
            AppDbContext appDb,
            AccountStatusService accountService,
            ISmsService smsService,
            ILogger<AccountStatusController> logger)
        {
            _db = db;
            _appDb = appDb;
            _accountService = accountService;
            _smsService = smsService;
            _logger = logger;
        }

        // ══════════════════════════════════════
        // INDEX — Main page
        // ══════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            // Get custom domains (added from Account Status page only)
            var domains = await GetAvailableDomainsAsync();
            ViewBag.Domains = domains;

            // Get recent logs (last 50)
            var recentLogs = await _db.AccountStatusLogs
                .OrderByDescending(l => l.Timestamp)
                .Take(50)
                .ToListAsync();

            return View(recentLogs);
        }

        // ══════════════════════════════════════
        // GET DOMAINS — API for dropdown
        // ══════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> GetDomains()
        {
            var domains = await GetAvailableDomainsAsync();
            var safeDomains = domains.Select(d => new
            {
                d.Key,
                d.DisplayName,
                d.Server,
                d.Port,
                d.BaseDN,
                d.Username
            });
            return Json(safeDomains);
        }

        // ══════════════════════════════════════
        // TEST DOMAIN CONNECTION
        // ══════════════════════════════════════
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult TestDomainConnection([FromBody] CustomDomain model)
        {
            if (string.IsNullOrWhiteSpace(model.Server))
                return Json(new { success = false, message = "عنوان السيرفر مطلوب / Server is required" });
            if (string.IsNullOrWhiteSpace(model.BaseDN))
                return Json(new { success = false, message = "Base DN مطلوب / Base DN is required" });
            if (model.Port < 1 || model.Port > 65535)
                return Json(new { success = false, message = "رقم المنفذ غير صالح (1-65535) / Invalid port (1-65535)" });

            var (success, message) = _accountService.TestConnection(
                model.Server, model.Port, model.Username, model.Password);

            return Json(new { success, message });
        }

        // ══════════════════════════════════════
        // DELETE CUSTOM DOMAIN
        // ══════════════════════════════════════
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteDomain(int id)
        {
            var domain = await _db.CustomDomains.FindAsync(id);
            if (domain == null)
                return Json(new { success = false, message = "الدومين غير موجود / Domain not found" });

            _db.CustomDomains.Remove(domain);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Custom domain '{Name}' (ID {Id}) deleted", domain.DisplayName, id);
            return Json(new { success = true });
        }

        // ══════════════════════════════════════
        // SAVE CUSTOM DOMAIN
        // ══════════════════════════════════════
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveDomain([FromBody] CustomDomain model)
        {
            if (string.IsNullOrWhiteSpace(model.DisplayName))
                return Json(new { success = false, message = "اسم الدومين مطلوب / Domain name is required" });
            if (string.IsNullOrWhiteSpace(model.Server))
                return Json(new { success = false, message = "عنوان السيرفر مطلوب / Server is required" });
            if (string.IsNullOrWhiteSpace(model.BaseDN))
                return Json(new { success = false, message = "Base DN مطلوب / Base DN is required" });
            if (model.Port < 1 || model.Port > 65535)
                return Json(new { success = false, message = "رقم المنفذ غير صالح (1-65535) / Invalid port (1-65535)" });

            // Duplicate check
            var exists = await _db.CustomDomains
                .AnyAsync(d => d.Server == model.Server && d.BaseDN == model.BaseDN);
            if (exists)
                return Json(new { success = false, message = "هذا الدومين موجود مسبقاً / Domain already exists" });

            model.PhoneAttribute = string.IsNullOrWhiteSpace(model.PhoneAttribute) ? "mobile" : model.PhoneAttribute.Trim();

            _db.CustomDomains.Add(model);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Custom domain '{Name}' ({Server}) added with ID {Id}", model.DisplayName, model.Server, model.Id);
            return Json(new { success = true, domain = new { model.Id, model.DisplayName, model.Server, model.Port, model.BaseDN, model.Username } });
        }

        // ══════════════════════════════════════
        // GET ONE CUSTOM DOMAIN — for the edit form (password never returned)
        // ══════════════════════════════════════
        [HttpGet]
        public async Task<IActionResult> GetDomain(int id)
        {
            var d = await _db.CustomDomains.FindAsync(id);
            if (d == null) return Json(new { success = false, message = "الدومين غير موجود / Domain not found" });

            return Json(new
            {
                success = true,
                domain = new { d.Id, d.DisplayName, d.Server, d.Port, d.BaseDN, d.Username, d.PhoneAttribute }
            });
        }

        // ══════════════════════════════════════
        // UPDATE CUSTOM DOMAIN — edit & save (e.g. switch the mobile-number attribute)
        // ══════════════════════════════════════
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateDomain([FromBody] CustomDomain model)
        {
            var domain = await _db.CustomDomains.FindAsync(model.Id);
            if (domain == null)
                return Json(new { success = false, message = "الدومين غير موجود / Domain not found" });

            if (string.IsNullOrWhiteSpace(model.DisplayName))
                return Json(new { success = false, message = "اسم الدومين مطلوب / Domain name is required" });
            if (string.IsNullOrWhiteSpace(model.Server))
                return Json(new { success = false, message = "عنوان السيرفر مطلوب / Server is required" });
            if (string.IsNullOrWhiteSpace(model.BaseDN))
                return Json(new { success = false, message = "Base DN مطلوب / Base DN is required" });
            if (model.Port < 1 || model.Port > 65535)
                return Json(new { success = false, message = "رقم المنفذ غير صالح (1-65535) / Invalid port (1-65535)" });

            domain.DisplayName = model.DisplayName.Trim();
            domain.Server = model.Server.Trim();
            domain.Port = model.Port;
            domain.BaseDN = model.BaseDN.Trim();
            domain.Username = model.Username?.Trim();
            // 🔐 blank password = keep the stored one
            if (!string.IsNullOrEmpty(model.Password)) domain.Password = model.Password;
            // Switching the mobile attribute simply replaces the previous value.
            domain.PhoneAttribute = string.IsNullOrWhiteSpace(model.PhoneAttribute) ? "mobile" : model.PhoneAttribute.Trim();

            await _db.SaveChangesAsync();

            _logger.LogInformation("Custom domain '{Name}' (ID {Id}) updated — phone attribute: {Attr}",
                domain.DisplayName, domain.Id, domain.PhoneAttribute);
            return Json(new { success = true });
        }

        // ══════════════════════════════════════
        // SEARCH USER — AJAX
        // ══════════════════════════════════════
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SearchUser([FromBody] SearchUserRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.SamAccountName))
                return Json(new { success = false, message = "اسم المستخدم مطلوب / Username is required" });

            if (string.IsNullOrWhiteSpace(request.DomainKey))
                return Json(new { success = false, message = "يجب اختيار دومين / Please select a domain" });

            try
            {
                var domain = await ResolveDomainAsync(request.DomainKey);
                if (domain == null)
                    return Json(new { success = false, message = "الدومين غير موجود / Domain not found" });

                var user = _accountService.SearchUser(
                    domain.Server, domain.Port, domain.BaseDN,
                    domain.Username, domain.Password,
                    request.SamAccountName.Trim(), domain.PhoneAttribute);

                if (user == null)
                    return Json(new { success = false, message = "المستخدم غير موجود / User not found" });

                return Json(new
                {
                    success = true,
                    user = new
                    {
                        user.SamAccountName,
                        user.DisplayName,
                        user.Email,
                        user.PhoneNumber,
                        user.Department,
                        user.Title,
                        user.IsDisabled,
                        status = user.IsDisabled ? "Disabled" : "Enabled",
                        statusAr = user.IsDisabled ? "معطّل" : "مفعّل",
                        domain = domain.DisplayName
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching user {SamAccountName}", request.SamAccountName);
                return Json(new { success = false, message = $"خطأ في البحث: {ex.Message}" });
            }
        }

        // ══════════════════════════════════════
        // TOGGLE STATUS — Enable/Disable + SMS + Log
        // ══════════════════════════════════════
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleStatus([FromBody] ToggleStatusRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.SamAccountName))
                return Json(new { success = false, message = "اسم المستخدم مطلوب" });
            if (string.IsNullOrWhiteSpace(request.Reason))
                return Json(new { success = false, message = "السبب مطلوب / Reason is required" });

            try
            {
                var domain = await ResolveDomainAsync(request.DomainKey);
                if (domain == null)
                    return Json(new { success = false, message = "الدومين غير موجود" });

                // Get current user info first
                var user = _accountService.SearchUser(
                    domain.Server, domain.Port, domain.BaseDN,
                    domain.Username, domain.Password,
                    request.SamAccountName.Trim(), domain.PhoneAttribute);

                if (user == null)
                    return Json(new { success = false, message = "المستخدم غير موجود" });

                // Determine action
                bool enable = user.IsDisabled; // If disabled → enable, if enabled → disable
                string action = enable ? "Enable" : "Disable";
                string previousStatus = user.IsDisabled ? "Disabled" : "Enabled";
                string newStatus = enable ? "Enabled" : "Disabled";

                // Perform the toggle
                var success = _accountService.ToggleAccountStatus(
                    domain.Server, domain.Port, domain.BaseDN,
                    domain.Username, domain.Password,
                    request.SamAccountName.Trim(), enable);

                if (!success)
                    return Json(new { success = false, message = "فشل تغيير حالة الحساب / Failed to toggle account status" });

                // Send SMS notification
                var smsPhone = !string.IsNullOrWhiteSpace(request.PhoneNumber) ? request.PhoneNumber : user.PhoneNumber;
                var (smsSent, smsResult) = await SendToggleSmsAsync(request, user.DisplayName, enable, smsPhone);

                // Who did it comes from the authentication cookie. Falling back to "System" here
                // would be a lie — this endpoint requires a signed-in user to reach.
                var performedBy = User.Identity?.Name ?? "Unknown";

                // Save log entry
                var logEntry = new AccountStatusLog
                {
                    SamAccountName = request.SamAccountName.Trim(),
                    DisplayName = user.DisplayName,
                    Domain = domain.DisplayName,
                    Action = action,
                    Reason = request.Reason,
                    PreviousStatus = previousStatus,
                    NewStatus = newStatus,
                    SmsSent = smsSent,
                    SmsResult = smsResult,
                    PhoneNumber = smsPhone,
                    PerformedBy = performedBy,
                    Timestamp = DateTime.UtcNow
                };

                _db.AccountStatusLogs.Add(logEntry);
                await _db.SaveChangesAsync();

                _logger.LogInformation(
                    "Account {Action}: {SamAccountName} in {Domain} by {PerformedBy}. Reason: {Reason}",
                    action, request.SamAccountName, domain.DisplayName, performedBy, request.Reason);

                return Json(new
                {
                    success = true,
                    message = enable
                        ? "تم تفعيل الحساب بنجاح / Account enabled successfully"
                        : "تم تعطيل الحساب بنجاح / Account disabled successfully",
                    action,
                    newStatus,
                    smsSent,
                    smsResult,
                    logId = logEntry.Id
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error toggling status for {SamAccountName}", request.SamAccountName);
                return Json(new { success = false, message = $"خطأ: {ex.Message}" });
            }
        }

        // ══════════════════════════════════════
        // GET LOGS — with filtering and paging
        // ══════════════════════════════════════
        [HttpGet]
        // ⚠️ NOT named `action`: with the default {controller}/{action}/{id?} route, a parameter
        // called `action` binds from the route and silently receives the method name — so the
        // filter became Action == "GetLogs" and this log always came back empty.
        public async Task<IActionResult> GetLogs(string? search,
            [FromQuery(Name = "action")] string? actionType,
            string? domain, DateTime? dateFrom, DateTime? dateTo, int page = 1, int pageSize = 20)
        {
            var action = actionType;
            var query = _db.AccountStatusLogs.AsQueryable();

            if (!string.IsNullOrEmpty(search))
                query = query.Where(l => l.SamAccountName.Contains(search) || l.DisplayName.Contains(search));

            if (!string.IsNullOrEmpty(action))
                query = query.Where(l => l.Action == action);

            if (!string.IsNullOrEmpty(domain))
                query = query.Where(l => l.Domain == domain);

            if (dateFrom.HasValue)
                query = query.Where(l => l.Timestamp >= dateFrom.Value);

            if (dateTo.HasValue)
                query = query.Where(l => l.Timestamp < dateTo.Value.AddDays(1));

            var totalCount = await query.CountAsync();
            var logs = await query
                .OrderByDescending(l => l.Timestamp)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Json(new
            {
                logs = logs.Select(l => new
                {
                    l.Id,
                    l.SamAccountName,
                    l.DisplayName,
                    l.Domain,
                    l.Action,
                    l.Reason,
                    l.PreviousStatus,
                    l.NewStatus,
                    l.SmsSent,
                    l.SmsResult,
                    l.PhoneNumber,
                    l.PerformedBy,
                    timestamp = l.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                }),
                totalCount,
                totalPages = (int)Math.Ceiling((double)totalCount / pageSize),
                currentPage = page
            });
        }

        // ══════════════════════════════════════
        // EXPORT EXCEL
        // ══════════════════════════════════════
        [HttpGet]
        // ⚠️ See GetLogs — `action` collides with the route token and must stay renamed.
        public async Task<IActionResult> ExportExcel(string? search,
            [FromQuery(Name = "action")] string? actionType,
            string? domain, DateTime? dateFrom, DateTime? dateTo)
        {
            var action = actionType;
            var query = _db.AccountStatusLogs.AsQueryable();

            if (!string.IsNullOrEmpty(search))
                query = query.Where(l => l.SamAccountName.Contains(search) || l.DisplayName.Contains(search));

            if (!string.IsNullOrEmpty(action))
                query = query.Where(l => l.Action == action);

            if (!string.IsNullOrEmpty(domain))
                query = query.Where(l => l.Domain == domain);

            if (dateFrom.HasValue)
                query = query.Where(l => l.Timestamp >= dateFrom.Value);

            if (dateTo.HasValue)
                query = query.Where(l => l.Timestamp < dateTo.Value.AddDays(1));

            var logs = await query.OrderByDescending(l => l.Timestamp).ToListAsync();

            // Determine language
            var langSetting = await _appDb.AppSettings.FirstOrDefaultAsync(s => s.Key == "Language");
            var isArabic = langSetting?.Value != "en";

            var excelBytes = _accountService.ExportToExcel(logs, isArabic);

            var fileName = $"AccountStatusLogs_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
            return File(excelBytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                fileName);
        }

        // ══════════════════════════════════════
        // PRIVATE HELPERS
        // ══════════════════════════════════════

        private async Task<(bool Sent, string? Result)> SendToggleSmsAsync(
            ToggleStatusRequest request, string? displayName, bool enable, string smsPhone)
        {
            // Unified SMS log entry (reviewed/retried from the SMS Center alongside sync + offboarding).
            var smsLog = new SmsSendLog
            {
                Source = "AccountStatus",
                IdentityId = 0,
                Account = request.SamAccountName,
                DisplayName = displayName,
                PhoneNumber = smsPhone,
                Status = "Skipped",
                CreatedAt = DateTime.UtcNow,
                LastAttemptAt = DateTime.UtcNow
            };
            _appDb.SmsSendLogs.Add(smsLog);

            try
            {
                if (string.IsNullOrWhiteSpace(smsPhone) || string.IsNullOrWhiteSpace(request.SmsMessageTemplate))
                {
                    smsLog.GatewayResponse = string.IsNullOrWhiteSpace(smsPhone) ? "No phone number" : "No message template";
                    return (false, smsLog.GatewayResponse);
                }

                string apiUrl = "", apiUsername = "", apiPassword = "", senderName = "", providerName = "(inline)";
                IdentitySyncPro.Core.Models.Settings.SmsProvider? resolvedProvider = null;

                if (request.SmsProviderId.HasValue)
                {
                    var provider = await _appDb.SmsProviders.FindAsync(request.SmsProviderId.Value);
                    if (provider == null || !provider.IsActive)
                    {
                        smsLog.GatewayResponse = "SMS provider not found or inactive";
                        return (false, smsLog.GatewayResponse);
                    }

                    resolvedProvider = provider;
                    apiUrl = provider.ApiUrl;
                    apiUsername = provider.ApiUsername;
                    apiPassword = provider.ApiPassword;
                    senderName = provider.SenderName;
                    providerName = provider.Name;
                }
                else
                {
                    var smsSettings = await GetSmsSettingsAsync();
                    if (smsSettings != null)
                    {
                        apiUrl = smsSettings.SmsApiUrl;
                        apiUsername = smsSettings.SmsApiUsername;
                        apiPassword = smsSettings.SmsApiPassword;
                        senderName = smsSettings.SmsSenderName;
                    }
                }

                if (string.IsNullOrWhiteSpace(apiUrl))
                {
                    smsLog.GatewayResponse = "SMS not configured";
                    return (false, smsLog.GatewayResponse);
                }

                var actionText = enable ? "تفعيل" : "تعطيل";
                var message = request.SmsMessageTemplate
                    .Replace("{USERNAME}", request.SamAccountName)
                    .Replace("{DISPLAY_NAME}", displayName ?? "")
                    .Replace("{ACTION}", actionText)
                    .Replace("{REASON}", request.Reason);

                smsLog.ProviderName = providerName;

                var smsRequest = new SmsRequest
                {
                    ApiUrl = apiUrl,
                    ApiUsername = apiUsername,
                    ApiPassword = apiPassword,
                    SenderName = senderName,
                    PhoneNumber = smsPhone,
                    Username = request.SamAccountName,
                    DisplayName = displayName ?? "",
                    IdentityId = request.SamAccountName,
                    MessageTemplate = message,
                    Password = ""
                };
                // Carry the provider's generic gateway config (method/format/template/headers).
                if (resolvedProvider != null) smsRequest.WithProvider(resolvedProvider);

                var result = await _smsService.SendCredentialsAsync(smsRequest);

                smsLog.Status = result.Success ? "Success" : "Failed";
                smsLog.GatewayResponse = TruncSms(result.Success ? result.Response : result.Error);
                smsLog.SentMessage = result.Success ? null : message; // keep for retry only on failure

                return (result.Success, result.Success ? "Sent" : result.Error);
            }
            catch (Exception smsEx)
            {
                smsLog.Status = "Failed";
                smsLog.GatewayResponse = TruncSms($"SMS Error: {smsEx.Message}");
                _logger.LogWarning(smsEx, "SMS failed for {SamAccountName}", request.SamAccountName);
                return (false, $"SMS Error: {smsEx.Message}");
            }
            finally
            {
                try { await _appDb.SaveChangesAsync(); }
                catch (Exception saveEx) { _logger.LogWarning(saveEx, "AccountStatus: failed to write SMS log for {Sam}", request.SamAccountName); }
            }
        }

        private static string? TruncSms(string? v) => v != null && v.Length > 2000 ? v[..2000] : v;

        private async Task<List<DomainInfo>> GetAvailableDomainsAsync()
        {
            var domains = new List<DomainInfo>();

            // Only from CustomDomains (added from Account Status page)
            var customDomains = await _db.CustomDomains.ToListAsync();
            foreach (var c in customDomains)
            {
                domains.Add(new DomainInfo
                {
                    Key = $"custom-{c.Id}",
                    DisplayName = $"{c.DisplayName} ({c.Server})",
                    Server = c.Server,
                    Port = c.Port,
                    BaseDN = c.BaseDN,
                    Username = c.Username,
                    Password = c.Password
                });
            }

            return domains;
        }

        private async Task<DomainInfo?> ResolveDomainAsync(string domainKey)
        {
            if (domainKey.StartsWith("custom-"))
            {
                var suffix = domainKey.Replace("custom-", "");
                if (!int.TryParse(suffix, out var id))
                {
                    _logger.LogWarning("Invalid custom domain key format: {DomainKey}", domainKey);
                    return null;
                }
                var custom = await _db.CustomDomains.FindAsync(id);
                if (custom == null) return null;

                return new DomainInfo
                {
                    Key = domainKey,
                    DisplayName = $"{custom.DisplayName} ({custom.Server})",
                    Server = custom.Server,
                    Port = custom.Port,
                    BaseDN = custom.BaseDN,
                    Username = custom.Username,
                    Password = custom.Password,
                    PhoneAttribute = custom.PhoneAttribute
                };
            }

            return null;
        }

        private async Task<SmsSettingsInfo?> GetSmsSettingsAsync()
        {
            var tenant = await _appDb.TenantSettings
                .Where(t => t.IsActive && t.EnableSmsNotification)
                .FirstOrDefaultAsync();

            if (tenant == null) return null;

            // Resolve from SmsProvider if configured
            if (tenant.SmsProviderId.HasValue)
            {
                var provider = await _appDb.SmsProviders.FindAsync(tenant.SmsProviderId.Value);
                if (provider != null && provider.IsActive)
                {
                    return new SmsSettingsInfo
                    {
                        SmsApiUrl = provider.ApiUrl,
                        SmsApiUsername = provider.ApiUsername,
                        SmsApiPassword = provider.ApiPassword,
                        SmsSenderName = provider.SenderName
                    };
                }
            }

            // Fallback to legacy inline settings
            return new SmsSettingsInfo
            {
                SmsApiUrl = tenant.SmsApiUrl,
                SmsApiUsername = tenant.SmsApiUsername,
                SmsApiPassword = tenant.SmsApiPassword,
                SmsSenderName = tenant.SmsSenderName
            };
        }
    }

    // ══════════════════════════════════════
    // DTOs
    // ══════════════════════════════════════

    public class DomainInfo
    {
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Server { get; set; } = string.Empty;
        public int Port { get; set; } = 389;
        public string BaseDN { get; set; } = string.Empty;
        public string? Username { get; set; }
        public string? Password { get; set; }
        /// <summary>Domain's configured AD attribute holding the mobile number.</summary>
        public string? PhoneAttribute { get; set; }
    }

    public class SearchUserRequest
    {
        public string SamAccountName { get; set; } = string.Empty;
        public string DomainKey { get; set; } = string.Empty;
    }

    public class ToggleStatusRequest
    {
        public string SamAccountName { get; set; } = string.Empty;
        public string DomainKey { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        // No PerformedBy: the operator is taken from the signed-in session, never from the request.
        // It used to be a free-text box on the page, so the record of who disabled an account was
        // whatever the browser chose to send — blank, or anyone's name.
        public int? SmsProviderId { get; set; }
        public string? PhoneNumber { get; set; }
        public string? SmsMessageTemplate { get; set; }
    }

    public class SmsSettingsInfo
    {
        public string SmsApiUrl { get; set; } = string.Empty;
        public string SmsApiUsername { get; set; } = string.Empty;
        public string SmsApiPassword { get; set; } = string.Empty;
        public string SmsSenderName { get; set; } = string.Empty;
    }
}
