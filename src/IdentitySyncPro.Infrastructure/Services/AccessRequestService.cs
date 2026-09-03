using Hangfire;
using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Governance;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Raising, deciding and carrying out access requests.
    ///
    /// The rules live in <see cref="AccessRequestPolicy"/>; this puts the directory and the
    /// database behind them. The split matters because the rules are the part that can be tested
    /// without a domain controller, and they are also the part whose failures are invisible.
    ///
    /// <b>Every membership question here fails closed in its own direction.</b> "Is this person an
    /// approver?" answered optimistically during a directory outage would hand the approval right
    /// to whoever asked, and would do it without a single error anywhere — so an unanswerable
    /// directory refuses the action and says which question it could not answer.
    /// </summary>
    public class AccessRequestService
    {
        private readonly GovernanceDbContext _gov;
        private readonly AppDbContext _app;
        private readonly ITenantConnectorFactory _connectors;
        private readonly IBackgroundJobClient _jobs;
        private readonly IAuditService _audit;
        private readonly ILogger<AccessRequestService> _logger;

        public AccessRequestService(
            GovernanceDbContext gov,
            AppDbContext app,
            ITenantConnectorFactory connectors,
            IBackgroundJobClient jobs,
            IAuditService audit,
            ILogger<AccessRequestService> logger)
        {
            _gov = gov;
            _app = app;
            _connectors = connectors;
            _jobs = jobs;
            _audit = audit;
            _logger = logger;
        }

        /// <summary>
        /// Queues a notification instead of sending it here.
        ///
        /// Sending inline hung the browser on the SMTP connect the first time this ran against a
        /// lab with no reachable mail server: the request had been created, the account resolved,
        /// and the page never came back. A notice is a consequence of the decision, not part of
        /// making it — and queueing also means a mail server that recovers an hour later still
        /// delivers, rather than the notice dying with the request that could not send it.
        ///
        /// Failing to queue is caught and logged: an unreachable job store must not undo governance
        /// work that has already been committed.
        /// </summary>
        private void Notify(long requestId, string moment, string? approver = null, string? comment = null)
        {
            try
            {
                _jobs.Enqueue<AccessNotificationJob>(
                    job => job.SendAsync(requestId, moment, approver, comment, CancellationToken.None));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "AccessRequest {Id}: could not queue the '{Moment}' notification — the request itself stands",
                    requestId, moment);
            }
        }

        public sealed record Outcome(bool Ok, string? Error = null, long RequestId = 0)
        {
            public static Outcome Fail(string error) => new(false, error);
            public static Outcome Success(long id = 0) => new(true, null, id);
        }

        private const string AuditCategory = "AccessGovernance";

        // ══════════════════════════════════════
        // RAISING
        // ══════════════════════════════════════

        public async Task<Outcome> CreateAsync(
            int catalogItemId, string subjectAccount, string requestedBy, string channel,
            string justification, CancellationToken ct = default)
        {
            var item = await _gov.CatalogItems.FirstOrDefaultAsync(c => c.Id == catalogItemId, ct);
            if (item == null) return Outcome.Fail("عنصر الكتالوج غير موجود / Catalog item not found.");

            // An item that was saved before a rule existed, or edited around one, is still refused
            // here — the request would otherwise be accepted into a queue nobody can clear.
            if (AccessRequestPolicy.ValidateCatalogItem(item) is { } broken)
                return Outcome.Fail($"This catalog item cannot be used: {broken}");

            var tenant = await _app.TenantSettings.FirstOrDefaultAsync(t => t.Id == item.TenantId, ct);
            if (tenant == null) return Outcome.Fail("الجهة المرتبطة بهذا العنصر لم تعد موجودة / The tenant this catalog item belongs to no longer exists.");

            var target = _connectors.CreateTargetConnector(tenant);

            var eligible = await IsEligibleAsync(target, item, requestedBy, ct);
            if (eligible.Error != null) return Outcome.Fail(eligible.Error);

            var alreadyMember = await target.TryIsMemberOfAnyAsync(subjectAccount, new[] { item.GroupName }, ct);
            if (alreadyMember == null)
                return Outcome.Fail("تعذّر التحقق من عضوية الحساب الحالية في AD — رُفض الطلب بدل قبول طلب قد يكون مكرّراً / Could not check the account's current membership in Active Directory.");

            var problem = AccessRequestPolicy.ValidateNewRequest(
                item, subjectAccount, requestedBy, justification,
                subjectIsAlreadyMember: alreadyMember.Value,
                requesterIsEligible: eligible.Eligible);
            if (problem != null) return Outcome.Fail(problem);

            var now = DateTime.UtcNow;
            var request = new GovAccessRequest
            {
                CatalogItemId = item.Id,
                SubjectAccount = subjectAccount.Trim(),
                SubjectDisplayName = await DisplayNameOfAsync(target, subjectAccount, ct),
                RequestedBy = requestedBy.Trim(),
                Channel = channel,
                Justification = justification.Trim(),
                Status = GovRequestStatus.Pending,
                CreatedUtc = now,
                DecisionDueUtc = AccessRequestPolicy.DecisionDeadline(item, now),
                ExecutionStatus = GovExecutionStatus.None
            };

            _gov.AccessRequests.Add(request);
            await _gov.SaveChangesAsync(ct);

            await _audit.LogAsync("AccessRequestRaised", AuditCategory, AuditSeverity.Info,
                entityType: nameof(GovAccessRequest), entityId: request.Id.ToString(),
                details: $"{requestedBy} requested '{item.DisplayName}' for {subjectAccount} via {channel}",
                performedBy: requestedBy);

            Notify(request.Id, AccessNotificationJob.Raised);
            return Outcome.Success(request.Id);
        }

        /// <summary>
        /// Whether the requester may ask for this item.
        ///
        /// An unset eligibility group means the item is open to everyone — that is a deliberate
        /// configuration, not an unanswered question, so it does not touch the directory at all.
        /// </summary>
        private static async Task<(bool Eligible, string? Error)> IsEligibleAsync(
            ITargetConnector target, GovCatalogItem item, string requestedBy, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(item.EligibleRequesterGroup)) return (true, null);

            var member = await target.TryIsMemberOfAnyAsync(requestedBy, new[] { item.EligibleRequesterGroup! }, ct);
            return member switch
            {
                true => (true, null),
                false => (false, null),
                _ => (false, "تعذّر التحقق من الأهلية في AD — رُفض الطلب بدل افتراضها / Could not verify eligibility in Active Directory.")
            };
        }

        private static async Task<string?> DisplayNameOfAsync(ITargetConnector target, string account, CancellationToken ct)
        {
            try
            {
                var attrs = await target.GetAttributesAsync(account, new[] { "displayName" }, ct);
                return attrs != null && attrs.TryGetValue("displayName", out var name) ? name : null;
            }
            catch
            {
                // A missing display name is cosmetic — it must never be the reason a request fails.
                return null;
            }
        }

        // ══════════════════════════════════════
        // DECIDING
        // ══════════════════════════════════════

        public async Task<Outcome> DecideAsync(
            long requestId, string approverUsername, string decision, string? comment,
            CancellationToken ct = default)
        {
            var request = await _gov.AccessRequests
                .Include(r => r.CatalogItem)
                .FirstOrDefaultAsync(r => r.Id == requestId, ct);
            if (request?.CatalogItem == null) return Outcome.Fail("الطلب غير موجود / Request not found.");

            var item = request.CatalogItem;
            var tenant = await _app.TenantSettings.FirstOrDefaultAsync(t => t.Id == item.TenantId, ct);
            if (tenant == null) return Outcome.Fail("الجهة المرتبطة بهذا الطلب لم تعد موجودة / The tenant this request belongs to no longer exists.");

            var isApprover = await IsApproverAsync(_connectors.CreateTargetConnector(tenant), item, approverUsername, ct);
            if (isApprover.Error != null) return Outcome.Fail(isApprover.Error);

            var problem = AccessRequestPolicy.ValidateDecision(
                request, approverUsername, isApprover.IsApprover, DateTime.UtcNow);
            if (problem != null) return Outcome.Fail(problem);

            string newStatus;
            try
            {
                newStatus = AccessRequestPolicy.StatusAfter(decision);
            }
            catch (InvalidOperationException ex)
            {
                return Outcome.Fail(ex.Message);
            }

            var now = DateTime.UtcNow;
            request.Status = newStatus;
            request.DecidedUtc = now;
            request.ExecutionStatus = AccessRequestPolicy.ExecutionStatusAfter(decision);
            if (decision == GovDecisions.Approve)
                request.AccessExpiresUtc = AccessRequestPolicy.AccessDeadline(item, now);

            _gov.RequestDecisions.Add(new GovRequestDecision
            {
                RequestId = request.Id,
                StepOrder = 1,
                ApproverUsername = approverUsername,
                Decision = decision,
                Comment = comment,
                DecidedUtc = now
            });

            await _gov.SaveChangesAsync(ct);

            await _audit.LogAsync($"AccessRequest{newStatus}", AuditCategory,
                decision == GovDecisions.Approve ? AuditSeverity.Info : AuditSeverity.Warning,
                entityType: nameof(GovAccessRequest), entityId: request.Id.ToString(),
                details: $"{approverUsername} {decision.ToLowerInvariant()}d '{item.DisplayName}' for {request.SubjectAccount}",
                performedBy: approverUsername);

            // Executed inline so the approver learns of a directory failure while still looking at
            // the screen. The sweep retries whatever fails here — the two paths write the same
            // fields, so a request never depends on which one reached it.
            if (decision == GovDecisions.Approve)
                await ExecuteAsync(request.Id, ct);
            else
                Notify(request.Id, AccessNotificationJob.Decided, approverUsername, comment);

            return Outcome.Success(request.Id);
        }

        /// <summary>
        /// Whether this person is an approver for the item — by name on its list, or by nested
        /// membership of its approver group.
        ///
        /// The name list is checked first because it needs no directory at all. Only when it does
        /// not settle the question is Active Directory asked, and an unanswerable directory refuses
        /// the decision rather than granting it.
        /// </summary>
        private static async Task<(bool IsApprover, string? Error)> IsApproverAsync(
            ITargetConnector target, GovCatalogItem item, string username, CancellationToken ct)
        {
            if (AccessRequestPolicy.NamesIn(item.ApproverUsers)
                .Contains(username, StringComparer.OrdinalIgnoreCase))
                return (true, null);

            if (string.IsNullOrWhiteSpace(item.ApproverAdGroup)) return (false, null);

            var member = await target.TryIsMemberOfAnyAsync(username, new[] { item.ApproverAdGroup! }, ct);
            return member switch
            {
                true => (true, null),
                false => (false, null),
                _ => (false, "تعذّر التحقق من كونك مُعتمِداً في AD — رُفض القرار بدل افتراضه / Could not verify your approver membership in Active Directory.")
            };
        }

        public async Task<Outcome> CancelAsync(long requestId, string byUsername, CancellationToken ct = default)
        {
            var request = await _gov.AccessRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct);
            if (request == null) return Outcome.Fail("الطلب غير موجود / Request not found.");

            if (AccessRequestPolicy.ValidateCancel(request, byUsername) is { } problem)
                return Outcome.Fail(problem);

            request.Status = GovRequestStatus.Cancelled;
            request.DecidedUtc = DateTime.UtcNow;
            await _gov.SaveChangesAsync(ct);

            await _audit.LogAsync("AccessRequestCancelled", AuditCategory, AuditSeverity.Info,
                entityType: nameof(GovAccessRequest), entityId: request.Id.ToString(),
                details: $"{byUsername} withdrew their request", performedBy: byUsername);

            return Outcome.Success(request.Id);
        }

        // ══════════════════════════════════════
        // EXECUTION
        // ══════════════════════════════════════

        /// <summary>
        /// Puts an approved request into Active Directory.
        ///
        /// A failure is recorded on the request and left visible rather than thrown away: the
        /// approver decided, the directory refused, and the gap between those two facts is exactly
        /// what <see cref="GovAccessRequest.ExecutionStatus"/> exists to hold. The sweep picks the
        /// row up again, so a transient outage costs a delay rather than the grant.
        /// </summary>
        public async Task<Outcome> ExecuteAsync(long requestId, CancellationToken ct = default)
        {
            var request = await _gov.AccessRequests
                .Include(r => r.CatalogItem)
                .FirstOrDefaultAsync(r => r.Id == requestId, ct);
            if (request?.CatalogItem == null) return Outcome.Fail("الطلب غير موجود / Request not found.");

            if (request.Status != GovRequestStatus.Approved)
                return Outcome.Fail("لا يُنفَّذ إلا الطلب المعتمَد / Only an approved request can be executed.");
            if (request.ExecutionStatus == GovExecutionStatus.Succeeded)
                return Outcome.Success(request.Id);   // already done — never add twice

            var item = request.CatalogItem;
            var tenant = await _app.TenantSettings.FirstOrDefaultAsync(t => t.Id == item.TenantId, ct);
            if (tenant == null) return await RecordFailureAsync(request, "الجهة لم تعد موجودة / The tenant no longer exists.", ct);

            try
            {
                var target = _connectors.CreateTargetConnector(tenant);
                var (success, addedCount, _) = await target.AddToGroupsAsync(
                    request.SubjectAccount, new[] { item.GroupName }, ct);

                if (!success)
                    return await RecordFailureAsync(request,
                        $"Active Directory did not confirm the membership (added {addedCount} of 1).", ct);

                request.ExecutionStatus = GovExecutionStatus.Succeeded;
                request.ExecutedUtc = DateTime.UtcNow;
                request.ExecutionError = null;
                await _gov.SaveChangesAsync(ct);

                await _audit.LogAsync("AccessRequestExecuted", AuditCategory, AuditSeverity.Info,
                    entityType: nameof(GovAccessRequest), entityId: request.Id.ToString(),
                    details: $"{request.SubjectAccount} added to '{item.GroupName}'",
                    performedBy: Core.Models.Audit.ActorNames.System);

                _logger.LogInformation(
                    "AccessRequest {Id}: {Subject} added to {Group}", request.Id, request.SubjectAccount, item.GroupName);

                Notify(request.Id, AccessNotificationJob.Executed);
                return Outcome.Success(request.Id);
            }
            catch (Exception ex)
            {
                return await RecordFailureAsync(request, ex.Message, ct);
            }
        }

        private async Task<Outcome> RecordFailureAsync(GovAccessRequest request, string error, CancellationToken ct)
        {
            request.ExecutionStatus = GovExecutionStatus.Failed;
            request.ExecutionError = error.Length > 2000 ? error[..2000] : error;
            await _gov.SaveChangesAsync(ct);

            await _audit.LogAsync("AccessRequestExecutionFailed", AuditCategory, AuditSeverity.Error,
                entityType: nameof(GovAccessRequest), entityId: request.Id.ToString(),
                details: error, performedBy: Core.Models.Audit.ActorNames.System);

            _logger.LogError("AccessRequest {Id}: execution failed — {Error}", request.Id, error);
            return Outcome.Fail(error);
        }

        // ══════════════════════════════════════
        // THE SWEEP
        // ══════════════════════════════════════

        public sealed record SweepResult(int Expired, int Executed, int Revoked, int Failed);

        /// <summary>
        /// The background pass: close overdue decisions, retry approvals that never reached the
        /// directory, and revoke access whose time is up.
        ///
        /// Retrying the failed executions is the reason this runs on a schedule rather than only at
        /// decision time. Without it an approval lost to a five-minute outage would stay "Approved,
        /// Failed" until a person happened to look at the row.
        /// </summary>
        public async Task<SweepResult> SweepAsync(CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            int expired = 0, executed = 0, revoked = 0, failed = 0;

            // ── Overdue decisions ──
            var overdue = await _gov.AccessRequests
                .Include(r => r.CatalogItem)
                .Where(r => r.Status == GovRequestStatus.Pending
                            && r.DecisionDueUtc != null && r.DecisionDueUtc <= now)
                .ToListAsync(ct);

            foreach (var request in overdue)
            {
                if (!AccessRequestPolicy.HasExpired(request, now)) continue;   // the rule decides, not the query

                request.Status = GovRequestStatus.Expired;
                request.DecidedUtc = now;
                expired++;

                await _audit.LogAsync("AccessRequestExpired", AuditCategory, AuditSeverity.Warning,
                    entityType: nameof(GovAccessRequest), entityId: request.Id.ToString(),
                    details: $"No decision within the window; requested by {request.RequestedBy}",
                    performedBy: Core.Models.Audit.ActorNames.Schedule);
            }
            if (expired > 0) await _gov.SaveChangesAsync(ct);

            foreach (var request in overdue.Where(r => r.Status == GovRequestStatus.Expired))
                Notify(request.Id, AccessNotificationJob.Expired);

            // ── Approvals that never reached the directory ──
            var unexecuted = await _gov.AccessRequests
                .Where(r => r.Status == GovRequestStatus.Approved
                            && (r.ExecutionStatus == GovExecutionStatus.Pending
                                || r.ExecutionStatus == GovExecutionStatus.Failed))
                .Select(r => r.Id)
                .ToListAsync(ct);

            foreach (var id in unexecuted)
            {
                var outcome = await ExecuteAsync(id, ct);
                if (outcome.Ok) executed++; else failed++;
            }

            // ── Access whose time is up ──
            var lapsed = await _gov.AccessRequests
                .Include(r => r.CatalogItem)
                .Where(r => r.Status == GovRequestStatus.Approved
                            && r.ExecutionStatus == GovExecutionStatus.Succeeded
                            && r.AccessRevokedUtc == null
                            && r.AccessExpiresUtc != null && r.AccessExpiresUtc <= now)
                .ToListAsync(ct);

            foreach (var request in lapsed)
            {
                if (!AccessRequestPolicy.AccessHasLapsed(request, now)) continue;
                if (await RevokeAsync(request, ct)) revoked++; else failed++;
            }

            if (expired + executed + revoked + failed > 0)
                _logger.LogInformation(
                    "AccessRequest sweep: {Expired} expired, {Executed} executed, {Revoked} revoked, {Failed} failed",
                    expired, executed, revoked, failed);

            return new SweepResult(expired, executed, revoked, failed);
        }

        /// <summary>
        /// Takes back time-bound access.
        ///
        /// A revocation that fails leaves <see cref="GovAccessRequest.AccessRevokedUtc"/> unset on
        /// purpose, so the next sweep tries again. Stamping it regardless would mark the access as
        /// taken back while the person still holds it — which is the failure a time-bound grant
        /// exists to prevent, wearing the appearance of the fix.
        /// </summary>
        private async Task<bool> RevokeAsync(GovAccessRequest request, CancellationToken ct)
        {
            var item = request.CatalogItem!;
            try
            {
                var tenant = await _app.TenantSettings.FirstOrDefaultAsync(t => t.Id == item.TenantId, ct);
                if (tenant == null)
                {
                    _logger.LogError("AccessRequest {Id}: cannot revoke — tenant {TenantId} no longer exists",
                        request.Id, item.TenantId);
                    return false;
                }

                var target = _connectors.CreateTargetConnector(tenant);
                var (success, _, _) = await target.RemoveFromSpecificGroupsAsync(
                    request.SubjectAccount, new[] { item.GroupName }, ct);

                if (!success)
                {
                    _logger.LogError("AccessRequest {Id}: revoking {Subject} from {Group} was not confirmed",
                        request.Id, request.SubjectAccount, item.GroupName);
                    return false;
                }

                request.AccessRevokedUtc = DateTime.UtcNow;
                await _gov.SaveChangesAsync(ct);

                await _audit.LogAsync("AccessRevoked", AuditCategory, AuditSeverity.Info,
                    entityType: nameof(GovAccessRequest), entityId: request.Id.ToString(),
                    details: $"{request.SubjectAccount} removed from '{item.GroupName}' — access period ended",
                    performedBy: Core.Models.Audit.ActorNames.Schedule);

                Notify(request.Id, AccessNotificationJob.Revoked);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AccessRequest {Id}: revocation failed", request.Id);
                return false;
            }
        }
    }
}
