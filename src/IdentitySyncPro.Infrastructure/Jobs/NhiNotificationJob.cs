using System.DirectoryServices.Protocols;
using System.Net;
using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Audit;
using IdentitySyncPro.Core.Models.Governance;
using IdentitySyncPro.Core.Models.Services;
using IdentitySyncPro.Infrastructure.Connectors;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Jobs
{
    /// <summary>
    /// Tells owners their service accounts need attention, and tells the operator everything.
    ///
    /// A screen nobody opens is not a reminder. Until this job existed, an account could go from
    /// discovered to quarantined without the person answerable for it ever being told — the whole
    /// lifecycle would have run correctly and nobody would have had the chance to act.
    ///
    /// <para>Two channels, deliberately. The per-owner notice is the one that produces action. The
    /// digest to the operator mailbox is the one that is guaranteed to arrive: it carries every
    /// account that is due, including the ones whose owner has no reachable address and the ones
    /// with no owner at all. If only the per-owner notice existed, an unreachable owner would mean
    /// silence, and silence here reads exactly like "nothing is due".</para>
    /// </summary>
    public class NhiNotificationJob
    {
        private readonly GovernanceDbContext _gov;
        private readonly ServicesDbContext _services;
        private readonly IEmailService _email;
        private readonly IAuditService _audit;
        private readonly ILogger<NhiNotificationJob> _logger;

        private const string AuditCategory = "NonHumanIdentity";

        public NhiNotificationJob(
            GovernanceDbContext gov,
            ServicesDbContext services,
            IEmailService email,
            IAuditService audit,
            ILogger<NhiNotificationJob> logger)
        {
            _gov = gov;
            _services = services;
            _email = email;
            _audit = audit;
            _logger = logger;
        }

        public async Task ExecuteAsync(CancellationToken ct = default)
        {
            var services = await _services.SvcServices
                .Where(s => s.IsEnabled && s.NhiLifecycleEnabled && s.ReportType == "NonHumanInventory")
                .ToListAsync(ct);

            if (services.Count == 0)
            {
                _logger.LogDebug("NhiNotification: no service has the non-human lifecycle switched on");
                return;
            }

            foreach (var service in services)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await NotifyForServiceAsync(service, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // One misconfigured service must not stop the others from being notified.
                    _logger.LogError(ex, "NhiNotification: service '{Name}' ({Id}) failed", service.Name, service.Id);
                }
            }
        }

        private async Task NotifyForServiceAsync(SvcService service, CancellationToken ct)
        {
            var now = DateTime.UtcNow;

            var config = new NhiLifecyclePolicy.LifecycleConfig(
                true, service.NhiClaimDays, service.NhiAttestationDays, service.NhiAttestationGraceDays,
                string.IsNullOrWhiteSpace(service.NhiQuarantineMode) ? GovNhiEnforcement.Report : service.NhiQuarantineMode!,
                service.NhiMaxQuarantinePercent);

            if (NhiLifecyclePolicy.ValidateConfig(config) is { } bad)
            {
                _logger.LogWarning("NhiNotification: service '{Name}' has invalid lifecycle settings — {Problem}", service.Name, bad);
                return;
            }

            var accounts = await _gov.NhiAccounts
                .Where(a => a.ServiceId == service.Id && a.RetiredUtc == null)
                .ToListAsync(ct);

            if (accounts.Count == 0) return;

            // Addresses are looked up once per owner, and a failure to resolve one is an answer the
            // plan knows how to carry rather than an exception.
            using var addresses = new OwnerAddressBook(service, _logger);
            var plan = NhiNotificationPlan.Build(accounts, config, NhiNotificationPlan.Timing.Default, addresses.Find, now);

            if (plan.Empty)
            {
                _logger.LogDebug("NhiNotification: nothing due for '{Name}'", service.Name);
                return;
            }

            var notified = new List<GovNhiAccount>();

            foreach (var (owner, items) in plan.ByOwner)
            {
                var address = addresses.Find(owner);
                if (string.IsNullOrWhiteSpace(address)) continue;   // already counted as unreachable

                var sent = await SendAsync(
                    address!,
                    $"حسابات غير بشرية تحتاج إجراءك ({items.Count}) / Service accounts needing your action",
                    OwnerBody(owner, items),
                    $"owner {owner}");

                if (sent) notified.AddRange(items.Select(i => i.Account));
            }

            // The digest is the guaranteed channel. It goes out even when every owner was reached,
            // because the accounts with no owner appear nowhere else.
            if (!string.IsNullOrWhiteSpace(service.NhiOwnerNotificationEmail))
            {
                var sent = await SendAsync(
                    service.NhiOwnerNotificationEmail!,
                    $"جرد الحسابات غير البشرية — {plan.Digest.Count} تحتاج متابعة / {plan.Digest.Count} account(s) need follow-up",
                    DigestBody(service, plan),
                    "digest");

                if (sent) notified.AddRange(plan.Digest.Select(i => i.Account));
            }
            else if (plan.Unreachable.Count > 0)
            {
                // No digest address and owners that cannot be reached: nobody is being told. Said
                // plainly, because this is the configuration in which the lifecycle runs silently.
                _logger.LogWarning(
                    "NhiNotification: service '{Name}' has no notification address, and {Count} owner(s) could not be reached — " +
                    "these accounts are heading for quarantine with nobody notified: {Owners}",
                    service.Name, plan.Unreachable.Count, string.Join(", ", plan.Unreachable));
            }

            if (plan.Unreachable.Count > 0)
                await _audit.LogAsync("NhiOwnerUnreachable", AuditCategory, AuditSeverity.Warning,
                    details: $"'{service.Name}': no address for {string.Join(", ", plan.Unreachable)}",
                    performedBy: ActorNames.System);

            // Only what actually went out is marked. Marking on the attempt would silence tomorrow's
            // reminder for an account whose notice never arrived.
            foreach (var a in notified.Distinct()) a.LastNotifiedUtc = now;
            await _gov.SaveChangesAsync(ct);

            _logger.LogInformation(
                "NhiNotification '{Name}': {Owners} owner notice(s), {Due} account(s) due, {Unreachable} owner(s) unreachable",
                service.Name, plan.ByOwner.Count, plan.Digest.Count, plan.Unreachable.Count);
        }

        private async Task<bool> SendAsync(string to, string subject, string body, string what)
        {
            var result = await _email.SendAsync(new EmailMessage { To = to, Subject = subject, Body = body, IsHtml = true });

            if (!result.Success)
                _logger.LogError("NhiNotification: the {What} notice failed to send — {Error}", what, result.Error);

            return result.Success;
        }

        // ══════════════════════════════════════
        // BODIES
        // ══════════════════════════════════════

        private static string OwnerBody(string owner, IReadOnlyList<NhiNotificationPlan.Item> items) =>
            Wrap("حسابات أنت المسؤول عنها / Accounts you are answerable for", "#0d6efd",
                $"<p>{Enc(owner)}،</p>" +
                "<p>الحسابات التالية مسجّلة باسمك وتحتاج إجراءً. أكّد أن الحساب ما زال لازماً، أو أعِده إن لم يعد يخصّك.<br>" +
                "<span style='color:#666'>The accounts below are recorded in your name. Confirm each is still needed, or hand it back.</span></p>" +
                Table(items) +
                "<p style='color:#666;font-size:13px'>حسابٌ لا يُقرّ به أحد يُحجر عند انتهاء المهلة. / " +
                "An account nobody confirms is quarantined once its deadline passes.</p>");

        private static string DigestBody(SvcService service, NhiNotificationPlan.Plan plan)
        {
            var unreachable = plan.Unreachable.Count == 0
                ? ""
                : "<h3 style='color:#dc3545'>مالكون لا يمكن الوصول إليهم / Owners with no reachable address</h3>" +
                  "<p style='color:#666'>هؤلاء لن يصلهم تذكير — حساباتهم متجهة إلى الحجر بلا إشعار.<br>" +
                  "<span>These people will not be reminded; their accounts are heading for quarantine unnotified.</span></p>" +
                  "<ul>" + string.Concat(plan.Unreachable.Select(u => $"<li>{Enc(u)}</li>")) + "</ul>";

            return Wrap($"جرد الحسابات غير البشرية — {Enc(service.Name)}", "#6f42c1",
                Table(plan.Digest) + unreachable);
        }

        private static string Table(IReadOnlyList<NhiNotificationPlan.Item> items)
        {
            var rows = string.Concat(items.Take(200).Select(i =>
                $"<tr><td style='padding:8px;border:1px solid #eee'>{Enc(i.Account.Account)}</td>" +
                $"<td style='padding:8px;border:1px solid #eee'>{Enc(NhiNotificationPlan.Label(i.Reason))}</td>" +
                $"<td style='padding:8px;border:1px solid #eee'>{i.Due?.ToString("yyyy-MM-dd") ?? "—"}</td>" +
                $"<td style='padding:8px;border:1px solid #eee'>{Enc(i.Account.OwnerUsername ?? "—")}</td></tr>"));

            var more = items.Count > 200
                ? $"<p style='color:#666'>و{items.Count - 200} حساباً آخر — انظر الشاشة. / and {items.Count - 200} more — see the console.</p>"
                : "";

            return "<table style='border-collapse:collapse;width:100%'>" +
                   "<tr style='background:#f1f1f1'>" +
                   "<th style='padding:8px;border:1px solid #eee;text-align:right'>الحساب / Account</th>" +
                   "<th style='padding:8px;border:1px solid #eee;text-align:right'>السبب / Reason</th>" +
                   "<th style='padding:8px;border:1px solid #eee;text-align:right'>التاريخ / Date</th>" +
                   "<th style='padding:8px;border:1px solid #eee;text-align:right'>المالك / Owner</th></tr>" +
                   rows + "</table>" + more;
        }

        private static string Wrap(string title, string colour, string inner) => $@"
<div dir='rtl' style='font-family: Segoe UI, Tahoma, Arial; padding:20px; background:#f8f9fa; border-radius:8px;'>
    <h2 style='color:{colour};border-bottom:2px solid {colour};padding-bottom:10px;'>{title}</h2>
    {inner}
    <p style='color:#999;font-size:12px;margin-top:24px'>IdentitySyncPro</p>
</div>";

        private static string Enc(string s) => WebUtility.HtmlEncode(s);
    }

    /// <summary>
    /// Looks up an owner's email address in the directory the discovering service is bound to.
    ///
    /// Best-effort by design. An owner is an identifier somebody signed in with, and this system
    /// never required it to be a directory account — so "not found" is an ordinary answer, reported
    /// upwards rather than thrown. What it must never do is fail silently: an unresolvable owner is
    /// carried into the plan and named in the digest.
    /// </summary>
    internal sealed class OwnerAddressBook : IDisposable
    {
        private readonly Dictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly SvcService _service;
        private readonly ILogger _logger;
        private LdapConnection? _ldap;
        private bool _connectionFailed;

        public OwnerAddressBook(SvcService service, ILogger logger)
        {
            _service = service;
            _logger = logger;
        }

        public string? Find(string owner)
        {
            if (string.IsNullOrWhiteSpace(owner)) return null;
            if (_cache.TryGetValue(owner, out var cached)) return cached;

            var found = Lookup(owner);
            _cache[owner] = found;
            return found;
        }

        private string? Lookup(string owner)
        {
            // An owner may already be an address — the institution decides what identifiers look
            // like, and refusing to notice that would be pedantry with a real cost.
            if (owner.Contains('@') && owner.Contains('.')) return owner;

            if (_connectionFailed) return null;

            try
            {
                if (_ldap == null)
                {
                    _ldap = LdapConnectionFactory.Create(_service.ToLdapOptions());
                    _ldap.Bind();
                }

                var name = BindIdentity.Parse(owner)?.Value ?? owner;
                var baseDn = string.IsNullOrWhiteSpace(_service.ADBaseDN) ? _service.OffboardingSearchOU : _service.ADBaseDN;
                if (string.IsNullOrWhiteSpace(baseDn)) return null;

                var response = (SearchResponse)_ldap.SendRequest(new SearchRequest(
                    baseDn,
                    $"(&(objectClass=user)(sAMAccountName={LdapSanitizer.EscapeFilterValue(name)}))",
                    SearchScope.Subtree, "mail"));

                if (response.Entries.Count == 0) return null;

                var entry = response.Entries[0];
                if (!entry.Attributes.Contains("mail")) return null;

                var mail = entry.Attributes["mail"][0]?.ToString();
                return string.IsNullOrWhiteSpace(mail) ? null : mail;
            }
            catch (Exception ex)
            {
                // The whole directory is unreachable, not just this owner. Said once, and every
                // owner then falls through to the digest rather than each retrying and timing out.
                _connectionFailed = true;
                _logger.LogWarning(ex,
                    "NhiNotification: could not reach the directory to resolve owner addresses for '{Service}' — " +
                    "owners will be listed in the digest instead", _service.Name);
                return null;
            }
        }

        public void Dispose() => _ldap?.Dispose();
    }
}
