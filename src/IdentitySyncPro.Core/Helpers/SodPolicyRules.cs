using IdentitySyncPro.Core.Models.Governance;

namespace IdentitySyncPro.Core.Helpers
{
    /// <summary>
    /// The rules of separation of duties: what makes a policy sound, who is in conflict, and
    /// whether a grant may proceed.
    ///
    /// Kept pure and apart from the directory because the failures here are quiet ones. A policy
    /// that matches everybody, a scan that read nothing and reports a clean domain, an acceptance of
    /// risk that never expires — none of these throw. They produce a screen full of green.
    /// </summary>
    public static class SodPolicyRules
    {
        /// <summary>Splits a comma-separated group list, trimmed, without empties.</summary>
        public static string[] Groups(string? csv) =>
            (csv ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(g => g.Length > 0)
                .ToArray();

        // ══════════════════════════════════════
        // سلامة القاعدة
        // ══════════════════════════════════════

        /// <summary>
        /// Refuses a policy that cannot mean what it says.
        ///
        /// The dangerous one is an overlap between the two duties: a group named on both sides
        /// conflicts with itself, so <b>everybody who holds it violates the policy</b> — a whole
        /// department flagged overnight by a rule that looks reasonable on screen. It is the same
        /// shape as a classifier with no signals, and it is refused for the same reason.
        /// </summary>
        public static string? ValidatePolicy(GovSodPolicy policy)
        {
            if (string.IsNullOrWhiteSpace(policy.Name))
                return "القاعدة تحتاج اسماً. / The policy needs a name.";

            if (string.IsNullOrWhiteSpace(policy.Rationale))
                return "القاعدة يجب أن تذكر سبب المنع — المُعتمِد الذي يقرأ رفضاً بلا سبب لا يملك ما يقرّر به. / " +
                       "The policy must state why: an approver reading a refusal with no reason has nothing to decide on.";

            var a = Groups(policy.DutyAGroups);
            var b = Groups(policy.DutyBGroups);

            if (a.Length == 0 || b.Length == 0)
                return "كلا الواجبين يحتاج مجموعة واحدة على الأقل — واجبٌ فارغ يجعل القاعدة إما بلا أثر أو شاملة للجميع. / " +
                       "Both duties need at least one group — an empty duty makes the policy either inert or universal.";

            var overlap = a.Intersect(b, StringComparer.OrdinalIgnoreCase).ToArray();
            if (overlap.Length > 0)
                return $"«{string.Join("»، «", overlap)}» مذكورة في الواجبين معاً، فتتعارض مع نفسها ويُخالف القاعدةَ كلُّ من يحملها. / " +
                       $"'{string.Join("', '", overlap)}' appears in both duties, so it conflicts with itself and every holder violates the policy.";

            if (!GovSodEnforcement.IsKnown(policy.Enforcement))
                return $"وضع تطبيق مجهول '{policy.Enforcement}'. / Unknown enforcement mode '{policy.Enforcement}'.";

            if (!GovSodSeverity.IsKnown(policy.Severity))
                return $"درجة خطورة مجهولة '{policy.Severity}'. / Unknown severity '{policy.Severity}'.";

            return null;
        }

        // ══════════════════════════════════════
        // من في تعارض
        // ══════════════════════════════════════

        /// <param name="Violates">هل يجتمع الواجبان عند هذا الشخص</param>
        /// <param name="MatchedA">ما طابق من الواجب الأول — يُعرض، فالمخالفة بلا تفصيل غير قابلة للتصرّف</param>
        public sealed record Conflict(bool Violates, IReadOnlyList<string> MatchedA, IReadOnlyList<string> MatchedB);

        /// <summary>
        /// Whether the entitlements this person holds put both duties in their hands.
        ///
        /// Both sides are reported, not just the fact of the conflict: "this person violates policy
        /// 4" is not something anybody can act on, while "they hold Vendor-Create through
        /// AP-Clerks and Payment-Approve through Finance-Managers" names the two things, one of
        /// which has to go.
        /// </summary>
        public static Conflict Evaluate(GovSodPolicy policy, IEnumerable<string> heldGroups)
        {
            var held = new HashSet<string>(heldGroups ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            var a = Groups(policy.DutyAGroups).Where(held.Contains).ToArray();
            var b = Groups(policy.DutyBGroups).Where(held.Contains).ToArray();

            return new Conflict(a.Length > 0 && b.Length > 0, a, b);
        }

        /// <summary>
        /// Whether granting <paramref name="wouldGain"/> to somebody who already holds
        /// <paramref name="held"/> creates a conflict.
        ///
        /// Asked before the grant, which is the only moment it can be prevented rather than
        /// reported. Note it deliberately re-uses <see cref="Evaluate"/> on the combined set instead
        /// of reasoning about the new group alone: a request can carry more than one entitlement,
        /// and two of them could conflict with each other even when the person held neither before.
        /// </summary>
        public static Conflict WouldViolate(GovSodPolicy policy, IEnumerable<string> held, IEnumerable<string> wouldGain) =>
            Evaluate(policy, (held ?? Array.Empty<string>()).Concat(wouldGain ?? Array.Empty<string>()));

        // ══════════════════════════════════════
        // ⛔ الحارس قبل قراءة «صفر مخالفات»
        // ══════════════════════════════════════

        /// <param name="Trustworthy">هل يجوز قراءة نتيجة هذا المسح</param>
        public sealed record ScanVerdict(bool Trustworthy, string? Reason);

        /// <summary>
        /// Whether a scan's result may be believed.
        ///
        /// Zero violations is the answer everybody wants, and it is also exactly what a scan that
        /// read nothing produces. A group that could not be resolved, a directory that refused the
        /// query, a policy whose groups do not exist — each yields an empty membership set and a
        /// clean report, and nothing anywhere says the question was never actually asked.
        ///
        /// <para>So a scan that failed to read any of its groups is <b>not</b> reported as clean.
        /// The same reasoning as the empty-source guard in OrphanCleanup and the empty-classifier
        /// guard in the non-human inventory: silence and safety look identical from the outside.</para>
        /// </summary>
        public static ScanVerdict MayTrustScan(int groupsAsked, int groupsRead, int membershipsRead)
        {
            if (groupsAsked <= 0)
                return new ScanVerdict(false, "no groups were named by any enabled policy — nothing was asked, so nothing can be concluded");

            if (groupsRead == 0)
                return new ScanVerdict(false,
                    $"none of the {groupsAsked} group(s) named by the policies could be read from the directory — " +
                    "a clean result here would mean the question was never asked");

            if (groupsRead < groupsAsked)
                return new ScanVerdict(false,
                    $"only {groupsRead} of {groupsAsked} group(s) could be read — the missing ones would hide exactly the conflicts they take part in");

            if (membershipsRead == 0)
                return new ScanVerdict(false,
                    "every group resolved but not one membership was returned — that is a permission problem, not an empty directory");

            return new ScanVerdict(true, null);
        }

        // ══════════════════════════════════════
        // هل يمرّ المنح
        // ══════════════════════════════════════

        /// <param name="Allowed">هل يجوز الاعتماد</param>
        /// <param name="Blocking">القواعد التي تمنع</param>
        /// <param name="Warning">القواعد التي تُحذّر ولا تمنع</param>
        public sealed record GrantVerdict(
            bool Allowed,
            IReadOnlyList<GovSodPolicy> Blocking,
            IReadOnlyList<GovSodPolicy> Warning,
            string? Message);

        /// <summary>
        /// Whether an approval may go through, given the conflicts it would create.
        ///
        /// A <see cref="GovSodEnforcement.Block"/> policy refuses; a <c>Warn</c> policy lets the
        /// approver proceed but says what they are accepting; <c>Detect</c> records and stays out of
        /// the way. Default is Detect, so switching this feature on never begins by refusing access
        /// nobody knew was in conflict.
        ///
        /// <para><paramref name="mitigatedPolicyIds"/> are conflicts somebody already accepted in
        /// writing, with a control named and an expiry. They do not block again — re-refusing an
        /// accepted risk teaches people to route around the system.</para>
        /// </summary>
        public static GrantVerdict MayGrant(
            IEnumerable<(GovSodPolicy Policy, Conflict Conflict)> conflicts,
            IEnumerable<int>? mitigatedPolicyIds = null)
        {
            var mitigated = new HashSet<int>(mitigatedPolicyIds ?? Array.Empty<int>());

            var live = conflicts
                .Where(c => c.Conflict.Violates && c.Policy.IsEnabled && !mitigated.Contains(c.Policy.Id))
                .ToList();

            var blocking = live.Where(c => GovSodEnforcement.Blocks(c.Policy.Enforcement)).Select(c => c.Policy).ToList();
            var warning = live.Where(c => GovSodEnforcement.Warns(c.Policy.Enforcement)).Select(c => c.Policy).ToList();

            if (blocking.Count > 0)
                return new GrantVerdict(false, blocking, warning,
                    "هذا المنح يخالف فصل المهام: " + string.Join("، ", blocking.Select(p => p.Name)) +
                    " / This grant would violate separation of duties: " + string.Join(", ", blocking.Select(p => p.Name)));

            if (warning.Count > 0)
                return new GrantVerdict(true, blocking, warning,
                    "تحذير فصل المهام: " + string.Join("، ", warning.Select(p => p.Name)) +
                    " / Separation-of-duties warning: " + string.Join(", ", warning.Select(p => p.Name)));

            return new GrantVerdict(true, blocking, warning, null);
        }

        // ══════════════════════════════════════
        // التخفيف
        // ══════════════════════════════════════

        /// <summary>
        /// A mitigation has to say what compensates for the conflict and when the acceptance runs
        /// out. Both are required: an acceptance with no end is not an acceptance, it is forgetting,
        /// and one with no stated control is a signature on nothing.
        /// </summary>
        public static string? ValidateMitigation(string? reason, DateTime? untilUtc, DateTime nowUtc, int maxDays = 365)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return "التخفيف يجب أن يذكر الضابط التعويضي. / A mitigation must name the compensating control.";

            if (untilUtc == null)
                return "التخفيف يجب أن يكون له تاريخ انتهاء — قبولُ خطرٍ بلا نهاية ليس قبولاً بل نسيان. / " +
                       "A mitigation must have an end date — an acceptance with no end is not acceptance, it is forgetting.";

            if (untilUtc <= nowUtc)
                return "تاريخ انتهاء التخفيف يجب أن يكون في المستقبل. / The mitigation end date must be in the future.";

            if (untilUtc > nowUtc.AddDays(maxDays))
                return $"لا يجوز أن يتجاوز التخفيف {maxDays} يوماً — جدّده، فالتجديد قرار يُتخذ من جديد. / " +
                       $"A mitigation cannot run longer than {maxDays} days. Renew it — a renewal is a decision made again.";

            return null;
        }

        /// <summary>Whether a mitigation is in force right now. An expired one is no mitigation at all.</summary>
        public static bool IsMitigated(GovSodViolation violation, DateTime nowUtc) =>
            violation.MitigationExpiresUtc is { } until && until > nowUtc;
    }
}
