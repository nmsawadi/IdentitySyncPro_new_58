using Hangfire;
using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Audit;
using IdentitySyncPro.Core.Models.Governance;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Jobs
{
    /// <summary>
    /// The two emails a campaign produces: it has started, and it has closed.
    ///
    /// Queued rather than sent inline, for the reason the access-request notifications were moved:
    /// an unreachable mail server once hung a request page indefinitely on the SMTP connect. A
    /// launch that reads a whole directory and then blocks on a mailbox would be the same failure
    /// on a much longer operation.
    ///
    /// The closing message carries the numbers, including the ones nobody wants. A campaign that
    /// closed without revoking anything because almost nothing was reviewed says exactly that —
    /// the report an auditor needs is the one that admits the review did not happen.
    /// </summary>
    public class CampaignNotificationJob
    {
        private readonly GovernanceDbContext _gov;
        private readonly IEmailService _email;
        private readonly IAuditService _audit;
        private readonly ILogger<CampaignNotificationJob> _logger;

        public CampaignNotificationJob(
            GovernanceDbContext gov, IEmailService email, IAuditService audit,
            ILogger<CampaignNotificationJob> logger)
        {
            _gov = gov;
            _email = email;
            _audit = audit;
            _logger = logger;
        }

        public const string Launched = "Launched";
        public const string Closed = "Closed";

        private const string AuditCategory = "AccessGovernance";

        [AutomaticRetry(Attempts = 3)]
        [Queue("default")]
        public async Task SendAsync(int campaignId, string moment, CancellationToken ct = default)
        {
            var campaign = await _gov.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId, ct);
            if (campaign == null)
            {
                _logger.LogWarning("CampaignNotification {Moment}: campaign {Id} no longer resolves", moment, campaignId);
                return;
            }

            var items = await _gov.CampaignItems
                .Where(i => i.CampaignId == campaignId)
                .Select(i => new { i.Decision, i.DecisionSource, i.ExecutionStatus })
                .ToListAsync(ct);

            var plan = AccessNotificationPlan.ForApprovers(
                campaign.ReviewerNotificationEmail,
                AccessRequestPolicy.NamesIn(campaign.ReviewerUsers),
                new Dictionary<string, string?>());

            if (!plan.HasRecipients)
            {
                // The same silence guarded against everywhere else in this module, and here it is
                // worse than a missed request: reviewers who never hear that a campaign opened do
                // not review it, and the deadline then revokes what they were meant to certify.
                _logger.LogWarning(
                    "Campaign {Id}: the '{Moment}' notice reached nobody — no reviewer mailbox is configured",
                    campaignId, moment);

                await _audit.LogAsync("CampaignNotificationWarning", AuditCategory, AuditSeverity.Warning,
                    entityType: nameof(GovCampaign), entityId: campaignId.ToString(),
                    details: $"{moment}: no recipient is configured for this campaign's reviewers.",
                    performedBy: ActorNames.System);
                return;
            }

            var (subject, body) = moment switch
            {
                Launched => (
                    $"مراجعة صلاحيات مطلوبة — {campaign.Name}",
                    Body("🔍 بدأت حملة مراجعة صلاحيات", "#0d6efd", new[]
                    {
                        ("الحملة", campaign.Name),
                        ("عدد العضويات", items.Count.ToString()),
                        ("مهلة المراجعة", $"{campaign.DueUtc:yyyy-MM-dd} UTC"),
                        // Said at the start, not discovered at the end: it is the fact that makes
                        // the deadline matter, and the reason a delegation exists.
                        ("عند انتهاء المهلة", $"ما لم يُقرَّر يُسحب تلقائياً (يتوقف السحب إن تجاوز غير المُراجَع {campaign.MaxUndecidedRevokePercent}٪)"),
                        ("إن كنت مسافراً", "فوّض زميلاً من شاشة التفويض قبل مغادرتك")
                    })),

                Closed => (
                    $"أُغلقت حملة المراجعة — {campaign.Name}",
                    Body("📋 أُغلقت حملة المراجعة", "#6c757d", new[]
                    {
                        ("الحملة", campaign.Name),
                        ("إجمالي العضويات", items.Count.ToString()),
                        ("أُبقيت", items.Count(i => i.Decision == GovReviewDecisions.Keep).ToString()),
                        ("سُحبت بقرار مُراجِع", items.Count(i => i.Decision == GovReviewDecisions.Revoke
                            && i.DecisionSource == GovDecisionSources.Reviewer).ToString()),
                        ("سُحبت لانتهاء المهلة بلا قرار", items.Count(i => i.DecisionSource == GovDecisionSources.AutoRevokedUndecided).ToString()),
                        ("لم يُنفَّذ في AD بعد", items.Count(i => i.ExecutionStatus == GovExecutionStatus.Failed).ToString()),
                        ("الخلاصة", campaign.ClosingNote ?? "—")
                    })),

                _ => (null, null)
            };

            if (subject == null)
            {
                _logger.LogError("CampaignNotification: unknown moment '{Moment}' for campaign {Id}", moment, campaignId);
                return;
            }

            var result = await _email.SendAsync(new EmailMessage
            {
                To = string.Join(",", plan.Recipients),
                Subject = subject,
                Body = body!,
                IsHtml = true
            });

            if (!result.Success)
            {
                _logger.LogError("Campaign {Id}: the '{Moment}' notice failed to send — {Error}",
                    campaignId, moment, result.Error);

                await _audit.LogAsync("CampaignNotificationError", AuditCategory, AuditSeverity.Error,
                    entityType: nameof(GovCampaign), entityId: campaignId.ToString(),
                    details: $"{moment}: send failed — {result.Error}", performedBy: ActorNames.System);
            }
        }

        private static string Body(string title, string colour, (string Label, string Value)[] rows)
        {
            var cells = string.Concat(rows.Select(row =>
                $"<tr><td style='padding:8px;border:1px solid #eee;font-weight:600;width:220px'>{row.Label}</td>" +
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
