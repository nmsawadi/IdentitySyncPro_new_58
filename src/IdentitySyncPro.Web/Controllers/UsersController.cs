using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Connectors;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Connectors;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Security;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentitySyncPro.Web.Controllers
{
    /// <summary>Console user management — Admin only.</summary>
    [Authorize(Roles = AppUserRoles.Admin)]
    public class UsersController : Controller
    {
        private readonly AppDbContext _db;
        private readonly IAuditService _audit;
        private readonly MfaService _mfa;

        public UsersController(AppDbContext db, IAuditService audit, MfaService mfa)
        {
            _db = db;
            _audit = audit;
            _mfa = mfa;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var users = await _db.AppUsers.AsNoTracking().OrderBy(u => u.Username).ToListAsync();
            // Surfaced on the list so "my AD user cannot log in" is answered before it is asked.
            ViewBag.SignInDomainCount = await _db.AuthDomains.CountAsync(d => d.IsActive);
            ViewBag.MfaSettings = await _mfa.GetSettingsAsync();
            return View(users);
        }

        // ══════════════════════════════════════════════════════════
        // Multi-factor authentication
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Turns the institution-wide MFA policy on or off.
        ///
        /// Enabling it while no administrator is enrolled is allowed: the sign-in flow walks the
        /// first one through enrollment rather than refusing them. What would be unrecoverable is
        /// the opposite — silently enabling it at upgrade time — which is why the stored default
        /// is off.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> SaveMfaSettings(bool isEnabled, string? requiredRoles, bool enforceEnrollment)
        {
            var settings = await _mfa.GetSettingsAsync();

            var roles = (requiredRoles ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(r => AppUserRoles.All.Contains(r, StringComparer.OrdinalIgnoreCase))
                .ToList();

            // An empty role list with MFA on protects nobody while looking like it is enforced.
            if (isEnabled && roles.Count == 0)
                return Json(new { success = false, message = "اختر دوراً واحداً على الأقل يُطبَّق عليه التحقق بخطوتين" });

            settings.IsEnabled = isEnabled;
            settings.RequiredRoles = string.Join(",", roles);
            settings.EnforceEnrollment = enforceEnrollment;
            settings.ModifiedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            await _audit.LogAsync(
                $"MFA policy changed: enabled={isEnabled}, roles=[{settings.RequiredRoles}], enforceEnrollment={enforceEnrollment} by {User.Identity?.Name}",
                "Security", Core.Enums.AuditSeverity.Warning);

            return Json(new { success = true });
        }

        /// <summary>
        /// Clears a user's enrollment so they can register a replacement device.
        ///
        /// The operational counterpart to a lost phone. It is a genuine privilege — an admin who
        /// can reset another admin's MFA can strip their second factor — so every use is audited
        /// at Warning level.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> ResetMfa(int id)
        {
            var user = await _db.AppUsers.FindAsync(id);
            if (user == null) return Json(new { success = false, message = "غير موجود" });
            if (!user.IsMfaEnrolled) return Json(new { success = false, message = "هذا المستخدم غير مسجَّل في التحقق بخطوتين" });

            await _mfa.ResetAsync(id);
            await _audit.LogAsync($"MFA reset for {user.Username} by {User.Identity?.Name}",
                "Security", Core.Enums.AuditSeverity.Warning);

            return Json(new { success = true, message = "أُلغي تسجيل التحقق بخطوتين — سيُطلب منه التسجيل من جديد عند الدخول." });
        }

        [HttpPost]
        public async Task<IActionResult> Create(string username, string displayName, string role, string authType, string? password)
        {
            username = (username ?? "").Trim();
            if (username.Length == 0)
                return Json(new { success = false, message = "اسم المستخدم مطلوب" });

            if (!AppUserRoles.All.Contains(role))
                return Json(new { success = false, message = "دور غير صالح" });

            if (await _db.AppUsers.AnyAsync(u => u.Username == username))
                return Json(new { success = false, message = "اسم المستخدم موجود مسبقاً" });

            var isLocal = authType != AppUserAuthTypes.ActiveDirectory;
            string? passwordHash = null;

            if (isLocal)
            {
                var policyError = AuthService.ValidatePasswordPolicy(password ?? "");
                if (policyError != null)
                    return Json(new { success = false, message = "كلمة المرور: 10 أحرف على الأقل وتحوي حروفاً وأرقاماً" });
                passwordHash = PasswordHasher.Hash(password!);
            }

            _db.AppUsers.Add(new AppUser
            {
                Username = username,
                DisplayName = (displayName ?? "").Trim(),
                Role = role,
                AuthType = isLocal ? AppUserAuthTypes.Local : AppUserAuthTypes.ActiveDirectory,
                PasswordHash = passwordHash,
                MustChangePassword = isLocal, // first login forces a personal password
                // Starts the maximum-age clock. Left null for AD users — the domain owns theirs.
                PasswordChangedUtc = isLocal ? DateTime.UtcNow : null,
                IsActive = true
            });
            await _db.SaveChangesAsync();

            await _audit.LogAsync($"Console user created: {username} ({role}, {authType}) by {User.Identity?.Name}", "Security", Core.Enums.AuditSeverity.Info);
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> Update(int id, string displayName, string role)
        {
            var user = await _db.AppUsers.FindAsync(id);
            if (user == null) return Json(new { success = false, message = "غير موجود" });
            if (!AppUserRoles.All.Contains(role)) return Json(new { success = false, message = "دور غير صالح" });

            // Never demote the last active admin
            if (user.Role == AppUserRoles.Admin && role != AppUserRoles.Admin && await IsLastActiveAdmin(id))
                return Json(new { success = false, message = "لا يمكن تغيير دور آخر مدير نشط" });

            user.DisplayName = (displayName ?? "").Trim();
            user.Role = role;
            await _db.SaveChangesAsync();

            await _audit.LogAsync($"Console user updated: {user.Username} → role {role} by {User.Identity?.Name}", "Security", Core.Enums.AuditSeverity.Info);
            return Json(new { success = true });
        }

        /// <summary>
        /// Renames a console account — the fix for the shipped default administrator name.
        ///
        /// Seeding a fresh install under a non-default name does nothing for systems already
        /// running as <c>admin</c>, and deleting that account is not an option when it is the last
        /// administrator. Renaming is the only route that satisfies the requirement without
        /// anybody losing access.
        ///
        /// Renaming yourself is allowed on purpose: the account that most needs renaming is
        /// usually the one signed in. The session survives because every claim that matters keys
        /// off the user id, not the name — only the displayed name goes stale until re-login.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> Rename(int id, string newUsername)
        {
            var user = await _db.AppUsers.FindAsync(id);
            if (user == null) return Json(new { success = false, message = "غير موجود" });

            newUsername = (newUsername ?? "").Trim();
            if (newUsername.Length == 0)
                return Json(new { success = false, message = "اسم المستخدم مطلوب" });

            if (string.Equals(newUsername, user.Username, StringComparison.Ordinal))
                return Json(new { success = false, message = "الاسم الجديد مطابق للحالي" });

            // The unique index would reject this anyway, but as a 500 rather than a message.
            if (await _db.AppUsers.AnyAsync(u => u.Id != id && u.Username == newUsername))
                return Json(new { success = false, message = "اسم المستخدم موجود مسبقاً" });

            var oldUsername = user.Username;
            user.Username = newUsername;
            await _db.SaveChangesAsync();

            await _audit.LogAsync(
                $"Console user renamed: {oldUsername} → {newUsername} by {User.Identity?.Name}",
                "Security", Core.Enums.AuditSeverity.Warning);

            // An AD user signs in by binding as this exact name, so a rename that does not match
            // the domain account silently costs them their login — say so rather than let them
            // discover it at the sign-in page.
            var warning = user.AuthType == AppUserAuthTypes.ActiveDirectory
                ? "⚠️ هذا مستخدم Active Directory — يجب أن يطابق الاسم الجديد sAMAccountName في الدومين وإلا لن يستطيع الدخول."
                : null;

            var isSelf = string.Equals(oldUsername, User.Identity?.Name, StringComparison.Ordinal);

            return Json(new
            {
                success = true,
                warning,
                selfRenamed = isSelf,
                message = isSelf
                    ? "تمت إعادة التسمية. جلستك الحالية تعمل، لكن سجّل خروجاً ودخولاً ليظهر الاسم الجديد."
                    : "تمت إعادة التسمية."
            });
        }

        [HttpPost]
        public async Task<IActionResult> ToggleActive(int id)
        {
            var user = await _db.AppUsers.FindAsync(id);
            if (user == null) return Json(new { success = false, message = "غير موجود" });

            if (user.IsActive && user.Role == AppUserRoles.Admin && await IsLastActiveAdmin(id))
                return Json(new { success = false, message = "لا يمكن تعطيل آخر مدير نشط" });

            user.IsActive = !user.IsActive;
            await _db.SaveChangesAsync();

            await _audit.LogAsync($"Console user {(user.IsActive ? "activated" : "deactivated")}: {user.Username} by {User.Identity?.Name}", "Security", Core.Enums.AuditSeverity.Warning);
            return Json(new { success = true, isActive = user.IsActive });
        }

        [HttpPost]
        public async Task<IActionResult> ResetPassword(int id, string newPassword)
        {
            var user = await _db.AppUsers.FindAsync(id);
            if (user == null) return Json(new { success = false, message = "غير موجود" });
            if (user.AuthType != AppUserAuthTypes.Local)
                return Json(new { success = false, message = "مستخدم AD — كلمة مروره تُدار في الدومين" });

            var policyError = AuthService.ValidatePasswordPolicy(newPassword ?? "");
            if (policyError != null)
                return Json(new { success = false, message = "كلمة المرور: 10 أحرف على الأقل وتحوي حروفاً وأرقاماً" });

            user.PasswordHash = PasswordHasher.Hash(newPassword!);
            user.MustChangePassword = true;
            user.PasswordChangedUtc = DateTime.UtcNow; // restarts the maximum-age clock
            user.FailedLoginAttempts = 0;
            user.LockoutUntilUtc = null;
            await _db.SaveChangesAsync();

            await _audit.LogAsync($"Console user password reset: {user.Username} by {User.Identity?.Name}", "Security", Core.Enums.AuditSeverity.Warning);
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            var user = await _db.AppUsers.FindAsync(id);
            if (user == null) return Json(new { success = false, message = "غير موجود" });

            if (user.Role == AppUserRoles.Admin && await IsLastActiveAdmin(id))
                return Json(new { success = false, message = "لا يمكن حذف آخر مدير نشط" });

            _db.AppUsers.Remove(user);
            await _db.SaveChangesAsync();

            await _audit.LogAsync($"Console user deleted: {user.Username} by {User.Identity?.Name}", "Security", Core.Enums.AuditSeverity.Warning);
            return Json(new { success = true });
        }

        private async Task<bool> IsLastActiveAdmin(int userId) =>
            !await _db.AppUsers.AnyAsync(u => u.Id != userId && u.Role == AppUserRoles.Admin && u.IsActive);

        // ══════════════════════════════════════════════════════════
        // Sign-in domains — this module's own AD connections
        //
        // Console sign-in used to bind against whichever AD connection happened to exist
        // elsewhere (an active sync tenant's, or an SSPR domain's), which made who can sign in a
        // side effect of unrelated settings. These are its own.
        // ══════════════════════════════════════════════════════════

        [HttpGet]
        public async Task<IActionResult> Domains()
        {
            ViewBag.AdUserCount = await _db.AppUsers
                .CountAsync(u => u.AuthType == AppUserAuthTypes.ActiveDirectory && u.IsActive);

            return View(await _db.AuthDomains.AsNoTracking()
                .OrderBy(d => d.SortOrder).ThenBy(d => d.Id).ToListAsync());
        }

        [HttpPost]
        public async Task<IActionResult> SaveDomain(int id, string name, string adServer, int adPort,
            int adSecurityMode, bool adAllowUntrustedCertificate, int sortOrder, bool isActive)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(adServer))
                return Json(new { success = false, message = "الاسم والسيرفر مطلوبان" });

            var domain = id > 0 ? await _db.AuthDomains.FindAsync(id) : new AuthDomain();
            if (domain == null) return Json(new { success = false, message = "غير موجود" });
            if (id == 0) _db.AuthDomains.Add(domain);

            domain.Name = name.Trim();
            domain.AdServer = adServer.Trim();
            domain.AdPort = adPort <= 0 ? 389 : adPort;
            domain.AdSecurityMode = Enum.IsDefined(typeof(LdapSecurityMode), adSecurityMode)
                ? (LdapSecurityMode)adSecurityMode
                : LdapSecurityMode.Auto;
            domain.AdSecurityModeSet = true;
            // Keep the legacy flag consistent with the explicit choice, so anything still reading
            // it sees the same thing the connection will actually do.
            domain.AdUseSsl = domain.AdSecurityMode == LdapSecurityMode.Ldaps;
            domain.AdAllowUntrustedCertificate = adAllowUntrustedCertificate;
            domain.SortOrder = sortOrder;
            domain.IsActive = isActive;
            await _db.SaveChangesAsync();

            await _audit.LogAsync(
                $"Console sign-in domain saved: {domain.Name} ({domain.AdServer}:{domain.AdPort}, active={isActive}) by {User.Identity?.Name}",
                "Security", Core.Enums.AuditSeverity.Info);
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteDomain(int id)
        {
            var domain = await _db.AuthDomains.FindAsync(id);
            if (domain == null) return Json(new { success = false, message = "غير موجود" });

            // Removing the last domain while AD users depend on it locks every one of them out,
            // and the screen that fixes it may itself be behind an AD login.
            var remaining = await _db.AuthDomains.CountAsync(d => d.Id != id && d.IsActive);
            if (remaining == 0 && await _db.AppUsers.AnyAsync(u => u.AuthType == AppUserAuthTypes.ActiveDirectory && u.IsActive))
                return Json(new
                {
                    success = false,
                    message = "هذا آخر دومين نشط ويوجد مستخدمون يدخلون بحساب الدومين — سيفقدون الدخول. عطّل أو احذف هؤلاء المستخدمين أولاً، أو أضف دوميناً بديلاً."
                });

            _db.AuthDomains.Remove(domain);
            await _db.SaveChangesAsync();

            await _audit.LogAsync($"Console sign-in domain deleted: {domain.Name} by {User.Identity?.Name}",
                "Security", Core.Enums.AuditSeverity.Warning);
            return Json(new { success = true });
        }

        /// <summary>
        /// Binds against the domain with credentials the administrator supplies for the test.
        ///
        /// There is no service account to test with — console sign-in binds as the person signing
        /// in — so the only test that proves anything is the real one. The credentials are used for
        /// this single bind and never stored.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> TestDomain(int id, string testUsername, string testPassword)
        {
            var domain = await _db.AuthDomains.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
            if (domain == null) return Json(new { success = false, message = "غير موجود" });

            if (string.IsNullOrWhiteSpace(testUsername) || string.IsNullOrWhiteSpace(testPassword))
                return Json(new { success = false, message = "أدخل اسم مستخدم وكلمة مرور من هذا الدومين للاختبار" });

            var opts = domain.ToLdapOptions();
            var channel = LdapConnectionFactory.Describe(opts.SecurityMode, domain.AdPort);

            try
            {
                using var connection = LdapConnectionFactory.Create(new LdapConnectionOptions
                {
                    Server = opts.Server,
                    Port = opts.Port,
                    SecurityMode = opts.SecurityMode,
                    AllowUntrustedCertificate = opts.AllowUntrustedCertificate,
                    Username = testUsername.Trim(),
                    Password = testPassword
                });
                connection.Bind();

                await _audit.LogAsync($"Console sign-in domain tested OK: {domain.Name} by {User.Identity?.Name}",
                    "Security", Core.Enums.AuditSeverity.Info);
                return Json(new { success = true, message = $"نجح الربط عبر {channel} — هذا الدومين جاهز لدخول المستخدمين." });
            }
            catch (Exception ex)
            {
                // The channel is named either way: a wrong channel and wrong credentials produce
                // the same "bind failed", and they are fixed in completely different places.
                return Json(new { success = false, message = $"فشل الربط عبر {channel}: {ex.Message}" });
            }
        }
    }
}
