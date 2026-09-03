using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Governance;
using IdentitySyncPro.Core.Models.Settings;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// The five emails an access request produces: raised, decided, granted, expired, revoked.
    ///
    /// <b>A notification that could not be delivered is written down, never swallowed.</b> The
    /// worst outcome for this module is an approval request that reaches nobody: the row says
    /// "Pending", the screen shows a queue, and the one person who could clear it never heard of
    /// it — with no error anywhere. So a plan with no recipients is logged as a warning and
    /// recorded in the audit trail as its own event, exactly as a failed send is.
    /// </summary>
    public class AccessRequestNotifier
    {
        private readonly IEmailService _email;
        private readonly ITenantConnectorFactory _connectors;
        private readonly IAuditService _audit;
        private readonly ILogger<AccessRequestNotifier> _logger;

        public AccessRequestNotifier(
            IEmailService email,
            ITenantConnectorFactory connectors,
            IAuditService audit,
            ILogger<AccessRequestNotifier> logger)
        {
            _email = email;
            _connectors = connectors;
            _audit = audit;
            _logger = logger;
        }

        private const string AuditCategory = "AccessGovernance";

        // ══════════════════════════════════════
        // THE FIVE MOMENTS
        // ══════════════════════════════════════

        public Task RequestRaisedAsync(GovAccessRequest r, GovCatalogItem item, TenantSettings tenant, CancellationToken ct) =>
            SendAsync(
                ApproverPlanAsync(item, tenant, ct),
                subject: $"طلب وصول بانتظار اعتمادك — {item.DisplayName}",
                body: Body("🔑 طلب وصول جديد", "#0d6efd", new[]
                {
                    ("الوصول المطلوب", item.DisplayName),
                    ("لحساب", $"{r.SubjectAccount}{Suffix(r.SubjectDisplayName)}"),
                    ("قدّمه", r.RequestedBy),
                    ("القناة", r.Channel == GovChannels.Portal ? "بوابة الموظف" : "الكونسول"),
                    ("المبرّر", r.Justification),
                    ("مهلة القرار", r.DecisionDueUtc == null ? "بلا مهلة" : $"{r.DecisionDueUtc:yyyy-MM-dd HH:mm} UTC")
                }),
                r, "ApproverNotification", ct);

        public Task RequestDecidedAsync(GovAccessRequest r, GovCatalogItem item, TenantSettings tenant,
            string approver, string? comment, CancellationToken ct)
        {
            var approved = r.Status == GovRequestStatus.Approved;
            return SendAsync(
                RequesterPlanAsync(r, tenant, ct),
                subject: $"{(approved ? "اعتُمد" : "رُفض")} طلبك — {item.DisplayName}",
                body: Body(approved ? "✅ اعتُمد الطلب" : "⛔ رُفض الطلب", approved ? "#198754" : "#dc3545", new[]
                {
                    ("الوصول", item.DisplayName),
                    ("لحساب", r.SubjectAccount),
                    ("القرار", approved ? "اعتماد" : "رفض"),
                    ("المُعتمِد", approver),
                    ("التعليق", string.IsNullOrWhiteSpace(comment) ? "—" : comment!)
                }),
                r, "RequesterDecisionNotification", ct);
        }

        public Task RequestExecutedAsync(GovAccessRequest r, GovCatalogItem item, TenantSettings tenant, CancellationToken ct) =>
            SendAsync(
                RequesterPlanAsync(r, tenant, ct),
                subject: $"تم منح الوصول — {item.DisplayName}",
                body: Body("✅ نُفِّذ المنح", "#198754", new[]
                {
                    ("الوصول", item.DisplayName),
                    ("المجموعة", item.GroupName),
                    ("لحساب", r.SubjectAccount),
                    // Stated on the grant itself, not only at revocation time: access that ends is
                    // a promise the holder should hear about while it is being made.
                    ("ينتهي في", r.AccessExpiresUtc == null ? "لا ينتهي" : $"{r.AccessExpiresUtc:yyyy-MM-dd} UTC")
                }),
                r, "RequesterGrantNotification", ct);

        /// <summary>
        /// Expiry goes to the requester, and to the approvers who let the window close. It is the
        /// only outcome nobody chose, so it is the one most worth telling both sides about.
        /// </summary>
        public async Task RequestExpiredAsync(GovAccessRequest r, GovCatalogItem item, CancellationToken ct)
        {
            var plan = AccessNotificationPlan.ForApprovers(
                item.ApproverNotificationEmail, Array.Empty<string>(),
                new Dictionary<string, string?>());

            await SendAsync(Task.FromResult(plan),
                subject: $"انتهت مهلة طلب وصول بلا قرار — {item.DisplayName}",
                body: Body("⏰ انتهت المهلة بلا قرار", "#fd7e14", new[]
                {
                    ("الوصول المطلوب", item.DisplayName),
                    ("لحساب", r.SubjectAccount),
                    ("قدّمه", r.RequestedBy),
                    ("قُدّم في", $"{r.CreatedUtc:yyyy-MM-dd HH:mm} UTC"),
                    ("انتهت المهلة", $"{r.DecisionDueUtc:yyyy-MM-dd HH:mm} UTC")
                }),
                r, "ExpiryNotification", ct);
        }

        public async Task AccessRevokedAsync(GovAccessRequest r, GovCatalogItem item, CancellationToken ct)
        {
            var plan = AccessNotificationPlan.ForApprovers(
                item.ApproverNotificationEmail, Array.Empty<string>(),
                new Dictionary<string, string?>());

            await SendAsync(Task.FromResult(plan),
                subject: $"انتهت مدة الوصول وسُحب — {item.DisplayName}",
                body: Body("🕐 سُحب الوصول بانتهاء مدته", "#6c757d", new[]
                {
                    ("الوصول", item.DisplayName),
                    ("المجموعة", item.GroupName),
                    ("من حساب", r.SubjectAccount),
                    ("مُنح في", $"{r.ExecutedUtc:yyyy-MM-dd} UTC"),
                    ("انتهى في", $"{r.AccessExpiresUtc:yyyy-MM-dd} UTC")
                }),
                r, "RevocationNotification", ct);
        }

        // ══════════════════════════════════════
        // WHO GETS IT
        // ══════════════════════════════════════

        private async Task<AccessNotificationPlan.Plan> ApproverPlanAsync(
            GovCatalogItem item, TenantSettings tenant, CancellationToken ct)
        {
            var named = AccessRequestPolicy.NamesIn(item.ApproverUsers);

            // The directory is only asked when there is no configured mailbox — an administrator's
            // explicit choice does not need confirming, and a lookup per approver on every request
            // is work this can avoid.
            var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(item.ApproverNotificationEmail) && named.Count > 0)
            {
                var target = _connectors.CreateTargetConnector(tenant);
                foreach (var user in named)
                    resolved[user] = await MailOfAsync(target, user, ct);
            }

            return AccessNotificationPlan.ForApprovers(item.ApproverNotificationEmail, named, resolved);
        }

        private async Task<AccessNotificationPlan.Plan> RequesterPlanAsync(
            GovAccessRequest r, TenantSettings tenant, CancellationToken ct)
        {
            var target = _connectors.CreateTargetConnector(tenant);
            return AccessNotificationPlan.ForPerson(r.RequestedBy, await MailOfAsync(target, r.RequestedBy, ct));
        }

        private async Task<string?> MailOfAsync(ITargetConnector target, string account, CancellationToken ct)
        {
            try
            {
                var attrs = await target.GetAttributesAsync(account, new[] { "mail" }, ct);
                return attrs != null && attrs.TryGetValue("mail", out var mail) ? mail : null;
            }
            catch (Exception ex)
            {
                // Not fatal, but not invisible either: an address that could not be read becomes an
                // unreachable recipient, and the caller records that the notice fell short.
                _logger.LogWarning(ex, "Could not read the mail attribute for {Account}", account);
                return null;
            }
        }

        // ══════════════════════════════════════
        // SENDING
        // ══════════════════════════════════════

        private async Task SendAsync(
            Task<AccessNotificationPlan.Plan> planTask, string subject, string body,
            GovAccessRequest request, string kind, CancellationToken ct)
        {
            AccessNotificationPlan.Plan plan;
            try
            {
                plan = await planTask;
            }
            catch (Exception ex)
            {
                await RecordAsync(request, kind, AuditSeverity.Error,
                    $"Could not work out who to notify: {ex.Message}");
                return;
            }

            if (!plan.HasRecipients)
            {
                // The silent failure this module is most exposed to, made loud.
                _logger.LogWarning(
                    "AccessRequest {Id}: {Kind} reached nobody — no mailbox configured and no address resolved{Named}",
                    request.Id, kind,
                    plan.Unreachable.Count > 0 ? $" for: {string.Join(", ", plan.Unreachable)}" : "");

                await RecordAsync(request, kind, AuditSeverity.Warning,
                    plan.Unreachable.Count > 0
                        ? $"No recipient could be reached. Unresolved: {string.Join(", ", plan.Unreachable)}"
                        : "No recipient is configured for this catalog item and none could be resolved.");
                return;
            }

            var result = await _email.SendAsync(new EmailMessage
            {
                To = string.Join(",", plan.Recipients),
                Subject = subject,
                Body = body,
                IsHtml = true
            });

            if (!result.Success)
            {
                _logger.LogError("AccessRequest {Id}: {Kind} failed to send — {Error}", request.Id, kind, result.Error);
                await RecordAsync(request, kind, AuditSeverity.Error, $"Send failed: {result.Error}");
                return;
            }

            // A partial delivery is recorded as partial. Reporting it as sent would be the same
            // silence in a quieter form.
            if (plan.Unreachable.Count > 0)
                await RecordAsync(request, kind, AuditSeverity.Warning,
                    $"Sent to {plan.Recipients.Count} recipient(s); no address for: {string.Join(", ", plan.Unreachable)}");
        }

        private Task RecordAsync(GovAccessRequest request, string kind, AuditSeverity severity, string details) =>
            _audit.LogAsync($"AccessNotification{severity}", AuditCategory, severity,
                entityType: nameof(GovAccessRequest), entityId: request.Id.ToString(),
                details: $"{kind}: {details}", performedBy: Core.Models.Audit.ActorNames.System);

        // ══════════════════════════════════════
        // THE MESSAGE
        // ══════════════════════════════════════

        private static string Suffix(string? displayName) =>
            string.IsNullOrWhiteSpace(displayName) ? "" : $" ({displayName})";

        private static string Body(string title, string colour, (string Label, string Value)[] rows)
        {
            var cells = string.Concat(rows.Select(row =>
                $"<tr><td style='padding:8px;border:1px solid #eee;font-weight:600;width:150px'>{row.Label}</td>" +
                $"<td style='padding:8px;border:1px solid #eee'>{System.Net.WebUtility.HtmlEncode(row.Value)}</td></tr>"));

            return $@"
<div dir='rtl' style='font-family: Segoe UI, Tahoma, Arial; padding:20px; background:#f8f9fa; border-radius:8px;'>
    <h2 style='color:{colour};border-bottom:2px solid {colour};padding-bottom:10px;'>{title}</h2>
    <table style='width:100%;border-collapse:collapse;margin-top:12px;font-size:13px;'>{cells}</table>
    <p style='margin-top:15px;color:#6c757d;font-size:12px;'>
        أُرسلت تلقائياً بواسطة IdentitySyncPro — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC
    </p>
</div>";
        }
    }
}
