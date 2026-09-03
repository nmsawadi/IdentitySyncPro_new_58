using System.ComponentModel.DataAnnotations.Schema;
using IdentitySyncPro.Core.Enums;

namespace IdentitySyncPro.Core.Models.Settings
{
    /// <summary>
    /// Represents a tenant configuration with its own data source, domain, and sync settings.
    /// Each tenant (organization) has its own DB, AD domain, mappings, and rules.
    /// </summary>
    public class TenantSettings
    {
        public int Id { get; set; }
        public string TenantName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsActive { get; set; } = true;

        // ══════════════════════════════════════
        // DATA SOURCE (Generic — Oracle/SQL/PG/MySQL)
        // ══════════════════════════════════════
        /// <summary>Database provider: Oracle, SqlServer, PostgreSQL, MySQL</summary>
        public string SourceProvider { get; set; } = "Oracle";
        public string SourceHost { get; set; } = string.Empty;
        public int SourcePort { get; set; } = 1521;
        /// <summary>Database name or Oracle Service Name</summary>
        public string SourceDatabase { get; set; } = string.Empty;
        public string SourceUsername { get; set; } = string.Empty;
        public string SourcePassword { get; set; } = string.Empty;

        /// <summary>
        /// Connect to a SQL Server source as the service account instead of with a stored password.
        ///
        /// The application's own database has offered this since the beginning
        /// (<see cref="DbIntegratedSecurity"/>); the source side did not, so a SQL Server that only
        /// accepts Windows authentication — the normal arrangement in a great many institutions —
        /// could not be used as a source at all. The asymmetry was not a decision, it was an
        /// omission.
        ///
        /// <para>Default <c>false</c>: an upgrade must not change how an existing tenant connects.
        /// SQL Server only — Oracle, PostgreSQL and MySQL take their own credentials.</para>
        /// </summary>
        public bool SourceIntegratedSecurity { get; set; } = false;
        /// <summary>Table or View name to read identity data from</summary>
        public string SourceTableOrView { get; set; } = string.Empty;
        public int SourceCommandTimeout { get; set; } = 300;

        // === Dynamic source schema (columns are NOT hardcoded) ===
        // Rows are read with SELECT * — every column in the view flows into the
        // mapping engine by its real name. Only the key/status columns are
        // structurally required; null/empty falls back to the legacy defaults
        // so existing installations keep working without any change.

        /// <summary>Column holding the numeric identifier (default: IDENTITY_ID)</summary>
        public string? SourceKeyColumn { get; set; }

        /// <summary>Column holding the numeric lifecycle status code (default: STATUSE_CODE)</summary>
        public string? SourceStatusColumn { get; set; }

        /// <summary>Optional column holding a status description (default: STATUS_DESC)</summary>
        public string? SourceStatusDescColumn { get; set; }

        /// <summary>Column holding the mobile phone used for credential SMS (default: MOBILE_PHONE)</summary>
        public string? SourcePhoneColumn { get; set; }

        /// <summary>Optional column holding the display name used in SMS logs (default: first+last legacy columns)</summary>
        public string? SourceDisplayNameColumn { get; set; }

        // NOTE: Self-service password reset is NOT configured per tenant — it is a
        // standalone module (SsprSettings + SsprDomain) that verifies users directly
        // against AD. See IdentitySyncPro.Web /SsprSettings.

        /// <summary>Computed connection string for the data source</summary>
        [NotMapped]
        public string SourceConnectionString => BuildSourceConnectionString();

        // ══════════════════════════════════════
        // TARGET SELECTION
        // ══════════════════════════════════════

