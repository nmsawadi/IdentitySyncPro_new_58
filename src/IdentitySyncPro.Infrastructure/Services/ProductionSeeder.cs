using IdentitySyncPro.Core.Models.Rules;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Seeds the database with default production data on first run.
    /// Creates: Tenant, Attribute Mappings, Group Rules, OU Rules,
    /// Sync Rules (V2), Lifecycle Rules, and Data Retention settings.
    /// 
    /// Safe to call on every startup — only seeds if no active tenant exists.
    /// </summary>
    public static class ProductionSeeder
    {
        public static async Task SeedAsync(AppDbContext db, ILogger logger)
        {
            // Only seed if no tenant exists yet
            if (await db.TenantSettings.AnyAsync())
            {
                logger.LogInformation("Production seed: Tenant already exists — skipping");
                return;
            }

            logger.LogInformation("Production seed: No tenant found — seeding default data...");

            try
            {
                // ═══════════════════════════════════════════
                // 1. CREATE DEFAULT TENANT
                // ═══════════════════════════════════════════
                var tenant = new TenantSettings
                {
                    TenantName = "الجهة الافتراضية",
                    Description = "مزامنة الهويات من قاعدة البيانات المصدر إلى Active Directory",
                    IsActive = true,

                    // Data Source (Oracle)
                    SourceProvider = "Oracle",
                    SourceHost = "YOUR_ORACLE_HOST",
                    SourcePort = 1521,
                    SourceDatabase = "YOUR_SERVICE_NAME",
                    SourceUsername = "YOUR_ORACLE_USER",
                    SourcePassword = "",
                    SourceTableOrView = "V_IDENTITY_DATA",
                    SourceCommandTimeout = 300,

                    // Active Directory
                    ADServer = "YOUR_AD_SERVER",
                    ADPort = 389,
                    ADUseSsl = false,
                    ADUsername = "YOUR_AD_SERVICE_ACCOUNT",
                    ADPassword = "",
                    ADBaseDN = "DC=example,DC=local",
                    ADDefaultPassword = "ChangeMe@2026",

                    // Application Database
                    DatabaseProvider = "SqlServer",
                    DbHost = "YOUR_SQL_SERVER",
                    DbPort = 1433,
                    DbName = "IdentitySyncProDB",
                    DbIntegratedSecurity = true,
                    DbTrustServerCertificate = true,

                    // Sync Schedule
                    DefaultBatchSize = 1000,
                    FullSyncMode = "daily",
                    FullSyncTime = "02:00",
                    FullSyncSchedule = "0 2 * * *",
                    DeltaSyncMode = "interval",
                    DeltaSyncIntervalMinutes = 30,
                    DeltaSyncSchedule = "*/30 * * * *",
                    HealthCheckMode = "interval",
                    HealthCheckIntervalMinutes = 10,
                    HealthCheckSchedule = "*/10 * * * *",
                    EnableAutoSync = false,

                    // SMS (disabled by default)
                    EnableSmsNotification = false,
                    SmsProviderId = null,
                    SmsApiUrl = "",
                    SmsSenderName = "",
                    SmsApiUsername = "",
                    SmsApiPassword = "",
                    SmsMessageTemplate = "مرحباً {DISPLAY_NAME}، تم إنشاء حسابك.\nاسم المستخدم: {USERNAME}\nكلمة المرور: {PASSWORD}",

                    CreatedDate = DateTime.UtcNow,
                    ModifiedDate = DateTime.UtcNow
                };

                db.TenantSettings.Add(tenant);
                await db.SaveChangesAsync();
                logger.LogInformation("Production seed: Tenant created (Id={TenantId})", tenant.Id);

                // ═══════════════════════════════════════════
                // 2. ATTRIBUTE MAPPINGS (34 mappings)
                // ═══════════════════════════════════════════
                var mappings = new List<TenantAttributeMapping>
                {
                    // === Core Identity ===
                    new() { TenantId = tenant.Id, SourceColumn = "IDENTITY_ID", TargetAttribute = "sAMAccountName", IsRequired = true, IsIdentifier = true, SortOrder = 0 },
                    new() { TenantId = tenant.Id, SourceColumn = "IDENTITY_ID", TargetAttribute = "employeeID", IsRequired = true, SortOrder = 1 },
                    new() { TenantId = tenant.Id, SourceColumn = "FIRST_NAME", TargetAttribute = "givenName", IsRequired = true, SortOrder = 2 },
                    new() { TenantId = tenant.Id, SourceColumn = "LAST_NAME", TargetAttribute = "sn", IsRequired = true, SortOrder = 3 },
                    new() { TenantId = tenant.Id, SourceColumn = "MIDDLE_NAME", TargetAttribute = "initials", Transform = "GetInitials", SortOrder = 4 },
                    new() { TenantId = tenant.Id, SourceColumn = "FIRST_NAME", TargetAttribute = "displayName", Transform = "Concat:{FIRST_NAME} {MIDDLE_NAME} {LAST_NAME}", SortOrder = 5 },
                    new() { TenantId = tenant.Id, SourceColumn = "DISPLAY_NAME", TargetAttribute = "description", SortOrder = 6 },

                    // === Email & Proxy (عدّل النطاق ليطابق نطاق جهتك) ===
                    new() { TenantId = tenant.Id, SourceColumn = "IDENTITY_ID", TargetAttribute = "mail", Transform = "Format:{0}@example.com", SortOrder = 7 },
                    new() { TenantId = tenant.Id, SourceColumn = "IDENTITY_ID", TargetAttribute = "userPrincipalName", Transform = "Format:{0}@example.com", SortOrder = 8 },
                    new() { TenantId = tenant.Id, SourceColumn = "IDENTITY_ID", TargetAttribute = "mailNickname", SortOrder = 9 },
                    new() { TenantId = tenant.Id, SourceColumn = "IDENTITY_ID", TargetAttribute = "proxyAddresses", Transform = "Format:SMTP:{0}@example.com", SortOrder = 10 },
                    new() { TenantId = tenant.Id, SourceColumn = "IDENTITY_ID", TargetAttribute = "proxyAddresses", Transform = "Format:smtp:{0}@example.mail.onmicrosoft.com", SortOrder = 11 },
                    new() { TenantId = tenant.Id, SourceColumn = "IDENTITY_ID", TargetAttribute = "targetAddress", Transform = "Format:SMTP:{0}@example.mail.onmicrosoft.com", SortOrder = 12 },

                    // === Contact & Department ===
                    new() { TenantId = tenant.Id, SourceColumn = "MOBILE_PHONE", TargetAttribute = "mobile", SortOrder = 13 },
                    new() { TenantId = tenant.Id, SourceColumn = "MOBILE_PHONE", TargetAttribute = "telephoneNumber", SortOrder = 14 },
                    new() { TenantId = tenant.Id, SourceColumn = "DEPARTMENT", TargetAttribute = "department", SortOrder = 15 },
                    new() { TenantId = tenant.Id, SourceColumn = "JOB_TITLE", TargetAttribute = "title", SortOrder = 16 },

                    // === Location & Nationality ===
                    new() { TenantId = tenant.Id, SourceColumn = "NATIONALITY", TargetAttribute = "co", SortOrder = 17 },
                    new() { TenantId = tenant.Id, SourceColumn = "LOCATION_DESC", TargetAttribute = "l", SortOrder = 18 },

                    // === Extension Attributes ===
                    new() { TenantId = tenant.Id, SourceColumn = "NATIONAL_ID", TargetAttribute = "extensionAttribute1", SortOrder = 21 },
                    new() { TenantId = tenant.Id, SourceColumn = "IDENTITY_ID", TargetAttribute = "extensionAttribute2", SortOrder = 22 },
                    new() { TenantId = tenant.Id, SourceColumn = "IDENTITY_ID", TargetAttribute = "extensionAttribute3", SortOrder = 23 },
                    new() { TenantId = tenant.Id, SourceColumn = "STATUS_DESC", TargetAttribute = "extensionAttribute4", SortOrder = 24 },
                    new() { TenantId = tenant.Id, SourceColumn = "CATEGORY_DESC", TargetAttribute = "extensionAttribute5", SortOrder = 25 },
                    new() { TenantId = tenant.Id, SourceColumn = "IDENTITY_ID", TargetAttribute = "extensionAttribute6", Transform = "Static:User", SortOrder = 26 },
                    new() { TenantId = tenant.Id, SourceColumn = "NATIONALITY", TargetAttribute = "extensionAttribute11", SortOrder = 27 },
                    new() { TenantId = tenant.Id, SourceColumn = "MOBILE_PHONE", TargetAttribute = "extensionAttribute13", SortOrder = 28 },
                    new() { TenantId = tenant.Id, SourceColumn = "MOBILE_PHONE", TargetAttribute = "extensionAttribute14", SortOrder = 29 },
                    new() { TenantId = tenant.Id, SourceColumn = "JOB_TITLE", TargetAttribute = "extensionAttribute15", SortOrder = 30 },

                    // === Standard HR ===
                    new() { TenantId = tenant.Id, SourceColumn = "NATIONAL_ID", TargetAttribute = "employeeNumber", SortOrder = 31 },
                    new() { TenantId = tenant.Id, SourceColumn = "STATUS_DESC", TargetAttribute = "employeeType", SortOrder = 32 },
                    new() { TenantId = tenant.Id, SourceColumn = "CATEGORY_DESC", TargetAttribute = "company", SortOrder = 33 },
                };
                db.TenantAttributeMappings.AddRange(mappings);
                logger.LogInformation("Production seed: Added {Count} attribute mappings", mappings.Count);

                // ═══════════════════════════════════════════
                // 3. GROUP RULES (3 rules)
                // ═══════════════════════════════════════════
                var groups = new List<TenantGroupRule>
                {
                    new() { TenantId = tenant.Id, GroupName = "All-Users-Group", IsDefault = true, Description = "جميع المستخدمين / All Users" },
                    new() { TenantId = tenant.Id, GroupName = "Group-A", ConditionField = "LOCATION_CODE", ConditionOperator = "==", ConditionValue = "1", Description = "مثال: مجموعة حسب الموقع / Example: site-based group" },
                };
                db.TenantGroupRules.AddRange(groups);
                logger.LogInformation("Production seed: Added {Count} group rules", groups.Count);

                // ═══════════════════════════════════════════
                // 4. OU RULES (1 rule)
                // ═══════════════════════════════════════════
                var ouRules = new List<TenantOURule>
                {
                    new()
                    {
                        TenantId = tenant.Id,
                        OUTemplate = "OU=Users,{BaseDN}",
                        Priority = 1,
                        Description = "القاعدة الافتراضية — كل الحسابات في OU واحد (يمكن استخدام قوالب مثل OU={DEPARTMENT},OU={LOCATION},{BaseDN})"
                    },
                };
                db.TenantOURules.AddRange(ouRules);
                logger.LogInformation("Production seed: Added {Count} OU rules", ouRules.Count);

                await db.SaveChangesAsync();

                // ═══════════════════════════════════════════
                // 5. SYNC RULES V2 (6 rules)
                // ═══════════════════════════════════════════
                var syncRules = new List<SyncRuleV2>
                {
                    // Rule 1: Join — Match identity to existing AD account
                    new()
                    {
                        TenantId = tenant.Id,
                        Name = "ربط الهوية بحسابها في AD",
                        Description = "يطابق المعرّف المصدري (IDENTITY_ID) مع sAMAccountName في Active Directory",
                        RuleType = "Join",
                        Direction = "Inbound",
                        SourceSystem = "Oracle",
                        TargetSystem = "ActiveDirectory",
                        Priority = 10,
                        Enabled = true,
                        ScopeFilter = "IdentityType == User",
                        ConfigurationJson = "{\"joinAttribute\":\"sAMAccountName\",\"sourceAttribute\":\"IDENTITY_ID\"}",
                        CreatedBy = "ProductionSeeder"
                    },

                    // Rule 2: Projection — Create Metaverse entry for new identity
                    new()
                    {
                        TenantId = tenant.Id,
                        Name = "إنشاء سجل Metaverse لهوية جديدة",
                        Description = "ينشئ سجل في قاعدة البيانات الوسيطة لكل هوية جديدة لم يتم ربطها",
                        RuleType = "Projection",
                        Direction = "Inbound",
                        SourceSystem = "Oracle",
                        TargetSystem = "Metaverse",
                        Priority = 20,
                        Enabled = true,
                        ScopeFilter = "IdentityType == User",
                        ConfigurationJson = "{\"identityType\":\"User\",\"initialState\":\"Pending\"}",
                        CreatedBy = "ProductionSeeder"
                    },

                    // Rule 3: ImportFlow — Map source attributes to Metaverse
                    new()
                    {
                        TenantId = tenant.Id,
                        Name = "استيراد بيانات الهوية من المصدر",
                        Description = "ينقل حقول الهوية من قاعدة البيانات المصدر إلى Metaverse (الاسم، القسم، الحالة، الجوال، إلخ)",
                        RuleType = "ImportFlow",
                        Direction = "Inbound",
                        SourceSystem = "Oracle",
                        TargetSystem = "Metaverse",
                        Priority = 30,
                        Enabled = true,
                        ConfigurationJson = "{\"mappings\":[{\"source\":\"FIRST_NAME\",\"target\":\"givenName\",\"transform\":\"none\"},{\"source\":\"LAST_NAME\",\"target\":\"sn\",\"transform\":\"none\"},{\"source\":\"MIDDLE_NAME\",\"target\":\"initials\",\"transform\":\"GetInitials\"},{\"source\":\"DEPARTMENT\",\"target\":\"department\",\"transform\":\"none\"},{\"source\":\"JOB_TITLE\",\"target\":\"title\",\"transform\":\"none\"},{\"source\":\"MOBILE_PHONE\",\"target\":\"mobile\",\"transform\":\"none\"},{\"source\":\"NATIONALITY\",\"target\":\"co\",\"transform\":\"none\"},{\"source\":\"STATUS_DESC\",\"target\":\"employeeType\",\"transform\":\"none\"}]}",
                        CreatedBy = "ProductionSeeder"
                    },

                    // Rule 4: ExportFlow — Map Metaverse attributes to AD
                    new()
                    {
                        TenantId = tenant.Id,
                        Name = "تصدير البيانات إلى Active Directory",
                        Description = "ينقل البيانات المحدثة من Metaverse إلى الحساب في AD",
                        RuleType = "ExportFlow",
                        Direction = "Outbound",
                        SourceSystem = "Metaverse",
                        TargetSystem = "ActiveDirectory",
                        Priority = 40,
                        Enabled = true,
                        ConfigurationJson = "{\"mappings\":[{\"source\":\"givenName\",\"target\":\"givenName\",\"transform\":\"none\"},{\"source\":\"sn\",\"target\":\"sn\",\"transform\":\"none\"},{\"source\":\"initials\",\"target\":\"initials\",\"transform\":\"none\"},{\"source\":\"department\",\"target\":\"department\",\"transform\":\"none\"},{\"source\":\"title\",\"target\":\"title\",\"transform\":\"none\"},{\"source\":\"mobile\",\"target\":\"extensionAttribute13\",\"transform\":\"none\"},{\"source\":\"employeeType\",\"target\":\"extensionAttribute4\",\"transform\":\"none\"}]}",
                        CreatedBy = "ProductionSeeder"
                    },

                    // Rule 5: Provisioning — Create AD account for new identity
                    new()
                    {
                        TenantId = tenant.Id,
                        Name = "إنشاء حساب AD لهوية جديدة",
                        Description = "ينشئ حساب Active Directory جديد للهويات النشطة الجديدة (STATUS_CODE=1) مع تعيين كلمة المرور وتفعيل الحساب",
                        RuleType = "Provisioning",
                        Direction = "Outbound",
                        SourceSystem = "Metaverse",
                        TargetSystem = "ActiveDirectory",
                        Priority = 50,
                        Enabled = true,
                        ScopeFilter = "IdentityType == User",
                        ConditionJson = "{\"field\":\"STATUS_CODE\",\"op\":\"==\",\"value\":\"1\"}",
                        ConfigurationJson = "{\"targetOU\":\"OU=Users,{BaseDN}\",\"enableAccount\":true,\"setPassword\":true,\"addToDefaultGroup\":true}",
                        CreatedBy = "ProductionSeeder"
                    },

                    // Rule 6: Deprovisioning — Handle inactive identities (Safe Sync protected)
                    new()
                    {
                        TenantId = tenant.Id,
                        Name = "⛔ معالجة الهويات غير الفعالة (محمي)",
                        Description = "ينقل حسابات الهويات غير الفعالة إلى OU مناسب. محمي بـ Safe Sync — لا يحذف ولا يعطل أي حساب أبداً",
                        RuleType = "Deprovisioning",
                        Direction = "Outbound",
                        SourceSystem = "Metaverse",
                        TargetSystem = "ActiveDirectory",
                        Priority = 100,
                        Enabled = true,
                        ScopeFilter = "IdentityType == User",
                        ConditionJson = "{\"field\":\"STATUS_CODE\",\"op\":\"not_in\",\"value\":\"1,7\"}",
                        ConfigurationJson = "{\"action\":\"moveOnly\",\"moveToOU\":\"OU=Inactive,{BaseDN}\",\"removeGroups\":false,\"disableAccount\":false}",
                        CreatedBy = "ProductionSeeder"
                    }
                };

                db.SyncRulesV2.AddRange(syncRules);
                await db.SaveChangesAsync();
                logger.LogInformation("Production seed: Added {Count} sync rules (V2)", syncRules.Count);

                // Add FlowMappings for ImportFlow and ExportFlow rules
                var importRule = syncRules.First(r => r.RuleType == "ImportFlow");
                var importFlowMappings = new List<SyncRuleFlowMapping>
                {
                    new() { SyncRuleV2Id = importRule.Id, SourceAttribute = "FIRST_NAME", TargetAttribute = "givenName", Transform = "none", IsRequired = true, DisplayOrder = 1 },
                    new() { SyncRuleV2Id = importRule.Id, SourceAttribute = "LAST_NAME", TargetAttribute = "sn", Transform = "none", IsRequired = true, DisplayOrder = 2 },
                    new() { SyncRuleV2Id = importRule.Id, SourceAttribute = "MIDDLE_NAME", TargetAttribute = "initials", Transform = "GetInitials", DisplayOrder = 3 },
                    new() { SyncRuleV2Id = importRule.Id, SourceAttribute = "FIRST_NAME", TargetAttribute = "displayName", Transform = "Concat:{FIRST_NAME} {MIDDLE_NAME} {LAST_NAME}", IsRequired = true, DisplayOrder = 4 },
                    new() { SyncRuleV2Id = importRule.Id, SourceAttribute = "DEPARTMENT", TargetAttribute = "department", Transform = "none", DisplayOrder = 5 },
                    new() { SyncRuleV2Id = importRule.Id, SourceAttribute = "JOB_TITLE", TargetAttribute = "title", Transform = "none", DisplayOrder = 6 },
                    new() { SyncRuleV2Id = importRule.Id, SourceAttribute = "MOBILE_PHONE", TargetAttribute = "mobile", Transform = "none", DisplayOrder = 7 },
                    new() { SyncRuleV2Id = importRule.Id, SourceAttribute = "NATIONALITY", TargetAttribute = "co", Transform = "none", DisplayOrder = 8 },
                    new() { SyncRuleV2Id = importRule.Id, SourceAttribute = "LOCATION_DESC", TargetAttribute = "l", Transform = "none", DisplayOrder = 9 },
                    new() { SyncRuleV2Id = importRule.Id, SourceAttribute = "STATUS_DESC", TargetAttribute = "employeeType", Transform = "none", DisplayOrder = 10 },
                    new() { SyncRuleV2Id = importRule.Id, SourceAttribute = "CATEGORY_DESC", TargetAttribute = "company", Transform = "none", DisplayOrder = 11 },
                    new() { SyncRuleV2Id = importRule.Id, SourceAttribute = "IDENTITY_ID", TargetAttribute = "mail", Transform = "Format:{0}@example.com", IsRequired = true, DisplayOrder = 12 },
                    new() { SyncRuleV2Id = importRule.Id, SourceAttribute = "IDENTITY_ID", TargetAttribute = "userPrincipalName", Transform = "Format:{0}@example.com", IsRequired = true, DisplayOrder = 13 },
                };
                db.SyncRuleFlowMappings.AddRange(importFlowMappings);

                var exportRule = syncRules.First(r => r.RuleType == "ExportFlow");
                var exportFlowMappings = new List<SyncRuleFlowMapping>
                {
                    new() { SyncRuleV2Id = exportRule.Id, SourceAttribute = "givenName", TargetAttribute = "givenName", Transform = "none", IsRequired = true, DisplayOrder = 1 },
                    new() { SyncRuleV2Id = exportRule.Id, SourceAttribute = "sn", TargetAttribute = "sn", Transform = "none", IsRequired = true, DisplayOrder = 2 },
                    new() { SyncRuleV2Id = exportRule.Id, SourceAttribute = "initials", TargetAttribute = "initials", Transform = "none", DisplayOrder = 3 },
                    new() { SyncRuleV2Id = exportRule.Id, SourceAttribute = "displayName", TargetAttribute = "displayName", Transform = "none", DisplayOrder = 4 },
                    new() { SyncRuleV2Id = exportRule.Id, SourceAttribute = "department", TargetAttribute = "department", Transform = "none", DisplayOrder = 5 },
                    new() { SyncRuleV2Id = exportRule.Id, SourceAttribute = "title", TargetAttribute = "title", Transform = "none", DisplayOrder = 6 },
                    new() { SyncRuleV2Id = exportRule.Id, SourceAttribute = "mobile", TargetAttribute = "extensionAttribute13", Transform = "none", DisplayOrder = 7 },
                    new() { SyncRuleV2Id = exportRule.Id, SourceAttribute = "mobile", TargetAttribute = "extensionAttribute14", Transform = "none", DisplayOrder = 8 },
                    new() { SyncRuleV2Id = exportRule.Id, SourceAttribute = "employeeType", TargetAttribute = "extensionAttribute4", Transform = "none", DisplayOrder = 9 },
                    new() { SyncRuleV2Id = exportRule.Id, SourceAttribute = "co", TargetAttribute = "extensionAttribute11", Transform = "none", DisplayOrder = 10 },
                };
                db.SyncRuleFlowMappings.AddRange(exportFlowMappings);
                await db.SaveChangesAsync();
                logger.LogInformation("Production seed: Added {Count} import + {Count2} export flow mappings",
                    importFlowMappings.Count, exportFlowMappings.Count);

                // ═══════════════════════════════════════════
                // 6. LIFECYCLE RULES (6 rules)
                // ═══════════════════════════════════════════
                var lifecycleRules = new List<LifecycleRule>
                {
                    // Rule 1: Activate new identity (STATUS_CODE == 1)
                    new()
                    {
                        TenantId = tenant.Id,
                        Name = "تفعيل هوية جديدة",
                        Description = "عند ورود هوية نشطة (STATUS_CODE=1)، يتم تغيير حالتها إلى Active فوراً. عدّل رموز الحالة لتطابق نظام جهتك",
                        Enabled = true,
                        Priority = 10,
                        TriggerType = "OnImport",
                        ConditionField = "STATUS_CODE",
                        ConditionOperator = "==",
                        ConditionValue = "1",
                        ActionType = "SetState",
                        ActionValue = "Active",
                        GracePeriodDays = null
                    },

                    // Rule 2: Suspend inactive identity with 30-day grace (example)
                    new()
                    {
                        TenantId = tenant.Id,
                        Name = "تعليق هوية غير نشطة (30 يوم سماح)",
                        Description = "مثال: عند تغيّر الحالة إلى رمز غير نشط (STATUS_CODE=4)، ينتظر 30 يوم ثم يغير الحالة إلى Suspended. إذا عادت خلال الفترة لا يتم التعليق",
                        Enabled = true,
                        Priority = 20,
                        TriggerType = "OnImport",
                        ConditionField = "STATUS_CODE",
                        ConditionOperator = "==",
                        ConditionValue = "4",
                        ActionType = "SetState",
                        ActionValue = "Suspended",
                        GracePeriodDays = 30
                    },

                    // Rule 3: Suspend with 14-day grace (example)
                    new()
                    {
                        TenantId = tenant.Id,
                        Name = "تعليق هوية (14 يوم سماح)",
                        Description = "مثال: عند تغيّر الحالة إلى الرمز 5، ينتظر 14 يوم ثم يغير الحالة إلى Suspended",
                        Enabled = true,
                        Priority = 25,
                        TriggerType = "OnImport",
                        ConditionField = "STATUS_CODE",
                        ConditionOperator = "==",
                        ConditionValue = "5",
                        ActionType = "SetState",
                        ActionValue = "Suspended",
                        GracePeriodDays = 14
                    },

                    // Rule 4: Suspend multiple inactive statuses (6, 9, 10) — immediate
                    new()
                    {
                        TenantId = tenant.Id,
                        Name = "تعليق هويات غير فعالة (فوري)",
                        Description = "مثال: عند تغيّر الحالة إلى أحد الرموز غير الفعالة (6 أو 9 أو 10)، يتم التعليق فوراً",
                        Enabled = true,
                        Priority = 30,
                        TriggerType = "OnImport",
                        ConditionField = "STATUS_CODE",
                        ConditionOperator = "in",
                        ConditionValue = "6,9,10",
                        ActionType = "SetState",
                        ActionValue = "Suspended",
                        GracePeriodDays = null
                    },

                    // Rule 5: Deprovision an ended identity (example: STATUS_CODE == 7)
                    new()
                    {
                        TenantId = tenant.Id,
                        Name = "إيقاف هوية منتهية",
                        Description = "مثال: عند انتهاء علاقة الهوية بالجهة (STATUS_CODE=7)، تُغيَّر الحالة إلى Deprovisioned",
                        Enabled = true,
                        Priority = 40,
                        TriggerType = "OnImport",
                        ConditionField = "STATUS_CODE",
                        ConditionOperator = "==",
                        ConditionValue = "7",
                        ActionType = "SetState",
                        ActionValue = "Deprovisioned",
                        GracePeriodDays = null
                    },

                    // Rule 6: Reactivate returning identity (STATUS_CODE == 1)
                    new()
                    {
                        TenantId = tenant.Id,
                        Name = "إعادة تفعيل هوية عائدة ونقل حسابها",
                        Description = "عند عودة هوية كانت معلقة أو مؤرشفة (STATUS_CODE يعود إلى 1)، يتم إعادة تفعيل حسابها ونقله إلى OU النشطين",
                        Enabled = true,
                        Priority = 50,
                        TriggerType = "OnImport",
                        ConditionField = "STATUS_CODE",
                        ConditionOperator = "==",
                        ConditionValue = "1",
                        ActionType = "Reactivate",
                        ActionValue = null, // OU is determined by OU Rules dynamically
                        GracePeriodDays = null
                    },

                    // Rule 7: Add groups for active identities (STATUS_CODE == 1)
                    new()
                    {
                        TenantId = tenant.Id,
                        Name = "إضافة المجموعات للهوية النشطة",
                        Description = "يضيف الهوية النشطة (STATUS_CODE=1) إلى المجموعات الافتراضية في AD. يعمل عند إعادة التفعيل أو عند معالجة الكل",
                        Enabled = true,
                        Priority = 55,
                        TriggerType = "OnExport",
                        ConditionField = "STATUS_CODE",
                        ConditionOperator = "==",
                        ConditionValue = "1",
                        ActionType = "AddGroups",
                        ActionValue = "All-Users-Group",
                        GracePeriodDays = null
                    },

                    // Rule 8: Move non-active identities to Inactive OU
                    new()
                    {
                        TenantId = tenant.Id,
                        Name = "نقل الهويات غير النشطة إلى OU غير النشطين",
                        Description = "ينقل حساب أي هوية غير نشطة (StatusCode ≠ 1 و ≠ 7) إلى OU=Inactive. المنتهون لهم قاعدة مستقلة",
                        Enabled = true,
                        Priority = 60,
                        TriggerType = "OnExport",
                        ConditionField = "STATUS_CODE",
                        ConditionOperator = "not_in",
                        ConditionValue = "1,7",
                        ActionType = "MoveOU",
                        ActionValue = "OU=Inactive,{BaseDN}",
                        GracePeriodDays = null
                    },

                    // Rule 9: Move archived identities to Archive OU
                    new()
                    {
                        TenantId = tenant.Id,
                        Name = "نقل الهويات المنتهية إلى OU الأرشيف",
                        Description = "ينقل حساب الهوية المنتهية (StatusCode=7) إلى OU=Archive",
                        Enabled = true,
                        Priority = 65,
                        TriggerType = "OnExport",
                        ConditionField = "STATUS_CODE",
                        ConditionOperator = "==",
                        ConditionValue = "7",
                        ActionType = "MoveOU",
                        ActionValue = "OU=Archive,{BaseDN}",
                        GracePeriodDays = null
                    },

                    // Rule 10: Remove non-active identities from all AD groups
                    new()
                    {
                        TenantId = tenant.Id,
                        Name = "إزالة المجموعات للهويات غير النشطة",
                        Description = "يزيل أي هوية غير نشطة (StatusCode ≠ 1) من جميع مجموعات AD (ما عدا Domain Users). احذف هذه القاعدة إذا لا تريد إزالة المجموعات",
                        Enabled = true,
                        Priority = 70,
                        TriggerType = "OnExport",
                        ConditionField = "STATUS_CODE",
                        ConditionOperator = "not_in",
                        ConditionValue = "1",
                        ActionType = "RemoveGroups",
                        ActionValue = null,
                        GracePeriodDays = null
                    }
                };

                db.LifecycleRules.AddRange(lifecycleRules);
                await db.SaveChangesAsync();
                logger.LogInformation("Production seed: Added {Count} lifecycle rules", lifecycleRules.Count);

                // ═══════════════════════════════════════════
                // 7. DATA RETENTION SETTINGS
                // ═══════════════════════════════════════════
                var retentionSettings = new List<AppSettings>
                {
                    new() { Key = "Retention.SyncOperationsDays", Value = "90" },
                    new() { Key = "Retention.SyncRunsDays", Value = "180" },
                    new() { Key = "Retention.AuditEntriesDays", Value = "365" },
                    new() { Key = "Retention.DeadLetterDays", Value = "30" },
                    new() { Key = "Retention.QuarantineDays", Value = "60" }
                };

                foreach (var setting in retentionSettings)
                {
                    if (!await db.AppSettings.AnyAsync(s => s.Key == setting.Key))
                    {
                        db.AppSettings.Add(setting);
                    }
                }
                await db.SaveChangesAsync();
                logger.LogInformation("Production seed: Data retention settings configured");

                logger.LogInformation("═══ Production seed completed successfully ═══");
                logger.LogInformation("  → Tenant: {TenantName}", tenant.TenantName);
                logger.LogInformation("  → Attribute Mappings: {Count}", mappings.Count);
                logger.LogInformation("  → Group Rules: {Count}", groups.Count);
                logger.LogInformation("  → OU Rules: {Count}", ouRules.Count);
                logger.LogInformation("  → Sync Rules (V2): {Count}", syncRules.Count);
                logger.LogInformation("  → Lifecycle Rules: {Count}", lifecycleRules.Count);
                logger.LogInformation("  → Data Retention: configured");
                logger.LogInformation("═══════════════════════════════════════════════");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Production seed failed");
                throw;
            }
        }

        /// <summary>
        /// Ensures critical lifecycle rules exist for existing installations.
        /// Safe to call on every startup — only adds rules that don't already exist.
        /// This handles the case where the database was created before AddGroups support was added.
        /// </summary>
        public static async Task EnsureLifecycleRulesAsync(AppDbContext db, ILogger logger)
        {
            var tenant = await db.TenantSettings.FirstOrDefaultAsync(t => t.IsActive);
            if (tenant == null)
            {
                logger.LogDebug("EnsureLifecycleRules: No active tenant — skipping");
                return;
            }

            var existingRules = await db.LifecycleRules
                .Where(r => r.TenantId == tenant.Id)
                .ToListAsync();

            var rulesAdded = 0;

            // ✅ Rule: AddGroups for active identities (STATUS_CODE == 1)
            if (!existingRules.Any(r => r.ActionType == "AddGroups"))
            {
                // Use the tenant's default group rule if one exists
                var defaultGroup = await db.TenantGroupRules
                    .Where(g => g.TenantId == tenant.Id && g.IsDefault)
                    .Select(g => g.GroupName)
                    .FirstOrDefaultAsync();

                db.LifecycleRules.Add(new LifecycleRule
                {
                    TenantId = tenant.Id,
                    Name = "إضافة المجموعات للهوية النشطة",
                    Description = "يضيف الهوية النشطة (STATUS_CODE=1) إلى المجموعات الافتراضية في AD. يعمل عند إعادة التفعيل أو عند معالجة الكل",
                    Enabled = true,
                    Priority = 55,
                    TriggerType = "OnExport",
                    ConditionField = "STATUS_CODE",
                    ConditionOperator = "==",
                    ConditionValue = "1",
                    ActionType = "AddGroups",
                    ActionValue = defaultGroup ?? "All-Users-Group",
                    GracePeriodDays = null
                });
                rulesAdded++;
                logger.LogInformation("EnsureLifecycleRules: Added 'AddGroups' rule for active identities");
            }

            // ✅ Rule: Reactivate — ensure it exists
            if (!existingRules.Any(r => r.ActionType == "Reactivate"))
            {
                db.LifecycleRules.Add(new LifecycleRule
                {
                    TenantId = tenant.Id,
                    Name = "إعادة تفعيل هوية عائدة ونقل حسابها",
                    Description = "عند عودة هوية كانت معلقة أو مؤرشفة (STATUS_CODE يعود إلى 1)، يتم إعادة تفعيل حسابها",
                    Enabled = true,
                    Priority = 50,
                    TriggerType = "OnImport",
                    ConditionField = "STATUS_CODE",
                    ConditionOperator = "==",
                    ConditionValue = "1",
                    ActionType = "Reactivate",
                    ActionValue = null,
                    GracePeriodDays = null
                });
                rulesAdded++;
                logger.LogInformation("EnsureLifecycleRules: Added 'Reactivate' rule for returning identities");
            }

            // ✅ Rule: RemoveGroups for non-active identities — ensure condition covers archived identities too
            var removeGroupsRule = existingRules.FirstOrDefault(r => r.ActionType == "RemoveGroups");
            if (removeGroupsRule != null && removeGroupsRule.ConditionValue == "1,7")
            {
                // Fix: archived identities should also have groups removed
                removeGroupsRule.ConditionValue = "1";
                removeGroupsRule.ModifiedDate = DateTime.UtcNow;
                logger.LogInformation("EnsureLifecycleRules: Updated 'RemoveGroups' rule — now removes groups from archived identities too");
                rulesAdded++;
            }

            if (rulesAdded > 0)
            {
                await db.SaveChangesAsync();
                logger.LogInformation("EnsureLifecycleRules: {Count} rules added/updated", rulesAdded);
            }
            else
            {
                logger.LogDebug("EnsureLifecycleRules: All lifecycle rules already exist — no changes");
            }
        }
    }
}
