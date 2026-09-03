using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Models.Governance;
using IdentitySyncPro.Core.Models.Services;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentitySyncPro.Web.Controllers
{
    /// <summary>
    /// The owner's side of non-human accounts: what I am answerable for, and what is going unclaimed.
    ///
    /// Open to any signed-in user, for the same reason approvals and reviews are. The whole
    /// difficulty with service accounts is finding somebody willing to be answerable at all;
    /// requiring a console role to claim one would restrict that to the handful of people who
    /// already have the console — which is exactly the group that does not know what the accounts
    /// are for. Owning an account grants no privilege: it is a person putting their name against
    /// something, and the guards on who may attest or release live in
    /// <see cref="NhiLifecyclePolicy"/>.
    /// </summary>
    public class NhiOwnershipController : Controller
    {
        private readonly GovernanceDbContext _gov;
        private readonly ServicesDbContext _services;
        private readonly NhiLifecycleService _lifecycle;

        public NhiOwnershipController(GovernanceDbContext gov, ServicesDbContext services, NhiLifecycleService lifecycle)
        {
            _gov = gov;
            _services = services;
            _lifecycle = lifecycle;
        }

        private string Me => User.Identity?.Name ?? string.Empty;

        /// <summary>
        /// The lifecycle settings of the service that discovered an account.
        ///
        /// Read from the service rather than assumed, because "when is attestation due" has no
        /// answer that is true for every institution — one runs a 90-day cycle, another an annual
        /// one, and the screen must show the reader their own dates.
        /// </summary>
        private async Task<Dictionary<int, NhiLifecyclePolicy.LifecycleConfig>> ConfigsAsync(
            IEnumerable<int> serviceIds, CancellationToken ct)
        {
            var ids = serviceIds.Distinct().ToList();
            var rows = await _services.SvcServices
                .Where(s => ids.Contains(s.Id))
                .Select(s => new
                {
                    s.Id, s.NhiClaimDays, s.NhiAttestationDays, s.NhiAttestationGraceDays,
                    s.NhiQuarantineMode, s.NhiMaxQuarantinePercent
                })
                .ToListAsync(ct);

            return rows.ToDictionary(s => s.Id, s => new NhiLifecyclePolicy.LifecycleConfig(
                true, s.NhiClaimDays, s.NhiAttestationDays, s.NhiAttestationGraceDays,
                string.IsNullOrWhiteSpace(s.NhiQuarantineMode) ? GovNhiEnforcement.Report : s.NhiQuarantineMode!,
                s.NhiMaxQuarantinePercent));
        }

        // ══════════════════════════════════════
        // WHAT I OWN
        // ══════════════════════════════════════

        public async Task<IActionResult> Index(CancellationToken ct)
        {
            var mine = await _lifecycle.OwnedByAsync(Me, ct);
            var configs = await ConfigsAsync(mine.Select(a => a.ServiceId), ct);

            ViewBag.Accounts = mine;
            ViewBag.Configs = configs;
            ViewBag.Now = DateTime.UtcNow;
            return View();
        }

        /// <summary>
        /// Accounts nobody has claimed — the reason the whole feature exists.
        ///
        /// Shown to everyone signed in, because the person who knows what <c>svc-billing</c> does is
        /// rarely the person running the console. Ordered by how little time is left, since the ones
        /// about to be quarantined are the ones worth a reader's attention.
        /// </summary>
        public async Task<IActionResult> Unclaimed(CancellationToken ct)
        {
            var rows = await _gov.NhiAccounts
                .Where(a => a.OwnerUsername == null && a.RetiredUtc == null &&
                            (a.State == GovNhiStates.Discovered || a.State == GovNhiStates.Quarantined))
                .OrderBy(a => a.ClaimDueUtc)
                .ThenBy(a => a.Account)
                .Take(500)
                .ToListAsync(ct);

            ViewBag.Accounts = rows;
            ViewBag.Now = DateTime.UtcNow;
            return View();
        }

        // ══════════════════════════════════════
        // ACTIONS
        // ══════════════════════════════════════

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Claim(long id, string? returnTo, CancellationToken ct)
        {
            var outcome = await _lifecycle.ClaimAsync(id, Me, DateTime.UtcNow, ct);
            Message(outcome, "تمت المطالبة بالحساب / Account claimed");
            return Back(returnTo);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Attest(long id, string? note, string? returnTo, CancellationToken ct)
        {
            var outcome = await _lifecycle.AttestAsync(id, Me, note, DateTime.UtcNow, ct);
            Message(outcome, "تم الإقرار / Attested");
            return Back(returnTo);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Release(long id, string? returnTo, CancellationToken ct)
        {
            var outcome = await _lifecycle.DisownAsync(id, Me, DateTime.UtcNow, ct);
            Message(outcome, "أُعيد الحساب بلا مالك / The account has no owner again");
            return Back(returnTo);
        }

        private void Message(NhiLifecycleService.Outcome outcome, string success)
        {
            if (outcome.Ok) TempData["Success"] = success;
            else TempData["Error"] = outcome.Problem;
        }

        private IActionResult Back(string? returnTo) =>
            RedirectToAction(string.Equals(returnTo, "Unclaimed", StringComparison.Ordinal) ? "Unclaimed" : "Index");
    }

    /// <summary>
    /// The administrator's view of the tracked population: every state, why each account is in it,
    /// and the two decisions only an operator makes — exempting an account and ending an exemption.
    /// </summary>
    [Authorize(Roles = AppUserRoles.Admin)]
    public class NhiAccountsController : Controller
    {
        private readonly GovernanceDbContext _gov;
        private readonly ServicesDbContext _services;
        private readonly NhiLifecycleService _lifecycle;

        public NhiAccountsController(GovernanceDbContext gov, ServicesDbContext services, NhiLifecycleService lifecycle)
        {
            _gov = gov;
            _services = services;
            _lifecycle = lifecycle;
        }

        /// <param name="state">Filter by lifecycle state. Named "state", never "action" — a
        /// controller parameter called action is bound from the route and silently arrives empty.</param>
        public async Task<IActionResult> Index(string? state, int? serviceId, string? q, CancellationToken ct)
        {
            var query = _gov.NhiAccounts.AsQueryable();

            if (!string.IsNullOrWhiteSpace(state)) query = query.Where(a => a.State == state);
            if (serviceId is { } sid) query = query.Where(a => a.ServiceId == sid);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                query = query.Where(a => a.Account.ToLower().Contains(term) ||
                                         (a.DisplayName != null && a.DisplayName.ToLower().Contains(term)) ||
                                         (a.OwnerUsername != null && a.OwnerUsername.ToLower().Contains(term)));
            }

            var rows = await query
                .OrderBy(a => a.State == GovNhiStates.Quarantined ? 0 : a.State == GovNhiStates.Discovered ? 1 : 2)
                .ThenBy(a => a.Account)
                .Take(500)
                .ToListAsync(ct);

            // The counts are of the whole population, not of the filtered page: a number that
            // changes when you filter cannot answer "how many are unowned".
            ViewBag.Counts = await _gov.NhiAccounts
                .GroupBy(a => a.State)
                .Select(g => new { State = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.State, x => x.Count, ct);

            ViewBag.Services = await _services.SvcServices
                .Where(s => s.ReportType == "NonHumanInventory")
                .Select(s => new { s.Id, s.Name, s.NhiLifecycleEnabled, s.NhiQuarantineMode })
                .ToListAsync(ct);

            ViewBag.Accounts = rows;
            ViewBag.State = state;
            ViewBag.ServiceId = serviceId;
            ViewBag.Query = q;
            ViewBag.Now = DateTime.UtcNow;
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Exempt(long id, string? reason, DateTime? untilUtc, string? state, CancellationToken ct)
        {
            var outcome = await _lifecycle.ExemptAsync(id, User.Identity?.Name ?? "", reason, untilUtc, DateTime.UtcNow, ct);

            if (outcome.Ok) TempData["Success"] = "استُثني الحساب حتى التاريخ المحدّد / Exempted until the stated date";
            else TempData["Error"] = outcome.Problem;

            return RedirectToAction("Index", new { state });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EndExemption(long id, string? state, CancellationToken ct)
        {
            var outcome = await _lifecycle.EndExemptionAsync(id, User.Identity?.Name ?? "", DateTime.UtcNow, ct);

            if (outcome.Ok) TempData["Success"] = "أُنهي الاستثناء / The exemption has ended";
            else TempData["Error"] = outcome.Problem;

            return RedirectToAction("Index", new { state });
        }
    }
}
