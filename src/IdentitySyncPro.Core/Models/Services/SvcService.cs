using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Models.Connectors;

namespace IdentitySyncPro.Core.Models.Services
{
    /// <summary>
    /// Represents a database-to-AD sync service.
    /// Completely independent from the IAM module — uses its own tables (Svc_ prefix).
    /// </summary>
    public class SvcService
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsEnabled { get; set; } = true;

        // ══════════════════════════════════════
        // SERVICE TYPE
        // ══════════════════════════════════════

        /// <summary>"Sync" = مزامنة | "Offboarding" = إخلاء طرف | "EmptyAttrDisable" = تعطيل ناقصي البيانات | "InactiveDisable" = تعطيل الحسابات غير المستخدمة</summary>
        public string ServiceType { get; set; } = "Sync";

        // ══════════════════════════════════════
        // EMPTY-ATTRIBUTE DISABLE SETTINGS (ServiceType = "EmptyAttrDisable")
        // ══════════════════════════════════════

        /// <summary>سمات AD المطلوب فحصها (مفصولة بفاصلة) — أي حساب مفعّل تكون إحداها فارغة يُعطَّل في مكانه. يُعاد استخدام OffboardingSearchOU كنطاق الفحص</summary>
        public string? EmptyCheckAttributes { get; set; }

        // ══════════════════════════════════════
        // INACTIVE-ACCOUNT DISABLE SETTINGS (ServiceType = "InactiveDisable")
        // ══════════════════════════════════════

        /// <summary>عدد أشهر عدم الاستخدام — أي حساب مفعّل لم يُستخدم منذ هذه المدة يُعطَّل في مكانه</summary>
        public int InactivityMonths { get; set; } = 6;

        /// <summary>سمة AD التي تحمل آخر تسجيل دخول (افتراضي lastLogonTimestamp). عند غيابها يُعتمد على whenCreated (لم يُستخدم منذ إنشائه)</summary>
        public string? LastLogonAttribute { get; set; } = "lastLogonTimestamp";

        // ══════════════════════════════════════
        // AD AUDIT / REPORT SETTINGS (ServiceType = "AdAudit")
        // ══════════════════════════════════════

        /// <summary>نوع التقرير: PrivilegedGroups | PasswordNeverExpires | DuplicateAccounts | LockedAccounts | AccessCertification | NonHumanInventory</summary>
        public string? ReportType { get; set; } = "PrivilegedGroups";

        /// <summary>مجموعات AD المطلوب تدقيق عضويتها (مفصولة بفاصلة، اسم أو DN) — لتقارير المجموعات الإدارية وشهادة الوصول</summary>
        public string? AuditGroups { get; set; }

        /// <summary>سمة AD التي يُكشف تكرارها (افتراضي employeeID) — لتقرير الحسابات المكرّرة</summary>
        public string? DuplicateAttribute { get; set; } = "employeeID";

        /// <summary>
        /// ما يفعله تقرير PasswordNeverExpires: <c>Report</c> (افتراضي — قراءة فقط) أو
        /// <c>Remove</c> (يُزيل علم DONT_EXPIRE_PASSWORD فعلياً).
        ///
        /// الافتراضي Report لأن الإزالة تجعل كلمات المرور تنتهي: حساب خدمة يعتمد على كلمة مرور
        /// دائمة قد يتوقف عند أول انتهاء. القيمة الفارغة تُقرأ Report — فأي خدمة أُعدّت قبل وجود
        /// هذا الحقل تبقى قراءة فقط كما كانت.
        /// </summary>
        public string? PwdNeverExpiresAction { get; set; }

        // ══════════════════════════════════════
        // NON-HUMAN ACCOUNT INVENTORY (ReportType = "NonHumanInventory")
        //
        // «ما هو الحساب غير البشري؟» سؤال سياسة لا سؤال كود: جهة تسمّيها svc_* وأخرى sa- وثالثة
        // تضعها في OU وحدها ورابعة تعلّمها بسمة. لذلك كل الإشارات هنا إعدادات، ولا يحمل الكود
        // أي اصطلاح تسمية. التقرير قراءة فقط دائماً — لا وضع كتابة له إطلاقاً.
        // ══════════════════════════════════════

        /// <summary>أنماط أسماء الحسابات غير البشرية (مفصولة بفاصلة، تدعم * مثل: svc_*, sa-*, *_bot)</summary>
        public string? NhiNamePatterns { get; set; }

        /// <summary>الـ OUs التي تُعتبر حساباتها غير بشرية (DN كامل، مفصولة بفاصلة)</summary>
        public string? NhiOUs { get; set; }

