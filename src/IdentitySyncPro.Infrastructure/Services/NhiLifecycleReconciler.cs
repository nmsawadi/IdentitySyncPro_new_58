using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Models.Governance;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Turns one inventory scan into a tracked population, and moves every account in it to the
    /// state the rules say it should be in.
    ///
    /// This is the join between a report that forgets and a lifecycle that remembers. Everything it
    /// decides comes from <see cref="NhiLifecyclePolicy"/>; what lives here is the reconciliation
    /// itself — matching what the directory returned against what is already tracked, noticing what
    /// has disappeared, and refusing to act when the numbers say the classifier is wrong.
    ///
    /// <para>It never touches the directory. The accounts it decides to quarantine are returned to
    /// the caller, which holds the connection and applies the guard on writing.</para>
    /// </summary>
    public class NhiLifecycleReconciler
    {
        private readonly GovernanceDbContext _gov;
        private readonly ILogger<NhiLifecycleReconciler> _logger;

        public NhiLifecycleReconciler(GovernanceDbContext gov, ILogger<NhiLifecycleReconciler> logger)
        {
            _gov = gov;
            _logger = logger;
        }

        /// <summary>
        /// One account as this run found it in the directory.
        ///
        /// <paramref name="ObjectGuid"/> is what makes it the <i>same</i> account as last time.
        /// Nothing here assumes what kind of institution this is or what its people are called —
        /// an owner is an opaque identifier, and every threshold is configuration.
        /// </summary>
        public sealed record Discovered(
            string ObjectGuid,
            string Account,
            string DistinguishedName,
            string? DisplayName,
            string? Description,
            string? Signals,
            bool Privileged,
            bool Enabled,
            string? DirectoryOwner,
            bool IsSelfAccount);

        /// <param name="Quarantine">من تقرّر حجرهم — يُنفَّذ عند المُستدعي لا هنا</param>
        /// <param name="Blocked">سبب إيقاف الحجر كله، إن أوقف</param>
        public sealed record Result(
            int Tracked,
            int Added,
            int Retired,
            int Returned,
            IReadOnlyList<GovNhiAccount> Quarantine,
            IReadOnlyList<GovNhiAccount> AttestationOverdue,
            IReadOnlyList<(GovNhiAccount Account, string Reason)> WithheldQuarantine,
            string? Blocked);

        /// <summary>
        /// Persists what the caller recorded about the directory writes it carried out.
        ///
        /// The accounts handed back are entities this context is still tracking, so the caller sets
        /// the effect on them and this writes it. Kept as an explicit call rather than saving on
        /// every field change, so a run that fails halfway leaves one clear picture of what was
        /// attempted rather than a partial one written a row at a time.
        /// </summary>
        public Task SaveEffectsAsync(CancellationToken ct = default) => _gov.SaveChangesAsync(ct);

        public async Task<Result> ReconcileAsync(
            int serviceId,
            IReadOnlyList<Discovered> found,
            NhiLifecyclePolicy.LifecycleConfig config,
            DateTime nowUtc,
            CancellationToken ct = default)
        {
            if (NhiLifecyclePolicy.ValidateConfig(config) is { } bad)
                throw new InvalidOperationException($"Non-human lifecycle configuration is invalid: {bad}");

            // An account that reached here without an identity would be tracked under an empty key,
            // and every such account would collapse onto the same row. Refused rather than skipped:
            // skipping shrinks the population silently, and the population is the denominator the
            // mass-quarantine ceiling is measured against.
            var anonymous = found.Where(f => string.IsNullOrWhiteSpace(f.ObjectGuid)).ToList();
            if (anonymous.Count > 0)
                throw new InvalidOperationException(
                    $"{anonymous.Count} account(s) were returned without a readable objectGUID " +
                    $"(first: {anonymous[0].Account}). The bind account may lack permission to read it. " +
                    "Tracking them would give several accounts the same identity, so the run stops instead.");

            var tracked = await _gov.NhiAccounts
                .Where(a => a.ServiceId == serviceId)
                .ToListAsync(ct);

            var byGuid = tracked.ToDictionary(a => a.ObjectGuid, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var added = 0;

            foreach (var f in found)
            {
                ct.ThrowIfCancellationRequested();
                seen.Add(f.ObjectGuid);

                if (!byGuid.TryGetValue(f.ObjectGuid, out var row))
                {
                    row = new GovNhiAccount
                    {
                        ObjectGuid = f.ObjectGuid,
                        ServiceId = serviceId,
                        FirstSeenUtc = nowUtc,
                        State = GovNhiStates.Discovered,
                        ClaimDueUtc = NhiLifecyclePolicy.ClaimDeadline(nowUtc, config)
                    };
                    _gov.NhiAccounts.Add(row);
                    byGuid[f.ObjectGuid] = row;
                    tracked.Add(row);
                    added++;
                }

                // Facts from the directory always win — including the name and the DN. A rename or
                // an OU move updates this row rather than creating another one, which is the whole
                // reason the key is the GUID.
                row.Account = f.Account;
                row.DistinguishedName = f.DistinguishedName;
                row.DisplayName = f.DisplayName;
                row.Description = f.Description;
                row.Signals = f.Signals;
                row.Privileged = f.Privileged;
                row.Enabled = f.Enabled;
                row.DirectoryOwner = f.DirectoryOwner;
                row.IsSelfAccount = f.IsSelfAccount;
                row.LastSeenUtc = nowUtc;

                // Came back after being marked gone. It is the same object, so it keeps its owner
                // and its history rather than starting again as something new.
                if (row.RetiredUtc != null)
                {
                    row.RetiredUtc = null;
                    if (string.Equals(row.State, GovNhiStates.Retired, StringComparison.Ordinal))
                        row.State = row.OwnerUsername != null ? GovNhiStates.Claimed : GovNhiStates.Discovered;
                }

                row.ClaimDueUtc ??= NhiLifecyclePolicy.ClaimDeadline(row.FirstSeenUtc, config);
            }

            // Gone from the directory. The row stays — a record of an account that was quarantined
            // and then deleted is exactly what an auditor asks about.
            var retired = 0;
            foreach (var row in tracked.Where(a => a.RetiredUtc == null && !seen.Contains(a.ObjectGuid)))
            {
                row.RetiredUtc = nowUtc;
                row.State = GovNhiStates.Retired;
                retired++;
            }

            // ── the verdicts ──
            var quarantine = new List<GovNhiAccount>();
            var overdue = new List<GovNhiAccount>();
            var withheld = new List<(GovNhiAccount, string)>();
            var returned = 0;

            foreach (var row in tracked)
            {
                var verdict = NhiLifecyclePolicy.Evaluate(row, config, nowUtc);

                if (verdict.SuppressedQuarantine is { } suppressed)
                    withheld.Add((row, suppressed));

                if (verdict.AttestationOverdue && row.RetiredUtc == null)
                    overdue.Add(row);

                if (string.Equals(verdict.TargetState, GovNhiStates.Quarantined, StringComparison.Ordinal) &&
                    !string.Equals(row.State, GovNhiStates.Quarantined, StringComparison.Ordinal))
                {
                    quarantine.Add(row);
                    continue;   // the state change is applied only if the ceiling allows the sweep
                }

                if (verdict.Changed(row))
                {
                    // An exemption that ran out, or an account that came back. Both are the
                    // lifecycle resuming rather than a decision being taken.
                    if (string.Equals(row.State, GovNhiStates.Exempt, StringComparison.Ordinal)) returned++;
                    row.State = verdict.TargetState;
                }
            }

            var population = tracked.Count(a => a.RetiredUtc == null);
            var ceiling = NhiLifecyclePolicy.MayQuarantine(population, quarantine.Count, config.MaxQuarantinePercent);

            string? blocked = null;
            if (!ceiling.Allowed && quarantine.Count > 0)
            {
                blocked = ceiling.Reason;
                _logger.LogWarning(
                    "NHI lifecycle (service {ServiceId}): quarantine stopped for the whole run — {Reason}",
                    serviceId, ceiling.Reason);
                quarantine.Clear();
            }
            else
            {
                foreach (var row in quarantine)
                {
                    var verdict = NhiLifecyclePolicy.Evaluate(row, config, nowUtc);
                    row.State = GovNhiStates.Quarantined;
                    row.QuarantineReason = verdict.QuarantineReason;
                    row.QuarantinedUtc = nowUtc;
                }
            }

            await _gov.SaveChangesAsync(ct);

            _logger.LogInformation(
                "NHI lifecycle (service {ServiceId}): {Tracked} tracked ({Added} new, {Retired} gone), " +
                "{Quarantined} quarantined, {Overdue} attestation overdue, {Withheld} withheld",
                serviceId, population, added, retired, quarantine.Count, overdue.Count, withheld.Count);

            foreach (var (account, reason) in withheld)
                _logger.LogWarning(
                    "NHI lifecycle: '{Account}' met the criteria for quarantine ({Reason}) and was spared — " +
                    "it is an IdentitySyncPro bind account. It still has no owner.",
                    account.Account, reason);

            return new Result(population, added, retired, returned, quarantine, overdue, withheld, blocked);
        }
    }
}
