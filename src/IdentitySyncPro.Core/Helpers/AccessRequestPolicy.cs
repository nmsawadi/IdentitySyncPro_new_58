using IdentitySyncPro.Core.Models.Governance;

namespace IdentitySyncPro.Core.Helpers
{
    /// <summary>
    /// The rules that decide whether a request may be raised, who may decide it, and what its
    /// status becomes — kept apart from the database and the directory so they can be tested.
    ///
    /// Everything here guards a failure that would otherwise complete successfully: a catalog item
    /// nobody can approve, a requester approving their own request, a second decision landing on a
    /// request that was already rejected. None of those throw on their own; each produces a row
    /// that looks exactly like a governed one.
    /// </summary>
    public static class AccessRequestPolicy
    {
        // ══════════════════════════════════════
        // THE CATALOG ITEM
        // ══════════════════════════════════════

        /// <summary>
        /// Rejects a catalog item that cannot function.
        ///
        /// An item with no approver is the one that matters: requests against it are accepted,
        /// appear in the requester's list as "Pending", and can never be decided by anybody. It is
        /// a black hole that reads as a working queue, so it is refused at save time rather than
        /// discovered by the person still waiting three weeks later.
        /// </summary>
        /// <returns>null when the item is usable, otherwise the reason it is not.</returns>
        public static string? ValidateCatalogItem(GovCatalogItem item)
        {
            if (string.IsNullOrWhiteSpace(item.DisplayName))
                return "يلزم اسم معروض للعنصر / The catalog item needs a display name.";

            if (string.IsNullOrWhiteSpace(item.GroupName))
                return "يلزم تحديد مجموعة AD التي يمنحها العنصر / The catalog item needs the AD group it grants.";

            if (item.TenantId <= 0)
                return "يلزم تحديد الجهة التي يُنفَّذ العنصر على اتصال AD الخاص بها / The catalog item needs the tenant whose AD connection executes it.";

            if (!string.Equals(item.TargetType, GovTargetTypes.AdGroup, StringComparison.OrdinalIgnoreCase))
                return $"نوع هدف غير معروف '{item.TargetType}' / Unknown target type.";

            if (string.IsNullOrWhiteSpace(item.ApproverAdGroup) &&
                NamesIn(item.ApproverUsers).Count == 0)
                return "لا يوجد مُعتمِد لهذا العنصر — أي طلب عليه سيبقى معلّقاً ولن يستطيع أحد أن يقرّره / No approver is configured — requests for this item could never be decided by anyone.";

            if (item.DecisionDueDays < 0)
                return "مهلة القرار لا تكون سالبة / The decision window cannot be negative.";

            if (item.AccessDurationDays < 0)
                return "مدة الوصول لا تكون سالبة / The access duration cannot be negative.";

            return null;
        }

        /// <summary>Comma-separated usernames, trimmed and de-duplicated. Blank yields an empty set, never null.</summary>
        public static IReadOnlyCollection<string> NamesIn(string? csv) =>
            string.IsNullOrWhiteSpace(csv)
                ? Array.Empty<string>()
                : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .ToArray();

        // ══════════════════════════════════════
        // RAISING A REQUEST
        // ══════════════════════════════════════

        /// <summary>
        /// Whether this request may be raised at all.
        ///
        /// <paramref name="subjectIsAlreadyMember"/> is checked here rather than at execution: a
        /// request for access somebody already holds would be approved, execute as a no-op, and
        /// leave an approval record implying a grant that never happened.
        /// </summary>
        public static string? ValidateNewRequest(
            GovCatalogItem item, string? subjectAccount, string? requestedBy, string? justification,
            bool subjectIsAlreadyMember, bool requesterIsEligible)
        {
            if (!item.IsEnabled)
                return "هذا العنصر موقوف / This catalog item is disabled.";

            if (string.IsNullOrWhiteSpace(subjectAccount))
                return "يلزم تحديد الحساب المستفيد / The request needs the account the access is for.";

            if (string.IsNullOrWhiteSpace(requestedBy))
                return "يلزم تحديد مُقدّم الطلب / The request needs a requester.";

            // A justification nobody wrote is a justification nobody can review. It is the single
            // field an auditor reads first, and an empty one makes the whole record decorative.
            if (string.IsNullOrWhiteSpace(justification))
                return "المبرّر إلزامي / A justification is required.";

            if (!requesterIsEligible)
                return "مُقدّم الطلب غير مؤهَّل لطلب هذا العنصر / The requester is not eligible for this catalog item.";

            if (subjectIsAlreadyMember)
                return "الحساب يملك هذا الوصول أصلاً / The account already has this access.";

            return null;
        }

        /// <summary>When the decision must be made by, or null when the item sets no window.</summary>
        public static DateTime? DecisionDeadline(GovCatalogItem item, DateTime nowUtc) =>
            item.DecisionDueDays > 0 ? nowUtc.AddDays(item.DecisionDueDays) : null;

