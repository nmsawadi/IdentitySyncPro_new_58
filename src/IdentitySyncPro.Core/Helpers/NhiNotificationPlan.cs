using IdentitySyncPro.Core.Models.Governance;

namespace IdentitySyncPro.Core.Helpers
{
    /// <summary>
    /// Decides who is told what about non-human accounts, and how often.
    ///
    /// Separated from the sending because the decisions are the part worth constraining. Three of
    /// them shape everything here:
    ///
    /// <para><b>One message per person, not per account.</b> Somebody answerable for twelve service
    /// accounts gets one notice listing twelve, not twelve notices. Twelve is a filter rule; one is
    /// a task.</para>
    ///
    /// <para><b>An owner nobody can reach is a finding, not a silence.</b> An owner is an opaque
    /// identifier — this system never assumed they exist in a directory, so their address may not be
    /// resolvable. When it is not, they are named in the digest that goes to the operator, because
    /// an account whose owner cannot be reached is exactly the account about to be quarantined with
    /// nobody warned.</para>
    ///
    /// <para><b>A daily reminder is not a reminder.</b> A sweep with no memory re-sends the same
    /// notice every morning until somebody acts, and people learn to file it unread. Reminders are
    /// spaced, and the spacing is a setting.</para>
    /// </summary>
    public static class NhiNotificationPlan
    {
        /// <param name="RemindEveryDays">أقلّ فاصل بين تذكيرين عن الحساب نفسه</param>
        /// <param name="WarnBeforeDays">كم يوماً قبل الاستحقاق يبدأ التذكير</param>
        public sealed record Timing(int RemindEveryDays, int WarnBeforeDays)
        {
            public static readonly Timing Default = new(7, 14);
        }

        /// <summary>لماذا يُذكَّر هذا الحساب — يُعرض للقارئ، ويُرتَّب به</summary>
        public enum Reason
        {
            /// <summary>مضى موعد الإقرار، والمهلة تجري</summary>
            AttestationOverdue,

            /// <summary>يقترب موعد الإقرار</summary>
            AttestationDueSoon,

            /// <summary>حُجر فعلاً</summary>
            Quarantined,

            /// <summary>بلا مالك ومهلته تقترب</summary>
            ClaimDueSoon,

            /// <summary>بلا مالك ومضت مهلته</summary>
            ClaimOverdue
        }

        public sealed record Item(GovNhiAccount Account, Reason Reason, DateTime? Due);

        /// <param name="ByOwner">ما يُرسَل لكل مالك — المفتاح هو معرّفه كما سجّله</param>
        /// <param name="Digest">كل شيء يستحق أن يراه المشغّل، بما فيه ما لا مالك له</param>
        /// <param name="Unreachable">مالكون لهم حسابات مستحقة ولم يُعرف لهم عنوان</param>
        public sealed record Plan(
            IReadOnlyDictionary<string, IReadOnlyList<Item>> ByOwner,
            IReadOnlyList<Item> Digest,
            IReadOnlyList<string> Unreachable)
        {
            public bool Empty => Digest.Count == 0;
        }

        /// <summary>
        /// What is worth saying today.
        ///
        /// <paramref name="addressOf"/> answers "can this owner be reached", and returning null is a
        /// legitimate answer rather than an error: the institution decides what its identifiers look
        /// like, and this system never required them to be directory accounts.
        /// </summary>
        public static Plan Build(
            IEnumerable<GovNhiAccount> accounts,
            NhiLifecyclePolicy.LifecycleConfig config,
            Timing timing,
            Func<string, string?> addressOf,
            DateTime nowUtc)
        {
            var byOwner = new Dictionary<string, List<Item>>(StringComparer.OrdinalIgnoreCase);
            var digest = new List<Item>();
            var unreachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var a in accounts)
            {
                if (a.RetiredUtc != null) continue;
                if (string.Equals(a.State, GovNhiStates.Exempt, StringComparison.Ordinal)) continue;

                // Spacing is checked before anything else: an account already mentioned this week
                // is not mentioned again, whatever its reason.
                if (a.LastNotifiedUtc is { } last && (nowUtc - last).TotalDays < timing.RemindEveryDays)
                    continue;

                if (Classify(a, config, timing, nowUtc) is not { } item) continue;

                digest.Add(item);

                if (a.OwnerUsername is not { Length: > 0 } owner) continue;

                if (string.IsNullOrWhiteSpace(addressOf(owner)))
                {
                    // Named rather than skipped. This is the account whose owner will not hear that
                    // it is about to be quarantined.
                    unreachable.Add(owner);
                    continue;
                }

                if (!byOwner.TryGetValue(owner, out var list))
                    byOwner[owner] = list = new List<Item>();
                list.Add(item);
            }

            // Most urgent first, in every list: a reader who stops after three lines should have read
            // the three that matter.
            static int Rank(Reason r) => r switch
            {
                Reason.Quarantined => 0,
                Reason.ClaimOverdue => 1,
                Reason.AttestationOverdue => 2,
                Reason.ClaimDueSoon => 3,
                _ => 4
            };

            return new Plan(
                byOwner.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<Item>)kv.Value
                        .OrderBy(i => Rank(i.Reason)).ThenBy(i => i.Due ?? DateTime.MaxValue).ToList(),
                    StringComparer.OrdinalIgnoreCase),
                digest.OrderBy(i => Rank(i.Reason)).ThenBy(i => i.Due ?? DateTime.MaxValue).ToList(),
                unreachable.OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList());
        }

        private static Item? Classify(
            GovNhiAccount a, NhiLifecyclePolicy.LifecycleConfig config, Timing timing, DateTime nowUtc)
        {
            if (string.Equals(a.State, GovNhiStates.Quarantined, StringComparison.Ordinal))
                return new Item(a, Reason.Quarantined, a.QuarantinedUtc);

            if (a.OwnerUsername is not { Length: > 0 })
            {
                var claimDue = a.ClaimDueUtc ?? NhiLifecyclePolicy.ClaimDeadline(a.FirstSeenUtc, config);

                if (nowUtc >= claimDue) return new Item(a, Reason.ClaimOverdue, claimDue);

                return (claimDue - nowUtc).TotalDays <= timing.WarnBeforeDays
                    ? new Item(a, Reason.ClaimDueSoon, claimDue)
                    : null;
            }

            if (NhiLifecyclePolicy.AttestationDue(a, config) is not { } attestDue) return null;

            if (nowUtc >= attestDue) return new Item(a, Reason.AttestationOverdue, attestDue);

            return (attestDue - nowUtc).TotalDays <= timing.WarnBeforeDays
                ? new Item(a, Reason.AttestationDueSoon, attestDue)
                : null;
        }

        /// <summary>Bilingual label for a reason — the console is Arabic and the notices follow it.</summary>
        public static string Label(Reason reason) => reason switch
        {
            Reason.Quarantined => "محجور / Quarantined",
            Reason.ClaimOverdue => "مضت مهلة المطالبة / Claim deadline passed",
            Reason.AttestationOverdue => "فات موعد الإقرار / Attestation overdue",
            Reason.ClaimDueSoon => "تقترب مهلة المطالبة / Claim deadline approaching",
            Reason.AttestationDueSoon => "يقترب موعد الإقرار / Attestation due soon",
            _ => reason.ToString()
        };
    }
}
