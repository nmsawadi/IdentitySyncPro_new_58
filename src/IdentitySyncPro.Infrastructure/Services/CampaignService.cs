using Hangfire;
using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Audit;
using IdentitySyncPro.Core.Models.Governance;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Running a certification campaign: take the snapshot, collect the decisions, carry out the
    /// revocations, and close it.
    ///
    /// The rules live in <see cref="CampaignPolicy"/>. This puts the directory and the database
    /// behind them — and it is the side that can take access away from a lot of people at once, so
    /// every step that could do so refuses on a missing answer rather than acting on one.
    /// </summary>
    public class CampaignService
    {
        private readonly GovernanceDbContext _gov;
        private readonly AppDbContext _app;
        private readonly ITenantConnectorFactory _connectors;
        private readonly IBackgroundJobClient _jobs;
        private readonly IAuditService _audit;
        private readonly ILogger<CampaignService> _logger;

        public CampaignService(
            GovernanceDbContext gov, AppDbContext app, ITenantConnectorFactory connectors,
            IBackgroundJobClient jobs, IAuditService audit, ILogger<CampaignService> logger)
        {
            _gov = gov;
            _app = app;
            _connectors = connectors;
            _jobs = jobs;
            _audit = audit;
            _logger = logger;
        }

        private const string AuditCategory = "AccessGovernance";

        public sealed record Outcome(bool Ok, string? Error = null, int Count = 0);

        // ══════════════════════════════════════
        // LAUNCHING — THE SNAPSHOT
        // ══════════════════════════════════════

        /// <summary>
        /// Turns a draft into a running campaign by recording who holds what, right now.
        ///
        /// <b>A group that cannot be read in full aborts the launch.</b> Nothing is saved, the
        /// campaign stays a draft, and the reason names the group. The alternative is the failure
        /// this whole feature would be worthless with: a campaign missing a group, reviewed
        /// diligently, closed as complete — and an auditor told that access was certified when a
        /// whole population was never in front of anybody.
        /// </summary>
        public async Task<Outcome> LaunchAsync(int campaignId, string startedBy, CancellationToken ct = default)
        {
            var campaign = await _gov.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId, ct);
            if (campaign == null) return new Outcome(false, "الحملة غير موجودة / Campaign not found.");

            if (campaign.Status != GovCampaignStatus.Draft)
                return new Outcome(false, $"الحملة في حالة {campaign.Status.ToLowerInvariant()} ولا تُطلق مرة أخرى / "
                                        + $"The campaign is already {campaign.Status.ToLowerInvariant()}.");

            if (CampaignPolicy.ValidateCampaign(campaign) is { } problem)
                return new Outcome(false, problem);

            var targets = await ResolveScopeAsync(campaign, ct);
            if (targets.Error != null) return new Outcome(false, targets.Error);

            var items = new List<GovCampaignItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (tenantId, groupName, catalogItemId) in targets.Groups)
            {
                ct.ThrowIfCancellationRequested();

                var tenant = await _app.TenantSettings.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
                if (tenant == null)
                    return new Outcome(false, $"الجهة {tenantId} غير موجودة / Tenant {tenantId} does not exist.");

                var target = _connectors.CreateTargetConnector(tenant);
                var (success, members, error) = await target.GetGroupMembersAsync(groupName, nested: false, ct);

                if (!success)
                    return new Outcome(false,
                        $"تعذّرت قراءة أعضاء '{groupName}' كاملةً، فلم تُطلق الحملة: {error} / "
                        + $"Could not read all members of '{groupName}' — the campaign was not launched: {error}");

                foreach (var member in members)
                {
                    // The same account can reach a campaign through an explicit group and a catalog
                    // entry naming it. One row per membership, or a reviewer decides the same thing
                    // twice and the two answers can disagree.
                    if (!seen.Add($"{tenantId}|{groupName}|{member.Account}")) continue;

                    items.Add(new GovCampaignItem
                    {
                        CampaignId = campaign.Id,
                        SubjectAccount = member.Account,
                        SubjectDisplayName = member.DisplayName,
                        GroupName = groupName,
                        TenantId = tenantId,
                        SourceCatalogItemId = catalogItemId,
                        Decision = GovReviewDecisions.Pending,
                        DecisionSource = GovDecisionSources.Reviewer,
                        ExecutionStatus = GovExecutionStatus.None
                    });
                }
            }

            // An empty scope is a configuration fault, not a clean directory — and launching it
            // would produce a campaign that closes instantly reporting nothing to review.
            if (items.Count == 0)
                return new Outcome(false,
                    "لا عضوية واحدة في نطاق الحملة — راجع المجموعات قبل الإطلاق / "
                    + "Not one membership is in scope — check the groups before launching.");

            var now = DateTime.UtcNow;
            campaign.Status = GovCampaignStatus.Active;
            campaign.StartedUtc = now;
            campaign.DueUtc = CampaignPolicy.Deadline(campaign, now);

            _gov.CampaignItems.AddRange(items);
            await _gov.SaveChangesAsync(ct);

            await _audit.LogAsync("CampaignLaunched", AuditCategory, AuditSeverity.Info,
                entityType: nameof(GovCampaign), entityId: campaign.Id.ToString(),
                details: $"'{campaign.Name}' launched with {items.Count} membership(s), due {campaign.DueUtc:yyyy-MM-dd}",
                performedBy: startedBy);

            _logger.LogInformation("Campaign {Id} '{Name}' launched: {Count} items, due {Due:yyyy-MM-dd}",
                campaign.Id, campaign.Name, items.Count, campaign.DueUtc);

            Notify(campaign.Id, CampaignNotificationJob.Launched);
            return new Outcome(true, null, items.Count);
        }

        /// <summary>Every (tenant, group) the campaign covers, from both scope sources.</summary>
        private async Task<(List<(int TenantId, string GroupName, int? CatalogItemId)> Groups, string? Error)>
            ResolveScopeAsync(GovCampaign campaign, CancellationToken ct)
        {
            var groups = new List<(int, string, int?)>();

            foreach (var name in AccessRequestPolicy.NamesIn(campaign.ScopeGroups))
                groups.Add((campaign.ScopeTenantId!.Value, name, null));

            foreach (var id in CampaignPolicy.ParseIds(campaign.ScopeCatalogItemIds))
            {
                var item = await _gov.CatalogItems.FirstOrDefaultAsync(c => c.Id == id, ct);

                // A catalog entry deleted after the campaign was drafted leaves a gap the reviewer
                // cannot see. Refusing beats launching a campaign whose scope quietly shrank.
                if (item == null)
                    return (groups, $"عنصر الكتالوج {id} لم يعد موجوداً / Catalog item {id} no longer exists.");

                groups.Add((item.TenantId, item.GroupName, item.Id));
            }

            return (groups, null);
        }

        // ══════════════════════════════════════
        // REVIEWING
        // ══════════════════════════════════════

        public async Task<Outcome> DecideAsync(
            long itemId, string username, string decision, string? comment, CancellationToken ct = default)
        {
            var item = await _gov.CampaignItems
                .Include(i => i.Campaign)
                .FirstOrDefaultAsync(i => i.Id == itemId, ct);
            if (item?.Campaign == null) return new Outcome(false, "العنصر غير موجود / Item not found.");

            var campaign = item.Campaign;
            var now = DateTime.UtcNow;

            var reviewerCheck = await IsReviewerAsync(campaign, username, ct);
            if (reviewerCheck.Error != null) return new Outcome(false, reviewerCheck.Error);

            var delegated = CampaignPolicy.AuthorityOf(
                username, await InForceDelegationsForAsync(username, ct), now);

            var right = CampaignPolicy.CanReview(campaign, item, username, reviewerCheck.IsReviewer, delegated);
            if (!right.Allowed) return new Outcome(false, right.Problem);

            string execution;
            try
            {
                execution = CampaignPolicy.ExecutionAfter(decision);
            }
            catch (InvalidOperationException ex)
            {
                return new Outcome(false, ex.Message);
            }

            item.Decision = decision;
            item.DecidedBy = username;
            item.DecidedOnBehalfOf = right.OnBehalfOf;   // null when they acted as themselves
            item.DecisionSource = GovDecisionSources.Reviewer;
            item.Comment = comment;
            item.DecidedUtc = now;
            item.ExecutionStatus = execution;

            await _gov.SaveChangesAsync(ct);

            // Both names in the audit line, for the same reason both are on the row: a certificate
            // that says the manager decided, while the manager was away, is not true.
            var who = right.OnBehalfOf == null ? username : $"{username} on behalf of {right.OnBehalfOf}";
            await _audit.LogAsync($"CampaignItem{decision}", AuditCategory,
                decision == GovReviewDecisions.Revoke ? AuditSeverity.Warning : AuditSeverity.Info,
                entityType: nameof(GovCampaignItem), entityId: item.Id.ToString(),
                details: $"{who} chose {decision} for {item.SubjectAccount} in '{item.GroupName}'",
                performedBy: username);

            if (decision == GovReviewDecisions.Revoke)
                await ExecuteAsync(item.Id, ct);

            return new Outcome(true);
        }

        /// <summary>
        /// Whether this person is a reviewer on the campaign — by name, or by the reviewer group.
        ///
        /// Fails closed for the same reason the approver check does: answered optimistically during
        /// a directory outage, it would let anyone certify anything, and the record would show a
        /// legitimate review.
        /// </summary>
        private async Task<(bool IsReviewer, string? Error)> IsReviewerAsync(
            GovCampaign campaign, string username, CancellationToken ct)
        {
            if (AccessRequestPolicy.NamesIn(campaign.ReviewerUsers)
                .Contains(username, StringComparer.OrdinalIgnoreCase))
                return (true, null);

            if (string.IsNullOrWhiteSpace(campaign.ReviewerAdGroup)) return (false, null);

            var tenantId = campaign.ScopeTenantId
                ?? (await _gov.CampaignItems.Where(i => i.CampaignId == campaign.Id)
                        .Select(i => (int?)i.TenantId).FirstOrDefaultAsync(ct));

            var tenant = tenantId == null
                ? null
                : await _app.TenantSettings.FirstOrDefaultAsync(t => t.Id == tenantId, ct);

            if (tenant == null)
                return (false, "تعذّر تحديد الجهة للتحقق من مجموعة المُراجِعين / "
                             + "Could not determine the tenant to check the reviewer group against.");

            var member = await _connectors.CreateTargetConnector(tenant)
                .TryIsMemberOfAnyAsync(username, new[] { campaign.ReviewerAdGroup! }, ct);

            return member switch
            {
                true => (true, null),
                false => (false, null),
                _ => (false, "تعذّر التحقق من عضويتك في مجموعة المُراجِعين — رُفض القرار بدل افتراضه / "
                           + "Could not verify your reviewer group membership — refusing rather than assuming it.")
            };
        }

        private Task<List<GovReviewDelegation>> InForceDelegationsForAsync(string username, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            return _gov.ReviewDelegations
                .Where(d => d.ToUsername == username && d.RevokedUtc == null
                            && d.StartUtc <= now && d.EndUtc > now)
                .ToListAsync(ct);
        }

        // ══════════════════════════════════════
        // DELEGATION
        // ══════════════════════════════════════

        public async Task<Outcome> DelegateAsync(GovReviewDelegation delegation, CancellationToken ct = default)
        {
            if (CampaignPolicy.ValidateDelegation(delegation, DateTime.UtcNow) is { } problem)
                return new Outcome(false, problem);

            _gov.ReviewDelegations.Add(delegation);
            await _gov.SaveChangesAsync(ct);

            await _audit.LogAsync("ReviewDelegated", AuditCategory, AuditSeverity.Warning,
                entityType: nameof(GovReviewDelegation), entityId: delegation.Id.ToString(),
                details: $"{delegation.FromUsername} delegated reviews to {delegation.ToUsername} "
                       + $"from {delegation.StartUtc:yyyy-MM-dd} to {delegation.EndUtc:yyyy-MM-dd}",
                performedBy: delegation.FromUsername);

            return new Outcome(true);
        }

        /// <summary>Ends a delegation early — coming back from leave sooner than planned.</summary>
        public async Task<Outcome> EndDelegationAsync(long id, string byUsername, CancellationToken ct = default)
        {
            var d = await _gov.ReviewDelegations.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (d == null) return new Outcome(false, "التفويض غير موجود / Delegation not found.");

            // Only the person who gave the authority away can take it back. A stand-in ending their
            // own delegation would leave the reviewer covered by nobody, without their knowing.
            if (!string.Equals(d.FromUsername, byUsername, StringComparison.OrdinalIgnoreCase))
                return new Outcome(false, "لا يُنهي التفويض إلا من أصدره / Only the delegating reviewer can end it.");

            if (d.RevokedUtc != null) return new Outcome(true);

            d.RevokedUtc = DateTime.UtcNow;
            await _gov.SaveChangesAsync(ct);

            await _audit.LogAsync("ReviewDelegationEnded", AuditCategory, AuditSeverity.Info,
                entityType: nameof(GovReviewDelegation), entityId: d.Id.ToString(),
                details: $"{byUsername} ended the delegation to {d.ToUsername}", performedBy: byUsername);

            return new Outcome(true);
        }

        // ══════════════════════════════════════
        // EXECUTION
        // ══════════════════════════════════════

        /// <summary>
        /// Removes one certified-out membership from the directory.
        ///
        /// A failure is written to the row and left visible, exactly as an access grant's is: the
        /// reviewer decided, the directory refused, and the gap between those two facts is what the
        /// separate execution column is for. The sweep retries it.
        /// </summary>
        public async Task<Outcome> ExecuteAsync(long itemId, CancellationToken ct = default)
        {
            var item = await _gov.CampaignItems.FirstOrDefaultAsync(i => i.Id == itemId, ct);
            if (item == null) return new Outcome(false, "العنصر غير موجود / Item not found.");

            if (item.Decision != GovReviewDecisions.Revoke)
                return new Outcome(false, "لا يُنفَّذ إلا قرار السحب / Only a revocation is executed.");
            if (item.ExecutionStatus == GovExecutionStatus.Succeeded)
                return new Outcome(true);   // never remove twice

            try
            {
                var tenant = await _app.TenantSettings.FirstOrDefaultAsync(t => t.Id == item.TenantId, ct);
                if (tenant == null) return await FailAsync(item, "الجهة لم تعد موجودة / The tenant no longer exists.", ct);

                var target = _connectors.CreateTargetConnector(tenant);
                var (success, _, _) = await target.RemoveFromSpecificGroupsAsync(
                    item.SubjectAccount, new[] { item.GroupName }, ct);

                if (!success)
                    return await FailAsync(item, "لم يؤكّد AD إزالة العضوية / Active Directory did not confirm the removal.", ct);

                item.ExecutionStatus = GovExecutionStatus.Succeeded;
                item.ExecutedUtc = DateTime.UtcNow;
                item.ExecutionError = null;
                await _gov.SaveChangesAsync(ct);

                await _audit.LogAsync("CampaignRevocationExecuted", AuditCategory, AuditSeverity.Warning,
                    entityType: nameof(GovCampaignItem), entityId: item.Id.ToString(),
                    details: $"{item.SubjectAccount} removed from '{item.GroupName}' "
                           + $"({(item.DecisionSource == GovDecisionSources.AutoRevokedUndecided ? "undecided at the deadline" : "certified out")})",
                    performedBy: ActorNames.System);

                return new Outcome(true);
            }
            catch (Exception ex)
            {
                return await FailAsync(item, ex.Message, ct);
            }
        }

        private async Task<Outcome> FailAsync(GovCampaignItem item, string error, CancellationToken ct)
        {
            item.ExecutionStatus = GovExecutionStatus.Failed;
            item.ExecutionError = error.Length > 2000 ? error[..2000] : error;
            await _gov.SaveChangesAsync(ct);

            await _audit.LogAsync("CampaignRevocationFailed", AuditCategory, AuditSeverity.Error,
                entityType: nameof(GovCampaignItem), entityId: item.Id.ToString(),
                details: error, performedBy: ActorNames.System);

            _logger.LogError("Campaign item {Id}: revocation failed — {Error}", item.Id, error);
            return new Outcome(false, error);
        }

        // ══════════════════════════════════════
        // THE DEADLINE
        // ══════════════════════════════════════

        public sealed record SweepResult(int Closed, int AutoRevoked, int Retried, int Failed, int Halted);

        /// <summary>
        /// Closes campaigns whose deadline has passed, and retries revocations that never reached
        /// the directory.
        ///
        /// Undecided memberships are revoked — that is the policy. The ceiling decides whether this
        /// campaign is a case the policy is for: a few rows nobody reached is a reviewer who did the
        /// work, and revoking those is what certification means. A campaign where almost nothing was
        /// decided is not a verdict; it is a reviewer who never opened it. When the ceiling stops
        /// it, the campaign still closes — with the reason written into it, because the failure is
        /// that nobody reviewed, and hiding that would defeat the exercise as surely as revoking
        /// blindly would.
        /// </summary>
        public async Task<SweepResult> SweepAsync(CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            int closed = 0, autoRevoked = 0, retried = 0, failed = 0, halted = 0;

            var lapsed = await _gov.Campaigns
                .Where(c => c.Status == GovCampaignStatus.Active && c.DueUtc != null && c.DueUtc <= now)
                .ToListAsync(ct);

            foreach (var campaign in lapsed)
            {
                if (!CampaignPolicy.HasLapsed(campaign, now)) continue;   // the rule decides, not the query

                var items = await _gov.CampaignItems.Where(i => i.CampaignId == campaign.Id).ToListAsync(ct);
                var undecided = items.Where(i => i.Decision == GovReviewDecisions.Pending).ToList();

                var verdict = CampaignPolicy.MayAutoRevoke(
                    items.Count, undecided.Count, campaign.MaxUndecidedRevokePercent);

                if (verdict.Allowed)
                {
                    foreach (var item in undecided)
                    {
                        item.Decision = GovReviewDecisions.Revoke;
                        item.DecisionSource = GovDecisionSources.AutoRevokedUndecided;
                        item.DecidedUtc = now;
                        item.ExecutionStatus = GovExecutionStatus.Pending;
                        autoRevoked++;
                    }
                    campaign.ClosingNote = undecided.Count == 0
                        ? "اكتملت المراجعة / Fully reviewed."
                        : $"{undecided.Count} بلا قرار سُحبت تلقائياً / {undecided.Count} undecided, revoked at the deadline.";
                }
                else
                {
                    halted++;
                    campaign.ClosingNote = verdict.Reason;

                    // Loud, and at Error: a campaign that closed without reviewing anything is a
                    // governance failure, and the quiet version of it is indistinguishable from a
                    // clean run in every report that follows.
                    _logger.LogError(
                        "Campaign {Id} '{Name}' closed WITHOUT auto-revocation — {Reason}",
                        campaign.Id, campaign.Name, verdict.Reason);

                    await _audit.LogAsync("CampaignAutoRevokeHalted", AuditCategory, AuditSeverity.Error,
                        entityType: nameof(GovCampaign), entityId: campaign.Id.ToString(),
                        details: verdict.Reason!, performedBy: ActorNames.Schedule);
                }

                campaign.Status = GovCampaignStatus.Closed;
                campaign.ClosedUtc = now;
                closed++;

                await _audit.LogAsync("CampaignClosed", AuditCategory,
                    verdict.Allowed ? AuditSeverity.Info : AuditSeverity.Warning,
                    entityType: nameof(GovCampaign), entityId: campaign.Id.ToString(),
                    details: $"'{campaign.Name}': {items.Count} item(s), {undecided.Count} undecided. {campaign.ClosingNote}",
                    performedBy: ActorNames.Schedule);
            }

            if (closed > 0) await _gov.SaveChangesAsync(ct);
            foreach (var campaign in lapsed.Where(c => c.Status == GovCampaignStatus.Closed))
                Notify(campaign.Id, CampaignNotificationJob.Closed);

            // Revocations that never reached the directory — decided by a reviewer or by a deadline.
            var pending = await _gov.CampaignItems
                .Where(i => i.Decision == GovReviewDecisions.Revoke
                            && (i.ExecutionStatus == GovExecutionStatus.Pending
                                || i.ExecutionStatus == GovExecutionStatus.Failed))
                .Select(i => i.Id)
                .ToListAsync(ct);

            foreach (var id in pending)
            {
                var outcome = await ExecuteAsync(id, ct);
                if (outcome.Ok) retried++; else failed++;
            }

            if (closed + retried + failed + halted > 0)
                _logger.LogInformation(
                    "Campaign sweep: {Closed} closed ({Halted} without auto-revocation), {AutoRevoked} auto-revoked, {Retried} executed, {Failed} failing",
                    closed, halted, autoRevoked, retried, failed);

            return new SweepResult(closed, autoRevoked, retried, failed, halted);
        }

        private void Notify(int campaignId, string moment)
        {
            try
            {
                _jobs.Enqueue<CampaignNotificationJob>(
                    job => job.SendAsync(campaignId, moment, CancellationToken.None));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Campaign {Id}: could not queue the '{Moment}' notification — the campaign itself stands",
                    campaignId, moment);
            }
        }
    }
}