        /// <summary>مجموعات AD التي تُعتبر عضويتها دليلاً على أن الحساب غير بشري (اسم أو DN، مفصولة بفاصلة، تشمل العضوية المتداخلة)</summary>
        public string? NhiGroups { get; set; }

        /// <summary>قواعد سمة=قيمة (مفصولة بفاصلة) مثل: employeeType=service, extensionAttribute5=svc</summary>
        public string? NhiAttributeRules { get; set; }

        /// <summary>
        /// إشارة: الحساب لا يحمل سمة المفتاح (<see cref="ADSearchAttribute"/>) — أي أنه خارج إدارة المزامنة.
        ///
        /// إشارة واسعة جداً وحدها في وضع «أيّ منها» (تشمل كل حساب بشري غير مُدار)، وقيمتها الحقيقية
        /// في وضع «كلها» كشرط تضييق: «اسمه svc_* **و** ليس مُداراً بالمزامنة».
        /// </summary>
        public bool NhiFlagNoKeyAttribute { get; set; } = false;

        /// <summary>إشارة: الحساب يحمل علم DONT_EXPIRE_PASSWORD</summary>
        public bool NhiFlagPwdNeverExpires { get; set; } = false;

        /// <summary>إشارة: الحساب يحمل servicePrincipalName — حساب مستخدم بـ SPN هو حساب خدمة بحكم التعريف</summary>
        public bool NhiFlagHasSpn { get; set; } = false;

        /// <summary>تضمين حسابات الخدمة المُدارة (gMSA/MSA) — غير بشرية بحكم التعريف لا بحكم اصطلاح محلي، لذلك الافتراضي مُفعّل وتُضاف دائماً بغضّ النظر عن وضع المطابقة</summary>
        public bool NhiIncludeManagedServiceAccounts { get; set; } = true;

        /// <summary>وضع المطابقة بين الإشارات: Any (افتراضي — أيّ إشارة تكفي) أو All (يجب تحقّق كل الإشارات المضبوطة). الفارغ يُقرأ Any</summary>
        public string? NhiMatchMode { get; set; } = "Any";

        /// <summary>عمر كلمة المرور الذي يُعتبر بعده الاعتماد قديماً (بالأيام، افتراضي 365؛ صفر = تعطيل الفحص)</summary>
        public int NhiCredentialMaxAgeDays { get; set; } = 365;

        /// <summary>مدة عدم النشاط التي يُعتبر بعدها الحساب خاملاً (بالأيام، افتراضي 180؛ صفر = تعطيل الفحص). يُقرأ النشاط من <see cref="LastLogonAttribute"/></summary>
        public int NhiDormantDays { get; set; } = 180;

        // ── دورة الحياة: المطالبة بالمالك، والإقرار الدوري، والحجر ──

        /// <summary>
        /// تشغيل دورة الحياة على ما يجده الجرد — <b>مُطفأة افتراضياً</b>.
        ///
        /// الجرد وحده قراءة فقط ولا يحتاجها، وتشغيلها يعني أن هذه الخدمة صارت تتعقّب مجتمعاً
        /// ويمكن أن تحجر منه. فهي قرار يُتخذ صراحةً لا يُورَث من ترقية.
        /// </summary>
        public bool NhiLifecycleEnabled { get; set; } = false;

        /// <summary>مهلة المطالبة بالمالك من أول ظهور (بالأيام، افتراضي 30)</summary>
        public int NhiClaimDays { get; set; } = 30;

        /// <summary>كل كم يوماً يُعيد المالك تأكيد أن الحساب ما زال لازماً (افتراضي 180)</summary>
        public int NhiAttestationDays { get; set; } = 180;

        /// <summary>مهلة إضافية بعد فوات موعد الإقرار قبل الحجر (بالأيام، افتراضي 14)</summary>
        public int NhiAttestationGraceDays { get; set; } = 14;

        /// <summary>
        /// ما يُسمح للحجر أن يفعله بالدليل: Report (افتراضي) | RemovePrivilege | Disable.
        ///
        /// الافتراضي لا يلمس الدليل إطلاقاً. وتعطيل حساب خدمة يكسر الإنتاج في ساعة لا يربطها
        /// أحد بهذه التشغيلة، فرفع المستوى قرار يُتخذ بعد أن يثبت الجرد أن الأرقام معقولة.
        /// </summary>
        public string? NhiQuarantineMode { get; set; } = "Report";

        /// <summary>أعلى نسبة من المجتمع المُتتبَّع يُسمح بحجرها في تشغيلة واحدة (٪، افتراضي 20)</summary>
        public int NhiMaxQuarantinePercent { get; set; } = 20;

