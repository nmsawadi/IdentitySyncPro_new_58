namespace IdentitySyncPro.Core.Models.Governance
{
    /// <summary>
    /// A rule saying two kinds of access must not rest in the same pair of hands.
    ///
    /// This is the question the system could not answer before it existed. It knows who holds what;
    /// it did not know that holding <i>these two things together</i> is the problem — that the
    /// person who registers a supplier must not also be the person who approves paying one, or that
    /// whoever grants access should not also be whoever certifies it.
    ///
    /// <para>Stated as two sets of entitlements rather than a pair of names, because the real
    /// conflict is between <b>duties</b>, and a duty is usually carried by several groups. "Anything
    /// that can create a payment" against "anything that can approve one" is the rule; the
    /// membership lists underneath it change without the rule changing.</para>
    /// </summary>
    public class GovSodPolicy
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// لماذا هذان الواجبان لا يجتمعان — يُقرأ في كل مخالفة وفي كل رفض.
        ///
        /// إلزامي: مخالفةٌ بلا سبب مكتوب تصل إلى مُعتمِد لا يعرف لماذا يُمنع، فيُخفّفها بلا فهم أو
        /// يُوقف طلباً مشروعاً. السبب هو ما يجعل القاعدة قابلة للتطبيق بعد أن يرحل من كتبها.
        /// </summary>
        public string Rationale { get; set; } = string.Empty;

        /// <summary>الجهة التي تُقرأ منها العضويات — القاعدة تخصّ دليلاً بعينه</summary>
        public int TenantId { get; set; }

        // ══════════════════════════════════════
        // الواجبان
        // ══════════════════════════════════════

        /// <summary>مجموعات الواجب الأول، مفصولة بفاصلة</summary>
        public string DutyAGroups { get; set; } = string.Empty;

        /// <summary>اسم الواجب الأول كما يُعرض — «إنشاء المورّدين» ونحوه</summary>
        public string DutyAName { get; set; } = string.Empty;

        public string DutyBGroups { get; set; } = string.Empty;
        public string DutyBName { get; set; } = string.Empty;

        // ══════════════════════════════════════
        // ما يفعله النظام عند التعارض
        // ══════════════════════════════════════

        /// <summary>Detect | Warn | Block — انظر <see cref="GovSodEnforcement"/></summary>
        public string Enforcement { get; set; } = GovSodEnforcement.Detect;

        /// <summary>Low | Medium | High | Critical — للترتيب والعرض، لا يُغيّر السلوك</summary>
        public string Severity { get; set; } = GovSodSeverity.High;

        public bool IsEnabled { get; set; } = true;

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
    }

    /// <summary>
    /// One person actually holding both duties.
    ///
    /// The row persists after the conflict is gone. "This person held both for eleven days in
    /// March" is the question an auditor asks, and a table that only shows today's violations
    /// cannot answer it.
    /// </summary>
    public class GovSodViolation
    {
        public long Id { get; set; }

        public int PolicyId { get; set; }
        public int TenantId { get; set; }

        public string SubjectAccount { get; set; } = string.Empty;
        public string? SubjectDisplayName { get; set; }

        /// <summary>المجموعات التي طابقت الواجب الأول عند هذا الشخص</summary>
        public string MatchedA { get; set; } = string.Empty;
        public string MatchedB { get; set; } = string.Empty;

        public DateTime DetectedUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

        /// <summary>متى اختفى التعارض. الصفّ يبقى — المدّة نفسها هي ما يُسأل عنه</summary>
        public DateTime? ClearedUtc { get; set; }

        // ══════════════════════════════════════
        // التخفيف
        // ══════════════════════════════════════

        /// <summary>
        /// A recorded acceptance that this conflict stands, with a compensating control named.
        ///
        /// Real institutions have conflicts they cannot remove — one person in a two-person office.
        /// The answer is not to delete the finding but to say who accepted the risk, what control
        /// compensates for it, and <b>when the acceptance runs out</b>.
        /// </summary>
        public string? MitigationReason { get; set; }
        public string? MitigatedBy { get; set; }
        public DateTime? MitigatedUtc { get; set; }

        /// <summary>
        /// نهاية التخفيف — <b>إلزامية</b>. قبولُ خطرٍ بلا نهاية ليس قبولاً، هو نسيان.
        /// </summary>
        public DateTime? MitigationExpiresUtc { get; set; }

        public GovSodPolicy? Policy { get; set; }
    }

    /// <summary>
    /// What the system does when a grant would create a conflict — per policy, defaulting to
    /// nothing but a record.
    /// </summary>
    public static class GovSodEnforcement
    {
        /// <summary>يُسجَّل ويُعرض ولا يُوقف شيئاً. الافتراضي</summary>
        public const string Detect = "Detect";

        /// <summary>يُحذَّر المُعتمِد ويُسمح له بالاعتماد مع تبرير مكتوب</summary>
        public const string Warn = "Warn";

        /// <summary>يُرفض الاعتماد — لا يمرّ إلا بتخفيف مُسجَّل مسبقاً</summary>
        public const string Block = "Block";

        public static bool IsKnown(string? mode) =>
            string.Equals(mode?.Trim(), Detect, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode?.Trim(), Warn, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode?.Trim(), Block, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether this mode stops a grant. An unknown mode does <b>not</b> — a policy nobody can
        /// read must not silently become the strictest one and start refusing access.
        /// </summary>
        public static bool Blocks(string? mode) =>
            string.Equals(mode?.Trim(), Block, StringComparison.OrdinalIgnoreCase);

        public static bool Warns(string? mode) =>
            string.Equals(mode?.Trim(), Warn, StringComparison.OrdinalIgnoreCase);
    }

    public static class GovSodSeverity
    {
        public const string Low = "Low";
        public const string Medium = "Medium";
        public const string High = "High";
        public const string Critical = "Critical";

        /// <summary>ترتيب العرض — الأشدّ أولاً، لأن من يتوقف بعد ثلاثة أسطر يجب أن يقرأ أهمّها</summary>
        public static int Rank(string? severity) => severity?.Trim() switch
        {
            Critical => 0,
            High => 1,
            Medium => 2,
            Low => 3,
            _ => 4
        };

        public static bool IsKnown(string? severity) => Rank(severity) < 4;
    }
}
