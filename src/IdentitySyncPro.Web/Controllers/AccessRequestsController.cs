using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Models.Governance;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentitySyncPro.Web.Controllers
{
    /// <summary>
    /// The console side of access requests: raise one, withdraw one, decide one.
    ///
    /// <b>Deciding is deliberately not gated on a console role.</b> An approver is a department head
    /// signing off on their own people's access, not a system operator — requiring Admin or Operator
    /// to approve would mean handing the whole console to every manager who has to sign something.
    /// The screen is open to any signed-in user and the authority comes from the catalog item's own
    /// approver list, checked in <see cref="AccessRequestService"/>. So a Viewer can approve what
    /// they were named on, and an Admin cannot approve what they were not.
    /// </summary>
    public class AccessRequestsController : Controller
    {
        private readonly GovernanceDbContext _gov;
        private readonly AppDbContext _app;
        private readonly AccessRequestService _service;

        public AccessRequestsController(GovernanceDbContext gov, AppDbContext app, AccessRequestService service)
        {
            _gov = gov;
            _app = app;
            _service = service;
        }

        private string CurrentUser => User.Identity?.Name ?? string.Empty;

        // ══════════════════════════════════════
        // MY REQUESTS
        // ══════════════════════════════════════

        public async Task<IActionResult> Index(CancellationToken ct)
        {
            var mine = await _gov.AccessRequests
                .Include(r => r.CatalogItem)
                .Where(r => r.RequestedBy == CurrentUser)
                .OrderByDescending(r => r.CreatedUtc)
                .Take(200)
                .ToListAsync(ct);

            ViewBag.PendingForMe = await CountAwaitingMeAsync(ct);
            return View(mine);
        }

        [HttpGet]
        public async Task<IActionResult> Create(CancellationToken ct)
        {
            await LoadCatalogAsync(ct);
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(int catalogItemId, string subjectAccount, string justification, CancellationToken ct)
        {
            var outcome = await _service.CreateAsync(
                catalogItemId, subjectAccount ?? "", CurrentUser, GovChannels.Console, justification ?? "", ct);

            if (!outcome.Ok)
            {
                // The form comes back with what was typed. Re-entering a justification because the
                // account name had a typo is the kind of small cruelty that stops people using a
                // request system at all.
                TempData["Error"] = outcome.Error;
                ViewBag.CatalogItemId = catalogItemId;
                ViewBag.SubjectAccount = subjectAccount;
                ViewBag.Justification = justification;
                await LoadCatalogAsync(ct);
                return View();
            }

            TempData["Success"] = "تم تقديم الطلب وأُشعر المُعتمِدون / Request submitted and approvers notified";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Cancel(long id, CancellationToken ct)
        {
            var outcome = await _service.CancelAsync(id, CurrentUser, ct);
            if (outcome.Ok) TempData["Success"] = "تم سحب الطلب / Request withdrawn";
            else TempData["Error"] = outcome.Error;
            return RedirectToAction(nameof(Index));
        }

        // ══════════════════════════════════════
        // AWAITING MY DECISION
        // ══════════════════════════════════════

        public async Task<IActionResult> Approvals(CancellationToken ct)
        {
            var pending = await PendingIDecideAsync(ct);
            return View(pending);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Decide(long id, string decision, string? comment, CancellationToken ct)
        {
            var outcome = await _service.DecideAsync(id, CurrentUser, decision, comment, ct);

            if (!outcome.Ok)
            {
                TempData["Error"] = outcome.Error;
            }
            else
            {
                // The execution result is read back rather than assumed: an approval that Active
                // Directory refused is still an approval, and telling the approver "done" while the
                // person has no access is the gap the two status columns exist to expose.
                var saved = await _gov.AccessRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
                if (decision == GovDecisions.Reject)
                    TempData["Success"] = "تم رفض الطلب / Request rejected";
                else if (saved?.ExecutionStatus == GovExecutionStatus.Succeeded)
                    TempData["Success"] = "تم الاعتماد ومُنح الوصول فعلياً / Approved and access granted";
                else
                    TempData["Error"] =
                        "اعتُمد الطلب لكن التنفيذ في AD لم ينجح — سيُعاد المحاولة تلقائياً كل 15 دقيقة. السبب: "
                        + (saved?.ExecutionError ?? "غير معروف");
            }

            return RedirectToAction(nameof(Approvals));
        }

        // ══════════════════════════════════════
        // WHO DECIDES WHAT
        // ══════════════════════════════════════

        /// <summary>
        /// Pending requests this user is named on.
        ///
        /// Only the name list is used to build the screen — resolving every catalog item's approver
        /// group in Active Directory on every page load would put a directory round trip behind a
        /// list view. The group is still honoured: it is checked in the service when a decision is
        /// actually taken, so a group approver can act on a request reached by its link even though
        /// it does not appear in their list. That limit is stated on the screen rather than left to
        /// be discovered.
        /// </summary>
        private async Task<List<GovAccessRequest>> PendingIDecideAsync(CancellationToken ct)
        {
            var pending = await _gov.AccessRequests
                .Include(r => r.CatalogItem)
                .Where(r => r.Status == GovRequestStatus.Pending)
                .OrderBy(r => r.DecisionDueUtc ?? DateTime.MaxValue)
                .ToListAsync(ct);

            return pending
                .Where(r => r.CatalogItem != null
                            && AccessRequestPolicy.NamesIn(r.CatalogItem.ApproverUsers)
                                                  .Contains(CurrentUser, StringComparer.OrdinalIgnoreCase)
                            // A request you raised, or one that grants access to your own account,
                            // is not yours to decide — so it never appears on your queue either.
                            && !string.Equals(r.RequestedBy, CurrentUser, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(r.SubjectAccount, CurrentUser, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private async Task<int> CountAwaitingMeAsync(CancellationToken ct) => (await PendingIDecideAsync(ct)).Count;

        private async Task LoadCatalogAsync(CancellationToken ct)
        {
            var items = await _gov.CatalogItems
                .Where(c => c.IsEnabled)
                .OrderBy(c => c.DisplayName)
                .ToListAsync(ct);

            // An item that cannot be decided is not offered. Listing it would let somebody file a
            // request into a queue nobody can clear — the service refuses it anyway, and refusing
            // after the form is filled in is worse than never showing it.
            ViewBag.Catalog = items.Where(i => AccessRequestPolicy.ValidateCatalogItem(i) == null).ToList();
            ViewBag.HiddenBroken = items.Count - ((List<GovCatalogItem>)ViewBag.Catalog).Count;
        }
    }

    /// <summary>
    /// The catalog itself — what may be requested at all. Administrators only: an entry here is a
    /// standing grant of the right to ask for something, and its approver list is what stands
    /// between a request and the group it names.
    /// </summary>
    [Authorize(Roles = AppUserRoles.Admin)]
    public class AccessCatalogController : Controller
    {
        private readonly GovernanceDbContext _gov;
        private readonly AppDbContext _app;

        public AccessCatalogController(GovernanceDbContext gov, AppDbContext app)
        {
            _gov = gov;
            _app = app;
        }

        public async Task<IActionResult> Index(CancellationToken ct)
        {
            var items = await _gov.CatalogItems.OrderBy(c => c.DisplayName).ToListAsync(ct);

            ViewBag.TenantNames = await _app.TenantSettings
                .ToDictionaryAsync(t => t.Id, t => t.TenantName, ct);

            // Surfaced on the list, not just refused on save: an item edited into an unusable state
            // by a later change would otherwise sit there looking healthy.
            ViewBag.Problems = items.ToDictionary(i => i.Id, AccessRequestPolicy.ValidateCatalogItem);

            ViewBag.PendingCounts = await _gov.AccessRequests
                .Where(r => r.Status == GovRequestStatus.Pending)
                .GroupBy(r => r.CatalogItemId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

            return View(items);
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int? id, CancellationToken ct)
        {
            var item = id == null
                ? new GovCatalogItem()
                : await _gov.CatalogItems.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (item == null) return NotFound();

            await LoadTenantsAsync(ct);
            return View(item);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(GovCatalogItem model, CancellationToken ct)
        {
            // The same rule the service enforces, applied at the moment it can still be corrected.
            // An item saved broken here is a queue nobody can clear later.
            if (AccessRequestPolicy.ValidateCatalogItem(model) is { } problem)
            {
                TempData["Error"] = problem;
                await LoadTenantsAsync(ct);
                return View(model);
            }

            if (model.Id == 0)
            {
                model.CreatedUtc = DateTime.UtcNow;
                model.UpdatedUtc = model.CreatedUtc;
                _gov.CatalogItems.Add(model);
            }
            else
            {
                var existing = await _gov.CatalogItems.FirstOrDefaultAsync(c => c.Id == model.Id, ct);
                if (existing == null) return NotFound();

                existing.DisplayName = model.DisplayName;
                existing.Description = model.Description;
                existing.TenantId = model.TenantId;
                existing.GroupName = model.GroupName;
                existing.ApproverAdGroup = model.ApproverAdGroup;
                existing.ApproverUsers = model.ApproverUsers;
                existing.ApproverNotificationEmail = model.ApproverNotificationEmail;
                existing.EligibleRequesterGroup = model.EligibleRequesterGroup;
                existing.DecisionDueDays = model.DecisionDueDays;
                existing.AccessDurationDays = model.AccessDurationDays;
                existing.IsEnabled = model.IsEnabled;
                existing.UpdatedUtc = DateTime.UtcNow;
            }

            await _gov.SaveChangesAsync(ct);
            TempData["Success"] = "تم الحفظ / Saved";
            return RedirectToAction(nameof(Index));
        }

        /// <summary>
        /// Disabling stops new requests; it never touches access already granted through the item,
        /// and never deletes the record a decided request points at.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Toggle(int id, CancellationToken ct)
        {
            var item = await _gov.CatalogItems.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (item == null) return NotFound();

            item.IsEnabled = !item.IsEnabled;
            item.UpdatedUtc = DateTime.UtcNow;
            await _gov.SaveChangesAsync(ct);

            TempData["Success"] = item.IsEnabled ? "تم التفعيل / Enabled" : "تم الإيقاف / Disabled";
            return RedirectToAction(nameof(Index));
        }

        private async Task LoadTenantsAsync(CancellationToken ct) =>
            ViewBag.Tenants = await _app.TenantSettings
                .Where(t => t.IsActive)
                .OrderBy(t => t.TenantName)
                .Select(t => new { t.Id, t.TenantName })
                .ToListAsync(ct);
    }
}