        /// <summary>بريد من يُبلَّغ بالحسابات بلا مالك وبالمحجورة — صندوق أو قائمة بريدية</summary>
        public string? NhiOwnerNotificationEmail { get; set; }

        // ══════════════════════════════════════
        // EXPIRY WARNING / DISABLE SETTINGS (ServiceType = "ExpiryDisable")
        // ══════════════════════════════════════

        /// <summary>سمة AD التي تحمل تاريخ الانتهاء — افتراضي accountExpires (FILETIME). تقبل أيضاً سمة مخصّصة بتاريخ/FILETIME/generalized-time</summary>
        public string? ExpiryAttribute { get; set; } = "accountExpires";

        /// <summary>أيام التنبيه قبل الانتهاء (قائمة مفصولة بفاصلة، مثل 30,7,1) — يُرسل تنبيه عندما تساوي الأيام المتبقية إحداها</summary>
        public string? ExpiryWarnDays { get; set; } = "30,7,1";

        // ══════════════════════════════════════
        // ORPHANED-ACCOUNT SETTINGS (ServiceType = "OrphanCleanup")
        // ══════════════════════════════════════

        /// <summary>الإجراء على الحساب اليتيم: Report (افتراضي) | Disable | DisableAndMove (ينقل إلى TargetOU)</summary>
        public string? OrphanAction { get; set; } = "Report";

        /// <summary>حارس أمان: إن رجعت قراءة المصدر أقل من هذا العدد من السجلّات تتوقّف الخدمة بلا أي إجراء (يمنع كارثة التعطيل الجماعي عند فشل المصدر). افتراضي 1</summary>
        public int MinSourceRecords { get; set; } = 1;

        // ══════════════════════════════════════
        // OFFBOARDING SETTINGS (ServiceType = "Offboarding")
        // ══════════════════════════════════════

        /// <summary>العمود الذي يحتوي على حالة الموظف (مثل STATUS)</summary>
        public string? StatusColumn { get; set; }
        /// <summary>القيمة التي تعني "غير فعّال" (مثل Inactive)</summary>
        public string? StatusValue { get; set; }
        /// <summary>الـ OU المستهدف لنقل الحسابات المعطّلة (مثل OU=EndedContracts,DC=company,DC=com)</summary>
        public string? TargetOU { get; set; }
        /// <summary>الـ OU المحددة للبحث عن المستخدمين عند إخلاء الطرف (بدلاً من البحث في كامل الدومين). إذا تُرك فارغاً يُستخدم ADBaseDN</summary>
        public string? OffboardingSearchOU { get; set; }
        /// <summary>مجموعة AD للاستثناء — أي مستخدم عضو فيها (مباشرة أو عبر مجموعات متداخلة) لا يُطبَّق عليه إخلاء الطرف. اسم المجموعة (sAMAccountName/CN) أو DN كامل</summary>
        public string? OffboardingExclusionGroup { get; set; }
        /// <summary>خاصية AD التي تحتوي على رقم الجوال (مثال: extensionAttribute13, mobile, telephoneNumber)</summary>
        public string? PhoneColumn { get; set; } = "extensionAttribute13";
        /// <summary>عمود اسم الموظف في الفيو</summary>
        public string? EmployeeNameColumn { get; set; }
        /// <summary>معرّف مزود SMS من مركز الرسائل النصية (SmsCenter)</summary>
        public int? SmsProviderId { get; set; }
        /// <summary>تفعيل إرسال SMS عند إخلاء الطرف</summary>
        public bool EnableSms { get; set; } = false;
        /// <summary>قالب رسالة SMS — يدعم {EMPLOYEE_NAME}, {EMPLOYEE_ID}</summary>
        public string? SmsTemplate { get; set; }

        /// <summary>تفعيل إشعار البريد الإلكتروني للإدارة</summary>
        public bool EnableEmailNotification { get; set; } = false;
        /// <summary>البريد الإلكتروني للإدارة (يدعم عدة عناوين مفصولة بـ ;)</summary>
        public string? NotificationEmail { get; set; }
        /// <summary>عنوان البريد — يدعم {EMPLOYEE_NAME}, {EMPLOYEE_ID}</summary>
        public string? EmailSubject { get; set; }
        /// <summary>محتوى البريد HTML — يدعم {EMPLOYEE_NAME}, {EMPLOYEE_ID}, {SAM_ACCOUNT}, {DEPARTMENT}, {JOB_TITLE}</summary>
        public string? EmailBodyTemplate { get; set; }

