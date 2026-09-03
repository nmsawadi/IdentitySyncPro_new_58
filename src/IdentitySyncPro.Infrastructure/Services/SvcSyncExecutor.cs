using IdentitySyncPro.Core.Models.Audit;
using IdentitySyncPro.Core.Models.Connectors;
using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Infrastructure.Connectors;
using System.DirectoryServices.Protocols;
using System.Net;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Services;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Core executor for database-to-AD sync services.
    /// Reads data from source database, finds users in AD by key attribute,
    /// and updates AD attributes based on field mappings.
    /// Completely independent from the IAM sync engine.
    /// </summary>
    public class SvcSyncExecutor
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly SvcDatabaseReader _dbReader;
        private readonly ISvcProgressNotifier _progressNotifier;
        private readonly ILogger<SvcSyncExecutor> _logger;

        // Save audit entries to DB every N records to prevent data loss
        private const int BatchSaveInterval = 50;

        public SvcSyncExecutor(
            IServiceScopeFactory scopeFactory,
            SvcDatabaseReader dbReader,
            ISvcProgressNotifier progressNotifier,
            ILogger<SvcSyncExecutor> logger)
        {
            _scopeFactory = scopeFactory;
            _dbReader = dbReader;
            _progressNotifier = progressNotifier;
            _logger = logger;
        }

        /// <summary>
        /// Execute a sync service by ID.
        /// </summary>
        public async Task<SvcRunLog> ExecuteAsync(int serviceId, string triggeredBy = ActorNames.System, CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();

            // Load service with mappings
            var service = await db.SvcServices
                .Include(s => s.FieldMappings)
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

            int processedCount = 0;
            var actedOn = new List<string>();
            List<Dictionary<string, string>> sourceData = new();

            try
            {
                _logger.LogInformation("SvcSyncExecutor: Starting service '{Name}' (ID: {Id}), triggered by {TriggeredBy}",
                    service.Name, serviceId, triggeredBy);

                // Broadcast start
                await BroadcastProgressAsync(serviceId, service.Name, 0, 0, runLog, "Running");

                // Step 1: Build connection string and read source data
                var connectionString = SvcDatabaseReader.BuildConnectionString(
                    service.SourceProvider, service.SourceHost, service.SourcePort,
                    service.SourceDatabase, service.SourceUsername, service.SourcePassword,
                    service.SourceIntegratedSecurity);

                sourceData = await _dbReader.ReadAllAsync(
                    service.SourceProvider, connectionString, service.SourceTableOrView, ct);

                runLog.TotalRecords = sourceData.Count;
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("SvcSyncExecutor: Read {Count} records from source", sourceData.Count);

                // Broadcast total records
                await BroadcastProgressAsync(serviceId, service.Name, 0, sourceData.Count, runLog, "Running");

                // Step 2: Get update mappings (exclude key mapping)
                var updateMappings = service.FieldMappings
                    .Where(m => !m.IsKeyMapping)
                    .OrderBy(m => m.SortOrder)
                    .ToList();

                if (string.IsNullOrWhiteSpace(service.KeySourceColumn))
                {
                    throw new InvalidOperationException("Key source column is not configured");
                }

                // Step 3: Connect to AD and process each record
                using var ldapConnection = CreateLdapConnection(service);
                ldapConnection.Bind();
                _logger.LogInformation("SvcSyncExecutor: Connected to AD server {Server}", service.ADServer);

                int unsavedCount = 0;

                foreach (var record in sourceData)
                {
                    ct.ThrowIfCancellationRequested();

                    // Get key value from source record
                    if (!record.TryGetValue(service.KeySourceColumn, out var keyValue) || string.IsNullOrWhiteSpace(keyValue))
                    {
                        runLog.SkippedRecords++;
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

                        // Batch save + broadcast progress
                        if (unsavedCount >= BatchSaveInterval)
                        {
                            await db.SaveChangesAsync(ct);
                            unsavedCount = 0;
                        }
                        await BroadcastProgressAsync(serviceId, service.Name, processedCount, sourceData.Count, runLog, "Running");
                        continue;
                    }

                    try
                    {
                        await ProcessRecordAsync(ldapConnection, service, updateMappings, record, keyValue, runLog, db, actedOn, ct);
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
                        _logger.LogError(ex, "SvcSyncExecutor: Error processing record with key '{Key}'", keyValue);
                    }

                    unsavedCount++;
                    processedCount++;

                    // Batch save audit entries periodically to prevent data loss
                    if (unsavedCount >= BatchSaveInterval)
                    {
                        await db.SaveChangesAsync(ct);
                        unsavedCount = 0;
                        _logger.LogDebug("SvcSyncExecutor: Batch saved audit entries at record {Current}/{Total}", processedCount, sourceData.Count);
                    }

                    // Broadcast progress
                    await BroadcastProgressAsync(serviceId, service.Name, processedCount, sourceData.Count, runLog, "Running");
                }

                // One entry describing the run itself. Sync already logs every record, so this is
                // a top-of-page overview rather than a fix for an empty page.
                db.SvcAuditEntries.Add(SvcRunSummary.Build(
                    runLog, serviceId, actedOn: actedOn,
                    note: $"Read {sourceData.Count} source row(s); matched AD on '{service.ADSearchAttribute}'."));

                // Step 4: Finalize
                runLog.Status = runLog.FailedRecords > 0 ? "CompletedWithErrors" : "Completed";
                runLog.EndTime = DateTime.UtcNow;

                service.LastRunAt = runLog.EndTime;
                service.LastRunStatus = runLog.Status;
                service.UpdatedAt = DateTime.UtcNow;

                await db.SaveChangesAsync(ct);

                // Broadcast completion
                await BroadcastProgressAsync(serviceId, service.Name, processedCount, sourceData.Count, runLog, runLog.Status);

                _logger.LogInformation(
                    "SvcSyncExecutor: Service '{Name}' completed. Total={Total}, Updated={Updated}, Failed={Failed}, Skipped={Skipped}, NotFound={NotFound}",
                    service.Name, runLog.TotalRecords, runLog.UpdatedRecords, runLog.FailedRecords, runLog.SkippedRecords, runLog.NotFoundRecords);

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
                await BroadcastProgressAsync(serviceId, service.Name, processedCount, sourceData.Count, runLog, "Cancelled");

                _logger.LogWarning("SvcSyncExecutor: Service '{Name}' was cancelled by user after processing {Count} records",
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

                _logger.LogError(ex, "SvcSyncExecutor: Service '{Name}' failed", service.Name);
                throw;
            }
        }

        private async Task ProcessRecordAsync(
            LdapConnection ldapConnection,
            SvcService service,
            List<SvcFieldMapping> updateMappings,
            Dictionary<string, string> record,
            string keyValue,
            SvcRunLog runLog,
            ServicesDbContext db,
            List<string> actedOn,
            CancellationToken ct)
        {
            // Search for user in AD by the search attribute
            var searchFilter = $"({LdapSanitizer.EscapeFilterValue(service.ADSearchAttribute)}={LdapSanitizer.EscapeFilterValue(keyValue)})";

            // Build list of attributes to request
            var requestedAttributes = updateMappings.Select(m => m.TargetAttribute).Distinct().ToList();
            requestedAttributes.Add("distinguishedName");
            requestedAttributes.Add("sAMAccountName");

            var searchRequest = new SearchRequest(
                service.ADBaseDN,
                searchFilter,
                SearchScope.Subtree,
                requestedAttributes.ToArray()
            );

            var searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);

            if (searchResponse.Entries.Count == 0)
            {
                // User not found in AD
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
            // apply the mapped updates to every matching account, not just the first.
            bool anyAccountUpdated = false;
            var unchangedSams = new List<string>();
            var updatedSams = new List<string>();

            foreach (SearchResultEntry adEntry in searchResponse.Entries)
            {
                var userDn = adEntry.DistinguishedName;
                var samAccount = GetAttributeValue(adEntry, "sAMAccountName") ?? userDn;

                // Compare and update attributes
                var modifications = new List<DirectoryAttributeModification>();

                foreach (var mapping in updateMappings)
                {
                    if (!record.TryGetValue(mapping.SourceColumn, out var newValue))
                        continue;

                    if (string.IsNullOrEmpty(newValue))
                        continue;

                    var currentValue = GetAttributeValue(adEntry, mapping.TargetAttribute) ?? "";

                    if (currentValue != newValue)
                    {
                        var mod = new DirectoryAttributeModification
                        {
                            Name = mapping.TargetAttribute,
                            Operation = DirectoryAttributeOperation.Replace
                        };
                        mod.Add(newValue);
                        modifications.Add(mod);

                        // Log each attribute change
                        db.SvcAuditEntries.Add(new SvcAuditEntry
                        {
                            SvcRunLogId = runLog.Id,
                            SvcServiceId = service.Id,
                            Timestamp = DateTime.UtcNow,
                            Action = "Update",
                            KeyValue = keyValue,
                            ADIdentity = samAccount,
                            AttributeName = mapping.TargetAttribute,
                            OldValue = string.IsNullOrEmpty(currentValue) ? "(empty)" : currentValue,
                            NewValue = newValue
                        });
                    }
                }

                if (modifications.Count > 0)
                {
                    var modifyRequest = new ModifyRequest(userDn, modifications.ToArray());
                    ldapConnection.SendRequest(modifyRequest);
                    anyAccountUpdated = true;
                    updatedSams.Add(samAccount);

                    _logger.LogDebug("SvcSyncExecutor: Updated AD user {SAM} ({Count} attributes changed)",
                        samAccount, modifications.Count);
                }
                else
                {
                    unchangedSams.Add(samAccount);
                }
            }

            // The record counts once (per employee): Updated if any of their accounts changed.
            if (anyAccountUpdated)
            {
                runLog.UpdatedRecords++;
                actedOn.Add($"• {keyValue} — updated: {string.Join(", ", updatedSams.DefaultIfEmpty("(no account listed)"))}");
            }
            else
            {
                runLog.SkippedRecords++;
                var joinedSams = string.Join(", ", unchangedSams);
                db.SvcAuditEntries.Add(new SvcAuditEntry
                {
                    SvcRunLogId = runLog.Id,
                    SvcServiceId = service.Id,
                    Timestamp = DateTime.UtcNow,
                    Action = "NoChange",
                    KeyValue = keyValue,
                    ADIdentity = joinedSams.Length > 500 ? joinedSams[..500] : joinedSams
                });
            }
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
                // Don't let SignalR errors break the sync process
                _logger.LogDebug(ex, "SvcSyncExecutor: Failed to broadcast progress");
            }
        }

        private static string? GetAttributeValue(SearchResultEntry entry, string attributeName)
        {
            if (entry.Attributes.Contains(attributeName))
            {
                var attr = entry.Attributes[attributeName];
                if (attr.Count > 0)
                    return attr[0]?.ToString();
            }
            return null;
        }

        /// <summary>
        /// Shared factory — this used to enable SSL only, leaving the plain port (389)
        /// completely unencrypted.
        /// </summary>
        private static LdapConnection CreateLdapConnection(SvcService service)
            => LdapConnectionFactory.Create(service.ToLdapOptions());

        /// <summary>
        /// Test AD connection for a service configuration.
        /// </summary>
        public (bool Success, string Message) TestAdConnection(SvcService service)
        {
            try
            {
                using var connection = CreateLdapConnection(service);
                connection.Bind();
                return (true, $"Connected to AD: {service.ADServer}:{service.ADPort}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SvcSyncExecutor: AD connection test failed for {Server}", service.ADServer);
                return (false, $"AD connection failed: {ex.Message}");
            }
        }
    }
}
