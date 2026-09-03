namespace IdentitySyncPro.Core.Models.Governance
{
    /// <summary>
    /// A round of access certification: a snapshot of who holds what, put in front of the people
    /// answerable for it, and acted on.
    ///
    /// The difference from the <c>AccessCertification</c> report is the whole point. The report
    /// lists members; nobody has to read it, nothing records that they did, and nothing changes if
    /// they do not. A campaign names the reviewer, holds a deadline, records each decision beside
    /// the person who made it, and carries the revocations out.
    /// </summary>
    public class GovCampaign
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }

        // ══════════════════════════════════════
        // النطاق — من مصدرين
        // ══════════════════════════════════════

        /// <summary>مجموعات AD صريحة، مفصولة بفاصلة. تغطي ما ليس في الكتالوج — وأخطر ما يُراجَع لم يُطلب من الكتالوج قط</summary>
        public string? ScopeGroups { get; set; }

        /// <summary>الجهة التي تُقرأ منها المجموعات الصريحة. إلزامية متى وُجدت مجموعة صريحة واحدة</summary>
        public int? ScopeTenantId { get; set; }

        /// <summary>معرّفات عناصر كتالوج، مفصولة بفاصلة — يرث كل عنصر جهته ومجموعته</summary>
        public string? ScopeCatalogItemIds { get; set; }

        // ══════════════════════════════════════
        // من يراجع
        // ══════════════════════════════════════

        /// <summary>
        /// مُراجِعو الحملة، مفصولون بفاصلة.
        ///
        /// المُراجِع صفة الحملة لا صفة العنصر — حتى عندما يأتي النطاق من الكتالوج. وارثةُ مُعتمِدي
        /// كل عنصر كانت ستجعل «من يراجع ماذا» تتغيّر بتعديلٍ في شاشة أخرى بعد بدء الحملة، وشهادةٌ
        /// لا يُعرف من كان مسؤولاً عنها ليست شهادة.
        /// </summary>
        public string? ReviewerUsers { get; set; }

        /// <summary>مجموعة AD أعضاؤها مُراجِعون (تشمل العضوية المتداخلة)</summary>
        public string? ReviewerAdGroup { get; set; }

        /// <summary>بريد إشعار المُراجِعين — صندوق أو قائمة بريدية</summary>
        public string? ReviewerNotificationEmail { get; set; }

        // ══════════════════════════════════════
        // المهلة وما بعدها
        // ══════════════════════════════════════

        /// <summary>مهلة المراجعة بالأيام من لحظة الإطلاق</summary>
        public int ReviewDays { get; set; } = 14;

        public DateTime? DueUtc { get; set; }

        /// <summary>
        /// أعلى نسبة مهملة يُسمح معها بالسحب التلقائي (٪).
        ///
        /// السحب التلقائي للمهمل هو سياسة النظام، وليس اختيارياً. لكن **مُراجِعاً لم يدخل قط**
        /// يُنتج حملةً مهملةً بالكامل، والسحب حينها يُسقط وصول قسم كامل ليلاً — وهو ليس تنفيذاً
        /// للسياسة بل عرَضٌ لعدم تشغيلها. فإن تجاوز المهمل هذا الحد **تُوقَف السحوبات التلقائية
        /// للحملة كلها** وتُرفع صراحةً، على نفس منطق حارس المصدر الفارغ في `OrphanCleanup`.
        /// </summary>
        public int MaxUndecidedRevokePercent { get; set; } = 50;

        // ══════════════════════════════════════
        // الحالة
        // ══════════════════════════════════════

        /// <summary>Draft | Active | Closed</summary>
        public string Status { get; set; } = GovCampaignStatus.Draft;

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? StartedUtc { get; set; }
        public DateTime? ClosedUtc { get; set; }

        /// <summary>لماذا أُغلقت هكذا — يُملأ عند الإغلاق، ويشمل سبب إيقاف السحب التلقائي إن وقع</summary>
        public string? ClosingNote { get; set; }

        public List<GovCampaignItem> Items { get; set; } = new();
    }

    /// <summary>
    /// One membership under review: this account, in this group, as it stood when the campaign was
    /// launched.
    ///
    /// A snapshot rather than a live read, deliberately. A reviewer decides on what they were shown;
    /// re-reading the directory at decision time would let a membership added mid-campaign be
    /// certified by somebody who never saw it.
    /// </summary>
    public class GovCampaignItem
    {
        public long Id { get; set; }
        public int CampaignId { get; set; }

        public string SubjectAccount { get; set; } = string.Empty;
        public string? SubjectDisplayName { get; set; }

        public string GroupName { get; set; } = string.Empty;
        public int TenantId { get; set; }

        /// <summary>عنصر الكتالوج الذي جاء منه هذا النطاق، إن وُجد — أثرٌ لا أكثر</summary>
        public int? SourceCatalogItemId { get; set; }

        // ══════════════════════════════════════
        // القرار
        // ══════════════════════════════════════

        /// <summary>Pending | Keep | Revoke</summary>
        public string Decision { get; set; } = GovReviewDecisions.Pending;

        /// <summary>من ضغط الزر فعلاً — المُراجِع أو المفوَّض عنه</summary>
        public string? DecidedBy { get; set; }

        /// <summary>
        /// صاحب الصلاحية الأصلي حين يقرّر مفوَّض نيابةً عنه.
        ///
        /// يُسجَّل الاثنان معاً لأن الشهادة التي تقول «قرّر المدير» والمدير في إجازة ليست صحيحة،
        /// والمدقّق الذي يقرأها لا يملك ما يكشف ذلك. `null` تعني أن صاحب الصلاحية قرّر بنفسه.
        /// </summary>
        public string? DecidedOnBehalfOf { get; set; }

        /// <summary>Reviewer | AutoRevokedUndecided — كيف نشأ القرار</summary>
        public string DecisionSource { get; set; } = GovDecisionSources.Reviewer;

        public string? Comment { get; set; }
        public DateTime? DecidedUtc { get; set; }

        // ══════════════════════════════════════
        // التنفيذ — منفصل عن القرار، كما في طلبات الوصول
        // ══════════════════════════════════════

        /// <summary>None | Pending | Succeeded | Failed</summary>
        public string ExecutionStatus { get; set; } = GovExecutionStatus.None;

        public DateTime? ExecutedUtc { get; set; }
        public string? ExecutionError { get; set; }

        public GovCampaign? Campaign { get; set; }
    }

    /// <summary>
    /// One reviewer handing their duty to somebody else for a stated period.
    ///
    /// It exists because undecided memberships are revoked when the deadline passes: without it, a
    /// reviewer on leave silently costs their whole department its access. The delegation is what
    /// makes that policy safe to hold.
    ///
    /// Created by the reviewer for themselves — the person going away is the one who knows when
    /// they leave, when they return, and who can stand in.
    /// </summary>
    public class GovReviewDelegation
    {
        public long Id { get; set; }

        /// <summary>صاحب الصلاحية — من يفوّض</summary>
        public string FromUsername { get; set; } = string.Empty;

        /// <summary>المفوَّض إليه</summary>
        public string ToUsername { get; set; } = string.Empty;

        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }

        public string? Reason { get; set; }

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>متى أُلغي مبكراً — عودة من إجازة قبل موعدها. `null` = ساري حتى نهايته</summary>
        public DateTime? RevokedUtc { get; set; }
    }

    public static class GovCampaignStatus
    {
        public const string Draft = "Draft";
        public const string Active = "Active";
        public const string Closed = "Closed";
    }

    public static class GovReviewDecisions
    {
        public const string Pending = "Pending";
        public const string Keep = "Keep";
        public const string Revoke = "Revoke";
    }

    public static class GovDecisionSources
    {
        /// <summary>قرّره إنسان</summary>
        public const string Reviewer = "Reviewer";

        /// <summary>سُحب لانتهاء المهلة بلا قرار — لا يجوز أن يُقرأ كأن أحداً راجعه</summary>
        public const string AutoRevokedUndecided = "AutoRevokedUndecided";
    }
}