        /// <summary>
        /// أي نوع هدف تُزوَّد إليه هذه الجهة: <c>ActiveDirectory</c> (افتراضي) أو <c>Scim</c>.
        ///
        /// الافتراضي هو ما كان عليه النظام قبل وجود هذا العمود، فترقيةٌ لا تُغيّر هدف جهة عاملة.
        /// وجانب المصدر سبق إلى هذا النمط بـ <see cref="SourceProvider"/> — الفارق أن الهدف كان
        /// يُنشَأ مباشرةً بلا مُنتقٍ، فبقي النظام هدفاً واحداً لا لأن المعمارية تمنع، بل لأن
        /// أحداً لم يفتح الباب.
        /// </summary>
        /// <remarks>
        /// <b>Nullable, and it has to be.</b> The upgrade adds this column as NULL so that no
        /// existing tenant is repointed — <see cref="TargetProviders.Normalise"/> reads a blank as
        /// Active Directory, which is what every pre-upgrade row is.
        ///
        /// Declaring it non-nullable made that impossible: EF materialises the whole entity and
        /// calls GetString on the column, so a NULL threw <c>SqlNullValueException</c> before any
        /// of this class's own logic ran. It did not break the target feature — it broke <b>every
        /// screen that loads a tenant</b>, because they all materialise this entity.
        ///
        /// The in-memory provider the tests use has no SQL null semantics and cannot reproduce it.
        /// </remarks>
        public string? TargetProvider { get; set; } = TargetProviders.ActiveDirectory;

        // ══════════════════════════════════════
        // SCIM 2.0 (Target — when TargetProvider = "Scim")
        // ══════════════════════════════════════

        /// <summary>جذر خدمة SCIM، مثل https://idp.example.edu/scim/v2 (بلا /Users)</summary>
        public string? ScimBaseUrl { get; set; }

        /// <summary>رمز Bearer — 🔐 مشفَّر at-rest كسائر الأسرار</summary>
        public string? ScimBearerToken { get; set; }

        /// <summary>قبول شهادة من مرجع داخلي / موقّعة ذاتياً — لخدمة SCIM داخل شبكة معزولة</summary>
        public bool ScimAllowUntrustedCertificate { get; set; } = false;

        /// <summary>
        /// مهلة كل طلب بالثواني.
        ///
        /// موجودة لأن الدرس كلّف هذا النظام كل أتمتته الخلفية مرة: مهلةٌ غير محدودة على مقبس شبكة
        /// توقف مجمَّع العمّال بأكمله، ولا تُبلّغ عن شيء. لا يُضاف عميل HTTP هنا بلا حدّ.
        /// </summary>
        public int ScimTimeoutSeconds { get; set; } = 30;

        // ══════════════════════════════════════
        // ACTIVE DIRECTORY (Target)
        // ══════════════════════════════════════
        public string ADServer { get; set; } = string.Empty;
        public int ADPort { get; set; } = 389;

        /// <summary>Legacy switch — only used when <see cref="ADSecurityModeSet"/> is false.</summary>
        public bool ADUseSsl { get; set; } = false;

        /// <summary>
        /// Explicit LDAP channel mode (overrides <see cref="ADUseSsl"/> once chosen).
        /// Account creation sets a password, so this channel must be encrypted.
        /// </summary>
        public LdapSecurityMode ADSecurityMode { get; set; } = LdapSecurityMode.Auto;

        /// <summary>True once an admin picked a mode — distinguishes a real "Auto" from a pre-upgrade row.</summary>
        public bool ADSecurityModeSet { get; set; } = false;

        /// <summary>Accept an internal-CA / self-signed LDAPS certificate.</summary>
        public bool ADAllowUntrustedCertificate { get; set; } = false;

        /// <summary>Service account username — performs all AD operations (REQUIRED)</summary>
        public string ADUsername { get; set; } = string.Empty;
        /// <summary>Service account password (REQUIRED)</summary>
        public string ADPassword { get; set; } = string.Empty;
        public string ADBaseDN { get; set; } = string.Empty;
        /// <summary>
        /// Fallback password, used only when a create is issued without one. The normal path never
        /// reaches it: SyncEngine generates a random password per identity and sends it by SMS.
        ///
        /// The placeholder deliberately reads as a placeholder, matching every other default in the
        /// codebase. It previously read like a password somebody had chosen, which is worse in two
        /// ways: a tenant may leave such a value in place believing it was intended, and a default
        /// that could pass for a real credential sits in source control — and a repository is not
        /// private forever.
        ///
        /// The stored value is encrypted at rest (EncryptedStringConverter) and never rendered by
        /// the settings view.
        /// </summary>
        public string ADDefaultPassword { get; set; } = "ChangeMe@2026";

