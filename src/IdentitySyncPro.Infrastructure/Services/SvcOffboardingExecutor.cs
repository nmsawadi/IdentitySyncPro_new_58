using IdentitySyncPro.Core.Models.Audit;
using IdentitySyncPro.Core.Models.Connectors;
using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Infrastructure.Connectors;
using System.DirectoryServices.Protocols;
using System.Net;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Services;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Executor for Employee Offboarding services.
    /// Reads from DB view, finds Inactive employees, disables AD accounts,
    /// moves them to a target OU, and sends SMS notifications.
    /// </summary>
    public class SvcOffboardingExecutor
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly SvcDatabaseReader _dbReader;
        private readonly ISmsService _smsService;
        private readonly IEmailService _emailService;
        private readonly IConfiguration _configuration;
        private readonly ISvcProgressNotifier _progressNotifier;
        private readonly ILogger<SvcOffboardingExecutor> _logger;
        private readonly AppDbContext _appDb;

        // userAccountControl flag for ACCOUNTDISABLE
        private const int UF_ACCOUNTDISABLE = 0x0002;

        // Save audit entries to DB every N records to prevent data loss
        private const int BatchSaveInterval = 50;

        public SvcOffboardingExecutor(
            IServiceScopeFactory scopeFactory,
            SvcDatabaseReader dbReader,
            ISmsService smsService,
            IEmailService emailService,
            IConfiguration configuration,
            ISvcProgressNotifier progressNotifier,
            ILogger<SvcOffboardingExecutor> logger,
            AppDbContext appDb)
        {
            _scopeFactory = scopeFactory;
            _dbReader = dbReader;
            _smsService = smsService;
            _emailService = emailService;
            _configuration = configuration;
            _progressNotifier = progressNotifier;
            _logger = logger;
            _appDb = appDb;
        }

        /// <summary>
        /// Execute an offboarding service by ID.
        /// </summary>
        public async Task<SvcRunLog> ExecuteAsync(int serviceId, string triggeredBy = ActorNames.System, CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();

            var service = await db.SvcServices
                .FirstOrDefaultAsync(s => s.Id == serviceId, ct);

            if (service == null)
                throw new InvalidOperationException($"Service with ID {serviceId} not found");

            if (!service.IsEnabled)
                throw new InvalidOperationException($"Service '{service.Name}' is disabled");

            // Create run log
            var runLog = new SvcRunLog
            {
                SvcServiceId = serviceId,
                StartTime = DateTime.UtcNow,
                Status = "Running",
                TriggeredBy = triggeredBy
            };
            db.SvcRunLogs.Add(runLog);
            await db.SaveChangesAsync(ct);

            // Broadcast start
            await BroadcastProgressAsync(serviceId, service.Name, 0, 0, runLog, "Running");

            int processedCount = 0;
            List<Dictionary<string, string>> inactiveRecords = new();

            try
            {
                _logger.LogInformation("SvcOffboarding: Starting '{Name}' (ID: {Id}), triggered by {TriggeredBy}",
                    service.Name, serviceId, triggeredBy);

                // Validate configuration
                if (string.IsNullOrWhiteSpace(service.StatusColumn))
                    throw new InvalidOperationException("Status column is not configured");
                if (string.IsNullOrWhiteSpace(service.StatusValue))
                    throw new InvalidOperationException("Status value is not configured");
                if (string.IsNullOrWhiteSpace(service.KeySourceColumn))
                    throw new InvalidOperationException("Key source column is not configured");

                // Step 1: Read source data
                var connectionString = SvcDatabaseReader.BuildConnectionString(
                    service.SourceProvider, service.SourceHost, service.SourcePort,
                    service.SourceDatabase, service.SourceUsername, service.SourcePassword,
                    service.SourceIntegratedSecurity);

                var sourceData = await _dbReader.ReadAllAsync(
                    service.SourceProvider, connectionString, service.SourceTableOrView, ct);

                runLog.TotalRecords = sourceData.Count;
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("SvcOffboarding: Read {Count} records from source", sourceData.Count);

                // Broadcast total records
                await BroadcastProgressAsync(serviceId, service.Name, 0, sourceData.Count, runLog, "Running");

                // Step 2: Filter for inactive employees
                inactiveRecords = sourceData.Where(r =>
                    r.TryGetValue(service.StatusColumn, out var status) &&
                    status.Equals(service.StatusValue, StringComparison.OrdinalIgnoreCase)
                ).ToList();

                _logger.LogInformation("SvcOffboarding: Found {Count} inactive records out of {Total}",
                    inactiveRecords.Count, sourceData.Count);

                // Step 3: Connect to AD
                using var ldapConnection = CreateLdapConnection(service);
                ldapConnection.Bind();
                _logger.LogInformation("SvcOffboarding: Connected to AD server {Server}", service.ADServer);

                // Step 3b: Exclusion group — members (including nested) are exempt from offboarding.
                // Resolved once per run. If the configured group cannot be found, the run FAILS
                // rather than silently offboarding users the admin intended to protect.
                var exemptDns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(service.OffboardingExclusionGroup))
                {
                    exemptDns = LoadExclusionGroupMemberDns(ldapConnection, service);
                    _logger.LogInformation("SvcOffboarding: Exclusion group '{Group}' has {Count} members (exempt from offboarding)",
                        service.OffboardingExclusionGroup, exemptDns.Count);
                }

                // Step 4: Get previously offboarded keys.
                // NOT used to skip AD lookups — every inactive record is re-checked against live AD state
                // so a re-enabled/moved-back account gets offboarded again. The set serves two purposes:
                // distinguish "parked in target OU outside the search OU" from a genuine NotFound,
                // and tag repeat offboardings as "ReOffboarded" in the audit log.
                var previouslyOffboarded = await db.SvcAuditEntries
                    .Where(a => a.SvcServiceId == serviceId
                        && (a.Action == "Disabled" || a.Action == "ReOffboarded" || a.Action == "AlreadyDisabled" || a.Action == "Moved"))
                    .Select(a => a.KeyValue)
                    .Distinct()
                    .ToListAsync(ct);
                var offboardedSet = new HashSet<string>(previouslyOffboarded, StringComparer.OrdinalIgnoreCase);
                var skipReasons = new SvcRunSummary.Reasons();
                var actedOn = new List<string>();

                _logger.LogInformation("SvcOffboarding: {Count} keys were offboarded in previous runs",
                    offboardedSet.Count);

                // Step 5: Process each inactive record
                int unsavedCount = 0;

                foreach (var record in inactiveRecords)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!record.TryGetValue(service.KeySourceColumn, out var keyValue) ||
                        string.IsNullOrWhiteSpace(keyValue))
                    {
                        runLog.SkippedRecords++;
                        skipReasons.Add($"source row has no value in '{service.KeySourceColumn}'");
                        db.SvcAuditEntries.Add(new SvcAuditEntry
                        {
                            SvcRunLogId = runLog.Id,
                            SvcServiceId = serviceId,
                            Timestamp = DateTime.UtcNow,
                            Action = "Skip",
                            KeyValue = "(empty)",
                            ErrorMessage = $"Key column '{service.KeySourceColumn}' is empty"
                        });
                        unsavedCount++;
                        processedCount++;
                        if (unsavedCount >= BatchSaveInterval)
                        {
                            await db.SaveChangesAsync(ct);
                            unsavedCount = 0;
                        }
                        await BroadcastProgressAsync(serviceId, service.Name, processedCount, inactiveRecords.Count, runLog, "Running");
                        continue;
                    }

                    try
                    {
                        await ProcessOffboardingAsync(ldapConnection, service, record, keyValue, offboardedSet, exemptDns, runLog, db, skipReasons, actedOn, ct);
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
                            KeyValue = keyValue,
                            ErrorMessage = ex.Message
                        });
                        _logger.LogError(ex, "SvcOffboarding: Error processing record '{Key}'", keyValue);
                    }

                    unsavedCount++;
                    processedCount++;

                    // Batch save audit entries periodically to prevent data loss
                    if (unsavedCount >= BatchSaveInterval)
                    {
                        await db.SaveChangesAsync(ct);
                        unsavedCount = 0;
                        _logger.LogDebug("SvcOffboarding: Batch saved at record {Current}/{Total}", processedCount, inactiveRecords.Count);
                    }

                    // Broadcast progress
                    await BroadcastProgressAsync(serviceId, service.Name, processedCount, inactiveRecords.Count, runLog, "Running");
                }

                // Also count non-inactive as skipped
                var notInactive = sourceData.Count - inactiveRecords.Count;
                runLog.SkippedRecords += notInactive;
                for (int i = 0; i < notInactive; i++) skipReasons.Add("still active in the source");

                // One entry describing the run itself, written even when nobody was offboarded.
                db.SvcAuditEntries.Add(SvcRunSummary.Build(
                    runLog, serviceId, skipReasons, actedOn,
                    note: $"Read {sourceData.Count} source row(s); {inactiveRecords.Count} matched the inactive status."));
                await db.SaveChangesAsync(ct);

                // Finalize
                runLog.Status = runLog.FailedRecords > 0 ? "CompletedWithErrors" : "Completed";
                runLog.EndTime = DateTime.UtcNow;

                service.LastRunAt = runLog.EndTime;
                service.LastRunStatus = runLog.Status;
                service.UpdatedAt = DateTime.UtcNow;

                await db.SaveChangesAsync(ct);

                // Broadcast completion
                await BroadcastProgressAsync(serviceId, service.Name, processedCount, inactiveRecords.Count, runLog, runLog.Status);

                _logger.LogInformation(
                    "SvcOffboarding: '{Name}' completed. Total={Total}, Disabled={Updated}, Failed={Failed}, Skipped={Skipped}, NotFound={NotFound}",
                    service.Name, runLog.TotalRecords, runLog.UpdatedRecords, runLog.FailedRecords,
                    runLog.SkippedRecords, runLog.NotFoundRecords);

                return runLog;
            }
            catch (OperationCanceledException)
            {
                // === CANCELLED BY USER ===
                runLog.Status = "Cancelled";
                runLog.EndTime = DateTime.UtcNow;
                runLog.ErrorMessage = "تم إلغاء العملية بواسطة المستخدم / Cancelled by user";

                service.LastRunAt = runLog.EndTime;
                service.LastRunStatus = "Cancelled";
                service.UpdatedAt = DateTime.UtcNow;

                await db.SaveChangesAsync(CancellationToken.None);

                // Broadcast cancellation
                await BroadcastProgressAsync(serviceId, service.Name, processedCount, inactiveRecords.Count, runLog, "Cancelled");

                _logger.LogWarning("SvcOffboarding: Service '{Name}' was cancelled by user after processing {Count} records",
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

                // Broadcast failure
                await BroadcastProgressAsync(serviceId, service.Name, 0, runLog.TotalRecords, runLog, "Failed");

                _logger.LogError(ex, "SvcOffboarding: Service '{Name}' failed", service.Name);
                throw;
            }
        }

        private async Task ProcessOffboardingAsync(
            LdapConnection ldapConnection,
            SvcService service,
            Dictionary<string, string> record,
            string keyValue,
            HashSet<string> offboardedSet,
            HashSet<string> exemptDns,
            SvcRunLog runLog,
            ServicesDbContext db,
            SvcRunSummary.Reasons skipReasons,
            List<string> actedOn,
            CancellationToken ct)
        {
            // Search AD for the user by key attribute — use specific OU if configured
            var searchBaseDn = !string.IsNullOrWhiteSpace(service.OffboardingSearchOU)
                ? service.OffboardingSearchOU
                : service.ADBaseDN;

            var searchFilter = $"({LdapSanitizer.EscapeFilterValue(service.ADSearchAttribute)}={LdapSanitizer.EscapeFilterValue(keyValue)})";

            var searchRequest = new SearchRequest(
                searchBaseDn,
                searchFilter,
                SearchScope.Subtree,
                new[] { "distinguishedName", "sAMAccountName", "userAccountControl", "displayName", service.PhoneColumn ?? "extensionAttribute13" }
            );

            var searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);

            if (searchResponse.Entries.Count == 0)
            {
                // If the key was offboarded before, the account is parked in the target OU
                // (outside the search OU) — normal state, skip silently to keep the log clean.
                if (offboardedSet.Contains(keyValue))
                {
                    runLog.SkippedRecords++;
                    skipReasons.Add("already offboarded and parked outside the search OU");
                    _logger.LogDebug("SvcOffboarding: Key '{Key}' already offboarded and outside search OU, skipping", keyValue);
                    return;
                }

                runLog.NotFoundRecords++;
                db.SvcAuditEntries.Add(new SvcAuditEntry
                {
                    SvcRunLogId = runLog.Id,
                    SvcServiceId = service.Id,
                    Timestamp = DateTime.UtcNow,
                    Action = "NotFound",
                    KeyValue = keyValue,
                    ErrorMessage = $"No AD user found with {service.ADSearchAttribute}={keyValue}"
                });
                return;
            }

            // An employee may have more than one AD account with the same employee number —
            // process every matching account, not just the first.
            var offboardedAccounts = new List<(string Sam, string DisplayName, string Phone)>();
            bool anyActionTaken = false;
            var isRepeat = offboardedSet.Contains(keyValue);

            foreach (SearchResultEntry adEntry in searchResponse.Entries)
            {
                var userDn = adEntry.DistinguishedName;
                var samAccount = GetAttributeValue(adEntry, "sAMAccountName") ?? userDn;
                var displayName = GetAttributeValue(adEntry, "displayName") ?? samAccount;
                var phone = GetAttributeValue(adEntry, service.PhoneColumn ?? "extensionAttribute13") ?? string.Empty;
                var uacStr = GetAttributeValue(adEntry, "userAccountControl") ?? "512";
                var uac = int.TryParse(uacStr, out var uacVal) ? uacVal : 512;

                var isDisabled = (uac & UF_ACCOUNTDISABLE) != 0;
                var inTargetOu = !string.IsNullOrWhiteSpace(service.TargetOU) &&
                    userDn.EndsWith("," + service.TargetOU, StringComparison.OrdinalIgnoreCase);

                // Member of the exclusion group → exempt from offboarding entirely.
                // Logged only when an action would otherwise have been taken (account still enabled),
                // so permanently-exempt disabled accounts don't flood the log every run.
                if (exemptDns.Contains(userDn))
                {
                    if (!isDisabled)
                    {
                        db.SvcAuditEntries.Add(new SvcAuditEntry
                        {
                            SvcRunLogId = runLog.Id,
                            SvcServiceId = service.Id,
                            Timestamp = DateTime.UtcNow,
                            Action = "Excluded",
                            KeyValue = keyValue,
                            ADIdentity = samAccount,
                            AttributeName = "memberOf",
                            NewValue = service.OffboardingExclusionGroup
                        });
                        _logger.LogInformation("SvcOffboarding: {SAM} (key={Key}) is in exclusion group — skipped", samAccount, keyValue);
                    }
                    continue;
                }

                // Fully offboarded (disabled + already in target OU, or no target OU configured):
                // nothing to do — skip silently so repeated runs don't flood the audit log.
                if (isDisabled && (inTargetOu || string.IsNullOrWhiteSpace(service.TargetOU)))
                {
                    _logger.LogDebug("SvcOffboarding: User {SAM} already offboarded, nothing to do", samAccount);
                    continue;
                }

                if (!isDisabled)
                {
                    // === STEP A: Disable the account (again, if it was re-enabled) ===
                    var newUac = uac | UF_ACCOUNTDISABLE;
                    var disableMod = new DirectoryAttributeModification
                    {
                        Name = "userAccountControl",
                        Operation = DirectoryAttributeOperation.Replace
                    };
                    disableMod.Add(newUac.ToString());

                    var modifyRequest = new ModifyRequest(userDn, disableMod);
                    ldapConnection.SendRequest(modifyRequest);

                    db.SvcAuditEntries.Add(new SvcAuditEntry
                    {
                        SvcRunLogId = runLog.Id,
                        SvcServiceId = service.Id,
                        Timestamp = DateTime.UtcNow,
                        Action = isRepeat ? "ReOffboarded" : "Disabled",
                        KeyValue = keyValue,
                        ADIdentity = samAccount,
                        AttributeName = "userAccountControl",
                        OldValue = uac.ToString(),
                        NewValue = newUac.ToString()
                    });

                    _logger.LogInformation("SvcOffboarding: {Action} account {SAM} (key={Key})",
                        isRepeat ? "Re-offboarded" : "Disabled", samAccount, keyValue);

                    offboardedAccounts.Add((samAccount, displayName, phone));
                    anyActionTaken = true;
                }

                // === STEP B: Move to target OU (also covers: still disabled but moved out of it) ===
                if (!string.IsNullOrWhiteSpace(service.TargetOU) && !inTargetOu)
                {
                    try
                    {
                        // Extract CN from current DN
                        var cn = userDn.Split(',')[0]; // e.g., "CN=Ahmed Ali"
                        var moveRequest = new ModifyDNRequest(userDn, service.TargetOU, cn);
                        ldapConnection.SendRequest(moveRequest);

                        var newDn = $"{cn},{service.TargetOU}";

                        db.SvcAuditEntries.Add(new SvcAuditEntry
                        {
                            SvcRunLogId = runLog.Id,
                            SvcServiceId = service.Id,
                            Timestamp = DateTime.UtcNow,
                            Action = "Moved",
                            KeyValue = keyValue,
                            ADIdentity = samAccount,
                            AttributeName = "distinguishedName",
                            OldValue = userDn,
                            NewValue = newDn
                        });

                        _logger.LogInformation("SvcOffboarding: Moved {SAM} to {OU}", samAccount, service.TargetOU);
                        anyActionTaken = true;
                    }
                    catch (Exception moveEx)
                    {
                        db.SvcAuditEntries.Add(new SvcAuditEntry
                        {
                            SvcRunLogId = runLog.Id,
                            SvcServiceId = service.Id,
                            Timestamp = DateTime.UtcNow,
                            Action = "Error",
                            KeyValue = keyValue,
                            ADIdentity = samAccount,
                            AttributeName = "Move",
                            ErrorMessage = $"Failed to move to {service.TargetOU}: {moveEx.Message}"
                        });
                        _logger.LogWarning(moveEx, "SvcOffboarding: Failed to move {SAM} to {OU}", samAccount, service.TargetOU);
                    }
                }
            }

            // Notifications go out once per employee (not per account) to avoid duplicate SMS/emails
            // when the same employee number matches several AD accounts.
            if (offboardedAccounts.Count > 0)
            {
                var samList = string.Join(", ", offboardedAccounts.Select(a => a.Sam));
                var firstDisplayName = offboardedAccounts[0].DisplayName;
                var firstPhone = offboardedAccounts.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Phone)).Phone ?? string.Empty;

                // === STEP C: Send SMS notification ===
                if (service.EnableSms && !string.IsNullOrWhiteSpace(service.SmsTemplate))
                {
                    await SendOffboardingSmsAsync(service, record, keyValue, samList, firstDisplayName, firstPhone, runLog, db);
                }

                // === STEP D: Send Email notification to admin ===
                // Already inside `offboardedAccounts.Count > 0`, so the count is never zero here —
                // it goes through the gate anyway so the rule has one home, not two.
                if (SvcEmailGate.ShouldSend(service, offboardedAccounts.Count, _logger, "SvcOffboarding"))
                {
                    await SendOffboardingEmailAsync(service, record, keyValue, samList, firstDisplayName, runLog, db);
                }
            }

            if (anyActionTaken)
            {
                runLog.UpdatedRecords++;
                offboardedSet.Add(keyValue);
                // Key only: the display name and account list are scoped to the notification
                // block above. The per-account audit entries already carry the AD detail.
                actedOn.Add($"• {keyValue} — offboarded");
            }
            else
            {
                runLog.SkippedRecords++;
                skipReasons.Add("nothing to do (already disabled and in the target OU)");
            }
        }

        private async Task SendOffboardingSmsAsync(
            SvcService service,
            Dictionary<string, string> record,
            string keyValue,
            string samAccount,
            string displayName,
            string phone,
            SvcRunLog runLog,
            ServicesDbContext db)
        {
            // Unified SMS log entry (reviewed/retried from the SMS Center alongside sync messages).
            var smsLog = new SmsSendLog
            {
                Source = "Offboarding",
                IdentityId = int.TryParse(keyValue, out var empId) ? empId : 0,
                Account = samAccount,
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
                    db.SvcAuditEntries.Add(new SvcAuditEntry
                    {
                        SvcRunLogId = runLog.Id,
                        SvcServiceId = service.Id,
                        Timestamp = DateTime.UtcNow,
                        Action = "SmsFailed",
                        KeyValue = keyValue,
                        ADIdentity = samAccount,
                        ErrorMessage = "No phone number available"
                    });
                    smsLog.GatewayResponse = "No phone number available";
                    return;
                }

                // Get employee name — supports comma-separated columns
                // e.g., "FirstName,SecondName,ThirdName,FamilyName" → "أحمد محمد علي العتيبي"
                var employeeName = displayName;
                if (!string.IsNullOrWhiteSpace(service.EmployeeNameColumn))
                {
                    var nameColumns = service.EmployeeNameColumn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var nameParts = new List<string>();
                    foreach (var col in nameColumns)
                    {
                        if (record.TryGetValue(col, out var part) && !string.IsNullOrWhiteSpace(part))
                            nameParts.Add(part.Trim());
                    }
                    if (nameParts.Count > 0)
                        employeeName = string.Join(" ", nameParts);
                }
                smsLog.DisplayName = employeeName;

                // Build SMS message from template
                var message = service.SmsTemplate!
                    .Replace("{EMPLOYEE_NAME}", employeeName)
                    .Replace("{EMPLOYEE_ID}", keyValue)
                    .Replace("{SAM_ACCOUNT}", samAccount);

                // Resolve SMS provider settings
                string apiUrl, apiUsername, apiPassword, senderName;
                string providerName = "(inline)";
                IdentitySyncPro.Core.Models.Settings.SmsProvider? resolvedProvider = null;
                if (service.SmsProviderId.HasValue)
                {
                    var provider = await _appDb.SmsProviders.FindAsync(service.SmsProviderId.Value);
                    if (provider != null && provider.IsActive)
                    {
                        resolvedProvider = provider;
                        apiUrl = provider.ApiUrl;
                        apiUsername = provider.ApiUsername;
                        apiPassword = provider.ApiPassword;
                        senderName = provider.SenderName;
                        providerName = provider.Name;
                    }
                    else
                    {
                        db.SvcAuditEntries.Add(new SvcAuditEntry
                        {
                            SvcRunLogId = runLog.Id, SvcServiceId = service.Id,
                            Timestamp = DateTime.UtcNow, Action = "SmsFailed",
                            KeyValue = keyValue, ADIdentity = samAccount,
                            ErrorMessage = "SMS provider not found or inactive"
                        });
                        smsLog.GatewayResponse = "SMS provider not found or inactive";
                        return;
                    }
                }
                else
                {
                    // Fallback to configuration-based SMS settings
                    var smsSection = _configuration.GetSection("SmsSettings");
                    apiUrl = smsSection["ApiUrl"] ?? "";
                    apiUsername = smsSection["ApiUsername"] ?? "";
                    apiPassword = smsSection["ApiPassword"] ?? "";
                    senderName = smsSection["SenderName"] ?? "IdentitySyncPro";
                }
                smsLog.ProviderName = providerName;

                var smsRequest = new SmsRequest
                {
                    ApiUrl = apiUrl,
                    ApiUsername = apiUsername,
                    ApiPassword = apiPassword,
                    SenderName = senderName,
                    PhoneNumber = phone,
                    MessageTemplate = message,
                    Username = samAccount,
                    DisplayName = employeeName,
                    IdentityId = keyValue
                };
                // Carry the provider's generic gateway config (method/format/template/headers).
                if (resolvedProvider != null) smsRequest.WithProvider(resolvedProvider);

                var result = await _smsService.SendCredentialsAsync(smsRequest);

                db.SvcAuditEntries.Add(new SvcAuditEntry
                {
                    SvcRunLogId = runLog.Id,
                    SvcServiceId = service.Id,
                    Timestamp = DateTime.UtcNow,
                    Action = result.Success ? "SmsSent" : "SmsFailed",
                    KeyValue = keyValue,
                    ADIdentity = samAccount,
                    AttributeName = "SMS",
                    NewValue = phone,
                    ErrorMessage = result.Success ? null : result.Error
                });

                smsLog.Status = result.Success ? "Success" : "Failed";
                smsLog.GatewayResponse = TruncateResponse(result.Success ? result.Response : result.Error);
                smsLog.SentMessage = result.Success ? null : message; // keep for retry only on failure

                if (result.Success)
                    _logger.LogInformation("SvcOffboarding: SMS sent to {Phone} for {SAM}", phone, samAccount);
                else
                    _logger.LogWarning("SvcOffboarding: SMS failed for {SAM}: {Error}", samAccount, result.Error);
            }
            catch (Exception ex)
            {
                db.SvcAuditEntries.Add(new SvcAuditEntry
                {
                    SvcRunLogId = runLog.Id,
                    SvcServiceId = service.Id,
                    Timestamp = DateTime.UtcNow,
                    Action = "SmsFailed",
                    KeyValue = keyValue,
                    ADIdentity = samAccount,
                    ErrorMessage = ex.Message
                });
                smsLog.Status = "Failed";
                smsLog.GatewayResponse = TruncateResponse(ex.Message);
                _logger.LogError(ex, "SvcOffboarding: SMS error for {SAM}", samAccount);
            }
            finally
            {
                try { await _appDb.SaveChangesAsync(); }
                catch (Exception saveEx) { _logger.LogWarning(saveEx, "SvcOffboarding: failed to write SMS log for {SAM}", samAccount); }
            }
        }

        private static string? TruncateResponse(string? v) => v != null && v.Length > 2000 ? v[..2000] : v;

        private async Task SendOffboardingEmailAsync(
            SvcService service,
            Dictionary<string, string> record,
            string keyValue,
            string samAccount,
            string displayName,
            SvcRunLog runLog,
            ServicesDbContext db)
        {
            try
            {
                // Build employee name from columns
                var employeeName = displayName;
                if (!string.IsNullOrWhiteSpace(service.EmployeeNameColumn))
                {
                    var nameColumns = service.EmployeeNameColumn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var nameParts = new List<string>();
                    foreach (var col in nameColumns)
                    {
                        if (record.TryGetValue(col, out var part) && !string.IsNullOrWhiteSpace(part))
                            nameParts.Add(part.Trim());
                    }
                    if (nameParts.Count > 0) employeeName = string.Join(" ", nameParts);
                }

                // Get extra fields from record for template
                record.TryGetValue("DepartmentName", out var dept);
                record.TryGetValue("JobName", out var jobTitle);
                record.TryGetValue("Status", out var status);

                // Build subject
                var subject = service.EmailSubject ?? "إخلاء طرف موظف / Employee Offboarded: {EMPLOYEE_NAME}";
                subject = subject
                    .Replace("{EMPLOYEE_NAME}", employeeName)
                    .Replace("{EMPLOYEE_ID}", keyValue)
                    .Replace("{SAM_ACCOUNT}", samAccount);

                // Build body
                var body = service.EmailBodyTemplate;
                if (string.IsNullOrWhiteSpace(body))
                {
                    // Default HTML template
                    body = $@"
<div dir='rtl' style='font-family: Segoe UI, Tahoma, Arial; padding: 20px; background: #f8f9fa; border-radius: 8px;'>
    <h2 style='color: #dc3545; border-bottom: 2px solid #dc3545; padding-bottom: 10px;'>
        🚫 إشعار إخلاء طرف موظف
    </h2>
    <table style='width: 100%; border-collapse: collapse; margin-top: 15px;'>
        <tr><td style='padding: 8px; font-weight: bold; width: 150px;'>اسم الموظف:</td><td style='padding: 8px;'>{{EMPLOYEE_NAME}}</td></tr>
        <tr style='background: #f1f1f1;'><td style='padding: 8px; font-weight: bold;'>الرقم الوظيفي:</td><td style='padding: 8px;'>{{EMPLOYEE_ID}}</td></tr>
        <tr><td style='padding: 8px; font-weight: bold;'>حساب AD:</td><td style='padding: 8px;'>{{SAM_ACCOUNT}}</td></tr>
        <tr style='background: #f1f1f1;'><td style='padding: 8px; font-weight: bold;'>القسم:</td><td style='padding: 8px;'>{{DEPARTMENT}}</td></tr>
        <tr><td style='padding: 8px; font-weight: bold;'>المسمى الوظيفي:</td><td style='padding: 8px;'>{{JOB_TITLE}}</td></tr>
    </table>
    <div style='margin-top: 15px; padding: 10px; background: #fff3cd; border-radius: 5px; color: #856404;'>
        <strong>الإجراءات المتخذة:</strong> تعطيل الحساب ✅ | النقل إلى EndedContracts ✅
    </div>
    <p style='margin-top: 15px; color: #6c757d; font-size: 12px;'>
        تم الإرسال تلقائياً بواسطة IdentitySyncPro — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC
    </p>
</div>";
                }

                body = body
                    .Replace("{EMPLOYEE_NAME}", employeeName)
                    .Replace("{EMPLOYEE_ID}", keyValue)
                    .Replace("{SAM_ACCOUNT}", samAccount)
                    .Replace("{DEPARTMENT}", dept ?? "-")
                    .Replace("{JOB_TITLE}", jobTitle ?? "-");

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
                    KeyValue = keyValue,
                    ADIdentity = samAccount,
                    AttributeName = "Email",
                    NewValue = service.NotificationEmail,
                    ErrorMessage = result.Success ? null : result.Error
                });

                if (result.Success)
                    _logger.LogInformation("SvcOffboarding: Email sent to {To} for {SAM}", service.NotificationEmail, samAccount);
                else
                    _logger.LogWarning("SvcOffboarding: Email to {To} (about {SAM}) failed: {Error}", service.NotificationEmail, samAccount, result.Error);
            }
            catch (Exception ex)
            {
                db.SvcAuditEntries.Add(new SvcAuditEntry
                {
                    SvcRunLogId = runLog.Id,
                    SvcServiceId = service.Id,
                    Timestamp = DateTime.UtcNow,
                    Action = "EmailFailed",
                    KeyValue = keyValue,
                    ADIdentity = samAccount,
                    ErrorMessage = ex.Message
                });
                _logger.LogError(ex, "SvcOffboarding: Email error for {SAM}", samAccount);
            }
        }

        /// <summary>
        /// Resolves the exclusion group (by sAMAccountName/CN, or full DN) and returns the DNs
        /// of all its user members, including members of nested groups (LDAP_MATCHING_RULE_IN_CHAIN).
        /// Throws if the group cannot be found — failing loudly beats offboarding protected users.
        /// </summary>
        private HashSet<string> LoadExclusionGroupMemberDns(LdapConnection ldapConnection, SvcService service)
        {
            var configured = service.OffboardingExclusionGroup!.Trim();

            // Resolve the group DN — a value containing '=' is treated as a full DN
            string groupDn;
            if (configured.Contains('='))
            {
                groupDn = configured;
            }
            else
            {
                var escaped = LdapSanitizer.EscapeFilterValue(configured);
                var groupSearch = new SearchRequest(
                    service.ADBaseDN,
                    $"(&(objectClass=group)(|(sAMAccountName={escaped})(cn={escaped})))",
                    SearchScope.Subtree,
                    new[] { "distinguishedName" });

                var groupResponse = (SearchResponse)ldapConnection.SendRequest(groupSearch);
                if (groupResponse.Entries.Count == 0)
                    throw new InvalidOperationException(
                        $"Exclusion group '{configured}' was not found in AD — aborting run to avoid offboarding protected users");

                groupDn = groupResponse.Entries[0].DistinguishedName;
            }

            // Transitive membership: matching rule 1.2.840.113556.1.4.1941 walks nested groups
            var memberDns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var memberFilter = $"(&(objectCategory=person)(objectClass=user)(memberOf:1.2.840.113556.1.4.1941:={LdapSanitizer.EscapeFilterValue(groupDn)}))";

            var memberSearch = new SearchRequest(
                service.ADBaseDN,
                memberFilter,
                SearchScope.Subtree,
                new[] { "distinguishedName" });
            var pageControl = new PageResultRequestControl(500);
            memberSearch.Controls.Add(pageControl);

            while (true)
            {
                var response = (SearchResponse)ldapConnection.SendRequest(memberSearch);
                foreach (SearchResultEntry entry in response.Entries)
                    memberDns.Add(entry.DistinguishedName);

                var pageResponse = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
                if (pageResponse == null || pageResponse.Cookie.Length == 0)
                    break;
                pageControl.Cookie = pageResponse.Cookie;
            }

            return memberDns;
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

        private static LdapConnection CreateLdapConnection(SvcService service)
        {
            // Shared factory — this used to enable SSL only, leaving the plain port (389)
            // completely unencrypted.
            return LdapConnectionFactory.Create(service.ToLdapOptions());
        }

        /// <summary>
        /// Broadcast progress to SignalR clients monitoring this service.
        /// </summary>
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
                // Don't let SignalR errors break the offboarding process
                _logger.LogDebug(ex, "SvcOffboarding: Failed to broadcast progress");
            }
        }
    }
}
