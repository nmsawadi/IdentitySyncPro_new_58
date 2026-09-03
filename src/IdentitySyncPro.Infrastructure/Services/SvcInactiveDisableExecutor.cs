using IdentitySyncPro.Core.Models.Audit;
using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Text;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Services;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Connectors;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Executor for Inactive-Account Disable services.
    /// Scans an OU in AD and disables (in place — no move) every ENABLED user that has not been
    /// used (logged in) for at least the configured number of months, then:
    ///   • emails the administration a single summary of everything disabled, and
    ///   • optionally SMS-notifies each disabled user (mobile read from a configurable AD attribute).
    /// No source database is involved — AD itself is the source (last-logon + creation date).
    /// </summary>
    public class SvcInactiveDisableExecutor
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ISmsService _smsService;
        private readonly IEmailService _emailService;
        private readonly ISvcProgressNotifier _progressNotifier;
        private readonly ILogger<SvcInactiveDisableExecutor> _logger;
        private readonly AppDbContext _appDb;

        private const int UF_ACCOUNTDISABLE = 0x0002;
        private const int BatchSaveInterval = 50;
        private const int LdapPageSize = 500;

        public SvcInactiveDisableExecutor(
            IServiceScopeFactory scopeFactory,
            ISmsService smsService,
            IEmailService emailService,
            ISvcProgressNotifier progressNotifier,
            ILogger<SvcInactiveDisableExecutor> logger,
            AppDbContext appDb)
        {
            _scopeFactory = scopeFactory;
            _smsService = smsService;
            _emailService = emailService;
            _progressNotifier = progressNotifier;
            _logger = logger;
            _appDb = appDb;
        }

        /// <summary>One disabled account, collected for the admin summary email.</summary>
        private sealed record DisabledAccount(string SamAccount, string DisplayName, string Dn, DateTime? LastActivityUtc, bool NeverUsed);

        public async Task<SvcRunLog> ExecuteAsync(int serviceId, string triggeredBy = ActorNames.System, CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();

            var service = await db.SvcServices.FirstOrDefaultAsync(s => s.Id == serviceId, ct);
            if (service == null)
                throw new InvalidOperationException($"Service with ID {serviceId} not found");
            if (!service.IsEnabled)
                throw new InvalidOperationException($"Service '{service.Name}' is disabled");

            var runLog = new SvcRunLog
            {
                SvcServiceId = serviceId,
                StartTime = DateTime.UtcNow,
                Status = "Running",
                TriggeredBy = triggeredBy
            };
            db.SvcRunLogs.Add(runLog);
            await db.SaveChangesAsync(ct);
            await BroadcastProgressAsync(serviceId, service.Name, 0, 0, runLog, "Running");

            var disabled = new List<DisabledAccount>();
            var skipReasons = new SvcRunSummary.Reasons();
            int processedCount = 0;

            try
            {
                var months = service.InactivityMonths <= 0 ? 6 : service.InactivityMonths;
                var cutoffUtc = DateTime.UtcNow.AddMonths(-months);
                var lastLogonAttr = string.IsNullOrWhiteSpace(service.LastLogonAttribute)
                    ? "lastLogonTimestamp" : service.LastLogonAttribute!.Trim();
                var phoneAttr = string.IsNullOrWhiteSpace(service.PhoneColumn) ? null : service.PhoneColumn!.Trim();

                // Safety: this sweep DISABLES accounts, so it must be scoped to a specific OU —
                // never the whole domain by default.
                var searchBaseDn = service.OffboardingSearchOU;
                if (string.IsNullOrWhiteSpace(searchBaseDn))
                    throw new InvalidOperationException(
                        "Search OU is not configured. An inactive-account sweep must target a specific OU, not the whole domain.");

                _logger.LogInformation(
                    "SvcInactiveDisable: Starting '{Name}' (ID {Id}) by {By} — cutoff {Cutoff:yyyy-MM-dd} ({Months}mo), OU={OU}, lastLogonAttr={Attr}",
                    service.Name, serviceId, triggeredBy, cutoffUtc, months, searchBaseDn, lastLogonAttr);

                using var ldap = LdapConnectionFactory.Create(service.ToLdapOptions());
                ldap.Bind();

                var requested = new List<string>
                {
                    "distinguishedName", "sAMAccountName", "displayName", "userAccountControl",
                    "whenCreated", lastLogonAttr
                };
                if (phoneAttr != null) requested.Add(phoneAttr);

                var searchRequest = new SearchRequest(
                    searchBaseDn,
                    "(&(objectCategory=person)(objectClass=user))",
                    SearchScope.Subtree,
                    requested.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
                var pageControl = new PageResultRequestControl(LdapPageSize);
                searchRequest.Controls.Add(pageControl);

                int unsaved = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var response = (SearchResponse)ldap.SendRequest(searchRequest);

                    foreach (SearchResultEntry entry in response.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        processedCount++;
                        runLog.TotalRecords++;

                        try
                        {
                            var acted = await ProcessUserAsync(ldap, service, entry, lastLogonAttr, phoneAttr,
                                cutoffUtc, months, runLog, db, disabled, skipReasons);
                            if (acted) unsaved++;
                        }
                        catch (Exception ex)
                        {
                            runLog.FailedRecords++;
                            db.SvcAuditEntries.Add(new SvcAuditEntry
                            {
                                SvcRunLogId = runLog.Id,
                                SvcServiceId = serviceId,
                                Timestamp = DateTime.UtcNow,
                                Action = "Error",
                                KeyValue = GetAttr(entry, "sAMAccountName") ?? entry.DistinguishedName,
                                ErrorMessage = ex.Message
                            });
                            unsaved++;
                            _logger.LogError(ex, "SvcInactiveDisable: Error processing {DN}", entry.DistinguishedName);
                        }

                        if (unsaved >= BatchSaveInterval) { await db.SaveChangesAsync(ct); unsaved = 0; }
                        if (processedCount % 25 == 0)
                            await BroadcastProgressAsync(serviceId, service.Name, processedCount, 0, runLog, "Running");
                    }

                    var page = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
                    if (page == null || page.Cookie.Length == 0) break;
                    pageControl.Cookie = page.Cookie;
                }

                await db.SaveChangesAsync(ct);

                // One entry that describes the run itself, written whether or not anything was
                // disabled — so opening a run's details always answers "what did this do?".
                db.SvcAuditEntries.Add(SvcRunSummary.Build(
                    runLog, serviceId, skipReasons,
                    actedOn: disabled.Select(d =>
                        $"• {d.SamAccount}" +
                        (string.IsNullOrWhiteSpace(d.DisplayName) || d.DisplayName == d.SamAccount ? "" : $" ({d.DisplayName})") +
                        " — " +
                        (d.NeverUsed
                            ? $"never used, created {d.LastActivityUtc:yyyy-MM-dd}"
                            : $"last activity {d.LastActivityUtc:yyyy-MM-dd}")),
                    note: $"Disabled accounts unused for {months} month(s) or more, scanned under {searchBaseDn}."));

                await db.SaveChangesAsync(ct);

                // One summary email to the administration listing everything disabled this run.
                if (SvcEmailGate.ShouldSend(service, disabled.Count, _logger, "SvcInactiveDisable"))
                    await SendAdminSummaryEmailAsync(service, disabled, months, runLog, db);

                runLog.Status = runLog.FailedRecords > 0 ? "CompletedWithErrors" : "Completed";
                runLog.EndTime = DateTime.UtcNow;
                service.LastRunAt = runLog.EndTime;
                service.LastRunStatus = runLog.Status;
                service.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);

                await BroadcastProgressAsync(serviceId, service.Name, processedCount, processedCount, runLog, runLog.Status);
                _logger.LogInformation(
                    "SvcInactiveDisable: '{Name}' completed. Scanned={Total}, Disabled={Disabled}, Failed={Failed}, Active/Skipped={Skipped}",
                    service.Name, runLog.TotalRecords, runLog.UpdatedRecords, runLog.FailedRecords, runLog.SkippedRecords);
                return runLog;
            }
            catch (OperationCanceledException)
            {
                runLog.Status = "Cancelled";
                runLog.EndTime = DateTime.UtcNow;
                runLog.ErrorMessage = "تم إلغاء العملية بواسطة المستخدم / Cancelled by user";
                service.LastRunAt = runLog.EndTime; service.LastRunStatus = "Cancelled"; service.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                await BroadcastProgressAsync(serviceId, service.Name, processedCount, 0, runLog, "Cancelled");
                return runLog;
            }
            catch (Exception ex)
            {
                runLog.Status = "Failed";
                runLog.EndTime = DateTime.UtcNow;
                runLog.ErrorMessage = ex.Message;
                service.LastRunAt = runLog.EndTime; service.LastRunStatus = "Failed"; service.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                await BroadcastProgressAsync(serviceId, service.Name, 0, runLog.TotalRecords, runLog, "Failed");
                _logger.LogError(ex, "SvcInactiveDisable: Service '{Name}' failed", service.Name);
                throw;
            }
        }

        /// <summary>Check + (if inactive) disable one AD user. Returns true when an audit entry was added.</summary>
        private async Task<bool> ProcessUserAsync(
            LdapConnection ldap, SvcService service, SearchResultEntry entry,
            string lastLogonAttr, string? phoneAttr, DateTime cutoffUtc, int months,
            SvcRunLog runLog, ServicesDbContext db, List<DisabledAccount> disabled,
            SvcRunSummary.Reasons skipReasons)
        {
            var dn = entry.DistinguishedName;
            var sam = GetAttr(entry, "sAMAccountName") ?? dn;
            var displayName = GetAttr(entry, "displayName") ?? sam;
            var uac = int.TryParse(GetAttr(entry, "userAccountControl") ?? "512", out var u) ? u : 512;

            // Each skip is counted by reason. The counters alone said "4,988 skipped" without
            // saying why, which is the difference between "everything is already handled" and
            // "the last-logon attribute is unreadable on every account".
            if ((uac & UF_ACCOUNTDISABLE) != 0)
            {
                runLog.SkippedRecords++;
                skipReasons.Add("already disabled");
                return false;
            }

            // Effective "last activity": last-logon if present, else account creation date.
            var (lastActivity, neverUsed) = ResolveLastActivity(entry, lastLogonAttr);

            // No reference date at all (no last-logon, unparseable creation) → cannot judge, skip safely.
            if (lastActivity == null)
            {
                runLog.SkippedRecords++;
                skipReasons.Add($"no readable last-activity date ({lastLogonAttr} and whenCreated both unusable)");
                return false;
            }

            // Still within the allowed window → active, skip.
            if (lastActivity.Value >= cutoffUtc)
            {
                runLog.SkippedRecords++;
                skipReasons.Add($"used within the last {months} month(s)");
                return false;
            }

            // === Disable IN PLACE (no OU move) ===
            var newUac = uac | UF_ACCOUNTDISABLE;
            var mod = new DirectoryAttributeModification { Name = "userAccountControl", Operation = DirectoryAttributeOperation.Replace };
            mod.Add(newUac.ToString());
            ldap.SendRequest(new ModifyRequest(dn, mod));

            runLog.UpdatedRecords++;
            disabled.Add(new DisabledAccount(sam, displayName, dn, lastActivity, neverUsed));

            db.SvcAuditEntries.Add(new SvcAuditEntry
            {
                SvcRunLogId = runLog.Id,
                SvcServiceId = service.Id,
                Timestamp = DateTime.UtcNow,
                Action = "InactiveDisabled",
                KeyValue = sam,
                ADIdentity = dn.Length > 500 ? dn[..500] : dn,
                AttributeName = neverUsed ? "never-used" : lastLogonAttr,
                OldValue = uac.ToString(),
                NewValue = lastActivity.Value.ToString("yyyy-MM-dd")
            });
            _logger.LogInformation("SvcInactiveDisable: Disabled {SAM} — last activity {Last:yyyy-MM-dd} (>{Months}mo)", sam, lastActivity, months);

            // === Optional SMS to the user ===
            if (service.EnableSms && !string.IsNullOrWhiteSpace(service.SmsTemplate))
            {
                var phone = phoneAttr != null ? PhoneHelper.NormalizePhone(GetAttr(entry, phoneAttr)) : "";
                await SendUserSmsAsync(service, sam, displayName, phone, months, runLog, db);
            }

            return true;
        }

        /// <summary>last-logon (FILETIME) if present &amp; &gt;0, else whenCreated (generalized time). Null when neither parses.</summary>
        private static (DateTime? LastActivity, bool NeverUsed) ResolveLastActivity(SearchResultEntry entry, string lastLogonAttr)
        {
            var raw = GetAttr(entry, lastLogonAttr);
            if (long.TryParse(raw, out var fileTime) && fileTime > 0)
            {
                try { return (DateTime.FromFileTimeUtc(fileTime), false); }
                catch { /* out-of-range → fall through to creation date */ }
            }

            // Never logged on (or attribute absent) → judge by account age.
            var created = ParseAdGeneralizedTime(GetAttr(entry, "whenCreated"));
            return (created, true);
        }

        /// <summary>Parse AD generalized time like "20230115093000.0Z" to UTC.</summary>
        private static DateTime? ParseAdGeneralizedTime(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var s = value.Split('.')[0]; // drop fractional seconds + trailing Z
            if (DateTime.TryParseExact(s, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                return dt;
            return null;
        }

        private async Task SendUserSmsAsync(
            SvcService service, string sam, string displayName, string phone, int months,
            SvcRunLog runLog, ServicesDbContext db)
        {
            var smsLog = new SmsSendLog
            {
                Source = "InactiveDisable",
                Account = sam,
                DisplayName = displayName,
                PhoneNumber = phone,
                Status = "Skipped",
                CreatedAt = DateTime.UtcNow,
                LastAttemptAt = DateTime.UtcNow
            };
            _appDb.SmsSendLogs.Add(smsLog);

            try
            {
                if (string.IsNullOrWhiteSpace(phone))
                {
                    smsLog.GatewayResponse = "No phone number on the account";
                    db.SvcAuditEntries.Add(NewAudit(runLog, service, "SmsFailed", sam, "No phone number on the account"));
                    return;
                }

                if (!service.SmsProviderId.HasValue)
                {
                    smsLog.GatewayResponse = "No SMS provider selected";
                    db.SvcAuditEntries.Add(NewAudit(runLog, service, "SmsFailed", sam, "No SMS provider selected"));
                    return;
                }

                var provider = await _appDb.SmsProviders.FindAsync(service.SmsProviderId.Value);
                if (provider == null || !provider.IsActive)
                {
                    smsLog.GatewayResponse = "SMS provider not found or inactive";
                    db.SvcAuditEntries.Add(NewAudit(runLog, service, "SmsFailed", sam, "SMS provider not found or inactive"));
                    return;
                }
                smsLog.ProviderName = provider.Name;

                var message = service.SmsTemplate!
                    .Replace("{SAM_ACCOUNT}", sam)
                    .Replace("{DISPLAY_NAME}", displayName)
                    .Replace("{INACTIVE_MONTHS}", months.ToString());
                smsLog.SentMessage = message;

                var result = await _smsService.SendCredentialsAsync(new SmsRequest
                {
                    PhoneNumber = phone,
                    Username = sam,
                    DisplayName = displayName,
                    MessageTemplate = message
                }.WithProvider(provider));

                smsLog.Status = result.Success ? "Success" : "Failed";
                smsLog.GatewayResponse = Truncate(result.Success ? result.Response : result.Error, 2000);
                if (result.Success) smsLog.SentMessage = null; // delivered — drop stored copy

                db.SvcAuditEntries.Add(NewAudit(runLog, service, result.Success ? "SmsSent" : "SmsFailed", sam,
                    result.Success ? null : result.Error));
            }
            catch (Exception ex)
            {
                smsLog.Status = "Failed";
                smsLog.GatewayResponse = Truncate(ex.Message, 2000);
                db.SvcAuditEntries.Add(NewAudit(runLog, service, "SmsFailed", sam, ex.Message));
                _logger.LogError(ex, "SvcInactiveDisable: SMS error for {SAM}", sam);
            }
            finally
            {
                // SmsSendLog lives in AppDbContext (unified SMS Center log) — persist it here.
                try { await _appDb.SaveChangesAsync(); }
                catch (Exception saveEx) { _logger.LogWarning(saveEx, "SvcInactiveDisable: failed to write SMS log for {SAM}", sam); }
            }
        }

        private async Task SendAdminSummaryEmailAsync(
            SvcService service, List<DisabledAccount> disabled, int months, SvcRunLog runLog, ServicesDbContext db)
        {
            try
            {
                var subject = (service.EmailSubject ?? "تعطيل حسابات غير مستخدمة / Inactive accounts disabled: {COUNT}")
                    .Replace("{COUNT}", disabled.Count.ToString())
                    .Replace("{INACTIVE_MONTHS}", months.ToString());

                var rows = new StringBuilder();
                foreach (var d in disabled)
                {
                    var last = d.NeverUsed
                        ? $"لم يُستخدم (أُنشئ {d.LastActivityUtc:yyyy-MM-dd})"
                        : d.LastActivityUtc?.ToString("yyyy-MM-dd") ?? "-";
                    rows.Append($"<tr><td style='padding:6px;border:1px solid #eee'>{d.SamAccount}</td>" +
                                $"<td style='padding:6px;border:1px solid #eee'>{d.DisplayName}</td>" +
                                $"<td style='padding:6px;border:1px solid #eee'>{last}</td>" +
                                $"<td style='padding:6px;border:1px solid #eee;direction:ltr;font-size:12px'>{d.Dn}</td></tr>");
                }

                var body = service.EmailBodyTemplate;
                if (string.IsNullOrWhiteSpace(body))
                {
                    body = $@"
<div dir='rtl' style='font-family: Segoe UI, Tahoma, Arial; padding: 20px; background: #f8f9fa; border-radius: 8px;'>
    <h2 style='color: #dc3545; border-bottom: 2px solid #dc3545; padding-bottom: 10px;'>⛔ تعطيل حسابات غير مستخدمة</h2>
    <p>تم تعطيل <strong>{{COUNT}}</strong> حساب/حسابات لم تُستخدم منذ <strong>{{INACTIVE_MONTHS}}</strong> شهراً أو أكثر (تعطيل في المكان بدون نقل).</p>
    <table style='width:100%; border-collapse: collapse; margin-top: 12px; font-size: 13px;'>
        <thead><tr style='background:#f1f1f1'>
            <th style='padding:6px;border:1px solid #eee'>حساب AD</th>
            <th style='padding:6px;border:1px solid #eee'>الاسم</th>
            <th style='padding:6px;border:1px solid #eee'>آخر استخدام</th>
            <th style='padding:6px;border:1px solid #eee'>الموقع (DN)</th>
        </tr></thead>
        <tbody>{{ROWS}}</tbody>
    </table>
    <p style='margin-top: 15px; color: #6c757d; font-size: 12px;'>
        تم الإرسال تلقائياً بواسطة IdentitySyncPro — خدمة «{service.Name}» — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC
    </p>
</div>";
                }

                body = body
                    .Replace("{COUNT}", disabled.Count.ToString())
                    .Replace("{INACTIVE_MONTHS}", months.ToString())
                    .Replace("{ROWS}", rows.ToString());

                var result = await _emailService.SendAsync(new EmailMessage
                {
                    To = service.NotificationEmail!,
                    Subject = subject,
                    Body = body,
                    IsHtml = true
                });

                db.SvcAuditEntries.Add(NewAudit(runLog, service, result.Success ? "EmailSent" : "EmailFailed",
                    "(summary)", result.Success ? null : result.Error));
                if (result.Success)
                    _logger.LogInformation("SvcInactiveDisable: Summary email ({Count} accounts) sent to {To}", disabled.Count, service.NotificationEmail);
                else
                    _logger.LogWarning("SvcInactiveDisable: Summary email to {To} failed: {Error}", service.NotificationEmail, result.Error);
            }
            catch (Exception ex)
            {
                db.SvcAuditEntries.Add(NewAudit(runLog, service, "EmailFailed", "(summary)", ex.Message));
                _logger.LogError(ex, "SvcInactiveDisable: Summary email error");
            }
        }

        private static SvcAuditEntry NewAudit(SvcRunLog runLog, SvcService service, string action, string key, string? error) => new()
        {
            SvcRunLogId = runLog.Id,
            SvcServiceId = service.Id,
            Timestamp = DateTime.UtcNow,
            Action = action,
            KeyValue = key,
            ADIdentity = key,
            ErrorMessage = error
        };

        private static string? Truncate(string? v, int max) => v == null ? null : v.Length <= max ? v : v[..max];

        private static string? GetAttr(SearchResultEntry entry, string name)
        {
            if (entry.Attributes.Contains(name))
            {
                var a = entry.Attributes[name];
                if (a.Count > 0) return a[0]?.ToString();
            }
            return null;
        }

        private async Task BroadcastProgressAsync(int serviceId, string serviceName, int current, int total, SvcRunLog runLog, string status)
        {
            try
            {
                await _progressNotifier.NotifyProgressAsync(serviceId, new
                {
                    serviceId, serviceName, current, total,
                    percent = total > 0 ? (int)Math.Round((double)current / total * 100) : 0,
                    updated = runLog.UpdatedRecords, failed = runLog.FailedRecords,
                    skipped = runLog.SkippedRecords, notFound = runLog.NotFoundRecords,
                    status, runLogId = runLog.Id, timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex) { _logger.LogDebug(ex, "SvcInactiveDisable: progress broadcast failed"); }
        }
    }
}
