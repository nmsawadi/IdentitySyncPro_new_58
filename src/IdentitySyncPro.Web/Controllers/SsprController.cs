using IdentitySyncPro.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IdentitySyncPro.Web.Controllers
{
    /// <summary>
    /// Public self-service password reset portal (SMS OTP). Anonymous by design.
    ///
    /// The portal reports why a request failed (wrong username / national ID) and
    /// keeps the user on the entry screen — a deliberate business decision that
    /// trades account-enumeration resistance for usability. The per-IP block
    /// (configurable threshold + duration) is what contains abuse.
    /// All heavy lifting + auditing lives in SsprService.
    /// </summary>
    [AllowAnonymous]
    public class SsprController : Controller
    {
        private const string LangCookie = "sspr_lang";
        private readonly SsprService _sspr;

        public SsprController(SsprService sspr)
        {
            _sspr = sspr;
        }

        /// <summary>
        /// Portal language: ?lang=ar|en overrides and is remembered in a cookie
        /// for the rest of the flow; otherwise the last choice (or Arabic) is used.
        /// Independent from the admin console language.
        /// </summary>
        private string ResolveLang()
        {
            var q = HttpContext.Request.Query["lang"].ToString();
            if (q == "ar" || q == "en")
            {
                HttpContext.Response.Cookies.Append(LangCookie, q, new CookieOptions
                {
                    HttpOnly = false,
                    SameSite = SameSiteMode.Lax,
                    MaxAge = TimeSpan.FromHours(1)
                });
                return q;
            }
            var cookie = HttpContext.Request.Cookies[LangCookie];
            return cookie == "en" ? "en" : "ar";
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            ViewBag.SsprLang = ResolveLang();
            if (!await _sspr.IsPortalEnabledAsync(HttpContext.RequestAborted)) return View("Disabled");
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Request(string username, string nationalId)
        {
            ViewBag.SsprLang = ResolveLang();
            if (!await _sspr.IsPortalEnabledAsync(HttpContext.RequestAborted)) return View("Disabled");

            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            var result = await _sspr.RequestOtpAsync(username, nationalId, ip, (string)ViewBag.SsprLang, HttpContext.RequestAborted);

            // Anything other than a real OTP keeps the user on the entry screen with a
            // reason — they are NOT advanced to the code screen.
            if (result.Outcome != SsprRequestOutcome.OtpSent)
            {
                ViewBag.Error = result.Outcome.ToString();
                ViewBag.RetryAfterMinutes = result.RetryAfterMinutes;
                ViewBag.Username = username;
                return View("Index");
            }

            ViewBag.RequestGuid = result.RequestGuid;
            // Countdown expiry (display only — the server enforces the real OTP expiry)
            var lifetimeSec = await _sspr.GetOtpLifetimeSecondsAsync(HttpContext.RequestAborted);
            ViewBag.ExpiresAtMs = DateTimeOffset.UtcNow.AddSeconds(lifetimeSec).ToUnixTimeMilliseconds();
            ViewBag.LifetimeMs = lifetimeSec * 1000;
            return View("Verify");
        }

        [HttpPost]
        public async Task<IActionResult> Verify(string rid, string otp, long expiresAt)
        {
            ViewBag.SsprLang = ResolveLang();
            if (!await _sspr.IsPortalEnabledAsync(HttpContext.RequestAborted)) return View("Disabled");

            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            var (success, errorCode) = await _sspr.VerifyAndSendPasswordAsync(rid, otp, ip, (string)ViewBag.SsprLang, HttpContext.RequestAborted);

            if (success) return View("Done");

            // Re-render the OTP page carrying the same countdown expiry
            ViewBag.RequestGuid = rid;
            ViewBag.ExpiresAtMs = expiresAt;
            ViewBag.LifetimeMs = (await _sspr.GetOtpLifetimeSecondsAsync(HttpContext.RequestAborted)) * 1000;
            ViewBag.Error = errorCode;
            return View("Verify");
        }
    }
}
