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
    /// The reviewer's side: what is waiting on me, and who covers me while I am away.
    ///
    /// Open to any signed-in user for the same reason approvals are — a reviewer is a department
    /// head answering for their own people, not a system operator, and requiring a console role to
    /// certify would mean handing the console to everyone who has to sign something. The authority
    /// comes from the campaign's reviewer list and from delegations, checked in
    /// <see cref="CampaignService"/>.
    /// </summary>
    public class AccessReviewController : Controller
    {
        private readonly GovernanceDbContext _gov;
        private readonly CampaignService _service;

        public AccessReviewController(GovernanceDbContext gov, CampaignService service)
        {
            _gov = gov;
            _service = service;
        }

        private string Me => User.Identity?.Name ?? string.Empty;

        // ══════════════════════════════════════
        // WAITING ON ME
        // ══════════════════════════════════════

        public async Task<IActionResult> Index(int? campaignId, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var carried = await CarriedAuthorityAsync(now, ct);

            // Named reviewers only, plus whoever I stand in for. Resolving each campaign's reviewer
            // group in the directory on every page load would put a round trip behind a list view;
            // a group reviewer can still decide from the campaign link, and the screen says so.
            var mine = new List<string>(carried) { Me };

            var campaigns = await _gov.Campaigns
                .Where(c => c.Status == GovCampaignStatus.Active)
                .ToListAsync(ct);

            var visible = campaigns
                .Where(c => AccessRequestPolicy.NamesIn(c.ReviewerUsers)
                             .Any(r => mine.Contains(r, StringComparer.OrdinalIgnoreCase)))
                .Select(c => c.Id)
                .ToList();

            var query = _gov.CampaignItems
                .Include(i => i.Campaign)
                .Where(i => i.Decision == GovReviewDecisions.Pending && visible.Contains(i.CampaignId));

            if (campaignId != null) query = query.Where(i => i.CampaignId == campaignId);

            var items = await query
                .OrderBy(i => i.Campaign!.DueUtc)
                .ThenBy(i => i.GroupName)
                .ThenBy(i => i.SubjectAccount)
                .Take(500)
                .ToListAsync(ct);

            // Certifying your own membership is barred in the policy; keeping those rows off the
            // queue means nobody is shown a button that refuses them.
            ViewBag.Items = items
                .Where(i => !string.Equals(i.SubjectAccount, Me, StringComparison.OrdinalIgnoreCase))
                .ToList();

            ViewBag.Carried = carried;
            ViewBag.Campaigns = campaigns.Where(c => visible.Contains(c.Id)).ToList();
            ViewBag.SelectedCampaign = campaignId;

            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Decide(long id, string decision, string? comment, int? campaignId, CancellationToken ct)
        {
            var outcome = await _service.DecideAsync(id, Me, decision ?? "", comment, ct);

            if (!outcome.Ok)
            {
                TempData["Error"] = outcome.Error;
            }
            else
            {
                // The execution result is read back, never assumed. Telling a reviewer the access
                // is gone while it is still in the directory is the gap the separate execution
                // column exists to make visible.
                var saved = await _gov.CampaignItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
                if (decision == GovReviewDecisions.Keep)
                    TempData["Success"] = "تم الإبقاء على العضوية / Membership kept";
                else if (saved?.ExecutionStatus == GovExecutionStatus.Succeeded)
                    TempData["Success"] = "سُحبت العضوية من AD فعلياً / Membership removed from AD";
                else
                    TempData["Error"] = "سُجّل قرار السحب لكن التنفيذ في AD لم ينجح — يُعاد المحاولة كل 15 دقيقة. السبب: "
                                      + (saved?.ExecutionError ?? "غير معروف");
            }

            return RedirectToAction(nameof(Index), new { campaignId });
        }

        // ══════════════════════════════════════
        // DELEGATION
        // ══════════════════════════════════════

        public async Task<IActionResult> Delegations(CancellationToken ct)
        {
            ViewBag.Given = await _gov.ReviewDelegations
                .Where(d => d.FromUsername == Me)
                .OrderByDescending(d => d.CreatedUtc)
                .Take(50)
                .ToListAsync(ct);

            ViewBag.Received = await _gov.ReviewDelegations
                .Where(d => d.ToUsername == Me)
                .OrderByDescending(d => d.CreatedUtc)
                .Take(50)
                .ToListAsync(ct);

            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delegate(string toUsername, DateTime startUtc, DateTime endUtc, string? reason, CancellationToken ct)
        {
            var outcome = await _service.DelegateAsync(new GovReviewDelegation
            {
                // Always me. A delegation posted on somebody else's behalf would hand their
                // authority away without their knowing — and the record would still sign their name.
                FromUsername = Me,
                ToUsername = toUsername?.Trim() ?? "",
                StartUtc = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc),
                EndUtc = DateTime.SpecifyKind(endUtc, DateTimeKind.Utc),
                Reason = reason
            }, ct);

            if (outcome.Ok) TempData["Success"] = "تم التفويض / Delegation created";
            else TempData["Error"] = outcome.Error;

            return RedirectToAction(nameof(Delegations));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EndDelegation(long id, CancellationToken ct)
        {
            var outcome = await _service.EndDelegationAsync(id, Me, ct);
            if (outcome.Ok) TempData["Success"] = "أُنهي التفويض / Delegation ended";
            else TempData["Error"] = outcome.Error;
            return RedirectToAction(nameof(Delegations));
        }

        private async Task<IReadOnlyCollection<string>> CarriedAuthorityAsync(DateTime now, CancellationToken ct) =>
            CampaignPolicy.AuthorityOf(Me, await _gov.ReviewDelegations
                .Where(d => d.ToUsername == Me && d.RevokedUtc == null && d.StartUtc <= now && d.EndUtc > now)
                .ToListAsync(ct), now);
    }

    /// <summary>
    /// Managing campaigns: create, launch, watch, and read the closing certificate.
    ///
    /// Administrators only. Launching one puts a deadline on other people's access — every
    /// membership nobody certifies is revoked when it passes — so it is not a button that belongs
    /// beside the review queue.
    /// </summary>
    [Authorize(Roles = AppUserRoles.Admin)]
    public class AccessCampaignsController : Controller
    {
        private readonly GovernanceDbContext _gov;
        private readonly AppDbContext _app;
        private readonly CampaignService _service;

        public AccessCampaignsController(GovernanceDbContext gov, AppDbContext app, CampaignService service)
        {
            _gov = gov;
            _app = app;
            _service = service;
        }

        public async Task<IActionResult> Index(CancellationToken ct)
        {
            var campaigns = await _gov.Campaigns.OrderByDescending(c => c.CreatedUtc).ToListAsync(ct);

            ViewBag.Progress = await _gov.CampaignItems
                .GroupBy(i => i.CampaignId)
                .Select(g => new
                {
                    CampaignId = g.Key,
                    Total = g.Count(),
                    Pending = g.Count(i => i.Decision == GovReviewDecisions.Pending),
                    Revoked = g.Count(i => i.Decision == GovReviewDecisions.Revoke),
                    Failed = g.Count(i => i.ExecutionStatus == GovExecutionStatus.Failed)
                })
                .ToDictionaryAsync(x => x.CampaignId, x => (object)x, ct);

            // Shown before launching, not discovered at the deadline: a campaign that cannot be
            // reviewed revokes everything in scope when its window closes.
            ViewBag.Problems = campaigns.ToDictionary(c => c.Id, CampaignPolicy.ValidateCampaign);

            return View(campaigns);
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int? id, CancellationToken ct)
        {
            var campaign = id == null
                ? new GovCampaign()
                : await _gov.Campaigns.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (campaign == null) return NotFound();

            // A launched campaign is a snapshot people are deciding against. Editing its scope or
            // its reviewers afterwards would change what a decision meant after it was taken.
            if (campaign.Status != GovCampaignStatus.Draft && campaign.Id != 0)
            {
                TempData["Error"] = "لا تُعدَّل حملة بعد إطلاقها / A campaign cannot be edited once launched.";
                return RedirectToAction(nameof(Details), new { id = campaign.Id });
            }

            await LoadListsAsync(ct);
            return View(campaign);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(GovCampaign model, CancellationToken ct)
        {
            if (CampaignPolicy.ValidateCampaign(model) is { } problem)
            {
                TempData["Error"] = problem;
                await LoadListsAsync(ct);
                return View(model);
            }

            if (model.Id == 0)
            {
                model.Status = GovCampaignStatus.Draft;
                model.CreatedUtc = DateTime.UtcNow;
                _gov.Campaigns.Add(model);
            }
            else
            {
                var existing = await _gov.Campaigns.FirstOrDefaultAsync(c => c.Id == model.Id, ct);
                if (existing == null) return NotFound();
                if (existing.Status != GovCampaignStatus.Draft)
                {
                    TempData["Error"] = "لا تُعدَّل حملة بعد إطلاقها / A campaign cannot be edited once launched.";
                    return RedirectToAction(nameof(Details), new { id = existing.Id });
                }

                existing.Name = model.Name;
                existing.Description = model.Description;
                existing.ScopeGroups = model.ScopeGroups;
                existing.ScopeTenantId = model.ScopeTenantId;
                existing.ScopeCatalogItemIds = model.ScopeCatalogItemIds;
                existing.ReviewerUsers = model.ReviewerUsers;
                existing.ReviewerAdGroup = model.ReviewerAdGroup;
                existing.ReviewerNotificationEmail = model.ReviewerNotificationEmail;
                existing.ReviewDays = model.ReviewDays;
                existing.MaxUndecidedRevokePercent = model.MaxUndecidedRevokePercent;
            }

            await _gov.SaveChangesAsync(ct);
            TempData["Success"] = "تم الحفظ / Saved";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Launch(int id, CancellationToken ct)
        {
            var outcome = await _service.LaunchAsync(id, User.Identity?.Name ?? "", ct);

            if (outcome.Ok)
                TempData["Success"] = $"أُطلقت الحملة بـ {outcome.Count} عضوية / Launched with {outcome.Count} membership(s)";
            else
                TempData["Error"] = outcome.Error;

            return RedirectToAction(nameof(Details), new { id });
        }

        public async Task<IActionResult> Details(int id, CancellationToken ct)
        {
            var campaign = await _gov.Campaigns.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (campaign == null) return NotFound();

            ViewBag.Items = await _gov.CampaignItems
                .Where(i => i.CampaignId == id)
                .OrderBy(i => i.Decision == GovReviewDecisions.Pending ? 0 : 1)
                .ThenBy(i => i.GroupName)
                .ThenBy(i => i.SubjectAccount)
                .Take(1000)
                .ToListAsync(ct);

            ViewBag.Problem = CampaignPolicy.ValidateCampaign(campaign);
            return View(campaign);
        }

        private async Task LoadListsAsync(CancellationToken ct)
        {
            ViewBag.Tenants = await _app.TenantSettings
                .Where(t => t.IsActive)
                .OrderBy(t => t.TenantName)
                .Select(t => new { t.Id, t.TenantName })
                .ToListAsync(ct);

            ViewBag.CatalogItems = await _gov.CatalogItems
                .OrderBy(c => c.DisplayName)
                .Select(c => new { c.Id, c.DisplayName, c.GroupName })
                .ToListAsync(ct);
        }
    }
}