        // ══════════════════════════════════════
        // SOURCE DATABASE CONNECTION
        // ══════════════════════════════════════

        /// <summary>Provider type: "SqlServer" or "Oracle"</summary>
        public string SourceProvider { get; set; } = "SqlServer";
        public string SourceHost { get; set; } = string.Empty;
        public int SourcePort { get; set; } = 1433;
        public string? SourceDatabase { get; set; }
        public string? SourceUsername { get; set; }
        public string? SourcePassword { get; set; }
        /// <summary>The view or table name to read from</summary>
        public string SourceTableOrView { get; set; } = string.Empty;
        /// <summary>Optional: Use Windows Authentication (SQL Server only)</summary>
        public bool SourceIntegratedSecurity { get; set; } = false;

        // ══════════════════════════════════════
        // TARGET AD CONNECTION
        // ══════════════════════════════════════

        public string ADServer { get; set; } = string.Empty;
        public int ADPort { get; set; } = 389;

        /// <summary>Legacy switch — only used when <see cref="ADSecurityModeSet"/> is false.</summary>
        public bool ADUseSsl { get; set; } = false;

        /// <summary>Explicit LDAP channel mode (overrides <see cref="ADUseSsl"/> once chosen).</summary>
        public LdapSecurityMode ADSecurityMode { get; set; } = LdapSecurityMode.Auto;

        /// <summary>True once an admin picked a mode — distinguishes a real "Auto" from a pre-upgrade row.</summary>
        public bool ADSecurityModeSet { get; set; } = false;

        /// <summary>Accept an internal-CA / self-signed LDAPS certificate.</summary>
        public bool ADAllowUntrustedCertificate { get; set; } = false;

        public string? ADUsername { get; set; }
        public string? ADPassword { get; set; }
        public string ADBaseDN { get; set; } = string.Empty;

        /// <summary>Project the AD fields onto the shared LDAP options.</summary>
        public LdapConnectionOptions ToLdapOptions() => new()
        {
            Server = ADServer,
            Port = ADPort,
            Username = ADUsername,
            Password = ADPassword,
            SecurityMode = ADSecurityModeSet ? ADSecurityMode : LdapConnectionOptions.FromUseSsl(ADUseSsl),
            AllowUntrustedCertificate = ADAllowUntrustedCertificate
        };

        /// <summary>
        /// The AD attribute used to search/match records (e.g., extensionAttribute2, sAMAccountName).
        /// The value from KeySourceColumn will be searched against this attribute.
        /// </summary>
        public string ADSearchAttribute { get; set; } = "extensionAttribute2";

        // ══════════════════════════════════════
        // KEY MAPPING
        // ══════════════════════════════════════

        /// <summary>
        /// The source column used as the key to find the user in AD.
        /// Example: EMPLOYEE_ID — its value is searched in ADSearchAttribute.
        /// </summary>
        public string KeySourceColumn { get; set; } = string.Empty;

        // ══════════════════════════════════════
        // SCHEDULE
        // ══════════════════════════════════════

        /// <summary>Schedule mode: "interval", "daily", "weekly", "monthly", "custom"</summary>
        public string ScheduleMode { get; set; } = "daily";
        public string? ScheduleTime { get; set; } = "02:00";
        public string? ScheduleDays { get; set; }
        public int? ScheduleIntervalMinutes { get; set; }

        /// <summary>يوم الشهر للجدولة الشهرية (1–28). ما فوق 28 يتخطّى أشهراً كاملة في cron، فالحدّ مقصود</summary>
        public int ScheduleDayOfMonth { get; set; } = 1;

        /// <summary>
        /// تعبير cron كما كتبه المسؤول في وضع «مخصص».
        ///
        /// يُحفظ مستقلاً عن <see cref="ScheduleCron"/> — تماماً كما يُحفظ الوقت والأيام بجانبه —
        /// حتى يبقى ما كتبه المستخدم سليماً إن بدّل الوضع ثم عاد إليه.
        /// </summary>
        public string? ScheduleCustomCron { get; set; }

        /// <summary>Computed cron expression for Hangfire</summary>
        public string? ScheduleCron { get; set; }

        // ══════════════════════════════════════
        // METADATA
        // ══════════════════════════════════════

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastRunAt { get; set; }
        public string? LastRunStatus { get; set; }

        // ══════════════════════════════════════
        // NAVIGATION
        // ══════════════════════════════════════

        public List<SvcFieldMapping> FieldMappings { get; set; } = new();
        public List<SvcRunLog> RunLogs { get; set; } = new();
    }
}
