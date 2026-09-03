namespace IdentitySyncPro.Core.Models.Governance
{
    /// <summary>
    /// One person's request for one catalog item, from submission to execution.
    ///
    /// <b>The decision and the execution are two separate columns on purpose.</b> Folding them into
    /// one status is how a system ends up with an approved request that never reached Active
    /// Directory: the row says "Approved", the approver saw it succeed, the person still has no
    /// access, and nothing anywhere is in an error state. Keeping
    /// <see cref="ExecutionStatus"/> beside <see cref="Status"/> makes that gap a value you can
    /// query for rather than a silence.
    /// </summary>
    public class GovAccessRequest
    {
        public long Id { get; set; }

        public int CatalogItemId { get; set; }

        // ══════════════════════════════════════
        // من ولمن
        // ══════════════════════════════════════

        /// <summary>حساب AD الذي سيحصل على الوصول (sAMAccountName)</summary>
        public string SubjectAccount { get; set; } = string.Empty;

        /// <summary>اسم صاحب الحساب وقت الطلب — لقطة، فقد يتغيّر أو يختفي لاحقاً</summary>
        public string? SubjectDisplayName { get; set; }

        /// <summary>من قدّم الطلب: مستخدم كونسول أو موظف عبر البوابة</summary>
        public string RequestedBy { get; set; } = string.Empty;

        /// <summary>Console | Portal — القناة التي جاء منها الطلب</summary>
        public string Channel { get; set; } = GovChannels.Console;

        /// <summary>مبرّر الطلب — إلزامي، فهو ما يقرأه المُعتمِد والمدقّق بعد سنة</summary>
        public string Justification { get; set; } = string.Empty;

        // ══════════════════════════════════════
        // القرار
        // ══════════════════════════════════════

        /// <summary>Pending | Approved | Rejected | Cancelled | Expired</summary>
        public string Status { get; set; } = GovRequestStatus.Pending;

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>متى تنتهي مهلة القرار. null = بلا مهلة</summary>
        public DateTime? DecisionDueUtc { get; set; }

        public DateTime? DecidedUtc { get; set; }

        // ══════════════════════════════════════
        // التنفيذ — منفصل عن القرار عمداً
        // ══════════════════════════════════════

        /// <summary>None | Pending | Succeeded | Failed</summary>
        public string ExecutionStatus { get; set; } = GovExecutionStatus.None;

        public DateTime? ExecutedUtc { get; set; }

        /// <summary>سبب فشل التنفيذ كما ورد من AD — لا يُبتلع</summary>
        public string? ExecutionError { get; set; }

        /// <summary>متى يُسحب الوصول تلقائياً. null = دائم</summary>
        public DateTime? AccessExpiresUtc { get; set; }

        /// <summary>متى سُحب فعلاً. يبقى null إن لم يُسحب بعد</summary>
        public DateTime? AccessRevokedUtc { get; set; }

        // Navigation
        public GovCatalogItem? CatalogItem { get; set; }
        public List<GovRequestDecision> Decisions { get; set; } = new();
    }

    /// <summary>
    /// A single act of deciding, kept as its own row rather than two columns on the request.
    ///
    /// The request holds the outcome; this holds who produced it, when, and what they said. That
    /// separation is what an auditor asks for, and it is also what lets a second approval step be
    /// added later without moving any existing data.
    /// </summary>
    public class GovRequestDecision
    {
        public long Id { get; set; }
        public long RequestId { get; set; }

        /// <summary>ترتيب خطوة الاعتماد. واحدة اليوم، والعمود موجود لئلا تُرحَّل البيانات لاحقاً</summary>
        public int StepOrder { get; set; } = 1;

        public string ApproverUsername { get; set; } = string.Empty;

        /// <summary>Approve | Reject</summary>
        public string Decision { get; set; } = string.Empty;

        public string? Comment { get; set; }

        public DateTime DecidedUtc { get; set; } = DateTime.UtcNow;

        public GovAccessRequest? Request { get; set; }
    }

    public static class GovRequestStatus
    {
        public const string Pending = "Pending";
        public const string Approved = "Approved";
        public const string Rejected = "Rejected";
        public const string Cancelled = "Cancelled";
        public const string Expired = "Expired";
    }

    public static class GovExecutionStatus
    {
        /// <summary>لا تنفيذ مطلوب بعد — الطلب لم يُعتمد</summary>
        public const string None = "None";
        public const string Pending = "Pending";
        public const string Succeeded = "Succeeded";
        public const string Failed = "Failed";
    }

    public static class GovDecisions
    {
        public const string Approve = "Approve";
        public const string Reject = "Reject";
    }

    public static class GovChannels
    {
        public const string Console = "Console";
        public const string Portal = "Portal";
    }
}
