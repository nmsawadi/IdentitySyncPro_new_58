using IdentitySyncPro.Core.Models.Governance;

namespace IdentitySyncPro.Core.Helpers
{
    /// <summary>
    /// The rules a certification campaign runs on: what makes one launchable, who may review an
    /// item, when a delegation counts, and when the deadline is allowed to revoke.
    ///
    /// Kept apart from the database and the directory because these are the parts that decide
    /// whether access is taken away from people — and every one of them fails quietly if it is
    /// wrong. A campaign with no reviewer completes on its deadline by revoking everything. A
    /// delegation that never expires hands a reviewer's authority away permanently. Neither throws.
    /// </summary>
    public static class CampaignPolicy
    {
        // ══════════════════════════════════════
        // LAUNCHING
        // ══════════════════════════════════════

        /// <summary>
        /// Rejects a campaign that cannot be reviewed.
        ///
        /// The reviewer check matters more here than the equivalent one on a request catalog item.
        /// There, an item nobody can approve leaves requests waiting forever — bad, but static. Here
        /// it is worse than static: nobody can decide, the deadline arrives, and <b>every membership
        /// in scope is revoked</b> because none of them was certified. A campaign with no reviewer
        /// is not a stalled queue, it is a scheduled outage.
        /// </summary>
        /// <returns>null when the campaign can be launched, otherwise the reason it cannot.</returns>
        public static string? ValidateCampaign(GovCampaign campaign)
        {
            if (string.IsNullOrWhiteSpace(campaign.Name))
                return "الحملة تحتاج اسماً / The campaign needs a name.";

            var explicitGroups = AccessRequestPolicy.NamesIn(campaign.ScopeGroups);
            var catalogIds = ParseIds(campaign.ScopeCatalogItemIds);

            if (explicitGroups.Count == 0 && catalogIds.Count == 0)
                return "لا نطاق للحملة — حدّد مجموعات أو عناصر كتالوج / "
                     + "The campaign has no scope — name groups or catalog items.";

            // An explicit group name says nothing about which directory it lives in.
            if (explicitGroups.Count > 0 && (campaign.ScopeTenantId ?? 0) <= 0)
                return "المجموعات الصريحة تحتاج جهة يُقرأ منها / Explicit groups need the tenant they are read from.";

            if (string.IsNullOrWhiteSpace(campaign.ReviewerAdGroup) &&
                AccessRequestPolicy.NamesIn(campaign.ReviewerUsers).Count == 0)
                return "لا مُراجِع للحملة — وانتهاء مهلتها بلا مراجعة يسحب كل عضوياتها / "
                     + "The campaign has no reviewer — its deadline would revoke every membership in scope.";

            if (campaign.ReviewDays <= 0)
                return "مهلة المراجعة يجب أن تكون يوماً واحداً على الأقل / The review window must be at least one day.";

            if (campaign.MaxUndecidedRevokePercent is < 0 or > 100)
                return "حدّ السحب التلقائي نسبة بين 0 و100 / The auto-revoke ceiling is a percentage between 0 and 100.";

            return null;
        }

        /// <summary>Comma-separated integers, ignoring anything that is not one. Blank yields an empty list.</summary>
        public static IReadOnlyList<int> ParseIds(string? csv) =>
            AccessRequestPolicy.NamesIn(csv)
                .Select(t => int.TryParse(t, out var n) ? n : (int?)null)
                .Where(n => n.HasValue)
                .Select(n => n!.Value)
                .Distinct()
                .ToList();

        public static DateTime Deadline(GovCampaign campaign, DateTime startedUtc) =>
            startedUtc.AddDays(campaign.ReviewDays);

        // ══════════════════════════════════════
        // DELEGATION
        // ══════════════════════════════════════

        /// <summary>
        /// Rejects a delegation that would not mean what it says.
        ///
        /// The end date is required and must be real. An open-ended delegation is not a stand-in
        /// for a holiday, it is a permanent transfer of authority that nobody would have approved
        /// as one — and the audit trail would still show the original reviewer's name against every
        /// decision.
        /// </summary>
        public static string? ValidateDelegation(GovReviewDelegation d, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(d.FromUsername) || string.IsNullOrWhiteSpace(d.ToUsername))
                return "التفويض يحتاج طرفين / A delegation needs both people.";

            if (string.Equals(d.FromUsername, d.ToUsername, StringComparison.OrdinalIgnoreCase))
                return "لا تفويض للنفس / You cannot delegate to yourself.";

            if (d.EndUtc <= d.StartUtc)
                return "نهاية التفويض بعد بدايته / The delegation must end after it starts.";

            // A window that has already closed grants nothing and reads on the screen as though it
            // does — the reviewer would leave believing they were covered.
            if (d.EndUtc <= nowUtc)
                return "مدة التفويض انتهت قبل أن تبدأ / The delegation period has already passed.";

            return null;
        }

        /// <summary>Whether a delegation is in force at this moment.</summary>
        public static bool IsInForce(GovReviewDelegation d, DateTime nowUtc) =>
            d.RevokedUtc == null && d.StartUtc <= nowUtc && d.EndUtc > nowUtc;

