using IdentitySyncPro.Core.Models.Audit;
using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Text;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Governance;
using IdentitySyncPro.Core.Models.Services;
using IdentitySyncPro.Infrastructure.Connectors;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// AD audit / report service. Scans AD and produces findings (logged as audit entries —
    /// searchable and Excel-exportable from the audit log) plus a summary email to the
    /// administration. One executor covers several report types via ReportType:
    ///   PrivilegedGroups     — effective (nested) members of sensitive admin groups + change alerts
    ///   PasswordNeverExpires — enabled accounts flagged DONT_EXPIRE_PASSWORD
    ///   DuplicateAccounts    — accounts sharing the same value of a chosen attribute (e.g. employeeID)
    ///   LockedAccounts       — currently locked accounts (+ lockoutTime / badPwdCount)
    ///   AccessCertification  — direct members of chosen groups, for manager attestation
    ///   NonHumanInventory    — service accounts / bots / workloads, with owner and credential age
    ///
    /// Read-only in every mode EXCEPT PasswordNeverExpires with PwdNeverExpiresAction = "Remove",
    /// which clears the DONT_EXPIRE_PASSWORD flag. That mode is opt-in, requires a Search OU, and
    /// honours the exclusion group — stated here because "this service never writes to AD" was true
    /// of every earlier version and is the kind of assumption that outlives the code it described.
    /// </summary>
    public class SvcAdAuditExecutor
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IEmailService _emailService;
        private readonly ISvcProgressNotifier _progressNotifier;
        private readonly SelfAccountRegistry _selfAccounts;
        private readonly ILogger<SvcAdAuditExecutor> _logger;

        private const int UF_ACCOUNTDISABLE = 0x0002;
        private const int UF_DONT_EXPIRE_PASSWORD = 0x10000;   // 65536
        private const int LdapPageSize = 500;
        private const string ChainRule = "1.2.840.113556.1.4.1941";   // LDAP_MATCHING_RULE_IN_CHAIN (nested)
        private const string BitAndRule = "1.2.840.113556.1.4.803";   // bitwise AND

        // Well-known privileged groups (English defaults — localized AD may need editing).
        private static readonly string[] DefaultPrivilegedGroups =
        {
            "Domain Admins", "Enterprise Admins", "Schema Admins", "Administrators",
            "Account Operators", "Backup Operators", "Server Operators", "Group Policy Creator Owners"
        };

        public SvcAdAuditExecutor(
            IServiceScopeFactory scopeFactory,
            IEmailService emailService,
            ISvcProgressNotifier progressNotifier,
            SelfAccountRegistry selfAccounts,
            ILogger<SvcAdAuditExecutor> logger)
        {
            _scopeFactory = scopeFactory;
            _emailService = emailService;
            _progressNotifier = progressNotifier;
            _selfAccounts = selfAccounts;
            _logger = logger;
        }

        /// <summary>
        /// One row of a report.
        ///
        /// <paramref name="Attr"/>, <paramref name="Old"/> and <paramref name="New"/> are optional
        /// and exist so a finding that CHANGED something records the before and after in their own
        /// columns — the audit log then renders it the same way it renders a sync update, instead
        /// of as one opaque sentence.
        /// </summary>
        internal sealed record Finding(
            string Action, string Sam, string? Dn, string? Context, string? Detail,
            string? Attr = null, string? Old = null, string? New = null);

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

            var findings = new List<Finding>();
            var reportType = string.IsNullOrWhiteSpace(service.ReportType) ? "PrivilegedGroups" : service.ReportType!.Trim();
            var scanBase = string.IsNullOrWhiteSpace(service.OffboardingSearchOU) ? service.ADBaseDN : service.OffboardingSearchOU!;

            try
            {
                if (string.IsNullOrWhiteSpace(scanBase))
                    throw new InvalidOperationException("Base DN / Search OU is not configured");

                _logger.LogInformation("SvcAdAudit: '{Name}' (ID {Id}) report={Report} base={Base}", service.Name, serviceId, reportType, scanBase);
                using var ldap = LdapConnectionFactory.Create(service.ToLdapOptions());
                ldap.Bind();

                // Accounts the report actually looked at. Kept separate from the findings count
                // because they answer different questions: a report that examined 4,000 accounts
                // and found none is a result, while "scanned=0" reads as a run that did nothing.
                int examined;

                switch (reportType)
                {
                    case "PasswordNeverExpires": examined = await ReportPasswordNeverExpiresAsync(ldap, service, scanBase, findings, runLog, ct); break;
                    case "DuplicateAccounts": await ReportDuplicatesAsync(ldap, service, scanBase, findings, runLog, ct); examined = findings.Count; break;
                    case "LockedAccounts": await ReportLockedAsync(ldap, scanBase, findings, runLog, ct); examined = findings.Count; break;
                    case "AccessCertification": await ReportGroupMembersAsync(ldap, service, scanBase, findings, runLog, nested: false, action: "AccessMember", ct); examined = findings.Count; break;
                    case "NonHumanInventory": examined = ReportNonHumanInventory(ldap, scope.ServiceProvider, service, scanBase, findings, ct); break;
                    default: await ReportPrivilegedAsync(ldap, service, db, scanBase, findings, runLog, ct); examined = findings.Count; break;
                }

                // Persist findings as audit entries (searchable + Excel from the audit log).
                foreach (var f in findings)
                {
                    db.SvcAuditEntries.Add(new SvcAuditEntry
                    {
                        SvcRunLogId = runLog.Id,
                        SvcServiceId = serviceId,
                        Timestamp = DateTime.UtcNow,
                        Action = f.Action,
                        KeyValue = f.Sam,
                        ADIdentity = f.Dn != null && f.Dn.Length > 500 ? f.Dn[..500] : f.Dn,
                        // A finding that changed something fills Attr/Old/New; a read-only
                        // finding still reports through Context/Detail as before.
                        AttributeName = f.Attr ?? f.Context,
                        OldValue = f.Old,
                        NewValue = f.New ?? f.Detail
                    });
                }
                runLog.TotalRecords = examined;          // accounts looked at
                runLog.UpdatedRecords = findings.Count;  // findings / changes made

                // One entry describing the run itself. A report that found nothing is a real
                // result — "no privileged-group changes" is worth recording — but without this
                // it produced an empty page identical to a service that had never run.
                db.SvcAuditEntries.Add(SvcRunSummary.Build(
                    runLog, serviceId,
                    actedOn: findings
                        .GroupBy(f => f.Action)
                        .OrderByDescending(g => g.Count())
                        .Select(g => $"• {g.Count()} × {g.Key}"),
                    note: SummaryNote(reportType, scanBase, examined, findings.Count)));

                var storedEntries = await SvcAuditWriter.FlushAndVerifyAsync(db, _logger, runLog.Id, serviceId, service.Name, ct);

                _logger.LogInformation(
                    "SvcAdAudit['{Service}'] run {RunId}: report={Report}, findings={Findings}, stored entries={Stored}",
                    service.Name, runLog.Id, reportType, findings.Count, storedEntries);

                // A report with no findings is a real result, but it is not worth mailing: the
                // empty table says nothing the run summary above does not already record.
                if (SvcEmailGate.ShouldSend(service, findings.Count, _logger, "SvcAdAudit"))
                    await SendReportEmailAsync(service, reportType, findings, runLog, db);

                runLog.Status = "Completed";
                runLog.EndTime = DateTime.UtcNow;
                service.LastRunAt = runLog.EndTime; service.LastRunStatus = "Completed"; service.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                await BroadcastAsync(serviceId, service.Name, findings.Count, findings.Count, runLog, "Completed");
                _logger.LogInformation("SvcAdAudit: '{Name}' completed — {Count} findings ({Report})", service.Name, findings.Count, reportType);
                return runLog;
            }
            catch (OperationCanceledException)
            {
                runLog.Status = "Cancelled"; runLog.EndTime = DateTime.UtcNow;
                service.LastRunAt = runLog.EndTime; service.LastRunStatus = "Cancelled"; service.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                await BroadcastAsync(serviceId, service.Name, 0, 0, runLog, "Cancelled");
                return runLog;
            }
            catch (Exception ex)
            {
                runLog.Status = "Failed"; runLog.EndTime = DateTime.UtcNow; runLog.ErrorMessage = ex.Message;
                service.LastRunAt = runLog.EndTime; service.LastRunStatus = "Failed"; service.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                await BroadcastAsync(serviceId, service.Name, 0, 0, runLog, "Failed");
                _logger.LogError(ex, "SvcAdAudit: '{Name}' failed", service.Name);
                throw;
            }
        }

        // ── Report: privileged group members (nested) + change detection vs previous run ──
        private async Task ReportPrivilegedAsync(LdapConnection ldap, SvcService service, ServicesDbContext db,
            string scanBase, List<Finding> findings, SvcRunLog runLog, CancellationToken ct)
        {
            var groups = ResolveGroupList(service.AuditGroups) ?? DefaultPrivilegedGroups;

            // Previous run's membership snapshot for change alerts.
            var prevRunId = await db.SvcRunLogs
                .Where(r => r.SvcServiceId == service.Id && r.Id != runLog.Id && (r.Status == "Completed" || r.Status == "CompletedWithErrors"))
                .OrderByDescending(r => r.StartTime).Select(r => (long?)r.Id).FirstOrDefaultAsync(ct);
            var previous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (prevRunId != null)
            {
                var prev = await db.SvcAuditEntries
                    .Where(a => a.SvcRunLogId == prevRunId && (a.Action == "PrivilegedMember" || a.Action == "PrivilegedNew"))
                    .Select(a => a.AttributeName + "|" + a.KeyValue).ToListAsync(ct);
                foreach (var p in prev) previous.Add(p!);
            }

            var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                ct.ThrowIfCancellationRequested();
                var groupDn = ResolveGroupDn(ldap, scanBase, g);
                if (groupDn == null)
                {
                    findings.Add(new Finding("GroupNotFound", g, null, g, "group not found"));
                    continue;
                }
                foreach (var (sam, dn) in FindMembers(ldap, scanBase, groupDn, nested: true, ct))
                {
                    var key = $"{g}|{sam}";
                    current.Add(key);
                    var isNew = prevRunId != null && !previous.Contains(key);
                    findings.Add(new Finding(isNew ? "PrivilegedNew" : "PrivilegedMember", sam, dn, g, isNew ? "NEW since last run" : null));
                    runLog.UpdatedRecords++;
                }
            }
            // Members present last run but gone now → removed.
            foreach (var gone in previous.Where(p => !current.Contains(p)))
            {
                var parts = gone.Split('|', 2);
                findings.Add(new Finding("PrivilegedRemoved", parts.Length > 1 ? parts[1] : gone, null, parts[0], "REMOVED since last run"));
            }
        }

        // ── Report: direct group members (attestation) ──
        private Task ReportGroupMembersAsync(LdapConnection ldap, SvcService service, string scanBase,
            List<Finding> findings, SvcRunLog runLog, bool nested, string action, CancellationToken ct)
        {
            var groups = ResolveGroupList(service.AuditGroups);
            if (groups == null || groups.Length == 0)
                throw new InvalidOperationException("No groups configured for this report (AuditGroups)");

            foreach (var g in groups)
            {
                ct.ThrowIfCancellationRequested();
                var groupDn = ResolveGroupDn(ldap, scanBase, g);
                if (groupDn == null) { findings.Add(new Finding("GroupNotFound", g, null, g, "group not found")); continue; }
                foreach (var (sam, dn) in FindMembers(ldap, scanBase, groupDn, nested, ct))
                    findings.Add(new Finding(action, sam, dn, g, null));
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Enabled accounts carrying DONT_EXPIRE_PASSWORD — reported, and optionally cleared.
        ///
        /// Clearing the flag makes the password expire on the domain's normal schedule. That is the
        /// point, but it is also why <c>Report</c> is the default and why the exclusion group is
        /// honoured here: service accounts frequently rely on a password that never expires, and
        /// they stop working at the next expiry with no obvious connection to this run.
        ///
        /// Accounts are re-read as a set first and modified afterwards, so the LDAP page cursor is
        /// never invalidated by a modification made while paging through results.
        /// </summary>
        /// <returns>How many accounts carried the flag and were therefore examined.</returns>
        private async Task<int> ReportPasswordNeverExpiresAsync(LdapConnection ldap, SvcService service, string scanBase,
            List<Finding> findings, SvcRunLog runLog, CancellationToken ct)
        {
            var remove = string.Equals(service.PwdNeverExpiresAction?.Trim(), "Remove", StringComparison.OrdinalIgnoreCase);

            // Writing to AD demands a narrowed scope. A report may safely cover the whole domain
            // (the default), but clearing a flag across an entire directory is not something a
            // misconfigured service should be able to do.
            if (remove && string.IsNullOrWhiteSpace(service.OffboardingSearchOU))
                throw new InvalidOperationException(
                    "Removing the password-never-expires flag requires a Search OU — refusing to modify the whole domain");

            var excluded = remove
                ? ResolveExclusionMembers(ldap, service, scanBase, ct)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var filter = $"(&(objectCategory=person)(objectClass=user)(userAccountControl:{BitAndRule}:=65536)(!(userAccountControl:{BitAndRule}:=2)))";
            var found = PagedSearch(ldap, scanBase, filter,
                    new[] { "sAMAccountName", "displayName", "distinguishedName", "pwdLastSet", "userAccountControl" }, ct)
                .Select(e => (
                    Sam: GetAttr(e, "sAMAccountName") ?? e.DistinguishedName,
                    Dn: e.DistinguishedName,
                    Display: GetAttr(e, "displayName"),
                    PwdLastSet: FileTimeToDate(GetAttr(e, "pwdLastSet"))?.ToString("yyyy-MM-dd") ?? "-",
                    Uac: int.TryParse(GetAttr(e, "userAccountControl"), out var u) ? u : 0))
                .ToList();

            foreach (var a in found)
            {
                ct.ThrowIfCancellationRequested();

                if (!remove)
                {
                    findings.Add(new Finding("PwdNeverExpires", a.Sam, a.Dn, a.Display, $"pwdLastSet: {a.PwdLastSet}"));
                    continue;
                }

                if (excluded.Contains(a.Dn))
                {
                    runLog.SkippedRecords++;
                    findings.Add(new Finding("PwdNeverExpiresExcluded", a.Sam, a.Dn, a.Display, "in exclusion group"));
                    continue;
                }

                // A userAccountControl that did not parse would turn a bit-clear into a wholesale
                // overwrite of every flag on the account.
                if (a.Uac == 0)
                {
                    runLog.FailedRecords++;
                    findings.Add(new Finding("PwdNeverExpiresFailed", a.Sam, a.Dn, a.Display,
                        "could not read userAccountControl — flag left unchanged"));
                    _logger.LogWarning("SvcAdAudit: unreadable userAccountControl on {Dn} — not modified", a.Dn);
                    continue;
                }

                try
                {
                    var newUac = a.Uac & ~UF_DONT_EXPIRE_PASSWORD;
                    var mod = new DirectoryAttributeModification
                    {
                        Name = "userAccountControl",
                        Operation = DirectoryAttributeOperation.Replace
                    };
                    mod.Add(newUac.ToString());
                    ldap.SendRequest(new ModifyRequest(a.Dn, mod));

                    findings.Add(new Finding(
                        "PwdNeverExpiresRemoved", a.Sam, a.Dn,
                        Context: a.Display,
                        Detail: $"pwdLastSet: {a.PwdLastSet}",
                        Attr: "userAccountControl",
                        // pwdLastSet rides along in the "before" value: it describes the state the
                        // account was in, and NewValue is already taken by the resulting flags.
                        Old: $"{a.Uac} · pwdLastSet {a.PwdLastSet}",
                        New: newUac.ToString()));
                    _logger.LogInformation("SvcAdAudit: cleared DONT_EXPIRE_PASSWORD on {Sam}", a.Sam);
                }
                catch (Exception ex)
                {
                    runLog.FailedRecords++;
                    findings.Add(new Finding("PwdNeverExpiresFailed", a.Sam, a.Dn, a.Display, ex.Message));
                    _logger.LogError(ex, "SvcAdAudit: failed clearing DONT_EXPIRE_PASSWORD on {Sam}", a.Sam);
                }
            }

            await Task.CompletedTask;
            return found.Count;
        }

        // ── Report: currently locked accounts ──
        private Task ReportLockedAsync(LdapConnection ldap, string scanBase,
            List<Finding> findings, SvcRunLog runLog, CancellationToken ct)
        {
            // lockoutTime >= 1 means a lockout timestamp is set (locked / was locked).
            var filter = "(&(objectCategory=person)(objectClass=user)(lockoutTime>=1))";
            foreach (var e in PagedSearch(ldap, scanBase, filter,
                         new[] { "sAMAccountName", "displayName", "distinguishedName", "lockoutTime", "badPwdCount", "badPasswordTime" }, ct))
            {
                var sam = GetAttr(e, "sAMAccountName") ?? e.DistinguishedName;
                var when = FileTimeToDate(GetAttr(e, "lockoutTime"))?.ToString("yyyy-MM-dd HH:mm") ?? "-";
                var bad = GetAttr(e, "badPwdCount") ?? "0";
                var lastBad = FileTimeToDate(GetAttr(e, "badPasswordTime"))?.ToString("yyyy-MM-dd HH:mm") ?? "-";
                findings.Add(new Finding("LockedAccount", sam, e.DistinguishedName, GetAttr(e, "displayName"),
                    $"locked {when} · badPwd {bad} · lastBad {lastBad}"));
            }
            return Task.CompletedTask;
        }

        // ── Report: duplicate accounts sharing an attribute value ──
        private Task ReportDuplicatesAsync(LdapConnection ldap, SvcService service, string scanBase,
            List<Finding> findings, SvcRunLog runLog, CancellationToken ct)
        {
            var attr = string.IsNullOrWhiteSpace(service.DuplicateAttribute) ? "employeeID" : service.DuplicateAttribute!.Trim();
            var byValue = new Dictionary<string, List<(string Sam, string Dn)>>(StringComparer.OrdinalIgnoreCase);
            var filter = $"(&(objectCategory=person)(objectClass=user)({attr}=*))";

            foreach (var e in PagedSearch(ldap, scanBase, filter, new[] { "sAMAccountName", "distinguishedName", attr }, ct))
            {
                var val = GetAttr(e, attr);
                if (string.IsNullOrWhiteSpace(val)) continue;
                var sam = GetAttr(e, "sAMAccountName") ?? e.DistinguishedName;
                if (!byValue.TryGetValue(val, out var list)) { list = new(); byValue[val] = list; }
                list.Add((sam, e.DistinguishedName));
            }

            foreach (var (val, list) in byValue.Where(kv => kv.Value.Count > 1))
                foreach (var (sam, dn) in list)
                    findings.Add(new Finding("DuplicateAccount", sam, dn, $"{attr}={val}", $"{list.Count} accounts share this value"));
            return Task.CompletedTask;
        }

        // ── Report: non-human account inventory ──

        /// <summary>The user population this report measures against. Managed service accounts are not persons and are collected separately.</summary>
        private const string PersonFilter = "(&(objectCategory=person)(objectClass=user))";

        /// <summary>
        /// Inventories the accounts that belong to systems rather than to people, and reports what
        /// governing them would need: who owns each one, how old its credential is, whether it is
        /// still used, and whether it holds administrative rights.
        ///
        /// Read-only in every configuration — there is no write mode, deliberately. Disabling a
        /// service account breaks production at an hour nobody connects to this run, so the first
        /// thing this feature is allowed to do is count.
        ///
        /// The count is the point, which is why a misconfigured classifier aborts instead of
        /// returning zero: "no non-human accounts found" and "no rules configured" produce the
        /// same empty report, and only one of them is good news.
        /// </summary>
        /// <returns>Total accounts in scope — the denominator that makes the finding count mean something.</returns>
        private int ReportNonHumanInventory(LdapConnection ldap, IServiceProvider services, SvcService service,
            string scanBase, List<Finding> findings, CancellationToken ct)
        {
            var signals = new NonHumanClassifier.Signals(
                NonHumanClassifier.SplitList(service.NhiNamePatterns),
                NonHumanClassifier.SplitList(service.NhiOUs),
                NonHumanClassifier.SplitList(service.NhiGroups),
                NonHumanClassifier.ParseAttributeRules(service.NhiAttributeRules),
                service.NhiFlagNoKeyAttribute,
                service.NhiFlagPwdNeverExpires,
                service.NhiFlagHasSpn,
                service.NhiIncludeManagedServiceAccounts,
                string.IsNullOrWhiteSpace(service.NhiMatchMode) ? NonHumanClassifier.ModeAny : service.NhiMatchMode!.Trim());

            if (NonHumanClassifier.Validate(signals) is { } problem)
                throw new InvalidOperationException(problem);

            var logonAttr = NonHumanClassifier.RequireAttributeName(
                string.IsNullOrWhiteSpace(service.LastLogonAttribute) ? "lastLogonTimestamp" : service.LastLogonAttribute,
                "Last-logon attribute");
            var keyAttr = NonHumanClassifier.RequireAttributeName(
                string.IsNullOrWhiteSpace(service.ADSearchAttribute) ? "extensionAttribute2" : service.ADSearchAttribute,
                "Key attribute");

            var wanted = new List<string>
            {
                "sAMAccountName", "distinguishedName", "displayName", "description", "managedBy",
                "userAccountControl", "pwdLastSet", "whenCreated", "accountExpires", "servicePrincipalName",
                // The identity the lifecycle tracks an account by. Requested even when the
                // lifecycle is off, so that switching it on later starts from real identities
                // rather than re-discovering every account as new.
                "objectGUID",
                logonAttr, keyAttr
            };
            wanted.AddRange(signals.AttributeRules.Select(r => r.Attr));
            var attrs = wanted.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            // One entry cache shared by every signal: an account matched by three rules is read
            // from the directory once, and the labels of all three end up on its row.
            var seen = new Dictionary<string, SearchResultEntry>(StringComparer.OrdinalIgnoreCase);
            var matchedBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var sets = new List<IReadOnlyCollection<string>>();

            List<string> Collect(string label, string baseDn, string filter)
            {
                var dns = new List<string>();
                foreach (var e in PagedSearch(ldap, baseDn, filter, attrs, ct))
                {
                    seen[e.DistinguishedName] = e;
                    dns.Add(e.DistinguishedName);
                    if (!matchedBy.TryGetValue(e.DistinguishedName, out var labels))
                        matchedBy[e.DistinguishedName] = labels = new List<string>();
                    if (!labels.Contains(label)) labels.Add(label);
                }
                _logger.LogInformation("SvcAdAudit[NHI]: signal '{Signal}' matched {Count} account(s)", label, dns.Count);
                return dns;
            }

            // ── Each configured signal contributes one candidate set ──

            if (NonHumanClassifier.BuildNameFilter(signals.NamePatterns) is { } nameFilter)
                sets.Add(Collect("name", scanBase, $"(&{PersonFilter}{nameFilter})"));

            foreach (var ou in signals.OrganizationalUnits)
            {
                RequireOuInScope(ou, scanBase);
                RequireExists(ldap, ou, $"OU '{ou}'");
                sets.Add(Collect("ou", ou, PersonFilter));
            }

            foreach (var g in signals.Groups)
            {
                var groupDn = ResolveGroupDn(ldap, scanBase, g)
                    ?? throw new InvalidOperationException(
                        $"Classifier group '{g}' was not found — aborting rather than reporting a smaller inventory than the directory holds.");
                sets.Add(Collect("group", scanBase,
                    $"(&{PersonFilter}(memberOf:{ChainRule}:={LdapSanitizer.EscapeFilterValue(groupDn)}))"));
            }

            foreach (var (attr, value) in signals.AttributeRules)
                sets.Add(Collect($"{attr}", scanBase, $"(&{PersonFilter}({attr}={LdapSanitizer.EscapeFilterValue(value)}))"));

            if (signals.NoKeyAttribute)
                sets.Add(Collect("no-key-attr", scanBase, $"(&{PersonFilter}(!({keyAttr}=*)))"));

            if (signals.PasswordNeverExpires)
                sets.Add(Collect("pwd-never-expires", scanBase,
                    $"(&{PersonFilter}(userAccountControl:{BitAndRule}:={UF_DONT_EXPIRE_PASSWORD}))"));

            if (signals.HasServicePrincipalName)
                sets.Add(Collect("spn", scanBase, $"(&{PersonFilter}(servicePrincipalName=*))"));

            var matched = NonHumanClassifier.Combine(sets, signals.RequiresAll);

            // gMSA/MSA are non-human by definition, not by local convention, so they join the
            // result whatever the match mode says — an "All" intersection of site-specific naming
            // rules would otherwise drop the one population nobody has to classify.
            var managedCount = 0;
            if (signals.IncludeManagedServiceAccounts)
            {
                var managed = Collect("gMSA/MSA", scanBase,
                    $"(|(objectClass={NonHumanClassifier.GmsaClass})(objectClass={NonHumanClassifier.MsaClass}))");
                managedCount = managed.Count;
                matched.UnionWith(managed);
            }

            // Which accounts hold administrative rights. A configured group that does not resolve
            // aborts; a well-known default that does not resolve is reported and skipped, because
            // the English defaults simply do not exist on a localized directory and refusing to
            // run there would be worse than saying so.
            var privileged = ResolvePrivilegedMembers(ldap, service, scanBase, findings, ct);

            // IdentitySyncPro's own bind accounts. They are marked, not hidden: they belong in the
            // inventory — they are among the most privileged non-human identities the institution
            // has — and removing them would make the count understate exactly the accounts nobody
            // else is watching. A future service that acts on accounts reads the same registry and
            // must treat an unresolved entry as fatal instead.
            var self = _selfAccounts.Resolve(services, ldap, scanBase, ct);
            foreach (var bad in self.Unresolved)
                findings.Add(new Finding("SelfAccountUnresolved", bad.Configured, null, bad.Source,
                    $"{bad.Problem} — this account is not marked in the inventory below"));

            // The denominator. Without it "412 non-human accounts" is a number nobody can size.
            // Managed service accounts are added to it because they are added to the numerator:
            // counting them only on top would let the findings exceed the population they came from.
            var examined = PagedSearch(ldap, scanBase, PersonFilter, new[] { "distinguishedName" }, ct).Count()
                         + managedCount;

            var now = DateTime.UtcNow;
            var rows = new List<(string Action, Finding F, string Sam)>();
            var discovered = new List<NhiLifecycleReconciler.Discovered>();

            foreach (var dn in matched)
            {
                ct.ThrowIfCancellationRequested();
                if (!seen.TryGetValue(dn, out var e)) continue;   // set membership always comes with a cached entry

                var sam = GetAttr(e, "sAMAccountName") ?? dn;
                var uac = int.TryParse(GetAttr(e, "userAccountControl"), out var u) ? u : 0;
                var enabled = (uac & UF_ACCOUNTDISABLE) == 0;
                var owner = OwnerName(GetAttr(e, "managedBy"));
                var pwdSet = FileTimeToDate(GetAttr(e, "pwdLastSet"));
                var lastLogon = FileTimeToDate(GetAttr(e, logonAttr));
                var created = ParseAdGeneralizedTime(GetAttr(e, "whenCreated"));
                var expires = FileTimeToDate(GetAttr(e, "accountExpires"));
                var isPrivileged = privileged.Contains(dn);

                var risks = NonHumanClassifier.EvaluateRisks(
                    new NonHumanClassifier.AccountFacts(
                        Enabled: enabled,
                        HasOwner: owner != null,
                        Privileged: isPrivileged,
                        PasswordNeverExpires: (uac & UF_DONT_EXPIRE_PASSWORD) != 0,
                        PasswordLastSet: pwdSet,
                        LastActivity: lastLogon ?? created,
                        Expires: expires),
                    service.NhiCredentialMaxAgeDays, service.NhiDormantDays, now);

                var signalList = matchedBy.TryGetValue(dn, out var l) ? string.Join("+", l) : "-";
                var detail = new StringBuilder($"[{signalList}]");
                // Stated first, because it changes what the rest of the row means: a stale
                // credential on this account is IdentitySyncPro's own stale credential.
                if (self.Dns.Contains(dn)) detail.Append(" · ⚙ IdentitySyncPro bind account");
                detail.Append($" · pwd {Day(pwdSet)}");
                detail.Append(lastLogon != null ? $" · logon {Day(lastLogon)}" : $" · logon never (created {Day(created)})");
                detail.Append($" · expires {(expires == null ? "never" : Day(expires))}");
                if (GetAttr(e, "description") is { Length: > 0 } desc) detail.Append($" · {desc}");
                if (risks.Count > 0) detail.Append($" · ⚠ {string.Join(", ", risks)}");

                discovered.Add(new NhiLifecycleReconciler.Discovered(
                    ObjectGuid: GetGuidAttr(e, "objectGUID") ?? string.Empty,
                    Account: sam,
                    DistinguishedName: dn,
                    DisplayName: GetAttr(e, "displayName"),
                    Description: GetAttr(e, "description"),
                    Signals: signalList,
                    Privileged: isPrivileged,
                    Enabled: enabled,
                    DirectoryOwner: owner,
                    IsSelfAccount: self.Dns.Contains(dn)));

                var action = NonHumanClassifier.ChooseAction(isPrivileged, owner != null);
                rows.Add((action,
                    new Finding(action, sam, dn,
                        Context: owner ?? GetAttr(e, "displayName"), Detail: detail.ToString()),
                    sam));
            }

            // Most urgent first: the email shows only the first 300 rows, so the ordering decides
            // which findings a reader ever sees.
            foreach (var r in rows.OrderBy(r => Severity(r.Action)).ThenBy(r => r.Sam, StringComparer.OrdinalIgnoreCase))
                findings.Add(r.F);

            var nonHuman = rows.Count;
            _logger.LogInformation(
                "SvcAdAudit[NHI]: {Matched} non-human account(s) of {Examined} in scope ({Priv} privileged, {Unowned} unowned)",
                nonHuman, examined,
                rows.Count(r => r.Action == NonHumanClassifier.ActionPrivileged),
                rows.Count(r => r.Action == NonHumanClassifier.ActionUnowned));

            // A classifier that claims most of the directory is a rule that is too broad, not a
            // domain built out of robots. Saying so beats letting the number stand unchallenged.
            if (examined > 0 && nonHuman * 2 > examined)
                _logger.LogWarning(
                    "SvcAdAudit[NHI]: the classifier matched {Matched} of {Examined} accounts in scope — review the rules before treating this as an inventory",
                    nonHuman, examined);

            // The lifecycle rides on the scan that just ran: same population, same classifier, one
            // pass over the directory. Off unless this service was explicitly switched to it.
            if (service.NhiLifecycleEnabled)
                RunLifecycle(ldap, services, service, discovered, self, scanBase, findings, ct);

            return examined;
        }

        /// <summary>
        /// Advances the lifecycle for everything the scan just found, and carries out whatever this
        /// service is allowed to carry out.
        ///
        /// Deliberately at the end of the inventory: it rides on the population that scan produced
        /// rather than reading the directory a second time, so the accounts governed are exactly the
        /// accounts reported, with no window between the two in which they could differ.
        /// </summary>
        private void RunLifecycle(LdapConnection ldap, IServiceProvider services, SvcService service,
            List<NhiLifecycleReconciler.Discovered> discovered,
            SelfAccountRegistry.SelfAccounts self,
            string scanBase,
            List<Finding> findings, CancellationToken ct)
        {
            var config = new NhiLifecyclePolicy.LifecycleConfig(
                Enabled: true,
                ClaimDays: service.NhiClaimDays,
                AttestationDays: service.NhiAttestationDays,
                GraceDays: service.NhiAttestationGraceDays,
                Enforcement: string.IsNullOrWhiteSpace(service.NhiQuarantineMode)
                    ? GovNhiEnforcement.Report
                    : service.NhiQuarantineMode!.Trim(),
                MaxQuarantinePercent: service.NhiMaxQuarantinePercent);

            // Thrown, not logged and skipped: a lifecycle running on settings nobody validated is
            // how a claim window of zero quarantines a domain.
            if (NhiLifecyclePolicy.ValidateConfig(config) is { } bad)
                throw new InvalidOperationException($"Non-human lifecycle settings are invalid: {bad}");

            var reconciler = services.GetRequiredService<NhiLifecycleReconciler>();
            var result = reconciler.ReconcileAsync(service.Id, discovered, config, DateTime.UtcNow, ct)
                                   .GetAwaiter().GetResult();

            findings.Add(new Finding("NhiLifecycle", $"{result.Tracked} tracked", null, null,
                $"{result.Added} new · {result.Retired} gone from the directory · " +
                $"{result.Quarantine.Count} quarantined · {result.AttestationOverdue.Count} attestation overdue"));

            foreach (var a in result.AttestationOverdue)
                findings.Add(new Finding("NhiAttestationOverdue", a.Account, a.DistinguishedName, a.OwnerUsername,
                    $"not re-attested since {a.LastAttestedUtc:yyyy-MM-dd} — {service.NhiAttestationGraceDays} day(s) of grace remain"));

            // Withheld, never hidden. An unclaimed bind account is a real gap, and the accounts this
            // system runs on are exactly the ones nobody else is watching.
            foreach (var (a, reason) in result.WithheldQuarantine)
                findings.Add(new Finding("NhiQuarantineWithheld", a.Account, a.DistinguishedName, a.OwnerUsername,
                    $"met the criteria for quarantine ({reason}) and was spared — IdentitySyncPro bind account. It still has no owner."));

            if (result.Blocked is { } blocked)
            {
                findings.Add(new Finding("NhiQuarantineStopped", service.Name, null, null, blocked));
                _logger.LogWarning("SvcAdAudit[NHI]: {Blocked}", blocked);
                return;
            }

            if (result.Quarantine.Count == 0) return;

            // ── may this run write to the directory at all? ──
            var right = NhiLifecyclePolicy.MayEnforce(config.Enforcement, self.Unresolved.Count);
            if (!right.Allowed)
            {
                foreach (var a in result.Quarantine)
                {
                    a.QuarantineEffect = GovNhiQuarantineEffects.None;
                    a.QuarantineError = right.Reason;
                }
                reconciler.SaveEffectsAsync(ct).GetAwaiter().GetResult();

                findings.Add(new Finding("NhiEnforcementRefused", service.Name, null, null, right.Reason));
                _logger.LogWarning("SvcAdAudit[NHI]: enforcement refused for this run — {Reason}", right.Reason);
                return;
            }

            if (!GovNhiEnforcement.TouchesDirectory(config.Enforcement))
            {
                foreach (var a in result.Quarantine)
                    findings.Add(new Finding("NhiQuarantined", a.Account, a.DistinguishedName, a.OwnerUsername,
                        $"{a.QuarantineReason} — recorded only; this service does not act on the directory"));
                return;
            }

            // The institution's own do-not-touch list. Not replaced by the self-account registry: it
            // covers break-glass accounts and other systems' service accounts, which this system's
            // settings cannot know about. Fails closed — an unresolvable group aborts the run.
            var excluded = ResolveExclusionMembers(ldap, service, scanBase, ct);
            var privilegedGroups = string.Equals(config.Enforcement, GovNhiEnforcement.RemovePrivilege, StringComparison.OrdinalIgnoreCase)
                ? ResolvePrivilegedGroupDns(ldap, service, scanBase, ct)
                : new List<string>();

            foreach (var a in result.Quarantine)
            {
                ct.ThrowIfCancellationRequested();

                if (excluded.Contains(a.DistinguishedName))
                {
                    a.QuarantineEffect = GovNhiQuarantineEffects.None;
                    a.QuarantineError = "in the exclusion group — recorded, and the directory was not touched";
                    findings.Add(new Finding("NhiQuarantineWithheld", a.Account, a.DistinguishedName, a.OwnerUsername,
                        "in the exclusion group — quarantine recorded, no directory action taken"));
                    continue;
                }

                try
                {
                    if (string.Equals(config.Enforcement, GovNhiEnforcement.Disable, StringComparison.OrdinalIgnoreCase))
                    {
                        DisableAccount(ldap, a.DistinguishedName);
                        a.QuarantineEffect = GovNhiQuarantineEffects.Disabled;
                        a.Enabled = false;
                        findings.Add(new Finding("NhiQuarantineDisabled", a.Account, a.DistinguishedName, a.OwnerUsername,
                            $"{a.QuarantineReason} — account disabled", Attr: "userAccountControl"));
                    }
                    else
                    {
                        var removed = RemoveFromPrivilegedGroups(ldap, a.DistinguishedName, privilegedGroups);
                        a.QuarantineEffect = GovNhiQuarantineEffects.PrivilegeRemoved;
                        a.Privileged = false;
                        findings.Add(new Finding("NhiQuarantinePrivilegeRemoved", a.Account, a.DistinguishedName, a.OwnerUsername,
                            $"{a.QuarantineReason} — removed from {removed} administrative group(s)"));
                    }

                    a.QuarantineError = null;
                    _logger.LogWarning("SvcAdAudit[NHI]: quarantined '{Account}' ({Mode}) — {Reason}",
                        a.Account, config.Enforcement, a.QuarantineReason);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // The decision stands and the failure is recorded against it. A quarantine that
                    // silently did nothing is worse than one that says it could not.
                    a.QuarantineEffect = GovNhiQuarantineEffects.Failed;
                    a.QuarantineError = ex.Message;
                    findings.Add(new Finding("NhiQuarantineFailed", a.Account, a.DistinguishedName, a.OwnerUsername, ex.Message));
                    _logger.LogError(ex, "SvcAdAudit[NHI]: could not quarantine '{Account}'", a.Account);
                }
            }

            reconciler.SaveEffectsAsync(ct).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Sets the disable bit without disturbing the rest of userAccountControl.
        ///
        /// The account is re-read immediately before the write rather than trusting the value from
        /// the scan: minutes may have passed, and a stale flag word written back as a Replace would
        /// undo every other change made in between. An unreadable value aborts this one account
        /// instead of writing a guess over all of its flags.
        /// </summary>
        private static void DisableAccount(LdapConnection ldap, string dn)
        {
            var resp = (SearchResponse)ldap.SendRequest(
                new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, "userAccountControl"));

            if (resp.Entries.Count == 0)
                throw new InvalidOperationException("the account was not found at the moment of the write");

            if (!int.TryParse(GetAttr(resp.Entries[0], "userAccountControl"), out var uac) || uac == 0)
                throw new InvalidOperationException(
                    "userAccountControl could not be read — refusing to write a flag word that would overwrite every other flag");

            var mod = new DirectoryAttributeModification
            {
                Name = "userAccountControl",
                Operation = DirectoryAttributeOperation.Replace
            };
            mod.Add((uac | UF_ACCOUNTDISABLE).ToString());
            ldap.SendRequest(new ModifyRequest(dn, mod));
        }

        /// <summary>
        /// Removes the account from the administrative groups it directly belongs to.
        ///
        /// Direct membership only. Taking somebody out of a nested group would remove the access
        /// from everybody else in that group as well — a far larger act than the one authorised here.
        /// </summary>
        private static int RemoveFromPrivilegedGroups(LdapConnection ldap, string dn, IReadOnlyList<string> groupDns)
        {
            var removed = 0;
            foreach (var groupDn in groupDns)
            {
                var resp = (SearchResponse)ldap.SendRequest(new SearchRequest(groupDn,
                    $"(member={LdapSanitizer.EscapeFilterValue(dn)})", SearchScope.Base, "distinguishedName"));
                if (resp.Entries.Count == 0) continue;

                var mod = new DirectoryAttributeModification
                {
                    Name = "member",
                    Operation = DirectoryAttributeOperation.Delete
                };
                mod.Add(dn);
                ldap.SendRequest(new ModifyRequest(groupDn, mod));
                removed++;
            }
            return removed;
        }

        /// <summary>The administrative groups themselves — the report only ever needed their members.</summary>
        private List<string> ResolvePrivilegedGroupDns(LdapConnection ldap, SvcService service, string scanBase, CancellationToken ct)
        {
            var groups = ResolveGroupList(service.AuditGroups) ?? DefaultPrivilegedGroups;
            var dns = new List<string>();

            foreach (var g in groups)
            {
                ct.ThrowIfCancellationRequested();
                if (ResolveGroupDn(ldap, scanBase, g) is { } dn) dns.Add(dn);
            }
            return dns;
        }

        private static int Severity(string action) => action switch
        {
            NonHumanClassifier.ActionPrivileged => 0,
            NonHumanClassifier.ActionUnowned => 1,
            _ => 2
        };

        /// <summary>
        /// DNs holding administrative rights, via nested membership of the audited groups.
        /// Explicitly configured groups must exist; well-known English defaults may not, and are
        /// reported as findings rather than aborting a run on a localized directory.
        /// </summary>
        private HashSet<string> ResolvePrivilegedMembers(LdapConnection ldap, SvcService service, string scanBase,
            List<Finding> findings, CancellationToken ct)
        {
            var configured = ResolveGroupList(service.AuditGroups);
            var groups = configured ?? DefaultPrivilegedGroups;
            var dns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var g in groups)
            {
                ct.ThrowIfCancellationRequested();
                var groupDn = ResolveGroupDn(ldap, scanBase, g);
                if (groupDn == null)
                {
                    if (configured != null)
                        throw new InvalidOperationException(
                            $"Privileged group '{g}' was not found — aborting rather than reporting every account as unprivileged.");

                    findings.Add(new Finding("GroupNotFound", g, null, g,
                        "well-known group not found — privileged accounts in it are not flagged (localized AD? set the local names)"));
                    _logger.LogWarning("SvcAdAudit[NHI]: well-known privileged group '{Group}' not found", g);
                    continue;
                }

                foreach (var e in PagedSearch(ldap, scanBase,
                             $"(&(objectClass=user)(memberOf:{ChainRule}:={LdapSanitizer.EscapeFilterValue(groupDn)}))",
                             new[] { "distinguishedName" }, ct))
                    dns.Add(e.DistinguishedName);
            }
            return dns;
        }

        /// <summary>
        /// A classifier OU outside the scanned scope would silently pull in accounts the run
        /// claims not to cover. Compared with whitespace removed, because a DN copied out of ADUC
        /// and one typed by hand differ by the spaces after their commas and by nothing else.
        /// </summary>
        private static void RequireOuInScope(string ou, string scanBase)
        {
            static string Norm(string s) => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
            if (!Norm(ou).EndsWith(Norm(scanBase), StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Classifier OU '{ou}' is not inside the scanned scope '{scanBase}' — widen the scope or correct the OU.");
        }

        /// <summary>Confirms a DN resolves. A misspelled OU otherwise contributes zero accounts and reads as a clean one.</summary>
        private static void RequireExists(LdapConnection ldap, string dn, string label)
        {
            try
            {
                var resp = (SearchResponse)ldap.SendRequest(
                    new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, "distinguishedName"));
                if (resp.Entries.Count > 0) return;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"{label} could not be read: {ex.Message}", ex);
            }
            throw new InvalidOperationException($"{label} was not found.");
        }

        /// <summary>The owner's readable name from a managedBy DN, or null when nobody is named — which is the finding, not a blank.</summary>
        private static string? OwnerName(string? managedByDn)
        {
            if (string.IsNullOrWhiteSpace(managedByDn)) return null;
            var (leaf, _) = ActiveDirectoryConnector.SplitDn(managedByDn!);
            if (string.IsNullOrWhiteSpace(leaf)) return managedByDn;
            var eq = leaf.IndexOf('=');
            return eq >= 0 && eq < leaf.Length - 1 ? leaf[(eq + 1)..] : leaf;
        }

        private static string Day(DateTime? d) => d?.ToString("yyyy-MM-dd") ?? "-";

        /// <summary>AD generalized time (yyyyMMddHHmmss.0Z), as whenCreated carries it.</summary>
        private static DateTime? ParseAdGeneralizedTime(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var s = value!.Split('.')[0].TrimEnd('Z');
            return DateTime.TryParseExact(s, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
                ? dt
                : null;
        }

        /// <summary>
        /// The run-summary note.
        ///
        /// The inventory gets its own wording because the shared one is wrong for it in the way
        /// that matters: an empty result there means "nothing met the criteria", and here it far
        /// more often means the classifier never matched anything at all.
        /// </summary>
        internal static string SummaryNote(string reportType, string scanBase, int examined, int findingCount)
        {
            if (reportType != "NonHumanInventory")
                return $"Report: {reportType}; searched under {scanBase}; {examined} account(s) matched the report's filter." +
                       (findingCount == 0 ? " No findings — nothing in scope met the criteria." : "");

            return $"Report: {reportType}; searched under {scanBase}; {findingCount} non-human account(s) out of " +
                   $"{examined} account(s) in scope." +
                   (findingCount == 0
                       ? " No account matched the classifier — check the classifier rules before reading this as a clean directory."
                       : "");
        }

        internal const string CellStyle = "padding:6px;border:1px solid #eee";
        internal const int RowCap = 300;

        /// <summary>
        /// The report table's columns, in order. The header cells and the body rows are built from
        /// this one list and nowhere else, because they are a single unit: a row carrying one cell
        /// more or fewer than the header renders as a perfectly good table that reports the wrong
        /// value under every column past the mismatch, and neither the build nor the run would
        /// notice. Add a column here and in <see cref="BuildRows"/> together.
        /// </summary>
        internal static string[] HeaderLabels(string reportType) => new[]
        {
            "حساب AD", ContextHeader(reportType), "النوع", "تفاصيل", "الموقع"
        };

        /// <summary>
        /// Renders the header row. Spaces inside a label become non-breaking: "حساب AD" puts a
        /// Latin run against an Arabic one, and at that boundary mail clients were collapsing the
        /// separator so the header arrived as "حسابAD". Header labels are short by nature, so
        /// forbidding a wrap inside them costs nothing.
        /// </summary>
        internal static string BuildHeaderCells(string reportType) =>
            string.Concat(HeaderLabels(reportType)
                .Select(h => $"<th style='{CellStyle}'>{h.Replace(" ", "&nbsp;")}</th>"));

        internal static string BuildRows(IEnumerable<Finding> findings)
        {
            var rows = new StringBuilder();
            foreach (var f in findings.Take(RowCap))
                rows.Append($"<tr><td style='{CellStyle}'>{f.Sam}</td>" +
                            $"<td style='{CellStyle}'>{OrDash(f.Context)}</td>" +
                            $"<td style='{CellStyle}'>{ActionCell(f.Action)}</td>" +
                            $"<td style='{CellStyle}'>{OrDash(f.Detail)}</td>" +
                            $"<td style='{CellStyle};direction:ltr;font-size:12px'>{LocationOf(f.Dn)}</td></tr>");
            return rows.ToString();
        }

        /// <summary>
        /// Arabic label for a finding's action, matching the wording on the service results screen
        /// — the same run read in two places should not read as two different things.
        ///
        /// An action with no label returns null rather than an invented one: the caller then shows
        /// the raw name, which is honest about being unlabelled. This mirrors the results screen,
        /// where an unmapped action shows its own name instead of an empty cell.
        /// </summary>
        internal static string? ActionLabel(string action) => action switch
        {
            "PwdNeverExpires" => "كلمة مرور لا تنتهي",
            "PwdNeverExpiresRemoved" => "أُزيل «لا تنتهي»",
            "PwdNeverExpiresExcluded" => "مستثنى من الإزالة",
            "PwdNeverExpiresFailed" => "فشل الإزالة",
            "PrivilegedMember" => "عضو إداري",
            "PrivilegedNew" => "عضو إداري جديد",
            "PrivilegedRemoved" => "أُزيل من الإداريين",
            "DuplicateAccount" => "حساب مكرّر",
            "LockedAccount" => "حساب مقفل",
            "AccessMember" => "عضو مجموعة",
            "GroupNotFound" => "مجموعة غير موجودة",
            NonHumanClassifier.ActionPrivileged => "غير بشري بصلاحيات إدارية",
            NonHumanClassifier.ActionUnowned => "غير بشري بلا مالك",
            NonHumanClassifier.ActionOwned => "غير بشري له مالك",
            "SelfAccountUnresolved" => "حساب ربط للنظام لم يُحلّ",
            _ => null
        };

        /// <summary>
        /// The action as it appears in the email: the Arabic label with the programmatic name kept
        /// underneath it in small type. The raw name is what the audit log filters and support
        /// conversations use, so dropping it would cost more than the space it takes.
        /// </summary>
        private static string ActionCell(string action)
        {
            var label = ActionLabel(action);
            return label == null
                ? action
                : $"{label}<div style='color:#8a8a8a;font-size:11px;direction:ltr'>{action}</div>";
        }

        /// <summary>
        /// Header for the second column. The finding's <c>Context</c> carries a different thing in
        /// each report — the display name for the account reports, the group for the membership
        /// ones, the shared value for duplicates — so a single fixed header would be a wrong label
        /// over correct data on four of the five reports.
        /// </summary>
        private static string ContextHeader(string reportType) => reportType switch
        {
            "PasswordNeverExpires" or "LockedAccounts" => "الاسم",
            "DuplicateAccounts" => "القيمة المكررة",
            // The owner is the column this report exists for: it is the one fact the directory
            // cannot supply and the one an attestation cycle cannot start without.
            "NonHumanInventory" => "المالك (managedBy)",
            _ => "المجموعة",   // PrivilegedGroups, AccessCertification
        };

        /// <summary>
        /// Where the account lives: the DN with its leaf stripped. The full DN would only repeat
        /// the account name already shown in the first column, and it is the OU that answers the
        /// question the column is there for.
        ///
        /// Some findings legitimately have no DN — a group that could not be resolved, a member
        /// that disappeared since the previous run — and get a dash rather than a blank cell.
        /// </summary>
        private static string LocationOf(string? dn)
        {
            if (string.IsNullOrWhiteSpace(dn)) return Dash;
            var (_, parent) = ActiveDirectoryConnector.SplitDn(dn!);
            return string.IsNullOrEmpty(parent) ? dn! : parent;
        }

        private const string Dash = "—";

        /// <summary>
        /// Not every finding has a display name or a detail — a group member carries no detail, an
        /// account may have no displayName set. An empty cell reads as data that went missing;
        /// a dash reads as "there is none", which is what actually happened.
        /// </summary>
        private static string OrDash(string? value) =>
            string.IsNullOrWhiteSpace(value) ? Dash : value!;

        // ── Email summary ──
        private async Task SendReportEmailAsync(SvcService service, string reportType, List<Finding> findings, SvcRunLog runLog, ServicesDbContext db)
        {
            try
            {
                var title = ReportTitle(reportType);
                var subject = (service.EmailSubject ?? "تقرير تدقيق AD — {REPORT}: {COUNT}")
                    .Replace("{REPORT}", title).Replace("{COUNT}", findings.Count.ToString());

                // Same convention as the table's type column: Arabic first, raw name kept alongside.
                var byAction = findings.GroupBy(f => f.Action).OrderByDescending(g => g.Count())
                    .Select(g => ActionLabel(g.Key) is { } label
                        ? $"{label} ({g.Key}): {g.Count()}"
                        : $"{g.Key}: {g.Count()}");
                var rows = BuildRows(findings);
                var more = findings.Count > RowCap ? $"<p style='color:#6c757d'>… والقائمة الكاملة ({findings.Count}) في سجلّ تدقيق الخدمة (قابلة للتصدير Excel).</p>" : "";

                var body = service.EmailBodyTemplate;
                if (string.IsNullOrWhiteSpace(body))
                {
                    body = $@"
<div dir='rtl' style='font-family: Segoe UI, Tahoma, Arial; padding: 20px; background: #f8f9fa; border-radius: 8px;'>
    <h2 style='color:#0d6efd;border-bottom:2px solid #0d6efd;padding-bottom:10px;'>🛡️ {{REPORT}}</h2>
    <p>إجمالي النتائج: <strong>{{COUNT}}</strong> — {string.Join(" · ", byAction)}</p>
    <table style='width:100%;border-collapse:collapse;margin-top:12px;font-size:13px;'>
        <thead><tr style='background:#f1f1f1'>{BuildHeaderCells(reportType)}</tr></thead><tbody>{{ROWS}}</tbody>
    </table>
    {{MORE}}
    <p style='margin-top:15px;color:#6c757d;font-size:12px;'>تم الإرسال تلقائياً بواسطة IdentitySyncPro — خدمة «{service.Name}» — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC</p>
</div>";
                }
                body = body.Replace("{REPORT}", title).Replace("{COUNT}", findings.Count.ToString())
                           .Replace("{ROWS}", rows.ToString()).Replace("{MORE}", more);

                var result = await _emailService.SendAsync(new EmailMessage { To = service.NotificationEmail!, Subject = subject, Body = body, IsHtml = true });
                db.SvcAuditEntries.Add(new SvcAuditEntry
                {
                    SvcRunLogId = runLog.Id, SvcServiceId = service.Id, Timestamp = DateTime.UtcNow,
                    Action = result.Success ? "EmailSent" : "EmailFailed", KeyValue = "(report)", ADIdentity = "(report)",
                    ErrorMessage = result.Success ? null : result.Error
                });
            }
            catch (Exception ex)
            {
                db.SvcAuditEntries.Add(new SvcAuditEntry
                {
                    SvcRunLogId = runLog.Id, SvcServiceId = service.Id, Timestamp = DateTime.UtcNow,
                    Action = "EmailFailed", KeyValue = "(report)", ErrorMessage = ex.Message
                });
                _logger.LogError(ex, "SvcAdAudit: report email failed");
            }
        }

        // ── LDAP helpers ──
        private static string[]? ResolveGroupList(string? csv) =>
            string.IsNullOrWhiteSpace(csv) ? null
                : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        /// <summary>
        /// DNs of the exclusion group's members, including nested membership.
        ///
        /// Fails closed: a group that cannot be resolved aborts the run rather than proceeding with
        /// an empty exclusion set, because an empty set here means "exempt nobody" — the accounts
        /// most likely to be in it are exactly the ones that must not be touched.
        /// </summary>
        private HashSet<string> ResolveExclusionMembers(LdapConnection ldap, SvcService service, string baseDn, CancellationToken ct)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var groupNameOrDn = service.OffboardingExclusionGroup;
            if (string.IsNullOrWhiteSpace(groupNameOrDn)) return set;

            try
            {
                var groupDn = groupNameOrDn.Contains('=') ? groupNameOrDn : ResolveGroupDn(ldap, baseDn, groupNameOrDn);
                if (groupDn == null) throw new InvalidOperationException($"Exclusion group '{groupNameOrDn}' not found");

                var req = new SearchRequest(baseDn,
                    $"(&(objectCategory=person)(objectClass=user)(memberOf:{ChainRule}:={LdapSanitizer.EscapeFilterValue(groupDn)}))",
                    SearchScope.Subtree, "distinguishedName");
                var page = new PageResultRequestControl(LdapPageSize);
                req.Controls.Add(page);

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var resp = (SearchResponse)ldap.SendRequest(req);
                    foreach (SearchResultEntry e in resp.Entries) set.Add(e.DistinguishedName);
                    var pr = resp.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
                    if (pr == null || pr.Cookie.Length == 0) break;
                    page.Cookie = pr.Cookie;
                }

                _logger.LogInformation("SvcAdAudit: exclusion group '{Group}' resolved to {Count} member(s)", groupNameOrDn, set.Count);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not resolve exclusion group '{groupNameOrDn}' — aborting for safety: {ex.Message}", ex);
            }

            return set;
        }

        private static string? ResolveGroupDn(LdapConnection ldap, string baseDn, string nameOrDn)
        {
            if (nameOrDn.Contains('=')) return nameOrDn; // already a DN
            var req = new SearchRequest(baseDn, $"(&(objectClass=group)(sAMAccountName={LdapSanitizer.EscapeFilterValue(nameOrDn)}))",
                SearchScope.Subtree, "distinguishedName");
            var resp = (SearchResponse)ldap.SendRequest(req);
            return resp.Entries.Count > 0 ? resp.Entries[0].DistinguishedName : null;
        }

        private IEnumerable<(string Sam, string Dn)> FindMembers(LdapConnection ldap, string baseDn, string groupDn, bool nested, CancellationToken ct)
        {
            var rule = nested ? $":{ChainRule}:" : "";
            var filter = $"(&(objectCategory=person)(objectClass=user)(memberOf{rule}={LdapSanitizer.EscapeFilterValue(groupDn)}))";
            foreach (var e in PagedSearch(ldap, baseDn, filter, new[] { "sAMAccountName", "distinguishedName" }, ct))
                yield return (GetAttr(e, "sAMAccountName") ?? e.DistinguishedName, e.DistinguishedName);
        }

        private static IEnumerable<SearchResultEntry> PagedSearch(LdapConnection ldap, string baseDn, string filter, string[] attrs, CancellationToken ct)
        {
            var request = new SearchRequest(baseDn, filter, SearchScope.Subtree, attrs);
            var page = new PageResultRequestControl(LdapPageSize);
            request.Controls.Add(page);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var response = (SearchResponse)ldap.SendRequest(request);
                foreach (SearchResultEntry e in response.Entries) yield return e;
                var pr = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
                if (pr == null || pr.Cookie.Length == 0) break;
                page.Cookie = pr.Cookie;
            }
        }

        private static DateTime? FileTimeToDate(string? raw)
        {
            if (long.TryParse(raw, out var ft) && ft > 0)
            {
                try { return DateTime.FromFileTimeUtc(ft); } catch { return null; }
            }
            return null;
        }

        private static string? GetAttr(SearchResultEntry e, string name)
        {
            if (e.Attributes.Contains(name)) { var a = e.Attributes[name]; if (a.Count > 0) return a[0]?.ToString(); }
            return null;
        }

        /// <summary>
        /// Reads a binary attribute as a GUID.
        ///
        /// Separate from <see cref="GetAttr"/> because that one calls <c>ToString()</c> on whatever
        /// the directory returned, and for a binary attribute that is the literal text
        /// "System.Byte[]" — the same value for every account in the domain. Used as an identity
        /// key it would collapse the entire tracked population onto one row, which is a failure
        /// that reads as success right up until somebody asks why there is one service account.
        /// </summary>
        private static string? GetGuidAttr(SearchResultEntry e, string name)
        {
            if (!e.Attributes.Contains(name)) return null;

            var attr = e.Attributes[name];
            if (attr.Count == 0) return null;

            try
            {
                var values = attr.GetValues(typeof(byte[]));
                if (values.Length > 0 && values[0] is byte[] { Length: 16 } bytes)
                    return new Guid(bytes).ToString();
            }
            catch (Exception)
            {
                // Swallowed here and fatal at the caller: an account with no stable identity cannot
                // be tracked, and the run refuses rather than inventing a key.
            }

            return null;
        }

        private static string ReportTitle(string reportType) => reportType switch
        {
            "PasswordNeverExpires" => "كلمة المرور لا تنتهي",
            "DuplicateAccounts" => "الحسابات المكرّرة",
            "LockedAccounts" => "الحسابات المقفلة",
            "AccessCertification" => "شهادة الوصول",
            "NonHumanInventory" => "جرد الحسابات غير البشرية",
            _ => "المجموعات الإدارية"
        };

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
            catch (Exception ex) { _logger.LogDebug(ex, "SvcAdAudit: progress broadcast failed"); }
        }
    }
}
