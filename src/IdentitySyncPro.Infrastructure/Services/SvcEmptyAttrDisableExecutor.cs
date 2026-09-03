using IdentitySyncPro.Core.Models.Audit;
using IdentitySyncPro.Core.Models.Connectors;
using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Infrastructure.Connectors;
using System.DirectoryServices.Protocols;
using System.Net;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Services;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Executor for Empty-Attribute Disable services.
    /// Scans an OU in AD and disables (in place — no move) every ENABLED user
    /// that has any of the configured attributes empty, then emails the admin.
    /// No source database involved — AD itself is the source.
    /// </summary>
    public class SvcEmptyAttrDisableExecutor
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IEmailService _emailService;
        private readonly ISvcProgressNotifier _progressNotifier;
        private readonly ILogger<SvcEmptyAttrDisableExecutor> _logger;

        // userAccountControl flag for ACCOUNTDISABLE
        private const int UF_ACCOUNTDISABLE = 0x0002;

        // Save audit entries to DB every N records to prevent data loss
        private const int BatchSaveInterval = 50;

        // LDAP paged-search page size (server limit is usually 1000)
        private const int LdapPageSize = 500;

        public SvcEmptyAttrDisableExecutor(
            IServiceScopeFactory scopeFactory,
            IEmailService emailService,
            ISvcProgressNotifier progressNotifier,
            ILogger<SvcEmptyAttrDisableExecutor> logger)
        {
            _scopeFactory = scopeFactory;
            _emailService = emailService;
            _progressNotifier = progressNotifier;
            _logger = logger;
        }

        /// <summary>
        /// Execute an empty-attribute disable service by ID.
        /// </summary>
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

            int processedCount = 0;
            var skipReasons = new SvcRunSummary.Reasons();
            var actedOn = new List<string>();

            try
            {
                _logger.LogInformation("SvcEmptyAttrDisable: Starting '{Name}' (ID: {Id}), triggered by {TriggeredBy}",
                    service.Name, serviceId, triggeredBy);

                // Validate configuration
                var checkAttributes = (service.EmptyCheckAttributes ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (checkAttributes.Count == 0)
                    throw new InvalidOperationException("No attributes configured to check (EmptyCheckAttributes)");

                var searchBaseDn = !string.IsNullOrWhiteSpace(service.OffboardingSearchOU)
                    ? service.OffboardingSearchOU
                    : service.ADBaseDN;

                if (string.IsNullOrWhiteSpace(searchBaseDn))
                    throw new InvalidOperationException("Search OU / Base DN is not configured");

                using var ldapConnection = CreateLdapConnection(service);
                ldapConnection.Bind();
                _logger.LogInformation("SvcEmptyAttrDisable: Connected to AD server {Server}, scanning {OU}",
                    service.ADServer, searchBaseDn);

                var requestedAttributes = new List<string>(checkAttributes)
                {
                    "distinguishedName", "sAMAccountName", "displayName", "userAccountControl"
                };

                // Paged search over all user accounts in the OU
                var searchRequest = new SearchRequest(
                    searchBaseDn,
                    "(&(objectCategory=person)(objectClass=user))",
                    SearchScope.Subtree,
                    requestedAttributes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

                var pageControl = new PageResultRequestControl(LdapPageSize);
                searchRequest.Controls.Add(pageControl);

                int unsavedCount = 0;

                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    var searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);

                    foreach (SearchResultEntry adEntry in searchResponse.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        processedCount++;
                        runLog.TotalRecords++;

                        try
                        {
                            var acted = await ProcessUserAsync(ldapConnection, service, checkAttributes, adEntry, runLog, db, skipReasons, actedOn);
                            if (acted) unsavedCount++;
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
                                KeyValue = GetAttributeValue(adEntry, "sAMAccountName") ?? adEntry.DistinguishedName,
                                ErrorMessage = ex.Message
                            });
                            unsavedCount++;
                            _logger.LogError(ex, "SvcEmptyAttrDisable: Error processing {DN}", adEntry.DistinguishedName);
                        }

                        if (unsavedCount >= BatchSaveInterval)
                        {
                            await db.SaveChangesAsync(ct);
                            unsavedCount = 0;
                        }

                        if (processedCount % 25 == 0)
                            await BroadcastProgressAsync(serviceId, service.Name, processedCount, 0, runLog, "Running");
                    }

                    var pageResponse = searchResponse.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
                    if (pageResponse == null || pageResponse.Cookie.Length == 0)
                        break;
                    pageControl.Cookie = pageResponse.Cookie;
                }

                // One entry describing the run itself, written even when nothing was disabled.
                db.SvcAuditEntries.Add(SvcRunSummary.Build(
                    runLog, serviceId, skipReasons, actedOn,
                    note: $"Disabled accounts missing any of: {string.Join(", ", checkAttributes)}."));

                // Finalize
                runLog.Status = runLog.FailedRecords > 0 ? "CompletedWithErrors" : "Completed";
                runLog.EndTime = DateTime.UtcNow;

                service.LastRunAt = runLog.EndTime;
                service.LastRunStatus = runLog.Status;
                service.UpdatedAt = DateTime.UtcNow;

                await db.SaveChangesAsync(ct);

                await BroadcastProgressAsync(serviceId, service.Name, processedCount, processedCount, runLog, runLog.Status);

                _logger.LogInformation(
                    "SvcEmptyAttrDisable: '{Name}' completed. Scanned={Total}, Disabled={Updated}, Failed={Failed}, OK/Skipped={Skipped}",
                    service.Name, runLog.TotalRecords, runLog.UpdatedRecords, runLog.FailedRecords, runLog.SkippedRecords);

                return runLog;
            }
            catch (OperationCanceledException)
            {
                runLog.Status = "Cancelled";
                runLog.EndTime = DateTime.UtcNow;
                runLog.ErrorMessage = "تم إلغاء العملية بواسطة المستخدم / Cancelled by user";

                service.LastRunAt = runLog.EndTime;
                service.LastRunStatus = "Cancelled";
                service.UpdatedAt = DateTime.UtcNow;

                await db.SaveChangesAsync(CancellationToken.None);
                await BroadcastProgressAsync(serviceId, service.Name, processedCount, 0, runLog, "Cancelled");

                _logger.LogWarning("SvcEmptyAttrDisable: '{Name}' cancelled by user after {Count} accounts",
                    service.Name, processedCount);

                return runLog;
            }
            catch (Exception ex)
            {
                runLog.Status = "Failed";
                runLog.EndTime = DateTime.UtcNow;
                runLog.ErrorMessage = ex.Message;

                service.LastRunAt = runLog.EndTime;
                service.LastRunStatus = "Failed";
                service.UpdatedAt = DateTime.UtcNow;

                await db.SaveChangesAsync(CancellationToken.None);
                await BroadcastProgressAsync(serviceId, service.Name, 0, runLog.TotalRecords, runLog, "Failed");

                _logger.LogError(ex, "SvcEmptyAttrDisable: Service '{Name}' failed", service.Name);
                throw;
            }
        }

        /// <summary>
        /// Check a single AD user. Returns true if an audit entry was added (needs saving).
        /// </summary>
        private async Task<bool> ProcessUserAsync(
            LdapConnection ldapConnection,
            SvcService service,
            List<string> checkAttributes,
            SearchResultEntry adEntry,
            SvcRunLog runLog,
            ServicesDbContext db,
            SvcRunSummary.Reasons skipReasons,
            List<string> actedOn)
        {
            var userDn = adEntry.DistinguishedName;
            var samAccount = GetAttributeValue(adEntry, "sAMAccountName") ?? userDn;
            var displayName = GetAttributeValue(adEntry, "displayName") ?? samAccount;
            var uacStr = GetAttributeValue(adEntry, "userAccountControl") ?? "512";
            var uac = int.TryParse(uacStr, out var uacVal) ? uacVal : 512;

            // Already-disabled accounts are out of scope. Counted by reason rather than silently,
            // so a run that changed nothing can still say why.
            if ((uac & UF_ACCOUNTDISABLE) != 0)
            {
                runLog.SkippedRecords++;
                skipReasons.Add("already disabled");
                return false;
            }

            // Which of the configured attributes are empty on this account?
            var emptyAttributes = checkAttributes
                .Where(attr => string.IsNullOrWhiteSpace(GetAttributeValue(adEntry, attr)))
                .ToList();

            if (emptyAttributes.Count == 0)
            {
                // All required attributes present — nothing to do.
                runLog.SkippedRecords++;
                skipReasons.Add("all checked attributes are populated");
                return false;
            }

            // === Disable the account IN PLACE (no OU move) ===
            var newUac = uac | UF_ACCOUNTDISABLE;
            var disableMod = new DirectoryAttributeModification
            {
                Name = "userAccountControl",
                Operation = DirectoryAttributeOperation.Replace
            };
            disableMod.Add(newUac.ToString());
            ldapConnection.SendRequest(new ModifyRequest(userDn, disableMod));

            var emptyList = string.Join(", ", emptyAttributes);

            db.SvcAuditEntries.Add(new SvcAuditEntry
            {
                SvcRunLogId = runLog.Id,
                SvcServiceId = service.Id,
                Timestamp = DateTime.UtcNow,
                Action = "EmptyAttrDisabled",
                KeyValue = samAccount,
                ADIdentity = userDn.Length > 500 ? userDn[..500] : userDn,
                AttributeName = emptyList.Length > 200 ? emptyList[..200] : emptyList,
                OldValue = uac.ToString(),
                NewValue = newUac.ToString()
            });

            runLog.UpdatedRecords++;
            actedOn.Add($"• {samAccount}" +
                        (string.IsNullOrWhiteSpace(displayName) || displayName == samAccount ? "" : $" ({displayName})") +
                        $" — empty: {emptyList}");
            _logger.LogInformation("SvcEmptyAttrDisable: Disabled {SAM} — empty attributes: {Attrs}", samAccount, emptyList);

            // === Email notification to admin ===
            // One email per account this run actually disabled — reached only after the disable
            // above succeeded, so there is exactly one thing to report.
            if (SvcEmailGate.ShouldSend(service, 1, _logger, "SvcEmptyAttrDisable"))
            {
                await SendDisableEmailAsync(service, samAccount, displayName, userDn, emptyList, runLog, db);
            }

            return true;
        }

        private async Task SendDisableEmailAsync(
            SvcService service,
            string samAccount,
            string displayName,
            string userDn,
            string emptyAttributes,
            SvcRunLog runLog,
            ServicesDbContext db)
        {
            try
            {
                var subject = service.EmailSubject ?? "تعطيل حساب لنقص بيانات / Account Disabled (missing data): {SAM_ACCOUNT}";
                subject = subject
                    .Replace("{EMPLOYEE_NAME}", displayName)
                    .Replace("{SAM_ACCOUNT}", samAccount)
                    .Replace("{EMPTY_ATTRIBUTES}", emptyAttributes);

                var body = service.EmailBodyTemplate;
                if (string.IsNullOrWhiteSpace(body))
                {
                    // Default HTML template
                    body = $@"
<div dir='rtl' style='font-family: Segoe UI, Tahoma, Arial; padding: 20px; background: #f8f9fa; border-radius: 8px;'>
    <h2 style='color: #fd7e14; border-bottom: 2px solid #fd7e14; padding-bottom: 10px;'>
        ⚠️ تعطيل حساب — بيانات ناقصة
    </h2>
    <table style='width: 100%; border-collapse: collapse; margin-top: 15px;'>
        <tr><td style='padding: 8px; font-weight: bold; width: 170px;'>الاسم:</td><td style='padding: 8px;'>{{EMPLOYEE_NAME}}</td></tr>
        <tr style='background: #f1f1f1;'><td style='padding: 8px; font-weight: bold;'>حساب AD:</td><td style='padding: 8px;'>{{SAM_ACCOUNT}}</td></tr>
        <tr><td style='padding: 8px; font-weight: bold;'>السمات الفارغة:</td><td style='padding: 8px; color:#dc3545; font-weight:600'>{{EMPTY_ATTRIBUTES}}</td></tr>
        <tr style='background: #f1f1f1;'><td style='padding: 8px; font-weight: bold;'>الموقع (DN):</td><td style='padding: 8px; direction:ltr; text-align:left; font-size:12px'>{{DN}}</td></tr>
    </table>
    <div style='margin-top: 15px; padding: 10px; background: #fff3cd; border-radius: 5px; color: #856404;'>
        <strong>الإجراء المتخذ:</strong> تم تعطيل الحساب في مكانه (بدون نقل) لأن إحدى السمات المطلوبة فارغة.
        عند استكمال البيانات يمكن إعادة تفعيله يدوياً.
    </div>
    <p style='margin-top: 15px; color: #6c757d; font-size: 12px;'>
        تم الإرسال تلقائياً بواسطة IdentitySyncPro — خدمة «{service.Name}» — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC
    </p>
</div>";
                }

                body = body
                    .Replace("{EMPLOYEE_NAME}", displayName)
                    .Replace("{SAM_ACCOUNT}", samAccount)
                    .Replace("{EMPTY_ATTRIBUTES}", emptyAttributes)
                    .Replace("{DN}", userDn);

                var result = await _emailService.SendAsync(new EmailMessage
                {
                    To = service.NotificationEmail!,
                    Subject = subject,
                    Body = body,
                    IsHtml = true
                });

                db.SvcAuditEntries.Add(new SvcAuditEntry
                {
                    SvcRunLogId = runLog.Id,
                    SvcServiceId = service.Id,
                    Timestamp = DateTime.UtcNow,
                    Action = result.Success ? "EmailSent" : "EmailFailed",
                    KeyValue = samAccount,
                    ADIdentity = samAccount,
                    AttributeName = "Email",
                    NewValue = service.NotificationEmail,
                    ErrorMessage = result.Success ? null : result.Error
                });

                if (result.Success)
                    _logger.LogInformation("SvcEmptyAttrDisable: Email sent to {To} for {SAM}", service.NotificationEmail, samAccount);
                else
                    _logger.LogWarning("SvcEmptyAttrDisable: Email to {To} (about {SAM}) failed: {Error}", service.NotificationEmail, samAccount, result.Error);
            }
            catch (Exception ex)
            {
                db.SvcAuditEntries.Add(new SvcAuditEntry
                {
                    SvcRunLogId = runLog.Id,
                    SvcServiceId = service.Id,
                    Timestamp = DateTime.UtcNow,
                    Action = "EmailFailed",
                    KeyValue = samAccount,
                    ADIdentity = samAccount,
                    ErrorMessage = ex.Message
                });
                _logger.LogError(ex, "SvcEmptyAttrDisable: Email error for {SAM}", samAccount);
            }
        }

        private static string? GetAttributeValue(SearchResultEntry entry, string attributeName)
        {
            if (entry.Attributes.Contains(attributeName))
            {
                var attr = entry.Attributes[attributeName];
                if (attr.Count > 0) return attr[0]?.ToString();
            }
            return null;
        }

        /// <summary>
        /// Shared factory — this used to enable SSL only, leaving the plain port (389)
        /// completely unencrypted.
        /// </summary>
        private static LdapConnection CreateLdapConnection(SvcService service)
            => LdapConnectionFactory.Create(service.ToLdapOptions());

        private async Task BroadcastProgressAsync(int serviceId, string serviceName, int current, int total, SvcRunLog runLog, string status)
        {
            try
            {
                await _progressNotifier.NotifyProgressAsync(serviceId, new
                {
                    serviceId,
                    serviceName,
                    current,
                    total,
                    percent = total > 0 ? (int)Math.Round((double)current / total * 100) : 0,
                    updated = runLog.UpdatedRecords,
                    failed = runLog.FailedRecords,
                    skipped = runLog.SkippedRecords,
                    notFound = runLog.NotFoundRecords,
                    status,
                    runLogId = runLog.Id,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                // Don't let SignalR errors break the process
                _logger.LogDebug(ex, "SvcEmptyAttrDisable: Failed to broadcast progress");
            }
        }
    }
}