        /// <summary>
        /// The reviewers whose authority this person currently carries, besides their own.
        ///
        /// <b>One hop only, by construction:</b> the result is drawn from delegations pointing at
        /// this person and is never fed back in to be expanded again. A chain — A delegates to B, B
        /// to C — would leave a certificate signed by somebody two removes from anyone who was ever
        /// answerable for the access, and no reader could tell.
        /// </summary>
        public static IReadOnlyCollection<string> AuthorityOf(
            string username, IEnumerable<GovReviewDelegation> delegations, DateTime nowUtc) =>
            delegations
                .Where(d => IsInForce(d, nowUtc)
                            && string.Equals(d.ToUsername, username, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.FromUsername)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        // ══════════════════════════════════════
        // REVIEWING
        // ══════════════════════════════════════

        /// <param name="OnBehalfOf">The reviewer whose authority was used, or null when they acted as themselves.</param>
        public sealed record ReviewRight(bool Allowed, string? Problem, string? OnBehalfOf);

        /// <summary>
        /// Whether this person may decide this item, and under whose authority.
        ///
        /// <paramref name="isConfiguredReviewer"/> and <paramref name="delegatedReviewers"/> are
        /// supplied by the caller because resolving a reviewer group needs the directory. A caller
        /// that could not reach it must pass false and say so, never assume.
        /// </summary>
        public static ReviewRight CanReview(
            GovCampaign campaign, GovCampaignItem item, string? username,
            bool isConfiguredReviewer, IReadOnlyCollection<string> delegatedReviewers)
        {
            if (string.IsNullOrWhiteSpace(username))
                return new(false, "لا مُراجِع / No reviewer.", null);

            if (campaign.Status != GovCampaignStatus.Active)
                return new(false, $"الحملة في حالة {campaign.Status.ToLowerInvariant()} / The campaign is {campaign.Status.ToLowerInvariant()}.", null);

            if (item.Decision != GovReviewDecisions.Pending)
                return new(false, "هذا العنصر مُقرَّر بالفعل / This item has already been decided.", null);

            // Certifying your own access is the same hole as approving your own request, and a
            // certification campaign is precisely where it would be least visible: one row among
            // hundreds, marked "Keep" by the person who holds it.
            if (string.Equals(username, item.SubjectAccount, StringComparison.OrdinalIgnoreCase))
                return new(false, "لا تراجع عضويتك أنت / You cannot certify your own membership.", null);

            if (isConfiguredReviewer) return new(true, null, null);

            // Standing in for somebody who is away. The item is still theirs; the name of whoever
            // actually decided it travels with the record.
            var onBehalf = delegatedReviewers.FirstOrDefault();
            if (onBehalf != null) return new(true, null, onBehalf);

            return new(false, "لستَ مُراجِعاً لهذه الحملة / You are not a reviewer on this campaign.", null);
        }

        /// <summary>
        /// The execution a decision creates. Only a revocation reaches the directory; "Keep" is
        /// complete the moment it is recorded, and queueing it would leave work that never runs.
        /// </summary>
        public static string ExecutionAfter(string decision) => decision switch
        {
            GovReviewDecisions.Revoke => GovExecutionStatus.Pending,
            GovReviewDecisions.Keep => GovExecutionStatus.None,
            _ => throw new InvalidOperationException($"قرار مراجعة غير معروف '{decision}' / Unknown review decision.")
        };

        // ══════════════════════════════════════
        // THE DEADLINE
        // ══════════════════════════════════════

        /// <param name="Allowed">Whether the undecided items may be revoked.</param>
        /// <param name="Reason">Why not, when they may not — written for the closing note.</param>
        public sealed record AutoRevokeVerdict(bool Allowed, int Undecided, int Total, string? Reason);

        /// <summary>
        /// Whether a lapsed campaign's undecided items may be revoked.
        ///
        /// Revoking the undecided is the policy, and it is not optional. The ceiling is not a way
        /// around it — it separates the case the policy is for from the case it is not.
        ///
        /// A handful of rows nobody got to is a reviewer who did most of the work: revoking those is
        /// exactly what certification means. A campaign where almost nothing was decided is not a
        /// verdict on the access at all; it is a reviewer who never opened it, an invitation that
        /// went to a mailbox nobody reads, a person who left the organisation. Acting on that would
        /// take access from a whole department overnight, on the strength of no one's judgement.
        ///
        /// Same shape as <c>MinSourceRecords</c> in the orphan service: an action that is safe on a
        /// real answer and catastrophic on a missing one refuses the missing one out loud.
        /// </summary>
        public static AutoRevokeVerdict MayAutoRevoke(int total, int undecided, int maxUndecidedPercent)
        {
            if (total <= 0)
                return new(false, undecided, total, "لا عناصر في الحملة / The campaign has no items.");

            if (undecided <= 0)
                return new(true, undecided, total, null);   // nothing to auto-revoke; trivially fine

            var percent = (int)Math.Round(undecided * 100.0 / total);
            if (percent > maxUndecidedPercent)
                return new(false, undecided, total,
                    $"{undecided} من {total} ({percent}٪) بلا قرار، والحد {maxUndecidedPercent}٪ — أُوقف السحب التلقائي: "
                    + "هذه حملة لم تُراجَع، لا حكمٌ على الوصول / "
                    + $"{undecided} of {total} ({percent}%) undecided against a {maxUndecidedPercent}% ceiling — "
                    + "auto-revocation stopped: this is an unreviewed campaign, not a verdict.");

            return new(true, undecided, total, null);
        }

        /// <summary>Whether an active campaign has run past its deadline.</summary>
        public static bool HasLapsed(GovCampaign campaign, DateTime nowUtc) =>
            campaign.Status == GovCampaignStatus.Active
            && campaign.DueUtc != null
            && campaign.DueUtc <= nowUtc;
    }
}
