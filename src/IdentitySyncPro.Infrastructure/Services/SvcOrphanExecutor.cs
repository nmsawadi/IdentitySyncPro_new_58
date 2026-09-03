using IdentitySyncPro.Core.Models.Audit;
using System.DirectoryServices.Protocols;
using System.Text;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Services;
using IdentitySyncPro.Infrastructure.Connectors;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Executor for Orphaned-Account services (reconciliation — the reverse of sync).
    /// Reads all identity keys from the source view, scans the target AD OU for MANAGED accounts
    /// (those carrying the sync key attribute), and flags any whose key is no longer in the source
    /// as orphaned. Depending on OrphanAction it reports / disables / disables + moves them.
    /// NEVER deletes (Safe Sync). Guards against mass-disable when the source read fails/returns few.
    /// </summary>
    public class SvcOrphanExecutor
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly SvcDatabaseReader _dbReader;
        private readonly IEmailService _emailService;
        private readonly ISvcProgressNotifier _progressNotifier;
        private readonly ILogger<SvcOrphanExecutor> _logger;

        private const int UF_ACCOUNTDISABLE = 0x0002;
        private const int BatchSaveInterval = 50;
        private const int LdapPageSize = 500;
        private const string ChainRule = "1.2.840.113556.1.4.1941";

        public SvcOrphanExecutor(
            IServiceScopeFactory scopeFactory,
            SvcDatabaseReader dbReader,
            IEmailService emailService,
            ISvcProgressNotifier progressNotifier,
            ILogger<SvcOrphanExecutor> logger)
        {
            _scopeFactory = scopeFactory;
            _dbReader = dbReader;
            _emailService = emailService;
            _progressNotifier = progressNotifier;
            _logger = logger;
        }

        private sealed record Orphan(string Sam, string DisplayName, string Dn, string Key, string Outcome);

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

            var orphans = new List<Orphan>();
            var skipReasons = new SvcRunSummary.Reasons();
            int processed = 0;

            try
            {
                var action = string.IsNullOrWhiteSpace(service.OrphanAction) ? "Report" : service.OrphanAction!.Trim();
                var keyColumn = service.KeySourceColumn;
                var adAttr = string.IsNullOrWhiteSpace(service.ADSearchAttribute) ? "extensionAttribute2" : service.ADSearchAttribute!.Trim();
                var minSource = Math.Max(service.MinSourceRecords, 1);

                if (string.IsNullOrWhiteSpace(keyColumn))
                    throw new InvalidOperationException("Key source column is not configured");
                var searchBase = service.OffboardingSearchOU;
                if (string.IsNullOrWhiteSpace(searchBase))
                    throw new InvalidOperationException("Search OU is not configured. An orphan sweep must target a specific OU.");
                if (action == "DisableAndMove" && string.IsNullOrWhiteSpace(service.TargetOU))
                    throw new InvalidOperationException("Target OU (quarantine) is required for the DisableAndMove action");

                // 1) Read all source keys.
                var connStr = SvcDatabaseReader.BuildConnectionString(
                    service.SourceProvider, service.SourceHost, service.SourcePort,
                    service.SourceDatabase, service.SourceUsername, service.SourcePassword, service.SourceIntegratedSecurity);
                var sourceRows = await _dbReader.ReadAllAsync(service.SourceProvider, connStr, service.SourceTableOrView, ct);

                var sourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in sourceRows)
                    if (row.TryGetValue(keyColumn, out var v) && !string.IsNullOrWhiteSpace(v))
                        sourceKeys.Add(v.Trim());

                // 2) ⛔ SAFETY GUARD — never act on an empty/too-small source (would flag EVERY account).
                if (sourceKeys.Count < minSource)
                {
                    var msg = $"Source returned only {sourceKeys.Count} key(s) (< MinSourceRecords {minSource}). Aborting WITHOUT any AD change to prevent mass-disable.";
                    runLog.Status = "Failed"; runLog.EndTime = DateTime.UtcNow; runLog.ErrorMessage = msg;
                    service.LastRunAt = runLog.EndTime; service.LastRunStatus = "Failed"; service.UpdatedAt = DateTime.UtcNow;
                    db.SvcAuditEntries.Add(NewAudit(runLog, service, "Aborted", "(source-guard)", msg));
                    await db.SaveChangesAsync(ct);
                    await BroadcastAsync(serviceId, service.Name, 0, 0, runLog, "Failed");
                    _logger.LogError("SvcOrphan: {Msg}", msg);
                    return runLog;
                }

                _logger.LogInformation("SvcOrphan: '{Name}' (ID {Id}) action={Action} sourceKeys={Keys} OU={OU} adAttr={Attr}",
                    service.Name, serviceId, action, sourceKeys.Count, searchBase, adAttr);

                using var ldap = LdapConnectionFactory.Create(service.ToLdapOptions());
                ldap.Bind();

                // Exclusion group effective (nested) member DNs.
                var exempt = LoadExclusionDns(ldap, searchBase, service.OffboardingExclusionGroup);

                // 3) Scan the OU — only MANAGED accounts (those that carry the sync key attribute).
                var request = new SearchRequest(searchBase,
                    $"(&(objectCategory=person)(objectClass=user)({adAttr}=*))", SearchScope.Subtree,
                    new[] { "distinguishedName", "sAMAccountName", "displayName", "userAccountControl", adAttr });
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
                            if (await ProcessAsync(ldap, service, entry, adAttr, action, sourceKeys, exempt, runLog, db, orphans, skipReasons))
                                unsaved++;
                        }
                        catch (Exception ex)
                        {
                            runLog.FailedRecords++;
                            db.SvcAuditEntries.Add(NewAudit(runLog, service, "Error", GetAttr(entry, "sAMAccountName") ?? entry.DistinguishedName, ex.Message));
                            unsaved++;
                            _logger.LogError(ex, "SvcOrphan: error processing {DN}", entry.DistinguishedName);
                        }
                        if (unsaved >= BatchSaveInterval) { await db.SaveChangesAsync(ct); unsaved = 0; }
                        if (processed % 25 == 0) await BroadcastAsync(serviceId, service.Name, processed, 0, runLog, "Running");
                    }
                    var pr = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
                    if (pr == null || pr.Cookie.Length == 0) break;
                    page.Cookie = pr.Cookie;
                }

                await db.SaveChangesAsync(ct);

                // One entry describing the run itself, written even when no orphan was found.
                db.SvcAuditEntries.Add(SvcRunSummary.Build(
                    runLog, serviceId, skipReasons,
                    actedOn: orphans.Select(o => $"• {o.Sam} — key '{o.Key}' not in source → {o.Outcome}"),
                    note: $"Reconciled AD against {sourceKeys.Count} source key(s) on '{adAttr}'; action = {action}."));
                await db.SaveChangesAsync(ct);

                if (SvcEmailGate.ShouldSend(service, orphans.Count, _logger, "SvcOrphan"))
                    await SendAdminSummaryAsync(service, action, sourceKeys.Count, orphans, runLog, db);

                runLog.Status = runLog.FailedRecords > 0 ? "CompletedWithErrors" : "Completed";
                runLog.EndTime = DateTime.UtcNow;
                service.LastRunAt = runLog.EndTime; service.LastRunStatus = runLog.Status; service.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                await BroadcastAsync(serviceId, service.Name, processed, processed, runLog, runLog.Status);
                _logger.LogInformation("SvcOrphan: '{Name}' completed. Scanned={Total}, Orphans={O}, Failed={F} (action={Action})",
                    service.Name, runLog.TotalRecords, orphans.Count, runLog.FailedRecords, action);
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
                _logger.LogError(ex, "SvcOrphan: '{Name}' failed", service.Name);
                throw;
            }
        }

        private Task<bool> ProcessAsync(
            LdapConnection ldap, SvcService service, SearchResultEntry entry, string adAttr, string action,
            HashSet<string> sourceKeys, HashSet<string> exempt, SvcRunLog runLog, ServicesDbContext db, List<Orphan> orphans,
            SvcRunSummary.Reasons skipReasons)
        {
            var dn = entry.DistinguishedName;
            var sam = GetAttr(entry, "sAMAccountName") ?? dn;
            var displayName = GetAttr(entry, "displayName") ?? sam;
            var key = GetAttr(entry, adAttr);

            // No key value → not a managed account (shouldn't happen given the filter), skip.
            if (string.IsNullOrWhiteSpace(key)) { runLog.SkippedRecords++; skipReasons.Add("no key value (not a managed account)"); return Task.FromResult(false); }

            // Present in source → legitimate, skip.
            if (sourceKeys.Contains(key.Trim())) { runLog.SkippedRecords++; skipReasons.Add("present in the source (legitimate)"); return Task.FromResult(false); }

            // Excluded (break-glass / service) → skip with a trace.
            if (exempt.Contains(dn))
            {
                db.SvcAuditEntries.Add(NewAudit(runLog, service, "Excluded", sam, null));
                runLog.SkippedRecords++;
                skipReasons.Add("member of the exclusion group");
                return Task.FromResult(false);
            }

            var uac = int.TryParse(GetAttr(entry, "userAccountControl") ?? "512", out var u) ? u : 512;

            // === Orphan ===
            string outcome;
            if (action == "Report")
            {
                outcome = "reported";
            }
            else
            {
                if ((uac & UF_ACCOUNTDISABLE) == 0) // enable→disable in place
                {
                    var newUac = uac | UF_ACCOUNTDISABLE;
                    var mod = new DirectoryAttributeModification { Name = "userAccountControl", Operation = DirectoryAttributeOperation.Replace };
                    mod.Add(newUac.ToString());
                    ldap.SendRequest(new ModifyRequest(dn, mod));
                }
                outcome = "disabled";

                if (action == "DisableAndMove" && !string.IsNullOrWhiteSpace(service.TargetOU))
                {
                    try
                    {
                        var rdn = dn.Split(',')[0];
                        ldap.SendRequest(new ModifyDNRequest(dn, service.TargetOU, rdn));
                        outcome = "disabled+moved";
                    }
                    catch (Exception mvEx)
                    {
                        _logger.LogWarning(mvEx, "SvcOrphan: move failed for {SAM} → {OU}", sam, service.TargetOU);
                        outcome = "disabled (move failed)";
                    }
                }
                runLog.UpdatedRecords++;
            }

            orphans.Add(new Orphan(sam, displayName, dn, key, outcome));
            db.SvcAuditEntries.Add(new SvcAuditEntry
            {
                SvcRunLogId = runLog.Id, SvcServiceId = service.Id, Timestamp = DateTime.UtcNow,
                Action = action == "Report" ? "OrphanFound" : "OrphanDisabled",
                KeyValue = sam, ADIdentity = dn.Length > 500 ? dn[..500] : dn,
                AttributeName = $"{adAttr}={key}", NewValue = outcome
            });
            _logger.LogInformation("SvcOrphan: {SAM} orphaned (key {Key} not in source) → {Outcome}", sam, key, outcome);
            return Task.FromResult(true);
        }

        private async Task SendAdminSummaryAsync(SvcService service, string action, int sourceCount, List<Orphan> orphans, SvcRunLog runLog, ServicesDbContext db)
        {
            try
            {
                var subject = (service.EmailSubject ?? "الحسابات اليتيمة — {COUNT} ({ACTION})")
                    .Replace("{COUNT}", orphans.Count.ToString()).Replace("{ACTION}", action);

                var rows = new StringBuilder();
                foreach (var o in orphans.Take(500))
                    rows.Append($"<tr><td style='padding:6px;border:1px solid #eee'>{o.Sam}</td>" +
                                $"<td style='padding:6px;border:1px solid #eee'>{o.DisplayName}</td>" +
                                $"<td style='padding:6px;border:1px solid #eee'>{o.Key}</td>" +
                                $"<td style='padding:6px;border:1px solid #eee'>{o.Outcome}</td>" +
                                $"<td style='padding:6px;border:1px solid #eee;direction:ltr;font-size:12px'>{o.Dn}</td></tr>");

                var body = service.EmailBodyTemplate;
                if (string.IsNullOrWhiteSpace(body))
                {
                    body = $@"<div dir='rtl' style='font-family: Segoe UI, Tahoma, Arial; padding: 20px; background: #f8f9fa; border-radius: 8px;'>
    <h2 style='color:#dc3545;border-bottom:2px solid #dc3545;padding-bottom:10px;'>👻 حسابات يتيمة (بلا مصدر)</h2>
    <p>تم العثور على <strong>{{COUNT}}</strong> حساب/حسابات موجودة في AD لكن بلا سجلّ في المصدر (الإجراء: <strong>{{ACTION}}</strong>). عدد مفاتيح المصدر المقروءة: {sourceCount}.</p>
    <table style='width:100%;border-collapse:collapse;margin-top:12px;font-size:13px;'>
        <thead><tr style='background:#f1f1f1'>
            <th style='padding:6px;border:1px solid #eee'>حساب AD</th>
            <th style='padding:6px;border:1px solid #eee'>الاسم</th>
            <th style='padding:6px;border:1px solid #eee'>المفتاح</th>
            <th style='padding:6px;border:1px solid #eee'>النتيجة</th>
            <th style='padding:6px;border:1px solid #eee'>الموقع (DN)</th>
        </tr></thead><tbody>{{ROWS}}</tbody>
    </table>
    <p style='margin-top:15px;color:#6c757d;font-size:12px;'>القائمة الكاملة في سجلّ تدقيق الخدمة (تصدير Excel). خدمة «{service.Name}» — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC</p>
</div>";
                }
                body = body.Replace("{COUNT}", orphans.Count.ToString()).Replace("{ACTION}", action).Replace("{ROWS}", rows.ToString());

                var result = await _emailService.SendAsync(new EmailMessage { To = service.NotificationEmail!, Subject = subject, Body = body, IsHtml = true });
                db.SvcAuditEntries.Add(NewAudit(runLog, service, result.Success ? "EmailSent" : "EmailFailed", "(summary)", result.Success ? null : result.Error));
            }
            catch (Exception ex)
            {
                db.SvcAuditEntries.Add(NewAudit(runLog, service, "EmailFailed", "(summary)", ex.Message));
                _logger.LogError(ex, "SvcOrphan: summary email failed");
            }
        }

        private HashSet<string> LoadExclusionDns(LdapConnection ldap, string baseDn, string? groupNameOrDn)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(groupNameOrDn)) return set;
            try
            {
                string? groupDn = groupNameOrDn.Contains('=') ? groupNameOrDn : ResolveGroupDn(ldap, baseDn, groupNameOrDn);
                if (groupDn == null) throw new InvalidOperationException($"Exclusion group '{groupNameOrDn}' not found");

                var req = new SearchRequest(baseDn,
                    $"(&(objectCategory=person)(objectClass=user)(memberOf:{ChainRule}:={LdapSanitizer.EscapeFilterValue(groupDn)}))",
                    SearchScope.Subtree, "distinguishedName");
                var page = new PageResultRequestControl(LdapPageSize);
                req.Controls.Add(page);
                while (true)
                {
                    var resp = (SearchResponse)ldap.SendRequest(req);
                    foreach (SearchResultEntry e in resp.Entries) set.Add(e.DistinguishedName);
                    var pr = resp.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
                    if (pr == null || pr.Cookie.Length == 0) break;
                    page.Cookie = pr.Cookie;
                }
            }
            catch (Exception ex)
            {
                // Fail closed: if the exclusion can't be resolved, abort rather than risk disabling exempt accounts.
                throw new InvalidOperationException($"Could not resolve exclusion group — aborting for safety: {ex.Message}", ex);
            }
            return set;
        }

        private static string? ResolveGroupDn(LdapConnection ldap, string baseDn, string name)
        {
            var req = new SearchRequest(baseDn, $"(&(objectClass=group)(sAMAccountName={LdapSanitizer.EscapeFilterValue(name)}))",
                SearchScope.Subtree, "distinguishedName");
            var resp = (SearchResponse)ldap.SendRequest(req);
            return resp.Entries.Count > 0 ? resp.Entries[0].DistinguishedName : null;
        }

        private static SvcAuditEntry NewAudit(SvcRunLog runLog, SvcService service, string action, string key, string? error) => new()
        {
            SvcRunLogId = runLog.Id, SvcServiceId = service.Id, Timestamp = DateTime.UtcNow,
            Action = action, KeyValue = key, ADIdentity = key, ErrorMessage = error
        };

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
            catch (Exception ex) { _logger.LogDebug(ex, "SvcOrphan: progress broadcast failed"); }
        }
    }
}
