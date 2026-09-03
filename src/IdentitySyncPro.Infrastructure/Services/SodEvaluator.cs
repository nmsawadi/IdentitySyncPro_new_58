using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Governance;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Reads the directory and answers the two separation-of-duties questions: who is in conflict
    /// today, and would this grant put somebody in conflict.
    ///
    /// Both paths lean on the same rule in <see cref="SodPolicyRules"/>, so the answer a nightly
    /// scan gives and the answer an approver sees cannot drift apart. What lives here is the
    /// reading — and the reading is where this feature can lie.
    ///
    /// <para><b>An unknown membership is never read as "no conflict".</b> The directory being
    /// unreachable, a group missing, a bind account without permission — each produces an empty
    /// answer that looks exactly like a clean one. Every method here reports that it could not tell,
    /// and the callers refuse rather than proceed.</para>
    /// </summary>
    public class SodEvaluator
    {
        private readonly GovernanceDbContext _gov;
        private readonly ITenantConnectorFactory _connectors;
        private readonly ILogger<SodEvaluator> _logger;

        public SodEvaluator(GovernanceDbContext gov, ITenantConnectorFactory connectors, ILogger<SodEvaluator> logger)
        {
            _gov = gov;
            _connectors = connectors;
            _logger = logger;
        }

        public Task<List<GovSodPolicy>> EnabledPoliciesAsync(int tenantId, CancellationToken ct = default) =>
            _gov.SodPolicies.Where(p => p.TenantId == tenantId && p.IsEnabled).ToListAsync(ct);

        // ══════════════════════════════════════
        // قبل المنح
        // ══════════════════════════════════════

        /// <param name="Determined">هل أمكن معرفة العضوية أصلاً</param>
        /// <param name="Problem">لماذا تعذّرت — يُعرض للمُعتمِد كما هو</param>
        public sealed record SubjectCheck(
            bool Determined,
            string? Problem,
            IReadOnlyList<(GovSodPolicy Policy, SodPolicyRules.Conflict Conflict)> Conflicts);

        /// <summary>
        /// What conflicts this person would be in if they also held <paramref name="wouldGain"/>.
        ///
        /// Only the groups the policies actually name are looked up — a handful, not the whole
        /// directory — and each is asked once even when several policies mention it.
        ///
        /// <para>Uses <see cref="ITargetConnector.TryIsMemberOfAnyAsync"/>, whose whole reason for
        /// existing is this moment: the older <c>IsMemberOfAnyAsync</c> answers <c>false</c> when
        /// the directory is unreachable, which is right for "is this person excluded from a sweep"
        /// and catastrophic for "is this person already holding the conflicting duty". A
        /// <c>null</c> here means <i>unknown</i>, and unknown stops the check.</para>
        /// </summary>
        public async Task<SubjectCheck> CheckSubjectAsync(
            TenantSettings tenant, string subject, IEnumerable<string> wouldGain, CancellationToken ct = default)
        {
            var policies = await EnabledPoliciesAsync(tenant.Id, ct);
            if (policies.Count == 0)
                return new SubjectCheck(true, null, Array.Empty<(GovSodPolicy, SodPolicyRules.Conflict)>());

            var named = policies
                .SelectMany(p => SodPolicyRules.Groups(p.DutyAGroups).Concat(SodPolicyRules.Groups(p.DutyBGroups)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var connector = _connectors.CreateTargetConnector(tenant);
            var held = new List<string>();

            foreach (var group in named)
            {
                ct.ThrowIfCancellationRequested();

                var answer = await connector.TryIsMemberOfAnyAsync(subject, new[] { group }, ct);
                if (answer == null)
                    return new SubjectCheck(false,
                        $"تعذّر التحقق من عضوية «{group}» لـ {subject}، فلا يمكن الجزم بعدم وجود تعارض في المهام. / " +
                        $"Could not determine whether {subject} is in '{group}', so no conclusion about separation of duties is possible.",
                        Array.Empty<(GovSodPolicy, SodPolicyRules.Conflict)>());

                if (answer.Value) held.Add(group);
            }

            var conflicts = policies
                .Select(p => (Policy: p, Conflict: SodPolicyRules.WouldViolate(p, held, wouldGain)))
                .Where(x => x.Conflict.Violates)
                .ToList();

            return new SubjectCheck(true, null, conflicts);
        }

        /// <summary>Conflicts already accepted in writing and still in force, for this person.</summary>
        public async Task<HashSet<int>> MitigatedPolicyIdsAsync(string subject, DateTime nowUtc, CancellationToken ct = default)
        {
            var rows = await _gov.SodViolations
                .Where(v => v.SubjectAccount.ToLower() == subject.ToLower()
                            && v.MitigationExpiresUtc != null && v.MitigationExpiresUtc > nowUtc)
                .Select(v => v.PolicyId)
                .ToListAsync(ct);

            return new HashSet<int>(rows);
        }

        // ══════════════════════════════════════
        // المسح الكامل
        // ══════════════════════════════════════

        /// <param name="Trustworthy">هل يجوز قراءة هذه النتيجة — «صفر مخالفات» من مسح فاشل كذبة</param>
        public sealed record ScanResult(
            bool Trustworthy,
            string? Problem,
            int Opened,
            int Continuing,
            int Cleared,
            IReadOnlyList<GovSodViolation> Live);

        /// <summary>
        /// Finds everybody currently holding both sides of any enabled policy, and reconciles that
        /// against what is already recorded.
        ///
        /// <para>Group membership is read with <see cref="ITargetConnector.GetGroupMembersAsync"/>,
        /// which reports a partial read as a failure rather than returning a shorter list — a
        /// truncated membership would hide exactly the people whose second duty was in the part
        /// that never arrived.</para>
        /// </summary>
        public async Task<ScanResult> ScanTenantAsync(TenantSettings tenant, DateTime nowUtc, CancellationToken ct = default)
        {
            var policies = await EnabledPoliciesAsync(tenant.Id, ct);

            var named = policies
                .SelectMany(p => SodPolicyRules.Groups(p.DutyAGroups).Concat(SodPolicyRules.Groups(p.DutyBGroups)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var connector = _connectors.CreateTargetConnector(tenant);

            // account -> the named groups it belongs to
            var membership = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var display = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var groupsRead = 0;
            var membershipsRead = 0;

            foreach (var group in named)
            {
                ct.ThrowIfCancellationRequested();

                var (ok, members, error) = await connector.GetGroupMembersAsync(group, nested: true, ct);
                if (!ok)
                {
                    _logger.LogWarning("SoD scan: could not read '{Group}' in full — {Error}", group, error);
                    continue;
                }

                groupsRead++;
                foreach (var m in members)
                {
                    membershipsRead++;
                    if (!membership.TryGetValue(m.Account, out var set))
                        membership[m.Account] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(group);
                    display[m.Account] = m.DisplayName;
                }
            }

            // Before anything is written or cleared. A scan that could not read its groups must not
            // clear yesterday's violations — that would erase real findings and call it progress.
            var trust = SodPolicyRules.MayTrustScan(named.Length, groupsRead, membershipsRead);
            if (!trust.Trustworthy)
            {
                _logger.LogError("SoD scan for tenant '{Tenant}' is not trustworthy — {Reason}", tenant.TenantName, trust.Reason);
                return new ScanResult(false, trust.Reason, 0, 0, 0, Array.Empty<GovSodViolation>());
            }

            var existing = await _gov.SodViolations
                .Where(v => v.TenantId == tenant.Id && v.ClearedUtc == null)
                .ToListAsync(ct);

            var stillOpen = new HashSet<long>();
            int opened = 0, continuing = 0;
            var live = new List<GovSodViolation>();

            foreach (var policy in policies)
            {
                foreach (var (account, groups) in membership)
                {
                    ct.ThrowIfCancellationRequested();

                    var conflict = SodPolicyRules.Evaluate(policy, groups);
                    if (!conflict.Violates) continue;

                    var row = existing.FirstOrDefault(v =>
                        v.PolicyId == policy.Id &&
                        string.Equals(v.SubjectAccount, account, StringComparison.OrdinalIgnoreCase));

                    if (row == null)
                    {
                        row = new GovSodViolation
                        {
                            PolicyId = policy.Id,
                            TenantId = tenant.Id,
                            SubjectAccount = account,
                            DetectedUtc = nowUtc
                        };
                        _gov.SodViolations.Add(row);
                        opened++;
                    }
                    else
                    {
                        stillOpen.Add(row.Id);
                        continuing++;
                    }

                    row.SubjectDisplayName = display.GetValueOrDefault(account);
                    row.MatchedA = string.Join(", ", conflict.MatchedA);
                    row.MatchedB = string.Join(", ", conflict.MatchedB);
                    row.LastSeenUtc = nowUtc;
                    live.Add(row);
                }
            }

            // Gone. The row is closed, never deleted: "this person held both for eleven days in
            // March" is the question an auditor asks, and a table of today's conflicts cannot answer it.
            var cleared = 0;
            foreach (var row in existing.Where(v => !stillOpen.Contains(v.Id)))
            {
                row.ClearedUtc = nowUtc;
                cleared++;
            }

            await _gov.SaveChangesAsync(ct);

            _logger.LogInformation(
                "SoD scan '{Tenant}': {Policies} policy(ies), {Groups} group(s), {People} person(s) with named groups — " +
                "{Opened} new, {Continuing} continuing, {Cleared} cleared",
                tenant.TenantName, policies.Count, groupsRead, membership.Count, opened, continuing, cleared);

            return new ScanResult(true, null, opened, continuing, cleared, live);
        }
    }
}