        // ══════════════════════════════════════
        // ACCOUNT MATCHING (join to an existing AD account)
        // ══════════════════════════════════════
        // Empty values keep the original behaviour exactly: the account is looked up by the
        // sAMAccountName the identifier mapping produces. That is correct only while the account
        // name is itself derived from a stable source value — a tenant whose name is a numeric
        // identity number can never drift.
        //
        // It stops being correct as soon as the name is derived from a person's name: a spelling
        // correction, a new family name, or two people resolving to the same name all change the
        // lookup key, so the sync stops finding the account it created and creates a second one.
        // Naming those tenants an immutable attribute to join on is what prevents that.

        // ── Provisioning policy ──────────────────────────────────────────────
        // Whether an identity that has no AD account gets one is an organisational decision, not
        // a property of the sync. A tenant may exist purely to maintain and move accounts that
        // another process creates; for it, provisioning every unmatched source row would flood
        // the directory with accounts nobody asked for.

        /// <summary>
        /// When an account is created for a source identity that has none:
        /// <c>Always</c> (default — every unmatched identity is provisioned),
        /// <c>Never</c> (only existing accounts are updated and moved),
        /// <c>Conditional</c> (only identities matching the condition below).
        /// Empty is treated as <c>Always</c>: tenants configured before this setting existed
        /// must keep provisioning, since silently stopping it produces no error at all.
        /// </summary>
        public string? AccountCreationMode { get; set; }

        /// <summary>Source column tested when <see cref="AccountCreationMode"/> is Conditional.</summary>
        public string? AccountCreationConditionField { get; set; }

        /// <summary>Operator (==, !=, in, not_in, gt, lt) for the creation condition.</summary>
        public string? AccountCreationConditionOperator { get; set; }

        /// <summary>Value the creation condition compares against.</summary>
        public string? AccountCreationConditionValue { get; set; }

        /// <summary>
        /// AD attribute holding the immutable source key (e.g. extensionAttribute2), used to find
        /// the existing account instead of guessing its name. Empty = match by sAMAccountName.
        /// </summary>
        public string? ADMatchAttribute { get; set; }

        /// <summary>
        /// Source column whose value is written to (and matched against) <see cref="ADMatchAttribute"/>.
        /// Empty = the tenant's <see cref="SourceKeyColumn"/>.
        /// </summary>
        public string? ADMatchSourceColumn { get; set; }

        /// <summary>
        /// Shape of the discriminator when a generated account name is already taken.
        /// <c>{base}</c> = the generated name, <c>{n}</c> = the number. Empty = <c>{base}{n}</c>.
        /// </summary>
        public string? UsernameCollisionFormat { get; set; }

        /// <summary>First number tried when a generated name collides (default 2 — "maalhareth2").</summary>
        public int UsernameCollisionStart { get; set; } = 2;

        /// <summary>
        /// How many discriminators to try before giving up and failing the record. Bounded so a
        /// misconfigured pattern that maps everyone to one name cannot spin against AD forever.
        /// </summary>
        public int UsernameCollisionMaxAttempts { get; set; } = 20;

        // ══════════════════════════════════════
        // APPLICATION DATABASE (for storing sync state)
        // ══════════════════════════════════════
        public string DatabaseProvider { get; set; } = "SqlServer";
        public string DbHost { get; set; } = string.Empty;
        public int DbPort { get; set; } = 1433;
        public string DbName { get; set; } = string.Empty;
        public string DbUsername { get; set; } = string.Empty;
        public string DbPassword { get; set; } = string.Empty;
        public bool DbIntegratedSecurity { get; set; } = true;
        public bool DbTrustServerCertificate { get; set; } = true;

        [NotMapped]
        public string SqlConnectionString => BuildAppDbConnectionString();

        // ══════════════════════════════════════
        // SYNC SCHEDULING
        // ══════════════════════════════════════
        public int DefaultBatchSize { get; set; } = 1000;

        public string FullSyncMode { get; set; } = "daily";
        public string? FullSyncTime { get; set; } = "02:00";
        public string? FullSyncDays { get; set; }
        public int? FullSyncIntervalMinutes { get; set; }
        public string FullSyncSchedule { get; set; } = "0 2 * * *";

        public string DeltaSyncMode { get; set; } = "interval";
        public string? DeltaSyncTime { get; set; }
        public string? DeltaSyncDays { get; set; }
        public int? DeltaSyncIntervalMinutes { get; set; } = 30;
        public string DeltaSyncSchedule { get; set; } = "*/30 * * * *";

