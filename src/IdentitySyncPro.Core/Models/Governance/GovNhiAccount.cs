namespace IdentitySyncPro.Core.Models.Governance
{
    /// <summary>
    /// A non-human account the inventory has found, tracked across runs.
    ///
    /// The inventory report answers "what is out there" and forgets. A lifecycle needs the account
    /// to have a history: first seen on this date, claimed by this person, attested on that one,
    /// quarantined because nobody ever answered. That history is what this row is, and it is the
    /// only reason the report could not simply grow a few columns.
    ///
    /// <para><b>Nothing here is written by hand.</b> Rows appear because a scan matched the
    /// classifier, and the scan is the same one the report uses — so a change to the classifier
    /// changes the population, and the population is never a second list to maintain.</para>
    /// </summary>
    public class GovNhiAccount
    {
        public long Id { get; set; }

        // ══════════════════════════════════════
        // الهوية — ولماذا ليست الاسم ولا الـ DN
        // ══════════════════════════════════════

        /// <summary>
        /// <c>objectGUID</c> as a string — the account's identity across runs.
        ///
        /// This is the whole reason the account can have a history at all. A distinguished name
        /// changes the moment somebody moves the account to another OU, and a sAMAccountName
        /// changes on a rename. Keyed on either one, a routine tidy-up in Active Directory would
        /// make a tracked account vanish and a brand-new one appear in its place — losing its
        /// owner, restarting its claim window, and re-asking a question that was answered months
        /// ago. <c>objectGUID</c> is immutable for the life of the object; it survives both.
        /// </summary>
        public string ObjectGuid { get; set; } = string.Empty;

        /// <summary>الخدمة التي اكتشفته — قواعد المصنِّف صفة خدمة، فالمجتمع المُتتبَّع كذلك</summary>
        public int ServiceId { get; set; }

        /// <summary>آخر اسم معروف. يُحدَّث كل تشغيلة — للعرض والبحث لا للهوية</summary>
        public string Account { get; set; } = string.Empty;

        /// <summary>آخر DN معروف. يُحدَّث كل تشغيلة، وتغيّره حدث عادي لا حساب جديد</summary>
        public string DistinguishedName { get; set; } = string.Empty;

        public string? DisplayName { get; set; }
        public string? Description { get; set; }

        // ══════════════════════════════════════
        // ما رأته آخر تشغيلة
        // ══════════════════════════════════════

        public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

        /// <summary>الإشارات التي طابقت في آخر تشغيلة — «spn+ou» ونحوه</summary>
        public string? Signals { get; set; }

        /// <summary>هل يحمل صلاحيات إدارية، بحسب آخر تشغيلة</summary>
        public bool Privileged { get; set; }

        public bool Enabled { get; set; } = true;

        /// <summary>قيمة managedBy من الدليل — مرشَّح للمالك، وليس مالكاً</summary>
        public string? DirectoryOwner { get; set; }

        /// <summary>
        /// One of IdentitySyncPro's own bind accounts.
        ///
        /// Marked, never hidden — it belongs in the inventory, and concealing it would make the
        /// number lie. But it is <b>never quarantined</b>: quarantining a bind account stops every
        /// sync, every password reset, and every AD login at once.
        /// </summary>
        public bool IsSelfAccount { get; set; }

        // ══════════════════════════════════════
        // دورة الحياة
        // ══════════════════════════════════════

        /// <summary>Discovered | Claimed | Quarantined | Exempt | Retired</summary>
        public string State { get; set; } = GovNhiStates.Discovered;

        /// <summary>متى تنتهي مهلة المطالبة بالمالك. تُحسب مرة عند الاكتشاف</summary>
        public DateTime? ClaimDueUtc { get; set; }

        /// <summary>المالك الذي قَبِل المسؤولية — لا الذي اقترحه الدليل</summary>
        public string? OwnerUsername { get; set; }
        public DateTime? OwnerConfirmedUtc { get; set; }

        /// <summary>
        /// Who said "not mine", and when.
        ///
        /// Recorded and not erased, and it <b>does not reset the claim deadline</b>. The deadline
        /// is about how long the account has gone unowned, not about how many people have declined
        /// it — otherwise an account could be kept alive indefinitely by passing it around.
        /// </summary>
        public string? DisownedBy { get; set; }
        public DateTime? DisownedUtc { get; set; }

        // ══════════════════════════════════════
        // الإقرار الدوري
        // ══════════════════════════════════════

        /// <summary>آخر مرة أكّد المالك أن الحساب ما زال لازماً</summary>
        public DateTime? LastAttestedUtc { get; set; }
        public string? LastAttestedBy { get; set; }

        /// <summary>ملاحظة المالك عند آخر إقرار — «يُستخدم في تكامل البوابة»</summary>
        public string? AttestationNote { get; set; }

        // ══════════════════════════════════════
        // الحجر
        // ══════════════════════════════════════

        public DateTime? QuarantinedUtc { get; set; }

        /// <summary>UnclaimedPastDeadline | AttestationLapsed — لماذا حُجر</summary>
        public string? QuarantineReason { get; set; }

        /// <summary>
        /// What was actually carried out in the directory: None | PrivilegeRemoved | Disabled | Failed.
        ///
        /// Deliberately separate from <see cref="State"/>, on the same split between decision and
        /// execution used by access requests and campaigns: "quarantined" is a decision this system
        /// took, while "disabled" is an effect in a directory that may well have failed.
        /// </summary>
        public string QuarantineEffect { get; set; } = GovNhiQuarantineEffects.None;

        public string? QuarantineError { get; set; }

        // ══════════════════════════════════════
        // الاستثناء
        // ══════════════════════════════════════

        /// <summary>لماذا هذا الحساب خارج دورة الحياة — إلزامي مع الاستثناء</summary>
        public string? ExemptReason { get; set; }
        public string? ExemptBy { get; set; }

        /// <summary>
        /// When the exemption ends — <b>required</b>.
        ///
        /// An exemption with no end is a permanent hole opened for a temporary reason and closed by
        /// nobody, because nothing ever brings it back up. Letting it expire returns the account to
        /// the lifecycle and asks the question again.
        /// </summary>
        public DateTime? ExemptUntilUtc { get; set; }

        // ══════════════════════════════════════
        // الاختفاء
        // ══════════════════════════════════════

        // ══════════════════════════════════════
        // التذكير
        // ══════════════════════════════════════

        /// <summary>
        /// When a reminder about this account was last sent.
        ///
        /// A daily sweep with no memory re-sends the same notice every day until somebody acts, and
        /// a reminder that arrives daily is one people filter into a folder they never open. This is
        /// what makes the interval between reminders a choice rather than an accident.
        /// </summary>
        public DateTime? LastNotifiedUtc { get; set; }

        /// <summary>
        /// When it stopped appearing in the directory. The row <b>stays</b> — the record of an
        /// account that was quarantined and then deleted is exactly what an auditor asks about,
        /// and dropping the row erases the answer.
        /// </summary>
        public DateTime? RetiredUtc { get; set; }
    }

    public static class GovNhiStates
    {
        /// <summary>وُجد، ولا مالك قَبِله بعد</summary>
        public const string Discovered = "Discovered";

        /// <summary>مالك قَبِل المسؤولية</summary>
        public const string Claimed = "Claimed";

        /// <summary>مضت المهلة بلا مطالبة، أو انقضى الإقرار ومهلته</summary>
        public const string Quarantined = "Quarantined";

        /// <summary>خارج الدورة عمداً وبمدة منتهية — break-glass ونحوه</summary>
        public const string Exempt = "Exempt";

        /// <summary>لم يعد في الدليل</summary>
        public const string Retired = "Retired";
    }

    public static class GovNhiQuarantineReasons
    {
        public const string UnclaimedPastDeadline = "UnclaimedPastDeadline";
        public const string AttestationLapsed = "AttestationLapsed";
    }

    /// <summary>
    /// What quarantine is allowed to do to the directory — configured per service, defaulting to
    /// nothing at all.
    /// </summary>
    public static class GovNhiEnforcement
    {
        /// <summary>Marked and raised; the directory is not touched. The default.</summary>
        public const string Report = "Report";

        /// <summary>Removed from administrative groups only — reversible, and aimed at the risk rather than the service.</summary>
        public const string RemovePrivilege = "RemovePrivilege";

        /// <summary>The account is disabled.</summary>
        public const string Disable = "Disable";

        public static bool TouchesDirectory(string? mode) =>
            IsKnown(mode) && !string.Equals(mode!.Trim(), Report, StringComparison.OrdinalIgnoreCase);

        public static bool IsKnown(string? mode) =>
            string.Equals(mode?.Trim(), Report, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode?.Trim(), RemovePrivilege, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode?.Trim(), Disable, StringComparison.OrdinalIgnoreCase);
    }

    public static class GovNhiQuarantineEffects
    {
        public const string None = "None";
        public const string PrivilegeRemoved = "PrivilegeRemoved";
        public const string Disabled = "Disabled";
        public const string Failed = "Failed";
    }
}
