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
    /// Executor for Expiry-Warning / Disable services.
    /// Scans an OU in AD, reads an expiry date attribute (accountExpires by default), and:
    ///   • sends a WARNING (SMS to the user + admin summary) when the remaining days match one of
    ///     the configured milestones (e.g. 30 / 7 / 1), and
    ///   • DISABLES the account in place once the expiry date has passed.
    /// AD is the source (expiry attribute); no source database involved.
    /// </summary>
    public class SvcExpiryExecutor
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ISmsService _smsService;
        private readonly IEmailService _emailService;
        private readonly ISvcProgressNotifier _progressNotifier;
        private readonly ILogger<SvcExpiryExecutor> _logger;
        private readonly AppDbContext _appDb;

        private const int UF_ACCOUNTDISABLE = 0x0002;
        private const int BatchSaveInterval = 50;
        private const int LdapPageSize = 500;
        // accountExpires "never" sentinels.
        private const long NeverExpires = 9223372036854775807L;

        public SvcExpiryExecutor(
            IServiceScopeFactory scopeFactory,
            ISmsService smsService,
            IEmailService emailService,
            ISvcProgressNotifier progressNotifier,
            ILogger<SvcExpiryExecutor> logger,
            AppDbContext appDb)
        {
            _scopeFactory = scopeFactory;
            _smsService = smsService;
            _emailService = emailService;
            _progressNotifier = progressNotifier;
            _logger = logger;
            _appDb = appDb;
        }

        private sealed record Warned(string Sam, string DisplayName, string Dn, DateTime ExpiryUtc, int DaysLeft);
        private sealed record Disabled(string Sam, string DisplayName, string Dn, DateTime ExpiryUtc);

        public async Task<SvcRunLog> ExecuteAsync(int serviceId, string triggeredBy = ActorNames.System, CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();

            var service = await db.SvcServices.FirstOrDefaultAsync(s => s.Id == serviceId, ct);
            if (service == null) throw new InvalidOperationException($"Service with ID {serviceId} not found");
            if (!service.IsEnabled) throw new InvalidOperationException($"Service '{service.Name}' is disabled");

            var runLog = new SvcRunLog { SvcServiceId = serviceId, StartTime = DateTime.UtcNow, Status = "Running", TriggeredBy = triggeredBy };
            db.SvcRunLogs.Add(runLog);
            await db.SaveChangesAsync(ct);
            await BroadcastAsync(serviceId, service.Name, 0, 0, runLog, "Running");

            var warned = new List<Warned>();
            var disabled = new List<Disabled>();
            var skipReasons = new SvcRunSummary.Reasons();
            int processed = 0;

            try
            {
                var expiryAttr = string.IsNullOrWhiteSpace(service.ExpiryAttribute) ? "accountExpires" : service.ExpiryAttribute!.Trim();
                var milestones = ParseMilestones(service.ExpiryWarnDays);
                var phoneAttr = string.IsNullOrWhiteSpace(service.PhoneColumn) ? null : service.PhoneColumn!.Trim();

                // Safety: disabling accounts → must target a specific OU.
                var searchBase = service.OffboardingSearchOU;
                if (string.IsNullOrWhiteSpace(searchBase))
                    throw new InvalidOperationException("Search OU is not configured. An expiry sweep must target a specific OU.");

                var nowUtc = DateTime.UtcNow;
                _logger.LogInformation("SvcExpiry: '{Name}' (ID {Id}) attr={Attr} milestones=[{M}] OU={OU}",
                    service.Name, serviceId, expiryAttr, string.Join(",", milestones), searchBase);

                using var ldap = LdapConnectionFactory.Create(service.ToLdapOptions());
                ldap.Bind();

                var requested = new List<string> { "distinguishedName", "sAMAccountName", "displayName", "userAccountControl", expiryAttr };
                if (phoneAttr != null) requested.Add(phoneAttr);

                var request = new SearchRequest(searchBase,
                    "(&(objectCategory=person)(objectClass=user))", SearchScope.Subtree,
                    requested.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
                var page = new PageResultRequestControl(LdapPageSize);
                request.Controls.Add(page);

                int unsaved = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var response = (SearchResponse)ldap.SendRequest(request);
                    foreach (SearchResultEntry entry in response.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        processed++; runLog.TotalRecords++;
                        try
                        {
                            var acted = await ProcessAsync(ldap, service, entry, expiryAttr, phoneAttr, milestones, nowUtc, runLog, db, warned, disabled, skipReasons);
                            if (acted) unsaved++;
                        }
                        catch (Exception ex)
                        {
                            runLog.FailedRecords++;
                            db.SvcAuditEntries.Add(NewAudit(runLog, service, "Error", GetAttr(entry, "sAMAccountName") ?? entry.DistinguishedName, ex.Message));
                            unsaved++;
                            _logger.LogError(ex, "SvcExpiry: error processing {DN}", entry.DistinguishedName);
                        }
                        if (unsaved >= BatchSaveInterval) { await db.SaveChangesAsync(ct); unsaved = 0; }
                        if (processed % 25 == 0) await BroadcastAsync(serviceId, service.Name, processed, 0, runLog, "Running");
                    }
                    var pr = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
                    if (pr == null || pr.Cookie.Length == 0) break;
                    page.Cookie = pr.Cookie;
                }

                await db.SaveChangesAsync(ct);

                // One entry describing the run itself, written even when nothing expired.
                db.SvcAuditEntries.Add(SvcRunSummary.Build(
                    runLog, serviceId, skipReasons,
                    actedOn: disabled.Select(d => $"• {d.Sam} — disabled, expired {d.ExpiryUtc:yyyy-MM-dd}")
                        .Concat(warned.Select(w => $"• {w.Sam} — warned, expires {w.ExpiryUtc:yyyy-MM-dd} ({w.DaysLeft}d left)")),
                    note: $"Warned at {string.Join(",", milestones.OrderByDescending(m => m))} day(s); disabled accounts already past expiry ({expiryAttr})."));
                await db.SaveChangesAsync(ct);

                if (SvcEmailGate.ShouldSend(service, warned.Count + disabled.Count, _logger, "SvcExpiry"))
                    await SendAdminSummaryAsync(service, warned, disabled, runLog, db);

                runLog.Status = runLog.FailedRecords > 0 ? "CompletedWithErrors" : "Completed";
                runLog.EndTime = DateTime.UtcNow;
                service.LastRunAt = runLog.EndTime; service.LastRunStatus = runLog.Status; service.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                await BroadcastAsync(serviceId, service.Name, processed, processed, runLog, runLog.Status);
                _logger.LogInformation("SvcExpiry: '{Name}' completed. Scanned={Total}, Warned={W}, Disabled={D}, Failed={F}",
                    service.Name, runLog.TotalRecords, warned.Count, disabled.Count, runLog.FailedRecords);
                return runLog;
            }
            catch (OperationCanceledException)
            {
                runLog.Status = "Cancelled"; runLog.EndTime = DateTime.UtcNow;
                service.LastRunAt = runLog.EndTime; service.LastRunStatus = "Cancelled"; service.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                await BroadcastAsync(serviceId, service.Name, processed, 0, runLog, "Cancelled");
                return runLog;
            }
            catch (Exception ex)
            {
                runLog.Status = "Failed"; runLog.EndTime = DateTime.UtcNow; runLog.ErrorMessage = ex.Message;
                service.LastRunAt = runLog.EndTime; service.LastRunStatus = "Failed"; service.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                await BroadcastAsync(serviceId, service.Name, 0, 0, runLog, "Failed");
                _logger.LogError(ex, "SvcExpiry: '{Name}' failed", service.Name);
                throw;
            }
        }

        private async Task<bool> ProcessAsync(
            LdapConnection ldap, SvcService service, SearchResultEntry entry, string expiryAttr, string? phoneAttr,
            HashSet<int> milestones, DateTime nowUtc, SvcRunLog runLog, ServicesDbContext db,
            List<Warned> warned, List<Disabled> disabled, SvcRunSummary.Reasons skipReasons)
        {
            var dn = entry.DistinguishedName;
            var sam = GetAttr(entry, "sAMAccountName") ?? dn;
            var displayName = GetAttr(entry, "displayName") ?? sam;
            var uac = int.TryParse(GetAttr(entry, "userAccountControl") ?? "512", out var u) ? u : 512;

            // Already disabled → nothing to do.
            if ((uac & UF_ACCOUNTDISABLE) != 0) { runLog.SkippedRecords++; skipReasons.Add("already disabled"); return false; }

            var expiry = ParseExpiry(GetAttr(entry, expiryAttr));
            if (expiry == null) { runLog.SkippedRecords++; skipReasons.Add("no expiry date set (never expires)"); return false; }

            var daysLeft = (int)Math.Floor((expiry.Value - nowUtc).TotalDays);

            // Past expiry → disable in place.
            if (expiry.Value <= nowUtc)
            {
                var newUac = uac | UF_ACCOUNTDISABLE;
                var mod = new DirectoryAttributeModification { Name = "userAccountControl", Operation = DirectoryAttributeOperation.Replace };
                mod.Add(newUac.ToString());
                ldap.SendRequest(new ModifyRequest(dn, mod));

                runLog.UpdatedRecords++;
                disabled.Add(new Disabled(sam, displayName, dn, expiry.Value));
                db.SvcAuditEntries.Add(new SvcAuditEntry
                {
                    SvcRunLogId = runLog.Id, SvcServiceId = service.Id, Timestamp = DateTime.UtcNow,
                    Action = "ExpiryDisabled", KeyValue = sam, ADIdentity = dn.Length > 500 ? dn[..500] : dn,
                    AttributeName = expiryAttr, NewValue = expiry.Value.ToString("yyyy-MM-dd")
                });
                _logger.LogInformation("SvcExpiry: Disabled {SAM} — expired {Exp:yyyy-MM-dd}", sam, expiry);

                if (service.EnableSms && !string.IsNullOrWhiteSpace(service.SmsTemplate))
                {
                    var phone = phoneAttr != null ? PhoneHelper.NormalizePhone(GetAttr(entry, phoneAttr)) : "";
                    await SendUserSmsAsync(service, sam, displayName, phone, 0, expiry.Value, runLog, db);
                }
                return true;
            }

            // Approaching expiry → warn only on a configured milestone day.
            if (milestones.Contains(daysLeft))
            {
                warned.Add(new Warned(sam, displayName, dn, expiry.Value, daysLeft));
                db.SvcAuditEntries.Add(new SvcAuditEntry
                {
                    SvcRunLogId = runLog.Id, SvcServiceId = service.Id, Timestamp = DateTime.UtcNow,
                    Action = "ExpiryWarned", KeyValue = sam, ADIdentity = dn.Length > 500 ? dn[..500] : dn,
                    AttributeName = $"{daysLeft}d", NewValue = expiry.Value.ToString("yyyy-MM-dd")
                });
                _logger.LogInformation("SvcExpiry: Warned {SAM} — {Days}d before expiry {Exp:yyyy-MM-dd}", sam, daysLeft, expiry);

                if (service.EnableSms && !string.IsNullOrWhiteSpace(service.SmsTemplate))
                {
                    var phone = phoneAttr != null ? PhoneHelper.NormalizePhone(GetAttr(entry, phoneAttr)) : "";
                    await SendUserSmsAsync(service, sam, displayName, phone, daysLeft, expiry.Value, runLog, db);
                }
                return true;
            }

            runLog.SkippedRecords++;
            skipReasons.Add("not expired, and not at a warning milestone");
            return false;
        }

        private async Task SendUserSmsAsync(
            SvcService service, string sam, string displayName, string phone, int daysLeft, DateTime expiry,
            SvcRunLog runLog, ServicesDbContext db)
        {
            var smsLog = new SmsSendLog
            {
                Source = "ExpiryWarn",
                Account = sam, DisplayName = displayName, PhoneNumber = phone,
                Status = "Skipped", CreatedAt = DateTime.UtcNow, LastAttemptAt = DateTime.UtcNow
            };
            _appDb.SmsSendLogs.Add(smsLog);
            try
            {
                if (string.IsNullOrWhiteSpace(phone)) { smsLog.GatewayResponse = "No phone number on the account"; db.SvcAuditEntries.Add(NewAudit(runLog, service, "SmsFailed", sam, "No phone number on the account")); return; }
                if (!service.SmsProviderId.HasValue) { smsLog.GatewayResponse = "No SMS provider selected"; db.SvcAuditEntries.Add(NewAudit(runLog, service, "SmsFailed", sam, "No SMS provider selected")); return; }

                var provider = await _appDb.SmsProviders.FindAsync(service.SmsProviderId.Value);
                if (provider == null || !provider.IsActive) { smsLog.GatewayResponse = "SMS provider not found or inactive"; db.SvcAuditEntries.Add(NewAudit(runLog, service, "SmsFailed", sam, "SMS provider not found or inactive")); return; }
                smsLog.ProviderName = provider.Name;

                var message = service.SmsTemplate!
                    .Replace("{SAM_ACCOUNT}", sam)
                    .Replace("{DISPLAY_NAME}", displayName)
                    .Replace("{DAYS}", daysLeft.ToString())
                    .Replace("{EXPIRY_DATE}", expiry.ToString("yyyy-MM-dd"));
                smsLog.SentMessage = message;

                var result = await _smsService.SendCredentialsAsync(new SmsRequest
                {
                    PhoneNumber = phone, Username = sam, DisplayName = displayName, MessageTemplate = message
                }.WithProvider(provider));

                smsLog.Status = result.Success ? "Success" : "Failed";
                smsLog.GatewayResponse = Truncate(result.Success ? result.Response : result.Error, 2000);
                if (result.Success) smsLog.SentMessage = null;
                db.SvcAuditEntries.Add(NewAudit(runLog, service, result.Success ? "SmsSent" : "SmsFailed", sam, result.Success ? null : result.Error));
            }
            catch (Exception ex)
            {
                smsLog.Status = "Failed"; smsLog.GatewayResponse = Truncate(ex.Message, 2000);
                db.SvcAuditEntries.Add(NewAudit(runLog, service, "SmsFailed", sam, ex.Message));
                _logger.LogError(ex, "SvcExpiry: SMS error for {SAM}", sam);
            }
            finally
            {
                try { await _appDb.SaveChangesAsync(); }
                catch (Exception saveEx) { _logger.LogWarning(saveEx, "SvcExpiry: failed to write SMS log for {SAM}", sam); }
            }
        }

        private async Task SendAdminSummaryAsync(SvcService service, List<Warned> warned, List<Disabled> disabled, SvcRunLog runLog, ServicesDbContext db)
        {
            try
            {
                var subject = (service.EmailSubject ?? "تنبيه انتهاء الحسابات — تحذير: {WARNED} / تعطيل: {DISABLED}")
                    .Replace("{WARNED}", warned.Count.ToString())
                    .Replace("{DISABLED}", disabled.Count.ToString());

                string Table(string headDays, IEnumerable<(string Sam, string Name, string Dn, string Col3)> rows)
                {
                    var sb = new StringBuilder();
                    sb.Append($"<table style='width:100%;border-collapse:collapse;margin-top:8px;font-size:13px;'><thead><tr style='background:#f1f1f1'>" +
                              $"<th style='padding:6px;border:1px solid #eee'>حساب AD</th><th style='padding:6px;border:1px solid #eee'>الاسم</th>" +
                              $"<th style='padding:6px;border:1px solid #eee'>{headDays}</th><th style='padding:6px;border:1px solid #eee'>الموقع (DN)</th></tr></thead><tbody>");
                    foreach (var r in rows)
                        sb.Append($"<tr><td style='padding:6px;border:1px solid #eee'>{r.Sam}</td><td style='padding:6px;border:1px solid #eee'>{r.Name}</td>" +
                                  $"<td style='padding:6px;border:1px solid #eee'>{r.Col3}</td><td style='padding:6px;border:1px solid #eee;direction:ltr;font-size:12px'>{r.Dn}</td></tr>");
                    sb.Append("</tbody></table>");
                    return sb.ToString();
                }

                var warnTable = warned.Count == 0 ? "" :
                    $"<h3 style='color:#fd7e14'>⏳ تنبيهات قرب الانتهاء ({warned.Count})</h3>" +
                    Table("الأيام المتبقية / تاريخ الانتهاء", warned.Select(w => (w.Sam, w.DisplayName, w.Dn, $"{w.DaysLeft} يوم — {w.ExpiryUtc:yyyy-MM-dd}")));
                var disTable = disabled.Count == 0 ? "" :
                    $"<h3 style='color:#dc3545'>⛔ تعطيل بعد الانتهاء ({disabled.Count})</h3>" +
                    Table("تاريخ الانتهاء", disabled.Select(d => (d.Sam, d.DisplayName, d.Dn, d.ExpiryUtc.ToString("yyyy-MM-dd"))));

                var body = service.EmailBodyTemplate;
                if (string.IsNullOrWhiteSpace(body))
                {
                    body = $@"<div dir='rtl' style='font-family: Segoe UI, Tahoma, Arial; padding: 20px; background: #f8f9fa; border-radius: 8px;'>
    <h2 style='color:#0d6efd;border-bottom:2px solid #0d6efd;padding-bottom:10px;'>📅 تنبيه انتهاء الحسابات</h2>
    {{WARN_TABLE}}
    {{DISABLE_TABLE}}
    <p style='margin-top:15px;color:#6c757d;font-size:12px;'>تم الإرسال تلقائياً بواسطة IdentitySyncPro — خدمة «{service.Name}» — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC</p>
</div>";
                }
                body = body
                    .Replace("{WARNED}", warned.Count.ToString()).Replace("{DISABLED}", disabled.Count.ToString())
                    .Replace("{WARN_TABLE}", warnTable).Replace("{DISABLE_TABLE}", disTable);

                var result = await _emailService.SendAsync(new EmailMessage { To = service.NotificationEmail!, Subject = subject, Body = body, IsHtml = true });
                db.SvcAuditEntries.Add(NewAudit(runLog, service, result.Success ? "EmailSent" : "EmailFailed", "(summary)", result.Success ? null : result.Error));
            }
            catch (Exception ex)
            {
                db.SvcAuditEntries.Add(NewAudit(runLog, service, "EmailFailed", "(summary)", ex.Message));
                _logger.LogError(ex, "SvcExpiry: summary email failed");
            }
        }

        // Parse an expiry value: accountExpires FILETIME (0/max = never), or a custom attribute holding
        // FILETIME / generalized time / plain date. Returns null for "never" or unparseable.
        private static DateTime? ParseExpiry(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (long.TryParse(raw, out var ft))
            {
                if (ft == 0 || ft == NeverExpires || ft < 0) return null; // never
                try { return DateTime.FromFileTimeUtc(ft); } catch { return null; }
            }
            // generalized time e.g. 20260115093000.0Z
            var g = raw.Split('.')[0].TrimEnd('Z');
            if (DateTime.TryParseExact(g, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var gd)) return gd;
            // plain date/datetime
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var pd)) return pd;
            return null;
        }

        private static HashSet<int> ParseMilestones(string? csv)
        {
            var set = new HashSet<int>();
            foreach (var p in (csv ?? "30,7,1").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (int.TryParse(p, out var d) && d >= 0) set.Add(d);
            if (set.Count == 0) { set.Add(30); set.Add(7); set.Add(1); }
            return set;
        }

        private static SvcAuditEntry NewAudit(SvcRunLog runLog, SvcService service, string action, string key, string? error) => new()
        {
            SvcRunLogId = runLog.Id, SvcServiceId = service.Id, Timestamp = DateTime.UtcNow,
            Action = action, KeyValue = key, ADIdentity = key, ErrorMessage = error
        };

        private static string? Truncate(string? v, int max) => v == null ? null : v.Length <= max ? v : v[..max];

        private static string? GetAttr(SearchResultEntry e, string name)
        {
            if (e.Attributes.Contains(name)) { var a = e.Attributes[name]; if (a.Count > 0) return a[0]?.ToString(); }
            return null;
        }

        private async Task BroadcastAsync(int serviceId, string serviceName, int current, int total, SvcRunLog runLog, string status)
        {
            try
            {
                await _progressNotifier.NotifyProgressAsync(serviceId, new
                {
                    serviceId, serviceName, current, total,
                    percent = total > 0 ? (int)Math.Round((double)current / total * 100) : 0,
                    updated = runLog.UpdatedRecords, failed = runLog.FailedRecords, skipped = runLog.SkippedRecords,
                    notFound = runLog.NotFoundRecords, status, runLogId = runLog.Id, timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex) { _logger.LogDebug(ex, "SvcExpiry: progress broadcast failed"); }
        }
    }
}