        public string HealthCheckMode { get; set; } = "interval";
        public string? HealthCheckTime { get; set; }
        public int? HealthCheckIntervalMinutes { get; set; } = 10;
        public string HealthCheckSchedule { get; set; } = "*/10 * * * *";

        /// <summary>
        /// Master switch for this tenant's scheduled syncs. With it off, neither the full nor the
        /// delta job is registered, whatever the two switches below say.
        /// </summary>
        public bool EnableAutoSync { get; set; } = false;

        /// <summary>
        /// Narrows <see cref="EnableAutoSync"/> to the full sync alone. Defaults to true so an
        /// existing tenant behaves exactly as before: auto-sync on meant both jobs registered.
        ///
        /// It exists because the two schedules serve different purposes — a delta every 30 minutes
        /// and a full pass every few hours — and suspending one used to force suspending the other.
        /// </summary>
        public bool EnableFullSyncSchedule { get; set; } = true;

        /// <summary>Narrows <see cref="EnableAutoSync"/> to the delta sync alone. See above.</summary>
        public bool EnableDeltaSyncSchedule { get; set; } = true;

        /// <summary>
        /// عند التفعيل، يتم تحديث الميتافيرز وتطبيق قواعد دورة الحياة تلقائياً أثناء المزامنة الكاملة
        /// بدون الحاجة لجلب البيانات من Oracle مرة ثانية.
        /// When enabled, the Metaverse is updated and lifecycle rules are applied automatically
        /// during Full Sync, avoiding a redundant second Oracle data fetch.
        /// </summary>
        public bool EnableLifecycleDuringSync { get; set; } = false;

        // ══════════════════════════════════════
        // NAVIGATION — Mapping, Groups, OU Rules
        // ══════════════════════════════════════
        public List<TenantAttributeMapping> AttributeMappings { get; set; } = new();
        public List<TenantGroupRule> GroupRules { get; set; } = new();
        public List<TenantOURule> OURules { get; set; } = new();

        // ══════════════════════════════════════
        // GLOBAL DEFAULT VALUE (لملء الحقول الفارغة)
        // ══════════════════════════════════════
        /// <summary>استخدم قيمة افتراضية عالمية للحقول الفارغة بدلاً من إيقاف المزامنة</summary>
        public bool UseGlobalDefaultForEmptyFields { get; set; } = false;
        /// <summary>القيمة الافتراضية العالمية — مثل "." أو "-" أو "N/A"</summary>
        public string GlobalDefaultValue { get; set; } = ".";

        /// <summary>
        /// The placeholder as it should actually be applied, given what this tenant provisions to.
        ///
        /// <b>The placeholder is an Active Directory workaround.</b> AD refuses a write that sets an
        /// attribute to an empty string, so institutions put a dot — or a dash, or "N/A" — in the
        /// gap to keep the write legal. That convention makes no sense anywhere else: SCIM is
        /// perfectly happy for an attribute to be absent, and sending the placeholder writes
        /// nonsense into the target as though it were data. A source row with no email address
        /// produced <c>emails[0].value = "."</c> in a SCIM service — a syntactically invalid address
        /// that a stricter service would reject outright, and that a lenient one would store and
        /// hand to whatever reads it next.
        ///
        /// <para>Found by running a real sync end to end. No unit test would have caught it: the
        /// setting is correct, the mapping is correct, and the connector is correct — the mistake is
        /// only visible when an AD convention meets a target that is not AD.</para>
        /// </summary>
        [NotMapped]
        public string EffectiveGlobalDefault =>
            TargetProviders.UsesEmptyAttributePlaceholder(TargetProvider) ? GlobalDefaultValue : string.Empty;