        /// <summary>When the granted access should be revoked, or null when it is permanent.</summary>
        public static DateTime? AccessDeadline(GovCatalogItem item, DateTime grantedUtc) =>
            item.AccessDurationDays > 0 ? grantedUtc.AddDays(item.AccessDurationDays) : null;

        // ══════════════════════════════════════
        // DECIDING
        // ══════════════════════════════════════

        /// <summary>
        /// Whether this person may decide this request.
        ///
        /// The self-approval bar covers the requester <b>and</b> the subject. Approving access you
        /// asked for is the oldest hole in every request system, and approving access granted to
        /// your own account is the same hole from the other side — an approver who requests on
        /// behalf of themselves would otherwise clear their own path.
        /// </summary>
        /// <param name="isConfiguredApprover">
        /// Whether the caller is in the item's approver list or its approver group. Resolving group
        /// membership needs the directory, so it is answered by the caller and asserted here.
        /// </param>
        public static string? ValidateDecision(
            GovAccessRequest request, string? approverUsername, bool isConfiguredApprover, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(approverUsername))
                return "يلزم تحديد المُعتمِد / The decision needs an approver.";

            if (request.Status != GovRequestStatus.Pending)
                return $"هذا الطلب في حالة {request.Status.ToLowerInvariant()} ولا يمكن إعادة تقريره / This request is already {request.Status.ToLowerInvariant()} — it cannot be decided again.";

            if (request.DecisionDueUtc != null && request.DecisionDueUtc <= nowUtc)
                return "انتهت مهلة القرار لهذا الطلب / The decision window for this request has closed.";

            if (!isConfiguredApprover)
                return "لستَ مُعتمِداً لهذا العنصر / You are not an approver for this catalog item.";

            if (string.Equals(approverUsername, request.RequestedBy, StringComparison.OrdinalIgnoreCase))
                return "لا يمكنك تقرير طلب قدّمته أنت / You cannot decide a request you raised yourself.";

            if (string.Equals(approverUsername, request.SubjectAccount, StringComparison.OrdinalIgnoreCase))
                return "لا يمكنك تقرير طلب يمنح وصولاً لحسابك أنت / You cannot decide a request that grants access to your own account.";

            return null;
        }

        /// <summary>
        /// The status a decision produces.
        ///
        /// Unknown decisions throw rather than defaulting. A silent default here would turn a typo
        /// or a tampered form field into a rejection — or worse, an approval — that the audit trail
        /// would record as deliberate.
        /// </summary>
        public static string StatusAfter(string decision) => decision switch
        {
            GovDecisions.Approve => GovRequestStatus.Approved,
            GovDecisions.Reject => GovRequestStatus.Rejected,
            _ => throw new InvalidOperationException($"قرار غير معروف '{decision}' / Unknown decision.")
        };

        /// <summary>
        /// Only an approval creates work for the directory. A rejection is complete the moment it
        /// is recorded, so it stays at <see cref="GovExecutionStatus.None"/> rather than sitting in
        /// a queue that will never run.
        /// </summary>
        public static string ExecutionStatusAfter(string decision) =>
            decision == GovDecisions.Approve ? GovExecutionStatus.Pending : GovExecutionStatus.None;

        // ══════════════════════════════════════
        // CANCELLING AND EXPIRING
        // ══════════════════════════════════════

        /// <summary>The requester may withdraw their own request while it is still undecided; nobody else may.</summary>
        public static string? ValidateCancel(GovAccessRequest request, string? byUsername)
        {
            if (request.Status != GovRequestStatus.Pending)
                return $"هذا الطلب في حالة {request.Status.ToLowerInvariant()} / This request is already {request.Status.ToLowerInvariant()}.";

            return string.Equals(byUsername, request.RequestedBy, StringComparison.OrdinalIgnoreCase)
                ? null
                : "لا يسحب الطلب إلا مُقدّمه / Only the requester can withdraw a request.";
        }

        /// <summary>
        /// Whether an undecided request has run out of time.
        ///
        /// Expiry is a decision the system makes, so it is deliberately narrow: only a Pending
        /// request with a deadline that has passed. A request already approved but not yet executed
        /// must never expire — the grant was decided, and losing it here would be a promise
        /// silently withdrawn.
        /// </summary>
        public static bool HasExpired(GovAccessRequest request, DateTime nowUtc) =>
            request.Status == GovRequestStatus.Pending
            && request.DecisionDueUtc != null
            && request.DecisionDueUtc <= nowUtc;

        /// <summary>
        /// Whether granted access has reached its end and should be revoked.
        ///
        /// Requires the grant to have actually reached the directory. Revoking on the strength of
        /// an approval alone would attempt to remove a membership that was never added, and record
        /// a revocation for access the person never had.
        /// </summary>
        public static bool AccessHasLapsed(GovAccessRequest request, DateTime nowUtc) =>
            request.Status == GovRequestStatus.Approved
            && request.ExecutionStatus == GovExecutionStatus.Succeeded
            && request.AccessRevokedUtc == null
            && request.AccessExpiresUtc != null
            && request.AccessExpiresUtc <= nowUtc;
    }
}
