using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentitySyncPro.Web.Controllers
{
    /// <summary>
    /// Email transport (SMTP) providers, managed from the Notifications Center.
    /// Supports Authenticated SMTP and Direct Send; the active one is used for all outbound email.
    /// </summary>
    [Microsoft.AspNetCore.Authorization.Authorize(Roles = IdentitySyncPro.Core.Models.Settings.AppUserRoles.Admin)]
    public class EmailProviderController : Controller
    {
        private readonly AppDbContext _db;
        private readonly IEmailService _emailService;
        private readonly ILogger<EmailProviderController> _logger;

        public EmailProviderController(AppDbContext db, IEmailService emailService, ILogger<EmailProviderController> logger)
        {
            _db = db;
            _emailService = emailService;
            _logger = logger;
        }

        private string Lang => HttpContext?.Items["Lang"] as string ?? "ar";

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var providers = await _db.EmailProviders
                .OrderByDescending(e => e.IsActive)
                .ThenBy(e => e.Name)
                .ToListAsync();
            ViewBag.Lang = Lang;
            return View(providers);
        }

        [HttpGet]
        public IActionResult Create()
        {
            ViewBag.Lang = Lang;
            return View("Form", new EmailProvider());
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var provider = await _db.EmailProviders.FindAsync(id);
            if (provider == null) return NotFound();
            ViewBag.Lang = Lang;
            return View("Form", provider);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(EmailProvider model)
        {
            if (string.IsNullOrWhiteSpace(model.Name) || string.IsNullOrWhiteSpace(model.SmtpHost))
            {
                ModelState.AddModelError("", "الاسم وخادم SMTP مطلوبان / Name and SMTP host are required");
                ViewBag.Lang = Lang;
                return View("Form", model);
            }

            if (model.Id == 0)
            {
                model.CreatedAt = DateTime.UtcNow;
                _db.EmailProviders.Add(model);
                await _db.SaveChangesAsync();
                if (model.IsActive) await SetActiveExclusiveAsync(model.Id);
            }
            else
            {
                var existing = await _db.EmailProviders.FindAsync(model.Id);
                if (existing == null) return NotFound();

                existing.Name = model.Name;
                existing.Mode = model.Mode;
                existing.SmtpHost = model.SmtpHost;
                existing.SmtpPort = model.SmtpPort;
                existing.Username = model.Username;
                if (!string.IsNullOrEmpty(model.Password)) existing.Password = model.Password; // blank = keep
                existing.FromEmail = model.FromEmail;
                existing.FromName = model.FromName;
                existing.EnableSsl = model.EnableSsl;
                existing.Notes = model.Notes;
                existing.IsActive = model.IsActive;
                await _db.SaveChangesAsync();
                if (model.IsActive) await SetActiveExclusiveAsync(existing.Id);
            }

            TempData["Success"] = "تم حفظ إعدادات البريد / Email settings saved";
            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var provider = await _db.EmailProviders.FindAsync(id);
            if (provider != null)
            {
                _db.EmailProviders.Remove(provider);
                await _db.SaveChangesAsync();
                TempData["Success"] = "تم الحذف / Deleted";
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleActive(int id)
        {
            var provider = await _db.EmailProviders.FindAsync(id);
            if (provider == null) return Json(new { success = false });

            provider.IsActive = !provider.IsActive;
            await _db.SaveChangesAsync();
            if (provider.IsActive) await SetActiveExclusiveAsync(id);

            return Json(new { success = true, isActive = provider.IsActive });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Test(int id, string toEmail)
        {
            if (string.IsNullOrWhiteSpace(toEmail))
                return Json(new { success = false, error = "أدخل بريد المستلم / Enter a recipient" });

            var provider = await _db.EmailProviders.FindAsync(id);
            if (provider == null)
                return Json(new { success = false, error = "المزود غير موجود / Provider not found" });

            var result = await _emailService.SendViaAsync(provider, new EmailMessage
            {
                To = toEmail,
                Subject = "IdentitySyncPro — SMTP Test",
                Body = "<p>This is a test email from IdentitySyncPro. If you received it, the email settings work.</p>",
                IsHtml = true
            });

            return result.Success
                ? Json(new { success = true })
                : Json(new { success = false, error = result.Error });
        }

        /// <summary>Ensure only one provider is active (single transport).</summary>
        private async Task SetActiveExclusiveAsync(int keepId)
        {
            var others = await _db.EmailProviders.Where(e => e.Id != keepId && e.IsActive).ToListAsync();
            foreach (var o in others) o.IsActive = false;
            if (others.Count > 0) await _db.SaveChangesAsync();
        }
    }
}
