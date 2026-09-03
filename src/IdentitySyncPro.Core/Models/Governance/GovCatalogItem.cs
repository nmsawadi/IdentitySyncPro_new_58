namespace IdentitySyncPro.Core.Models.Governance
{
    /// <summary>
    /// Something a person can ask for: today an Active Directory group membership.
    ///
    /// The catalog exists so that "what may be requested" is a curated list rather than the whole
    /// directory. Without it the request screen would offer every group in the domain, including
    /// the administrative ones, and the approval step would be the only thing standing between a
    /// casual request and Domain Admins.
    ///
    /// <see cref="TargetType"/> is a single value today. It is a column rather than an assumption
    /// because an OU move and an attribute change are the obvious next entries, and a table that
    /// silently means "group" everywhere is one that has to be migrated to say so later.
    /// </summary>
    public class GovCatalogItem
    {
        public int Id { get; set; }

        /// <summary>ما يراه الطالب — لا اسم المجموعة التقني بالضرورة</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>لماذا يُطلب هذا الوصول وما يمنحه — يُعرض للطالب وللمُعتمِد</summary>
        public string? Description { get; set; }

        /// <summary>نوع الهدف: AdGroup (الوحيد حالياً)</summary>
        public string TargetType { get; set; } = GovTargetTypes.AdGroup;

        /// <summary>
        /// الجهة التي يُنفَّذ الطلب على اتصال AD الخاص بها.
        ///
        /// إلزامي: في تثبيت متعدد الجهات لكل جهة دومينها وحساب ربطها، ومجموعة بلا جهة هي مجموعة
        /// لا يُعرف أي دليل تُنفَّذ فيه.
        /// </summary>
        public int TenantId { get; set; }

        /// <summary>اسم مجموعة AD أو DN كامل</summary>
        public string GroupName { get; set; } = string.Empty;

        // ══════════════════════════════════════
        // من يعتمد
        // ══════════════════════════════════════

        /// <summary>مجموعة AD أعضاؤها معتمِدون لهذا العنصر (تشمل العضوية المتداخلة)</summary>
        public string? ApproverAdGroup { get; set; }

        /// <summary>مستخدمو كونسول معتمِدون، مفصولون بفاصلة (أسماء الدخول في AppUsers)</summary>
        public string? ApproverUsers { get; set; }

        /// <summary>
        /// بريد إشعار المُعتمِدين — صندوق أو قائمة بريدية.
        ///
        /// موجود لأن `AppUsers` بلا حقل بريد، وتعداد أعضاء مجموعة الاعتماد عبر LDAP في كل طلب
        /// عملٌ ثقيل وهشّ. فارغ = يُحاول النظام قراءة سمة `mail` لكل مُعتمِد من AD، وإن لم يجد
        /// أحداً **سُجِّل ذلك صراحةً** ولم يُبتلع: طلبٌ لا يعلم به مُعتمِده هو طلب لا يُقرَّر.
        /// </summary>
        public string? ApproverNotificationEmail { get; set; }

        // ══════════════════════════════════════
        // من يطلب
        // ══════════════════════════════════════

        /// <summary>مجموعة AD يُسمح لأعضائها بطلب هذا العنصر. فارغ = متاح للجميع</summary>
        public string? EligibleRequesterGroup { get; set; }

        // ══════════════════════════════════════
        // المهل
        // ══════════════════════════════════════

        /// <summary>مهلة اتخاذ القرار بالأيام (0 = بلا مهلة، يبقى معلّقاً حتى يُقرَّر)</summary>
        public int DecisionDueDays { get; set; } = 7;

        /// <summary>
        /// مدة الوصول بالأيام قبل سحبه تلقائياً (0 = وصول دائم).
        ///
        /// الوصول المؤقّت هو ما يمنع تراكم الصلاحيات، لكنه أيضاً السبب الذي يجعل عضوية تختفي
        /// فجأة — فالسحب يُشعِر الطالب والمُعتمِد، ولا يقع بصمت.
        /// </summary>
        public int AccessDurationDays { get; set; } = 0;

        public bool IsEnabled { get; set; } = true;

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    public static class GovTargetTypes
    {
        public const string AdGroup = "AdGroup";
    }
}