        // ══════════════════════════════════════
        // SMS NOTIFICATION SETTINGS
        // ══════════════════════════════════════
        /// <summary>Enable SMS notification when new identity accounts are created</summary>
        public bool EnableSmsNotification { get; set; } = false;
        /// <summary>Reference to a centrally-managed SMS provider (SmsCenter)</summary>
        public int? SmsProviderId { get; set; }
        /// <summary>SMS API endpoint URL (legacy — use SmsProviderId for new configs)</summary>
        public string SmsApiUrl { get; set; } = string.Empty;
        /// <summary>SMS sender name (legacy — use SmsProviderId for new configs)</summary>
        public string SmsSenderName { get; set; } = string.Empty;
        /// <summary>SMS API username for authentication (legacy)</summary>
        public string SmsApiUsername { get; set; } = string.Empty;
        /// <summary>SMS API password for authentication (legacy)</summary>
        public string SmsApiPassword { get; set; } = string.Empty;
        /// <summary>
        /// SMS message template. Available placeholders:
        /// {USERNAME}, {PASSWORD}, {DISPLAY_NAME}, {IDENTITY_ID}
        /// </summary>
        public string SmsMessageTemplate { get; set; } = "مرحباً {DISPLAY_NAME}، تم إنشاء حسابك الجامعي.\nاسم المستخدم: {USERNAME}\nكلمة المرور: {PASSWORD}";

        // ══════════════════════════════════════
        // METADATA
        // ══════════════════════════════════════
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;

        // ══════════════════════════════════════
        // CONNECTION STRING BUILDERS
        // ══════════════════════════════════════
        /// <summary>
        /// The server part of a SQL Server source connection.
        ///
        /// A named instance and an explicit port contradict each other: <c>HOST\INSTANCE,1433</c>
        /// makes the client use the port and ignore the instance, so it tries the default instance,
        /// finds nothing, and times out fifteen seconds later with "the server was not found". The
        /// port belongs to the default instance; a named instance is resolved by the SQL Browser
        /// service and takes no port at all.
        ///
        /// <para>The effect of appending it unconditionally was that <b>no institution running a
        /// named instance could configure a source</b> — a very ordinary arrangement — and the
        /// error it produced pointed at the network rather than at the setting.</para>
        /// </summary>
        private string SqlServerAddress() =>
            SourceHost.Contains('\\') || SourcePort <= 0
                ? SourceHost
                : $"{SourceHost},{SourcePort}";

        private string BuildSourceConnectionString()
        {
            if (string.IsNullOrEmpty(SourceHost)) return string.Empty;
            return SourceProvider switch
            {
                "Oracle" => $"Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={SourceHost})(PORT={SourcePort}))(CONNECT_DATA=(SERVICE_NAME={SourceDatabase})));User Id={SourceUsername};Password={SourcePassword};",
                "SqlServer" => SourceIntegratedSecurity
                    ? $"Server={SqlServerAddress()};Database={SourceDatabase};Integrated Security=True;TrustServerCertificate=True;"
                    : $"Server={SqlServerAddress()};Database={SourceDatabase};User Id={SourceUsername};Password={SourcePassword};TrustServerCertificate=True;",
                "PostgreSQL" => $"Host={SourceHost};Port={SourcePort};Database={SourceDatabase};Username={SourceUsername};Password={SourcePassword};",
                "MySQL" => $"Server={SourceHost};Port={SourcePort};Database={SourceDatabase};Uid={SourceUsername};Pwd={SourcePassword};",
                _ => string.Empty
            };
        }

        private string BuildAppDbConnectionString()
        {
            if (string.IsNullOrEmpty(DbHost)) return string.Empty;
            return DatabaseProvider switch
            {
                "SqlServer" => DbIntegratedSecurity
                    ? $"Server={DbHost},{DbPort};Database={DbName};Integrated Security=True;TrustServerCertificate={DbTrustServerCertificate};MultipleActiveResultSets=true"
                    : $"Server={DbHost},{DbPort};Database={DbName};User Id={DbUsername};Password={DbPassword};TrustServerCertificate={DbTrustServerCertificate};MultipleActiveResultSets=true",
                "Oracle" => $"Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={DbHost})(PORT={DbPort}))(CONNECT_DATA=(SERVICE_NAME={DbName})));User Id={DbUsername};Password={DbPassword};",
                "PostgreSQL" => $"Host={DbHost};Port={DbPort};Database={DbName};Username={DbUsername};Password={DbPassword};",
                "MySQL" => $"Server={DbHost};Port={DbPort};Database={DbName};Uid={DbUsername};Pwd={DbPassword};",
                _ => string.Empty
            };
        }
    }

    /// <summary>
    /// Application-level settings stored in the database.
    /// </summary>
    public class AppSettings
    {
        public int Id { get; set; }
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;
    }
}
