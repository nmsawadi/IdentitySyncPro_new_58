using System.Security.Claims;
using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Governance;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentitySyncPro.Web.Controllers
{
    /// <summary>
    /// The employee-facing portal: sign in with your directory account, ask for access to it, and
    /// see what happened.
    ///
    /// The people this serves have no row in <c>AppUsers</c> — they are the identities the system
    /// provisions, not its operators. So sign-in here proves a directory bind and nothing more, and
    /// the principal it issues carries <b>no role claim at all</b>.
    ///
    /// <b>Two independent barriers, the same pair the MFA work established:</b>
    /// <list type="number">
    /// <item>No role claim, so every <c>[Authorize(Roles = ...)]</c> console screen rejects a portal
    /// principal on its own — without knowing this controller exists.</item>
    /// <item><see cref="Filters.PortalGuardFilter"/> closes the remainder: console screens that ask
    /// only for an authenticated user, which barrier one cannot see.</item>
    /// </list>
    /// The first is the safety net. If the filter were ever removed or misordered, the
    /// administrative screens stay shut.
    /// </summary>
    [AllowAnonymous]
    public class PortalController : Controller
    {
        private readonly AuthService _auth;
        private readonly GovernanceDbContext _gov;
        private readonly AppDbContext _app;
        private readonly AccessRequestService _service;
        private readonly ITenantConnectorFactory _connectors;
        private readonly IAuditService _audit;
        private readonly ILogger<PortalController> _logger;

        public PortalController(
            AuthService auth, GovernanceDbContext gov, AppDbContext app,
            AccessRequestService service, ITenantConnectorFactory connectors,
            IAuditService audit, ILogger<PortalController> logger)
        {
            _auth = auth;
            _gov = gov;
            _app = app;
            _service = service;
            _connectors = connectors;
            _audit = audit;
            _logger = logger;
        }

        /// <summary>Marks a principal as belonging to the portal rather than the console.</summary>
        public const string PortalClaim = "portal_user";

        private bool IsPortalUser => User.HasClaim(PortalClaim, "1");
        private string CurrentAccount => User.Identity?.Name ?? string.Empty;

        // ══════════════════════════════════════
        // SIGNING IN
        // ══════════════════════════════════════

        [HttpGet]
        public IActionResult Login()
        {
            if (IsPortalUser) return RedirectToAction(nameof(Index));
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(string username, string password, CancellationToken ct)
        {
            var ok = await _auth.AuthenticateDirectoryOnlyAsync(username ?? "", password ?? "", ct);

            if (!ok)
            {
                // One message for a wrong name and a wrong password alike: telling them apart tells
                // an attacker which accounts exist.
                ViewBag.Error = "بيانات الدخول غير صحيحة / Invalid credentials";

                await _audit.LogAsync("PortalSignInFailed", "AccessGovernance", AuditSeverity.Warning,
                    details: $"Portal sign-in failed for '{username}'", performedBy: username);
                return View();
            }

            // No role claim, deliberately — see the class summary. The portal marker is what this
            // controller checks; it grants nothing anywhere else.
            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, username!.Trim()),
                new(PortalClaim, "1")
            };

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
                new AuthenticationProperties { IsPersistent = false });

            await _audit.LogAsync("PortalSignIn", "AccessGovernance", AuditSeverity.Info,
                details: "Employee signed in to the access portal", performedBy: username.Trim());

            _logger.LogInformation("Portal sign-in succeeded for '{Account}'", username.Trim());
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction(nameof(Login));
        }

        // ══════════════════════════════════════
        // MY ACCESS
        // ══════════════════════════════════════

        public async Task<IActionResult> Index(CancellationToken ct)
        {
            if (!IsPortalUser) return RedirectToAction(nameof(Login));

            // Their own requests, whichever way the request was raised — an operator who filed one
            // on their behalf should not make it invisible to the person it is for.
            var mine = await _gov.AccessRequests
                .Include(r => r.CatalogItem)
                .Where(r => r.SubjectAccount == CurrentAccount)
                .OrderByDescending(r => r.CreatedUtc)
                .Take(100)
                .ToListAsync(ct);

            return View(mine);
        }

        [HttpGet]
        public async Task<IActionResult> Request(CancellationToken ct)
        {
            if (!IsPortalUser) return RedirectToAction(nameof(Login));

            await LoadCatalogAsync(ct);
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Request(int catalogItemId, string justification, CancellationToken ct)
        {
            if (!IsPortalUser) return RedirectToAction(nameof(Login));

            // The subject is the signed-in account and is never read from the form. A portal user
            // asks for access to themselves; accepting a posted account name would turn this page
            // into a way to grant access to anybody, from an anonymous-facing form.
            var outcome = await _service.CreateAsync(
                catalogItemId, CurrentAccount, CurrentAccount, GovChannels.Portal, justification ?? "", ct);

            if (!outcome.Ok)
            {
                TempData["Error"] = outcome.Error;
                ViewBag.CatalogItemId = catalogItemId;
                ViewBag.Justification = justification;
                await LoadCatalogAsync(ct);
                return View();
            }

            TempData["Success"] = "تم إرسال طلبك إلى المُعتمِد / Your request has been sent to the approver";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Cancel(long id, CancellationToken ct)
        {
            if (!IsPortalUser) return RedirectToAction(nameof(Login));

            var outcome = await _service.CancelAsync(id, CurrentAccount, ct);
            if (outcome.Ok) TempData["Success"] = "تم سحب الطلب / Request withdrawn";
            else TempData["Error"] = outcome.Error;
            return RedirectToAction(nameof(Index));
        }

        // ══════════════════════════════════════
        // WHAT THEY MAY ASK FOR
        // ══════════════════════════════════════

        /// <summary>
        /// The catalog entries this person is eligible for, and does not already hold.
        ///
        /// Both filters are applied before the list is drawn rather than only when the request is
        /// submitted. The service refuses either case anyway — but offering somebody a button that
        /// always fails is a worse way to say no than not offering it.
        ///
        /// A directory that cannot answer hides the entry: on a self-service page the honest
        /// reading of "I could not check" is "do not offer it", never "assume yes".
        /// </summary>
        private async Task LoadCatalogAsync(CancellationToken ct)
        {
            var items = await _gov.CatalogItems
                .Where(c => c.IsEnabled)
                .OrderBy(c => c.DisplayName)
                .ToListAsync(ct);

            var offered = new List<GovCatalogItem>();
            var unavailable = 0;

            foreach (var item in items)
            {
                if (AccessRequestPolicy.ValidateCatalogItem(item) != null) { unavailable++; continue; }

                var tenant = await _app.TenantSettings.FirstOrDefaultAsync(t => t.Id == item.TenantId, ct);
                if (tenant == null) { unavailable++; continue; }

                var target = _connectors.CreateTargetConnector(tenant);

                if (!string.IsNullOrWhiteSpace(item.EligibleRequesterGroup))
                {
                    var eligible = await target.TryIsMemberOfAnyAsync(
                        CurrentAccount, new[] { item.EligibleRequesterGroup! }, ct);
                    if (eligible != true) continue;
                }

                var held = await target.TryIsMemberOfAnyAsync(CurrentAccount, new[] { item.GroupName }, ct);
                if (held != false) continue;   // already holds it, or the directory could not say

                offered.Add(item);
            }

            ViewBag.Catalog = offered;
            ViewBag.Unavailable = unavailable;
        }
    }
}
