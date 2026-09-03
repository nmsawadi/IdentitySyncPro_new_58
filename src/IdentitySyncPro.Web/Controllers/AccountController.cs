using System.Security.Claims;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IdentitySyncPro.Web.Controllers
{
    public class AccountController : Controller
    {
        /// <summary>Claim marking a principal that passed the password but not the second factor.</summary>
        public const string MfaPendingClaim = "mfa_pending";

        /// <summary>Carries the candidate TOTP secret across the enrollment round trip.</summary>
        private const string PendingSecretKey = "mfa_pending_secret";

        private readonly AuthService _auth;
        private readonly MfaService _mfa;
        private readonly IAuditService _audit;
        private readonly ILogger<AccountController> _logger;
        private readonly AppDbContext _db;

        public AccountController(AuthService auth, MfaService mfa, IAuditService audit, ILogger<AccountController> logger, AppDbContext db)
        {
            _auth = auth;
            _mfa = mfa;
            _audit = audit;
            _logger = logger;
            _db = db;
        }

        /// <summary>
        /// The application's configured UI language, used to render the sign-in page before
        /// anyone is authenticated.
        ///
        /// Falls back to Arabic if the setting cannot be read: the sign-in page must still be
        /// reachable when the database is unavailable, so an unreadable preference is never
        /// allowed to turn into an error page.
        /// </summary>
        private string GetCurrentLang()
        {
            try
            {
                return _db.AppSettings.FirstOrDefault(s => s.Key == "Language")?.Value ?? "ar";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the UI language setting; falling back to Arabic");
                return "ar";
            }
        }

        [AllowAnonymous]
        [HttpGet]
        public IActionResult Login(string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToAction("Index", "Dashboard");

            ViewBag.ReturnUrl = returnUrl;
            ViewBag.Lang = GetCurrentLang();
            return View();
        }

        [AllowAnonymous]
        [HttpPost]
        public async Task<IActionResult> Login(string username, string password, string? returnUrl = null)
        {
            var (result, user) = await _auth.AuthenticateAsync(username, password, HttpContext.RequestAborted);

            if (result == AuthService.AuthResult.LockedOut)
            {
                await _audit.LogAsync($"Login blocked (locked out): {username}", "Security", Core.Enums.AuditSeverity.Warning);
                ViewBag.Error = "locked";
                ViewBag.ReturnUrl = returnUrl;
                return View();
            }

            if (result != AuthService.AuthResult.Success || user == null)
            {
                // One generic message — never reveal whether the account exists
                await _audit.LogAsync($"Failed login attempt: {username} from {HttpContext.Connection.RemoteIpAddress}", "Security", Core.Enums.AuditSeverity.Warning);
                ViewBag.Error = "invalid";
                ViewBag.ReturnUrl = returnUrl;
                return View();
            }

            // ── Second factor ────────────────────────────────────────────────
            // The password is proven; that is not the same as being signed in. A pending
            // principal is issued WITHOUT the role claim, so even if a filter were bypassed the
            // [Authorize(Roles = ...)] screens still refuse it.
            var requirement = await _mfa.GetRequirementAsync(user, HttpContext.RequestAborted);
            if (requirement != MfaService.MfaRequirement.NotRequired)
            {
                await SignInPendingMfaAsync(user, requirement, returnUrl);
                await _audit.LogAsync(
                    $"Password accepted, awaiting second factor: {user.Username} ({user.Role})",
                    "Security", Core.Enums.AuditSeverity.Info);

                return RedirectToAction(
                    requirement == MfaService.MfaRequirement.MustEnroll ? nameof(MfaSetup) : nameof(Mfa));
            }

            await SignInUserAsync(user);
            await _audit.LogAsync($"User logged in: {user.Username} ({user.Role})", "Security", Core.Enums.AuditSeverity.Info);

            if (user.MustChangePassword)
                return RedirectToAction(nameof(ChangePassword));

            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);

            return RedirectToAction("Index", "Dashboard");
        }

        [HttpPost]
        public async Task<IActionResult> Logout()
        {
            var username = User.Identity?.Name;
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            await _audit.LogAsync($"User logged out: {username}", "Security", Core.Enums.AuditSeverity.Info);
            return RedirectToAction(nameof(Login));
        }

        [AllowAnonymous]
        [HttpGet]
        public IActionResult AccessDenied() => View();

        [HttpGet]
        public IActionResult ChangePassword() => View();

        [HttpPost]
        public async Task<IActionResult> ChangePassword(string currentPassword, string newPassword, string confirmPassword)
        {
            if (newPassword != confirmPassword)
            {
                ViewBag.Error = "mismatch";
                return View();
            }

            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
            var (success, error) = await _auth.ChangePasswordAsync(userId, currentPassword, newPassword, HttpContext.RequestAborted);

            if (!success)
            {
                ViewBag.Error = error;
                return View();
            }

            await _audit.LogAsync($"User changed own password: {User.Identity?.Name}", "Security", Core.Enums.AuditSeverity.Info);

            // Re-issue the cookie without the must-change flag
            var db = HttpContext.RequestServices.GetRequiredService<IdentitySyncPro.Infrastructure.Data.AppDbContext>();
            var user = await db.AppUsers.FindAsync(userId);
            if (user != null) await SignInUserAsync(user);

            TempData["PasswordChanged"] = true;
            return RedirectToAction("Index", "Dashboard");
        }

        // ══════════════════════════════════════════════════════════
        // Multi-factor authentication
        //
        // Every action here runs on the PENDING principal (password proven, second factor not
        // yet). They are reachable while `mfa_pending` is set and by nothing else.
        // ══════════════════════════════════════════════════════════

        /// <summary>Challenge screen for an already-enrolled user.</summary>
        [HttpGet]
        public async Task<IActionResult> Mfa()
        {
            var user = await GetPendingUserAsync();
            if (user == null) return RedirectToAction(nameof(Login));
            if (!user.IsMfaEnrolled) return RedirectToAction(nameof(MfaSetup));

            ViewBag.Lang = GetCurrentLang();
            ViewBag.Username = user.Username;
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Mfa(string code)
        {
            var user = await GetPendingUserAsync();
            if (user == null) return RedirectToAction(nameof(Login));

            var result = await _mfa.VerifyAsync(user.Id, code ?? "", HttpContext.RequestAborted);
            if (result == MfaService.VerifyResult.Failed)
            {
                await _audit.LogAsync($"MFA verification failed: {user.Username} from {HttpContext.Connection.RemoteIpAddress}",
                    "Security", Core.Enums.AuditSeverity.Warning);

                ViewBag.Lang = GetCurrentLang();
                ViewBag.Username = user.Username;
                ViewBag.Error = "invalid_code";
                return View();
            }

            if (result == MfaService.VerifyResult.RecoveryCode)
            {
                // Worth its own warning entry: it means a device is gone, and the pool of codes
                // is now one smaller with no automatic replenishment.
                var left = MfaService.CountRemainingRecoveryCodes(user);
                await _audit.LogAsync($"MFA recovery code used: {user.Username} — {left} remaining",
                    "Security", Core.Enums.AuditSeverity.Warning);
                TempData["MfaRecoveryUsed"] = left;
            }

            return await CompleteSignInAfterMfaAsync(user, "MFA verified");
        }

        /// <summary>Enrollment screen for a user in scope who has no authenticator yet.</summary>
        [HttpGet]
        public async Task<IActionResult> MfaSetup()
        {
            var user = await GetPendingUserAsync();
            if (user == null) return RedirectToAction(nameof(Login));
            if (user.IsMfaEnrolled) return RedirectToAction(nameof(Mfa));

            // A fresh secret per visit, held in session until a code proves the app has it.
            var (secret, uri) = MfaService.BeginEnrollment("IdentitySync Pro", user.Username);
            HttpContext.Session.SetString(PendingSecretKey, secret);

            PopulateSetupView(user.Username, secret, uri);
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> MfaSetup(string code)
        {
            var user = await GetPendingUserAsync();
            if (user == null) return RedirectToAction(nameof(Login));

            var secret = HttpContext.Session.GetString(PendingSecretKey);
            if (string.IsNullOrEmpty(secret))
            {
                // Session expired mid-setup: start over rather than enroll an unknown secret.
                return RedirectToAction(nameof(MfaSetup));
            }

            var (ok, recoveryCodes) = await _mfa.CompleteEnrollmentAsync(
                user.Id, secret, code ?? "", HttpContext.RequestAborted);

            if (!ok)
            {
                PopulateSetupView(user.Username, secret,
                    Core.Security.TotpGenerator.BuildUri("IdentitySync Pro", user.Username, secret));
                ViewBag.Error = "invalid_code";
                return View();
            }

            HttpContext.Session.Remove(PendingSecretKey);
            await _audit.LogAsync($"MFA enrolled: {user.Username}", "Security", Core.Enums.AuditSeverity.Warning);

            // Shown exactly once — only hashes are stored, so there is no second chance.
            TempData["MfaRecoveryCodes"] = string.Join(",", recoveryCodes);
            return RedirectToAction(nameof(MfaRecoveryCodes));
        }

        /// <summary>One-time display of the recovery codes just generated.</summary>
        [HttpGet]
        public async Task<IActionResult> MfaRecoveryCodes()
        {
            var user = await GetPendingUserAsync();
            if (user == null) return RedirectToAction(nameof(Login));

            var codes = (TempData["MfaRecoveryCodes"] as string ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (codes.Length == 0) return await CompleteSignInAfterMfaAsync(user, "MFA enrollment completed");

            ViewBag.Lang = GetCurrentLang();
            ViewBag.Codes = codes;
            return View();
        }

        /// <summary>Acknowledges the codes and finishes signing in.</summary>
        [HttpPost]
        public async Task<IActionResult> MfaRecoveryCodesAck()
        {
            var user = await GetPendingUserAsync();
            if (user == null) return RedirectToAction(nameof(Login));
            return await CompleteSignInAfterMfaAsync(user, "MFA enrollment completed");
        }

        /// <summary>
        /// The pending principal carries only the user id, so the account is re-read here and
        /// the full cookie is issued from the database record — never from the pending claims.
        /// </summary>
        private async Task<AppUser?> GetPendingUserAsync()
        {
            if (User.Identity?.IsAuthenticated != true) return null;
            if (!User.HasClaim(MfaPendingClaim, "1")) return null;

            var id = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
            var user = await _db.AppUsers.FindAsync(id);
            return user is { IsActive: true } ? user : null;
        }

        private async Task<IActionResult> CompleteSignInAfterMfaAsync(AppUser user, string auditAction)
        {
            await SignInUserAsync(user); // full principal, roles included
            await _audit.LogAsync($"{auditAction}: {user.Username} ({user.Role})", "Security", Core.Enums.AuditSeverity.Info);

            if (user.MustChangePassword)
                return RedirectToAction(nameof(ChangePassword));

            var returnUrl = TempData["MfaReturnUrl"] as string;
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);

            return RedirectToAction("Index", "Dashboard");
        }

        private void PopulateSetupView(string username, string secret, string uri)
        {
            ViewBag.Lang = GetCurrentLang();
            ViewBag.Username = username;
            ViewBag.Secret = Core.Security.Base32.FormatForDisplay(secret);
            ViewBag.OtpAuthUri = uri;
            ViewBag.QrSvg = BuildQrSvg(uri);
        }

        /// <summary>
        /// Renders the otpauth URI as an inline SVG QR code.
        ///
        /// Generated server-side and embedded in the markup on purpose: a client-side QR library
        /// would mean another script to host, and a hosted QR image service would send the TOTP
        /// secret to a third party.
        /// </summary>
        private static string BuildQrSvg(string uri)
        {
            using var generator = new QRCoder.QRCodeGenerator();
            using var data = generator.CreateQrCode(uri, QRCoder.QRCodeGenerator.ECCLevel.Q);
            return new QRCoder.SvgQRCode(data).GetGraphic(4, "#000000", "#FFFFFF", drawQuietZones: true);
        }

        /// <summary>
        /// Issues the half-authenticated principal: identity only, deliberately NO role claim.
        /// </summary>
        private async Task SignInPendingMfaAsync(AppUser user, MfaService.MfaRequirement requirement, string? returnUrl)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name, user.Username),
                new("display_name", string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName),
                new(MfaPendingClaim, "1")
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = false });

            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                TempData["MfaReturnUrl"] = returnUrl;
        }

        private async Task SignInUserAsync(AppUser user)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name, user.Username),
                new("display_name", string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName),
                new(ClaimTypes.Role, user.Role)
            };
            if (user.MustChangePassword)
                claims.Add(new Claim("pwd_change", "1"));

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = false });
        }
    }
}
