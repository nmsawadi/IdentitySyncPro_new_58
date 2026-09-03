using IdentitySyncPro.Core.Models.Audit;
using Hangfire;
using Hangfire.SqlServer;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Connectors;
using IdentitySyncPro.Infrastructure.Connectors;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Jobs;
using IdentitySyncPro.Infrastructure.Security;
using IdentitySyncPro.Infrastructure.Services;
using IdentitySyncPro.Web.Filters;
using IdentitySyncPro.Web.Hubs;
using IdentitySyncPro.Web.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Serilog;
using System.Net;

// ProductionSeeder is already in IdentitySyncPro.Infrastructure.Services namespace (already imported on line 8)

var builder = WebApplication.CreateBuilder(args);

// Kestrel announces itself in a "Server: Kestrel" header on every response, which tells an
// attacker the stack before they probe anything. Nothing depends on it. (ZAP, 2026-08-10.)
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

// The log messages are bilingual, and on a Windows console the Arabic half arrives as "??????"
// under the default codepage. The text is written correctly — Logs/identitysync-*.log holds it
// intact — so this is purely what the console can render, but a warning nobody can read is a
// warning that does not work.
//
// Wrapped because there is no console to configure when the app runs under IIS or as a service,
// and a logging convenience must not be able to stop startup.
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (System.IO.IOException) { }

// === Serilog ===
builder.Host.UseSerilog((context, config) =>
    config.ReadFrom.Configuration(context.Configuration));

// === Data Protection (encrypts DB-stored secrets at rest) ===
// Keys are persisted to disk so encrypted secrets remain decryptable across restarts/deployments.
// ⚠️ Back up the DataProtection-Keys folder — losing it makes encrypted passwords unrecoverable.
var dataProtectionKeysPath = Path.Combine(builder.Environment.ContentRootPath, "DataProtection-Keys");
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath))
    .SetApplicationName("IdentitySyncPro");

// === Database ===
// SQL command timeout. The provider default is 30s, which is not enough for the batched
// SaveChanges of a full sync over 100k+ identities on a busy or modest server — a real run died
// at 17k of 111k with "Execution Timeout Expired", leaving the sync half-applied. Configurable
// so a slower environment can raise it without a rebuild.
var sqlCommandTimeout = builder.Configuration.GetValue<int?>("SyncSettings:SqlCommandTimeoutSeconds") ?? 180;
void ConfigureSql(Microsoft.EntityFrameworkCore.Infrastructure.SqlServerDbContextOptionsBuilder sql) =>
    sql.CommandTimeout(sqlCommandTimeout);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"), ConfigureSql));

// === Services Module Database (separate context, same connection, independent tables) ===
builder.Services.AddDbContext<ServicesDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"), ConfigureSql));

// === Account Status Module Database (separate context, same connection, independent table) ===
builder.Services.AddDbContext<AccountStatusDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"), ConfigureSql));

// === Access Governance Database (Gov_ tables: request catalog, requests, decisions) ===
builder.Services.AddDbContext<GovernanceDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"), ConfigureSql));

// === Hangfire ===
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("DefaultConnection"), new SqlServerStorageOptions
    {
        CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
        SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
        QueuePollInterval = TimeSpan.FromSeconds(15),
        UseRecommendedIsolationLevel = true,
        DisableGlobalLocks = true,
        SchemaName = "HangFire"
    }));
builder.Services.AddHangfireServer(options =>
{
    options.Queues = new[] { "sync", "services", "maintenance", "default" };
    options.WorkerCount = 2;
});

// === Default source/target connectors ===
// All source-DB and AD connections now live in the system database (per-tenant TenantSettings,
// plus the SSPR / Services / Account-Status module tables). There is NO appsettings fallback.
// These default singletons carry empty settings and exist only to satisfy DI: every real
// operation builds its connector from a specific tenant via TenantConnectorFactory (which fails
// clearly when a tenant has no connection configured), and the connector diagnostics resolve the
// active tenant from the DB.
var oracleSettings = new OracleConnectionSettings();
builder.Services.AddSingleton(oracleSettings);
builder.Services.AddSingleton<ISourceConnector, OracleConnector>();

var adSettings = new ADConnectionSettings();
builder.Services.AddSingleton(adSettings);
builder.Services.AddSingleton<ITargetConnector, ActiveDirectoryConnector>();

// === Per-tenant connector factory (multi-source support) ===
builder.Services.AddSingleton<ITenantConnectorFactory, TenantConnectorFactory>();

// === Services ===
// The acting user is resolved from the auth cookie and injected into every audit write, so no
// call site has to remember to name who acted. Outside a request (jobs) it resolves to "System".
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentActor, IdentitySyncPro.Web.Services.HttpCurrentActor>();
builder.Services.AddScoped<IdentitySyncPro.Web.Services.UserActivityAuditFilter>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ResilienceService>();
builder.Services.AddScoped<RuleVersioningService>();
builder.Services.AddScoped<RuleValidationService>();
builder.Services.AddSingleton<ISmsService, SmsService>();
builder.Services.AddScoped<SmsRetryService>();
builder.Services.AddTransient<SmsRetryJob>();
builder.Services.AddSingleton<ISyncEngine, SyncEngine>();
builder.Services.AddSingleton<ISyncProgressNotifier, IdentitySyncPro.Web.Services.SignalRProgressNotifier>();
builder.Services.AddScoped<ILifecycleEngine, LifecycleEngine>();
builder.Services.AddScoped<IRulesEngine, RulesEngineService>();
builder.Services.AddTransient<FullSyncJob>();
builder.Services.AddTransient<DeltaSyncJob>();
builder.Services.AddTransient<HealthCheckJob>();
builder.Services.AddTransient<DataRetentionJob>();

// === Services Module (independent from IAM) ===
builder.Services.AddScoped<SvcDatabaseReader>();
builder.Services.AddScoped<SvcSyncExecutor>();
builder.Services.AddScoped<SvcOffboardingExecutor>();
builder.Services.AddScoped<SvcEmptyAttrDisableExecutor>();
builder.Services.AddScoped<SvcInactiveDisableExecutor>();
// The bind accounts IdentitySyncPro itself uses, derived from the settings rather than kept as a
// second list by hand. Read-only reports mark them; anything that acts on accounts must refuse to
// run while one of them is unresolved.
builder.Services.AddScoped<SelfAccountRegistry>();

// === Access governance: requests, decisions, execution, notifications ===
builder.Services.AddScoped<AccessRequestNotifier>();
builder.Services.AddScoped<AccessRequestService>();
builder.Services.AddScoped<AccessGovernanceJob>();
builder.Services.AddScoped<AccessNotificationJob>();
builder.Services.AddScoped<CampaignService>();
builder.Services.AddScoped<CampaignNotificationJob>();

// === Non-human identity lifecycle: ownership, attestation, quarantine ===
builder.Services.AddScoped<NhiLifecycleReconciler>();
builder.Services.AddScoped<NhiLifecycleService>();
builder.Services.AddScoped<NhiNotificationJob>();
builder.Services.AddScoped<SvcAdAuditExecutor>();
builder.Services.AddScoped<SvcExpiryExecutor>();
builder.Services.AddScoped<SvcOrphanExecutor>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddTransient<SvcSyncJob>();
builder.Services.AddSingleton<ISvcProgressNotifier, IdentitySyncPro.Web.Services.SvcSignalRProgressNotifier>();

// === Account Status Module (independent from IAM and Services) ===
builder.Services.AddScoped<AccountStatusService>();
builder.Services.AddScoped<ExcelExportService>();
builder.Services.AddScoped<SettingsTransferService>();

// === API Security ===
builder.Services.AddScoped<ApiKeyAuthAttribute>();

// === CORS (Fix #10: Restrict in production) ===
builder.Services.AddCors(options =>
{
    if (builder.Environment.IsDevelopment())
    {
        options.AddDefaultPolicy(policy =>
            policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
    }
    else
    {
        // ✅ Production: no default CORS (same-origin only)
        options.AddDefaultPolicy(policy =>
            policy.AllowAnyMethod().AllowAnyHeader());
    }
    // ✅ SignalR is consumed by the app's own UI only — same-origin, no cross-origin policy.
    // (Cross-origin requests are rejected; same-origin requests never hit CORS.)
    options.AddPolicy("SignalR", policy => policy.AllowAnyMethod().AllowAnyHeader());
});

// === SignalR ===
builder.Services.AddSignalR();

// === Language Filter ===
builder.Services.AddScoped<IdentitySyncPro.Web.Filters.LanguageFilter>();

// === Session security policy (values are institution policy, so they live in config) ===
// IdleTimeoutMinutes  — sliding inactivity window. Default 10 (common regulatory baseline).
// RequireHttpsCookie  — send the auth cookie over HTTPS only.
//   ⚠️ If the site has no HTTPS binding, `true` means the browser never returns the cookie and
//   NOBODY CAN SIGN IN. Set it to false before deploying to an HTTP-only host.
var securityConfig = builder.Configuration.GetSection("Security");

var idleTimeoutMinutes = securityConfig.GetValue<int?>("IdleTimeoutMinutes") ?? 10;
if (idleTimeoutMinutes < 1 || idleTimeoutMinutes > 480)
{
    // Out-of-range value would otherwise mean "no session at all" or "effectively never expires".
    Console.WriteLine($"⚠️ Security:IdleTimeoutMinutes = {idleTimeoutMinutes} is out of range (1–480). Falling back to 10.");
    idleTimeoutMinutes = 10;
}
var requireHttpsCookie = securityConfig.GetValue<bool?>("RequireHttpsCookie") ?? true;

// Surfaced to the UI so the keep-alive ping period tracks the configured window.
builder.Services.AddSingleton(new IdentitySyncPro.Web.Security.SessionPolicy(idleTimeoutMinutes));

// Maximum password age for LOCAL console users. 0 disables expiry.
var passwordMaxAgeDays = securityConfig.GetValue<int?>("PasswordMaxAgeDays")
                         ?? IdentitySyncPro.Core.Models.Settings.PasswordPolicy.DefaultMaxAgeDays;
if (passwordMaxAgeDays < 0) passwordMaxAgeDays = IdentitySyncPro.Core.Models.Settings.PasswordPolicy.DefaultMaxAgeDays;
builder.Services.AddSingleton(new IdentitySyncPro.Core.Models.Settings.PasswordPolicy(passwordMaxAgeDays));

// === Authentication (cookie) + Authorization ===
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<MfaService>(); // second factor for privileged console accounts
builder.Services.AddScoped<SsprService>(); // public self-service password reset

// Session holds the candidate TOTP secret across the enrollment round trip only — it is never
// written to the database until a code proves the authenticator actually has it.
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(15);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
});
builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.ExpireTimeSpan = TimeSpan.FromMinutes(idleTimeoutMinutes);
        options.SlidingExpiration = true;
        options.Cookie.Name = "IdentitySyncPro.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = requireHttpsCookie
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
    });
builder.Services.AddAuthorization(options =>
{
    // ✅ Everything requires a logged-in user unless explicitly [AllowAnonymous]
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// === Antiforgery (global — AJAX calls send the token via header, injected in _Layout) ===
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");

// === MVC ===
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add<IdentitySyncPro.Web.Filters.LanguageFilter>();
    options.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute());
    // Order matters: prove the second factor before being asked to rotate a password.
    // Ordered before the MFA filter: a portal principal never reaches the MFA screens, and
    // sending it there first would bounce an employee through a console page that is not theirs.
    options.Filters.Add(new IdentitySyncPro.Web.Filters.PortalGuardFilter());
    options.Filters.Add(new IdentitySyncPro.Web.Filters.MfaPendingFilter());
    options.Filters.Add(new IdentitySyncPro.Web.Filters.MustChangePasswordFilter());
    // Registered globally on purpose: a screen added later is covered without anyone remembering.
    options.Filters.Add<IdentitySyncPro.Web.Services.UserActivityAuditFilter>();
});

var app = builder.Build();

// === Initialize secret encryption gateway (before any DbContext read/write) ===
SecretProtection.Initialize(app.Services.GetRequiredService<IDataProtectionProvider>());

// === LDAP request timeout (applies to every module that talks to the directory) ===
// 0 / unset keeps the .NET default of 30 seconds.
IdentitySyncPro.Infrastructure.Connectors.LdapConnectionFactory.DefaultTimeoutSeconds =
    builder.Configuration.GetSection("SyncSettings").GetValue<int>("LdapTimeoutSeconds");

// === Migrate Database ===
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        // Check if this is an existing database created without migrations
        var hasMigrationsTable = false;
        try
        {
            db.Database.ExecuteSqlRaw("SELECT TOP 1 1 FROM [__EFMigrationsHistory]");
            hasMigrationsTable = true;
        }
        catch { logger.LogWarning("__EFMigrationsHistory table not found — first run scenario"); }

        if (!hasMigrationsTable)
        {
            // Existing database: stamp the baseline migration so Migrate() doesn't try to recreate tables
            logger.LogInformation("Existing database detected — stamping baseline migration...");
            db.Database.EnsureCreated();

            // Create migrations history table and stamp the baseline
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '__EFMigrationsHistory')
                CREATE TABLE [__EFMigrationsHistory] (
                    [MigrationId] NVARCHAR(150) NOT NULL PRIMARY KEY,
                    [ProductVersion] NVARCHAR(32) NOT NULL
                );");

            var pendingMigrations = db.Database.GetPendingMigrations().ToList();
            foreach (var migration in pendingMigrations)
            {
            db.Database.ExecuteSqlRaw(
                "INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES ({0}, '8.0.0')", migration);
                logger.LogInformation("Stamped baseline migration: {Migration}", migration);
            }

            // Fix orphaned shadow FK column if it exists
            db.Database.ExecuteSqlRaw(@"
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('MetaverseHistory') AND name = 'MetaverseEntryId1')
                ALTER TABLE MetaverseHistory DROP COLUMN MetaverseEntryId1;");
        }
        else
        {
            // Normal path: apply any pending migrations
            var pending = db.Database.GetPendingMigrations().ToList();
            if (pending.Any())
            {
                logger.LogInformation("Applying {Count} pending migrations...", pending.Count);
                try
                {
                    db.Database.Migrate();
                    logger.LogInformation("Database migrations applied successfully");
                }
                catch (Exception migEx)
                {
                    // Self-heal: on installations first created via EnsureCreated() (full current schema)
                    // plus the idempotent ALTER blocks below, a pending migration whose columns already
                    // exist fails with "already exists". Stamp such pending migrations as applied so the
                    // history is consistent and future genuinely-new migrations aren't blocked behind them.
                    logger.LogWarning(migEx, "Migrate() failed (schema likely already present) — stamping pending migrations as applied");
                    foreach (var migration in pending)
                    {
                        try
                        {
                            db.Database.ExecuteSqlRaw(
                                "IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = {0}) " +
                                "INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES ({0}, '8.0.11')",
                                migration);
                            logger.LogInformation("Stamped pending migration as applied: {Migration}", migration);
                        }
                        catch (Exception stampEx)
                        {
                            logger.LogError(stampEx, "Failed to stamp migration {Migration}", migration);
                        }
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database migration failed");
        Console.WriteLine($"Database migration error: {ex.Message}");
    }

    // === TenantSettings columns added after the baseline schema (idempotent) ===
    // Must run BEFORE anything queries TenantSettings. EF selects every mapped property by
    // name, so a single column missing from the table fails the whole query -- which is
    // exactly what happened when these lived further down with the SMS tables: the seeder
    // ran first and died on "Invalid column name", was caught and logged, and startup
    // carried on with the lifecycle rules never ensured.
    try
    {
        // Concatenated rather than interpolated: ExecuteSqlRaw flags interpolation as an
        // injection risk (EF1002). Both arguments are compile-time literals from the calls
        // below — no caller-supplied value ever reaches this string.
        void AddTenantColumn(string name, string definition) =>
            db.Database.ExecuteSqlRaw(
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TenantSettings')" +
                " AND name = '" + name + "') ALTER TABLE TenantSettings ADD " + name + " " + definition);

        // Account matching / username generation. All nullable or defaulted to the previous
        // behaviour: an existing tenant reads ADMatchAttribute = NULL and keeps matching by
        // sAMAccountName exactly as before.
        AddTenantColumn("ADMatchAttribute", "nvarchar(100) NULL");
        AddTenantColumn("ADMatchSourceColumn", "nvarchar(100) NULL");
        AddTenantColumn("UsernameCollisionFormat", "nvarchar(100) NULL");
        AddTenantColumn("UsernameCollisionStart", "int NOT NULL DEFAULT 2");
        AddTenantColumn("UsernameCollisionMaxAttempts", "int NOT NULL DEFAULT 20");

        // Provisioning policy. Left NULL rather than defaulted to 'Always' so "never configured"
        // stays distinguishable from "explicitly set to Always"; both are read as Always.
        AddTenantColumn("AccountCreationMode", "nvarchar(20) NULL");
        AddTenantColumn("AccountCreationConditionField", "nvarchar(100) NULL");
        AddTenantColumn("AccountCreationConditionOperator", "nvarchar(20) NULL");
        AddTenantColumn("AccountCreationConditionValue", "nvarchar(400) NULL");

        // Per-type schedule switches. EnableAutoSync governed both the full and the delta job
        // together, so suspending one meant suspending the other. Defaulted to 1 so every existing
        // tenant keeps exactly the behaviour it has today: whatever EnableAutoSync says, applied to
        // both — these only ever narrow it.
        AddTenantColumn("EnableFullSyncSchedule", "bit NOT NULL DEFAULT 1");
        AddTenantColumn("EnableDeltaSyncSchedule", "bit NOT NULL DEFAULT 1");
    }
    catch (Exception ex)
    {
        // Fatal on purpose: every later step reads TenantSettings, so continuing only produces
        // a cascade of "Invalid column name" failures that each look like a separate problem.
        logger.LogCritical(ex, "Failed to add TenantSettings columns — startup cannot continue");
        throw;
    }

    // === Seed Production Data (first run only) ===
    try
    {
        await ProductionSeeder.SeedAsync(db, logger);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Production seed failed");
        Console.WriteLine($"Production seed error: {ex.Message}");
    }

    // === Ensure Lifecycle Rules (every startup — adds missing rules for existing installations) ===
    try
    {
        await ProductionSeeder.EnsureLifecycleRulesAsync(db, logger);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "EnsureLifecycleRules failed");
        Console.WriteLine($"EnsureLifecycleRules error: {ex.Message}");
    }

    // === Console users: ensure table exists (idempotent) + seed default admin ===
    try
    {
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'AppUsers')
            BEGIN
                CREATE TABLE [AppUsers] (
                    [Id] int NOT NULL IDENTITY(1,1),
                    [Username] nvarchar(200) NOT NULL,
                    [DisplayName] nvarchar(200) NOT NULL DEFAULT N'',
                    [PasswordHash] nvarchar(500) NULL,
                    [Role] nvarchar(30) NOT NULL DEFAULT N'Viewer',
                    [AuthType] nvarchar(30) NOT NULL DEFAULT N'Local',
                    [IsActive] bit NOT NULL DEFAULT 1,
                    [MustChangePassword] bit NOT NULL DEFAULT 0,
                    [FailedLoginAttempts] int NOT NULL DEFAULT 0,
                    [LockoutUntilUtc] datetime2 NULL,
                    [LastLoginUtc] datetime2 NULL,
                    [CreatedUtc] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                    CONSTRAINT [PK_AppUsers] PRIMARY KEY ([Id])
                );
                CREATE UNIQUE INDEX [IX_AppUsers_Username] ON [AppUsers] ([Username]);
            END

            -- Maximum-password-age tracking. Existing rows are stamped with the upgrade time,
            -- NOT with CreatedUtc: resetting a password never touched CreatedUtc, so treating it
            -- as the last-change date would expire people who changed theirs yesterday. Everyone
            -- gets one full period starting from this upgrade.
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('AppUsers') AND name = 'PasswordChangedUtc')
            BEGIN
                ALTER TABLE [AppUsers] ADD [PasswordChangedUtc] datetime2 NULL;
            END");

        // Separate batch: the column must exist before it can be written to.
        db.Database.ExecuteSqlRaw(@"
            UPDATE [AppUsers]
               SET [PasswordChangedUtc] = GETUTCDATE()
             WHERE [PasswordChangedUtc] IS NULL
               AND [PasswordHash] IS NOT NULL");

        // === Multi-factor authentication (idempotent) ===
        // No backfill and no default enrollment: MfaSettings.IsEnabled starts false so an
        // upgrade can never lock a running system out of its own console.
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('AppUsers') AND name = 'MfaSecret')
                ALTER TABLE [AppUsers] ADD [MfaSecret] nvarchar(1024) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('AppUsers') AND name = 'MfaEnabledUtc')
                ALTER TABLE [AppUsers] ADD [MfaEnabledUtc] datetime2 NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('AppUsers') AND name = 'MfaLastUsedTimeStep')
                ALTER TABLE [AppUsers] ADD [MfaLastUsedTimeStep] bigint NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('AppUsers') AND name = 'MfaRecoveryCodes')
                ALTER TABLE [AppUsers] ADD [MfaRecoveryCodes] nvarchar(4000) NULL;

            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'MfaSettings')
            BEGIN
                CREATE TABLE [MfaSettings] (
                    [Id] int NOT NULL IDENTITY(1,1),
                    [IsEnabled] bit NOT NULL DEFAULT 0,
                    [RequiredRoles] nvarchar(200) NOT NULL DEFAULT N'Admin',
                    [EnforceEnrollment] bit NOT NULL DEFAULT 1,
                    [ModifiedUtc] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                    CONSTRAINT [PK_MfaSettings] PRIMARY KEY ([Id])
                );
            END");

        // Seeded separately, and keyed on the ROW rather than the table: on a fresh database EF
        // creates MfaSettings from the model, so an INSERT nested inside the CREATE block above
        // never runs and the table is left empty. That is not harmless — the first two requests
        // to read the policy would each see no row and each insert one.
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM [MfaSettings])
                INSERT INTO [MfaSettings] ([IsEnabled], [RequiredRoles], [EnforceEnrollment], [ModifiedUtc])
                VALUES (0, N'Admin', 1, GETUTCDATE());");

        if (!await db.AppUsers.AnyAsync())
        {
            // The shipped default account was `admin` / `ChangeMe@2026` — a name and password
            // printed in the source tree. Both are now configurable, and the password defaults to
            // a freshly generated one printed once below, so a fresh install is never reachable
            // with credentials someone can look up.
            var seedUsername = (builder.Configuration["Security:DefaultAdminUsername"] ?? "").Trim();
            if (seedUsername.Length == 0) seedUsername = "isp-admin";

            var configuredPassword = builder.Configuration["Security:DefaultAdminPassword"];
            var seedPassword = string.IsNullOrWhiteSpace(configuredPassword)
                ? IdentitySyncPro.Infrastructure.Services.PasswordGenerator.Generate(16)
                : configuredPassword;

            db.AppUsers.Add(new IdentitySyncPro.Core.Models.Settings.AppUser
            {
                Username = seedUsername,
                DisplayName = "System Administrator",
                Role = IdentitySyncPro.Core.Models.Settings.AppUserRoles.Admin,
                AuthType = IdentitySyncPro.Core.Models.Settings.AppUserAuthTypes.Local,
                PasswordHash = IdentitySyncPro.Infrastructure.Security.PasswordHasher.Hash(seedPassword),
                MustChangePassword = true,
                PasswordChangedUtc = DateTime.UtcNow,
                IsActive = true
            });
            await db.SaveChangesAsync();

            if (string.IsNullOrWhiteSpace(configuredPassword))
            {
                // Printed once and never recoverable afterwards — only the hash is stored.
                // Banner-framed because missing this line means the install cannot be signed into.
                logger.LogWarning(
                    "\n" +
                    "════════════════════════════════════════════════════════════════\n" +
                    "  INITIAL ADMINISTRATOR ACCOUNT CREATED\n" +
                    "    username: {Username}\n" +
                    "    password: {Password}\n" +
                    "  This password is shown ONCE and is not stored in readable form.\n" +
                    "  A password change is FORCED at first sign-in.\n" +
                    "════════════════════════════════════════════════════════════════",
                    seedUsername, seedPassword);
            }
            else
            {
                logger.LogWarning(
                    "⚠️ Initial administrator '{Username}' created from Security:DefaultAdminPassword — " +
                    "a password change is FORCED at first sign-in. Remove that setting once you have signed in.",
                    seedUsername);
            }
        }
        else
        {
            // Existing installs are not touched automatically — renaming the account someone is
            // signed in as, unprompted at startup, is not a decision a boot sequence should make.
            // Surfacing it every boot is: the gap is otherwise invisible until an auditor finds it.
            var defaultNames = new[] { "admin", "administrator", "root", "sa" };
            var stillDefault = await db.AppUsers
                .Where(u => u.IsActive && defaultNames.Contains(u.Username.ToLower()))
                .Select(u => u.Username)
                .ToListAsync();

            if (stillDefault.Count > 0)
            {
                logger.LogWarning(
                    "⚠️ Console account(s) still using a default administrator name: {Names}. " +
                    "Cybersecurity requirements call for these to be renamed or disabled — " +
                    "rename them from Users → rename (the signed-in session survives a rename).",
                    string.Join(", ", stillDefault));
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Console users setup failed");
    }

    // === Self-service password reset: ensure tables exist (idempotent) + settings row ===
    try
    {
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'SsprSettings')
            BEGIN
                CREATE TABLE [SsprSettings] (
                    [Id] int NOT NULL IDENTITY(1,1),
                    [IsEnabled] bit NOT NULL DEFAULT 0,
                    [SmsProviderId] int NULL,
                    [MessageTemplate] nvarchar(1000) NOT NULL DEFAULT N'',
                    [MessageTemplateEn] nvarchar(1000) NOT NULL DEFAULT N'',
                    [NewPasswordTemplate] nvarchar(1000) NOT NULL DEFAULT N'',
                    [NewPasswordTemplateEn] nvarchar(1000) NOT NULL DEFAULT N'',
                    [OtpLifetimeSeconds] int NOT NULL DEFAULT 300,
                    [MaxVerifyAttempts] int NOT NULL DEFAULT 3,
                    [MaxRequestsPerIpPerHour] int NOT NULL DEFAULT 5,
                    [MaxRequestsPerUserPerHour] int NOT NULL DEFAULT 3,
                    [ModifiedUtc] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                    CONSTRAINT [PK_SsprSettings] PRIMARY KEY ([Id])
                );
            END
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('SsprSettings') AND name = 'MessageTemplateEn')
                ALTER TABLE [SsprSettings] ADD [MessageTemplateEn] nvarchar(1000) NOT NULL DEFAULT N'';
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('SsprSettings') AND name = 'NewPasswordTemplate')
                ALTER TABLE [SsprSettings] ADD [NewPasswordTemplate] nvarchar(1000) NOT NULL DEFAULT N'';
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('SsprSettings') AND name = 'NewPasswordTemplateEn')
                ALTER TABLE [SsprSettings] ADD [NewPasswordTemplateEn] nvarchar(1000) NOT NULL DEFAULT N'';
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('SsprSettings') AND name = 'OtpLifetimeSeconds')
                ALTER TABLE [SsprSettings] ADD [OtpLifetimeSeconds] int NOT NULL DEFAULT 300;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('SsprSettings') AND name = 'MaxVerifyAttempts')
                ALTER TABLE [SsprSettings] ADD [MaxVerifyAttempts] int NOT NULL DEFAULT 3;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('SsprSettings') AND name = 'MaxRequestsPerIpPerHour')
                ALTER TABLE [SsprSettings] ADD [MaxRequestsPerIpPerHour] int NOT NULL DEFAULT 5;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('SsprSettings') AND name = 'MaxRequestsPerUserPerHour')
                ALTER TABLE [SsprSettings] ADD [MaxRequestsPerUserPerHour] int NOT NULL DEFAULT 3;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('SsprSettings') AND name = 'MaxFailedIdentityAttempts')
                ALTER TABLE [SsprSettings] ADD [MaxFailedIdentityAttempts] int NOT NULL DEFAULT 5;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('SsprSettings') AND name = 'IpBlockDurationHours')
                ALTER TABLE [SsprSettings] ADD [IpBlockDurationHours] int NOT NULL DEFAULT 24;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('SsprSettings') AND name = 'MaxResetsPerUserPer24h')
                ALTER TABLE [SsprSettings] ADD [MaxResetsPerUserPer24h] int NOT NULL DEFAULT 3;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('SsprDomains') AND name = 'AdSecurityMode')
                ALTER TABLE [SsprDomains] ADD [AdSecurityMode] int NOT NULL DEFAULT 0;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('SsprDomains') AND name = 'AdSecurityModeSet')
                ALTER TABLE [SsprDomains] ADD [AdSecurityModeSet] bit NOT NULL DEFAULT 0;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('SsprDomains') AND name = 'AdAllowUntrustedCertificate')
                ALTER TABLE [SsprDomains] ADD [AdAllowUntrustedCertificate] bit NOT NULL DEFAULT 0;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TenantSettings') AND name = 'ADSecurityMode')
                ALTER TABLE [TenantSettings] ADD [ADSecurityMode] int NOT NULL DEFAULT 0;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TenantSettings') AND name = 'ADSecurityModeSet')
                ALTER TABLE [TenantSettings] ADD [ADSecurityModeSet] bit NOT NULL DEFAULT 0;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TenantSettings') AND name = 'ADAllowUntrustedCertificate')
                ALTER TABLE [TenantSettings] ADD [ADAllowUntrustedCertificate] bit NOT NULL DEFAULT 0;
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'SsprIpBlocks')
            BEGIN
                CREATE TABLE [SsprIpBlocks] (
                    [Id] int NOT NULL IDENTITY(1,1),
                    [ClientIp] nvarchar(64) NOT NULL,
                    [FailedCount] int NOT NULL DEFAULT 0,
                    [FirstFailureUtc] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                    [LastFailureUtc] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                    [BlockedUntilUtc] datetime2 NULL,
                    [LastUsername] nvarchar(256) NULL,
                    CONSTRAINT [PK_SsprIpBlocks] PRIMARY KEY ([Id])
                );
                CREATE UNIQUE INDEX [IX_SsprIpBlocks_ClientIp] ON [SsprIpBlocks] ([ClientIp]);
            END
            -- TriggeredBy now holds a USERNAME, not just the token 'Schedule'/'Manual'.
            -- The value is written when a run finishes, so an overflow would throw at the very end
            -- and take the run's final status with it. Widened to match AppUser.Username (200).
            IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('SyncRuns') AND name = 'TriggeredBy' AND max_length < 400)
                ALTER TABLE [SyncRuns] ALTER COLUMN [TriggeredBy] nvarchar(200) NULL;
            IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_RunLogs') AND name = 'TriggeredBy' AND max_length < 400)
                ALTER TABLE [Svc_RunLogs] ALTER COLUMN [TriggeredBy] nvarchar(200) NOT NULL;
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'AuthDomains')
            BEGIN
                CREATE TABLE [AuthDomains] (
                    [Id] int NOT NULL IDENTITY(1,1),
                    [Name] nvarchar(200) NOT NULL,
                    [AdServer] nvarchar(300) NOT NULL DEFAULT N'',
                    [AdPort] int NOT NULL DEFAULT 389,
                    [AdUseSsl] bit NOT NULL DEFAULT 0,
                    [AdSecurityMode] int NOT NULL DEFAULT 0,
                    [AdSecurityModeSet] bit NOT NULL DEFAULT 0,
                    [AdAllowUntrustedCertificate] bit NOT NULL DEFAULT 0,
                    [SortOrder] int NOT NULL DEFAULT 0,
                    [IsActive] bit NOT NULL DEFAULT 1,
                    [CreatedUtc] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                    CONSTRAINT [PK_AuthDomains] PRIMARY KEY ([Id])
                );
            END
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'SsprDomains')
            BEGIN
                CREATE TABLE [SsprDomains] (
                    [Id] int NOT NULL IDENTITY(1,1),
                    [Name] nvarchar(200) NOT NULL,
                    [AdServer] nvarchar(300) NOT NULL DEFAULT N'',
                    [AdPort] int NOT NULL DEFAULT 389,
                    [AdUseSsl] bit NOT NULL DEFAULT 0,
                    [AdUsername] nvarchar(200) NULL,
                    [AdPassword] nvarchar(1024) NULL,
                    [AdBaseDN] nvarchar(500) NOT NULL DEFAULT N'',
                    [NationalIdAttribute] nvarchar(100) NOT NULL DEFAULT N'employeeNumber',
                    [MobileAttribute] nvarchar(100) NOT NULL DEFAULT N'mobile',
                    [ExcludedGroups] nvarchar(max) NULL,
                    [IsActive] bit NOT NULL DEFAULT 1,
                    [CreatedUtc] datetime2 NOT NULL DEFAULT GETUTCDATE(),
                    CONSTRAINT [PK_SsprDomains] PRIMARY KEY ([Id])
                );
            END");

        if (!await db.SsprSettings.AnyAsync())
        {
            db.SsprSettings.Add(new IdentitySyncPro.Core.Models.Settings.SsprSettings());
            await db.SaveChangesAsync();
        }

        await AuthDomainSeeder.SeedOnceAsync(db, logger);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "SSPR setup failed");
    }
}

// === Migrate SMS Providers table ===
using (var smsScope = app.Services.CreateScope())
{
    var smsDb = smsScope.ServiceProvider.GetRequiredService<AppDbContext>();
    var smsLogger = smsScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var smsTableExists = false;
        try
        {
            var conn = smsDb.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CASE WHEN EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'SmsProviders') THEN 1 ELSE 0 END";
            var scalar = await cmd.ExecuteScalarAsync();
            smsTableExists = Convert.ToInt32(scalar) == 1;
            await conn.CloseAsync();
        }
        catch (Exception ex) { smsLogger.LogWarning(ex, "Could not check SmsProviders table existence"); }

        if (!smsTableExists)
        {
            smsDb.Database.ExecuteSqlRaw(@"
                CREATE TABLE SmsProviders (
                    Id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    Name nvarchar(200) NOT NULL,
                    ApiUrl nvarchar(500) NOT NULL,
                    ApiUsername nvarchar(200) NULL,
                    ApiPassword nvarchar(500) NULL,
                    SenderName nvarchar(100) NULL,
                    IsActive bit NOT NULL DEFAULT 1,
                    CreatedAt datetime2 NOT NULL DEFAULT GETUTCDATE(),
                    Notes nvarchar(500) NULL
                )");
            smsLogger.LogInformation("SmsProviders table created successfully");
        }
        else
        {
            smsLogger.LogInformation("SmsProviders table already exists");
        }

        // SmsSendLogs table — unified outbound SMS log (sync credentials + offboarding). Create if missing.
        smsDb.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'SmsSendLogs')
            CREATE TABLE SmsSendLogs (
                Id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                Source nvarchar(30) NOT NULL DEFAULT 'Sync',
                IdentityId int NOT NULL DEFAULT 0,
                Account nvarchar(256) NULL,
                DisplayName nvarchar(300) NULL,
                PhoneNumber nvarchar(50) NULL,
                Status nvarchar(20) NOT NULL,
                ProviderName nvarchar(200) NULL,
                GatewayResponse nvarchar(2000) NULL,
                SentMessage nvarchar(max) NULL,
                SyncRunId int NULL,
                RetryCount int NOT NULL DEFAULT 0,
                CreatedAt datetime2 NOT NULL DEFAULT GETUTCDATE(),
                LastAttemptAt datetime2 NULL
            )");
        // Upgrade path — add the unified columns to an existing SmsSendLogs table.
        smsDb.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('SmsSendLogs') AND name = 'Source')
                ALTER TABLE SmsSendLogs ADD Source nvarchar(30) NOT NULL DEFAULT 'Sync';
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('SmsSendLogs') AND name = 'Account')
                ALTER TABLE SmsSendLogs ADD Account nvarchar(256) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('SmsSendLogs') AND name = 'SentMessage')
                ALTER TABLE SmsSendLogs ADD SentMessage nvarchar(max) NULL;");
        smsDb.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SmsSendLogs_Status' AND object_id = OBJECT_ID('SmsSendLogs'))
            CREATE INDEX IX_SmsSendLogs_Status ON SmsSendLogs(Status);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SmsSendLogs_CreatedAt' AND object_id = OBJECT_ID('SmsSendLogs'))
            CREATE INDEX IX_SmsSendLogs_CreatedAt ON SmsSendLogs(CreatedAt);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SmsSendLogs_Account' AND object_id = OBJECT_ID('SmsSendLogs'))
            CREATE INDEX IX_SmsSendLogs_Account ON SmsSendLogs(Account);");

        // EmailProviders table — SMTP transport config managed from the Notifications Center
        smsDb.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'EmailProviders')
            CREATE TABLE EmailProviders (
                Id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                Name nvarchar(200) NOT NULL,
                Mode nvarchar(30) NOT NULL DEFAULT 'Authenticated',
                SmtpHost nvarchar(300) NULL,
                SmtpPort int NOT NULL DEFAULT 587,
                Username nvarchar(300) NULL,
                Password nvarchar(1024) NULL,
                FromEmail nvarchar(300) NULL,
                FromName nvarchar(200) NULL,
                EnableSsl bit NOT NULL DEFAULT 1,
                IsActive bit NOT NULL DEFAULT 1,
                Notes nvarchar(500) NULL,
                CreatedAt datetime2 NOT NULL DEFAULT GETUTCDATE()
            )");

        // Add SmsProviderId column to TenantSettings if not exists
        smsDb.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TenantSettings') AND name = 'SmsProviderId')
            ALTER TABLE TenantSettings ADD SmsProviderId int NULL");

        // Add GlobalDefaultValue columns to TenantSettings if not exist
        smsDb.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TenantSettings') AND name = 'UseGlobalDefaultForEmptyFields')
            ALTER TABLE TenantSettings ADD UseGlobalDefaultForEmptyFields bit NOT NULL DEFAULT 0");
        smsDb.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TenantSettings') AND name = 'GlobalDefaultValue')
            ALTER TABLE TenantSettings ADD GlobalDefaultValue nvarchar(max) NOT NULL DEFAULT '.'");

        // Add EnableLifecycleDuringSync column to TenantSettings if not exists
        smsDb.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TenantSettings') AND name = 'EnableLifecycleDuringSync')
            ALTER TABLE TenantSettings ADD EnableLifecycleDuringSync bit NOT NULL DEFAULT 0");

        // TenantSettings columns are added earlier, in the migration block — they must exist
        // before the seeder queries that table, which runs well before this point.
    }
    catch (Exception ex)
    {
        smsLogger.LogError(ex, "SMS Providers table setup failed");
    }
}

// === Migrate Services Database (Svc_ tables) ===
using (var svcScope = app.Services.CreateScope())
{
    var svcDb = svcScope.ServiceProvider.GetRequiredService<ServicesDbContext>();
    var svcLogger = svcScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        // Check if Svc_Services table exists
        var tableExists = false;
        try
        {
            var conn = svcDb.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CASE WHEN EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Svc_Services') THEN 1 ELSE 0 END";
            var scalar = await cmd.ExecuteScalarAsync();
            tableExists = Convert.ToInt32(scalar) == 1;
            await conn.CloseAsync();
        }
        catch (Exception ex) { svcLogger.LogWarning(ex, "Could not check Svc_Services table existence"); }

        if (!tableExists)
        {
            // Create only the Svc_ tables — does not affect existing tables
            var creator = svcDb.Database.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>();
            creator.CreateTables();
            svcLogger.LogInformation("Services database tables (Svc_*) created successfully");
        }
        else
        {
            // Add new offboarding columns if they don't exist (upgrade path)
            var alterStatements = new[]
            {
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'ServiceType') ALTER TABLE Svc_Services ADD ServiceType nvarchar(50) NOT NULL DEFAULT 'Sync'",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'StatusColumn') ALTER TABLE Svc_Services ADD StatusColumn nvarchar(200) NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'StatusValue') ALTER TABLE Svc_Services ADD StatusValue nvarchar(200) NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'TargetOU') ALTER TABLE Svc_Services ADD TargetOU nvarchar(500) NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'EmployeeNameColumn') ALTER TABLE Svc_Services ADD EmployeeNameColumn nvarchar(200) NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'PhoneColumn') ALTER TABLE Svc_Services ADD PhoneColumn nvarchar(200) NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'SmsProviderId') ALTER TABLE Svc_Services ADD SmsProviderId int NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'EnableSms') ALTER TABLE Svc_Services ADD EnableSms bit NOT NULL DEFAULT 0",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'SmsTemplate') ALTER TABLE Svc_Services ADD SmsTemplate nvarchar(max) NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'EnableEmailNotification') ALTER TABLE Svc_Services ADD EnableEmailNotification bit NOT NULL DEFAULT 0",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'NotificationEmail') ALTER TABLE Svc_Services ADD NotificationEmail nvarchar(500) NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'EmailSubject') ALTER TABLE Svc_Services ADD EmailSubject nvarchar(500) NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'EmailBodyTemplate') ALTER TABLE Svc_Services ADD EmailBodyTemplate nvarchar(max) NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'EmptyCheckAttributes') ALTER TABLE Svc_Services ADD EmptyCheckAttributes nvarchar(500) NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'OffboardingExclusionGroup') ALTER TABLE Svc_Services ADD OffboardingExclusionGroup nvarchar(500) NULL",
                // LDAP channel mode (0 = Auto). ADSecurityModeSet=0 keeps legacy ADUseSsl in charge.
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'ADSecurityMode') ALTER TABLE Svc_Services ADD ADSecurityMode int NOT NULL DEFAULT 0",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'ADSecurityModeSet') ALTER TABLE Svc_Services ADD ADSecurityModeSet bit NOT NULL DEFAULT 0",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'ADAllowUntrustedCertificate') ALTER TABLE Svc_Services ADD ADAllowUntrustedCertificate bit NOT NULL DEFAULT 0",
                // Inactive-account disable service
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'InactivityMonths') ALTER TABLE Svc_Services ADD InactivityMonths int NOT NULL DEFAULT 6",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'LastLogonAttribute') ALTER TABLE Svc_Services ADD LastLogonAttribute nvarchar(100) NULL",
                // AD audit / report service
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'ReportType') ALTER TABLE Svc_Services ADD ReportType nvarchar(50) NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'AuditGroups') ALTER TABLE Svc_Services ADD AuditGroups nvarchar(2000) NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'DuplicateAttribute') ALTER TABLE Svc_Services ADD DuplicateAttribute nvarchar(100) NULL",
                // NULL reads as "Report", so an AdAudit service configured before this column
                // existed stays read-only rather than starting to clear flags.
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'PwdNeverExpiresAction') ALTER TABLE Svc_Services ADD PwdNeverExpiresAction nvarchar(20) NULL",
                // Non-human account inventory (ReportType = NonHumanInventory). Every classifier
                // rule is NULL/0 by default: an upgrade must not invent a naming convention on
                // behalf of an institution, and a service with no rules refuses to run rather than
                // reporting an empty — and falsely reassuring — inventory.
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'NhiNamePatterns') ALTER TABLE Svc_Services ADD NhiNamePatterns nvarchar(1000) NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'NhiOUs') ALTER TABLE Svc_Services ADD NhiOUs nvarchar(2000) NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'NhiGroups') ALTER TABLE Svc_Services ADD NhiGroups nvarchar(2000) NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'NhiAttributeRules') ALTER TABLE Svc_Services ADD NhiAttributeRules nvarchar(1000) NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'NhiFlagNoKeyAttribute') ALTER TABLE Svc_Services ADD NhiFlagNoKeyAttribute bit NOT NULL DEFAULT 0",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'NhiFlagPwdNeverExpires') ALTER TABLE Svc_Services ADD NhiFlagPwdNeverExpires bit NOT NULL DEFAULT 0",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'NhiFlagHasSpn') ALTER TABLE Svc_Services ADD NhiFlagHasSpn bit NOT NULL DEFAULT 0",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'NhiIncludeManagedServiceAccounts') ALTER TABLE Svc_Services ADD NhiIncludeManagedServiceAccounts bit NOT NULL DEFAULT 1",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'NhiMatchMode') ALTER TABLE Svc_Services ADD NhiMatchMode nvarchar(10) NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'NhiCredentialMaxAgeDays') ALTER TABLE Svc_Services ADD NhiCredentialMaxAgeDays int NOT NULL DEFAULT 365",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'NhiDormantDays') ALTER TABLE Svc_Services ADD NhiDormantDays int NOT NULL DEFAULT 180",
                // Lifecycle. Off by default: an upgrade must never switch on a sweep that can quarantine.
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'NhiLifecycleEnabled') ALTER TABLE Svc_Services ADD NhiLifecycleEnabled bit NOT NULL DEFAULT 0",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'NhiClaimDays') ALTER TABLE Svc_Services ADD NhiClaimDays int NOT NULL DEFAULT 30",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'NhiAttestationDays') ALTER TABLE Svc_Services ADD NhiAttestationDays int NOT NULL DEFAULT 180",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'NhiAttestationGraceDays') ALTER TABLE Svc_Services ADD NhiAttestationGraceDays int NOT NULL DEFAULT 14",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'NhiQuarantineMode') ALTER TABLE Svc_Services ADD NhiQuarantineMode nvarchar(30) NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'NhiMaxQuarantinePercent') ALTER TABLE Svc_Services ADD NhiMaxQuarantinePercent int NOT NULL DEFAULT 20",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'NhiOwnerNotificationEmail') ALTER TABLE Svc_Services ADD NhiOwnerNotificationEmail nvarchar(500) NULL",
                // Expiry warning / disable service
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'ExpiryAttribute') ALTER TABLE Svc_Services ADD ExpiryAttribute nvarchar(100) NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'ExpiryWarnDays') ALTER TABLE Svc_Services ADD ExpiryWarnDays nvarchar(100) NULL",
                // Orphaned-account cleanup service
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'OrphanAction') ALTER TABLE Svc_Services ADD OrphanAction nvarchar(30) NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'MinSourceRecords') ALTER TABLE Svc_Services ADD MinSourceRecords int NOT NULL DEFAULT 1",
                // Second target: which directory a tenant provisions into, and how to reach it.
                // TargetProvider is NULL on every existing row and reads as ActiveDirectory, so an
                // upgrade never repoints a working tenant.
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TenantSettings') AND name = 'TargetProvider') ALTER TABLE TenantSettings ADD TargetProvider nvarchar(30) NULL",
                // Windows authentication for a SQL Server source. Default 0 so an upgrade never
                // changes how an existing tenant connects.
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TenantSettings') AND name = 'SourceIntegratedSecurity') ALTER TABLE TenantSettings ADD SourceIntegratedSecurity bit NOT NULL DEFAULT 0",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TenantSettings') AND name = 'ScimBaseUrl') ALTER TABLE TenantSettings ADD ScimBaseUrl nvarchar(500) NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TenantSettings') AND name = 'ScimBearerToken') ALTER TABLE TenantSettings ADD ScimBearerToken nvarchar(2048) NULL",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TenantSettings') AND name = 'ScimAllowUntrustedCertificate') ALTER TABLE TenantSettings ADD ScimAllowUntrustedCertificate bit NOT NULL DEFAULT 0",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TenantSettings') AND name = 'ScimTimeoutSeconds') ALTER TABLE TenantSettings ADD ScimTimeoutSeconds int NOT NULL DEFAULT 30",
                // Monthly schedule + the custom cron expression the "custom" mode never had a field for.
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'ScheduleDayOfMonth') ALTER TABLE Svc_Services ADD ScheduleDayOfMonth int NOT NULL DEFAULT 1",
                "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Svc_Services') AND name = 'ScheduleCustomCron') ALTER TABLE Svc_Services ADD ScheduleCustomCron nvarchar(200) NULL",
                // A service already saved as "custom" has been running on the silent daily fallback
                // (0 2 * * *) because nothing could carry an expression to it. Its stored cron is
                // therefore what it actually does today, and seeding the new column from it keeps
                // that unchanged — the alternative is a service that stops saving, or one whose
                // schedule shifts on an upgrade nobody connected to it. Changing it is now a
                // deliberate edit on a field that finally exists.
                "UPDATE Svc_Services SET ScheduleCustomCron = ScheduleCron WHERE ScheduleMode = 'custom' AND ScheduleCustomCron IS NULL AND ScheduleCron IS NOT NULL",
                // Index for audit-log search by employee number / key
                "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Svc_AuditEntries_KeyValue' AND object_id = OBJECT_ID('Svc_AuditEntries')) CREATE INDEX IX_Svc_AuditEntries_KeyValue ON Svc_AuditEntries (KeyValue)"
            };

            foreach (var sql in alterStatements)
            {
                try { svcDb.Database.ExecuteSqlRaw(sql); } catch (Exception alterEx) { svcLogger.LogWarning(alterEx, "ALTER statement may have failed (column may already exist): {Sql}", sql); }
            }

            svcLogger.LogInformation("Services database tables verified and updated");
        }
    }
    catch (Exception ex)
    {
        svcLogger.LogError(ex, "Services database setup failed");
    }
}

// === Migrate Account Status Database (Acct_ tables) ===
using (var acctScope = app.Services.CreateScope())
{
    var acctDb = acctScope.ServiceProvider.GetRequiredService<AccountStatusDbContext>();
    var acctLogger = acctScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var acctTableExists = false;
        try
        {
            var conn = acctDb.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CASE WHEN EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Acct_StatusLogs') THEN 1 ELSE 0 END";
            var scalar = await cmd.ExecuteScalarAsync();
            acctTableExists = Convert.ToInt32(scalar) == 1;
            await conn.CloseAsync();
        }
        catch (Exception ex) { acctLogger.LogWarning(ex, "Could not check Acct_StatusLogs table existence"); }

        if (!acctTableExists)
        {
            var creator = acctDb.Database.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>();
            creator.CreateTables();
            acctLogger.LogInformation("Account Status database tables created successfully");
        }
        else
        {
            acctLogger.LogInformation("Account Status database tables verified");
            // Ensure Acct_CustomDomains table exists
            try
            {
                var conn = acctDb.Database.GetDbConnection();
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Acct_CustomDomains') BEGIN CREATE TABLE Acct_CustomDomains (Id int IDENTITY(1,1) PRIMARY KEY, DisplayName nvarchar(200) NOT NULL, Server nvarchar(500) NOT NULL, Port int NOT NULL DEFAULT 389, BaseDN nvarchar(500) NOT NULL, Username nvarchar(200) NULL, Password nvarchar(500) NULL, CreatedAt datetime2 NOT NULL DEFAULT GETUTCDATE()) END";
                await cmd.ExecuteNonQueryAsync();
                // Configurable mobile-number attribute per domain (upgrade path)
                cmd.CommandText = "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Acct_CustomDomains') AND name = 'PhoneAttribute') ALTER TABLE Acct_CustomDomains ADD PhoneAttribute nvarchar(100) NULL";
                await cmd.ExecuteNonQueryAsync();
                await conn.CloseAsync();
            }
            catch (Exception exCreate) { acctLogger.LogWarning(exCreate, "Could not ensure Acct_CustomDomains table"); }
        }
    }
    catch (Exception ex)
    {
        acctLogger.LogError(ex, "Account Status database setup failed");
    }
}

// === Access Governance Database (Gov_ tables) ===
// Same shape as the two module contexts above: create the tables when they are absent, verify
// otherwise. A failure here is logged and does NOT stop the host — the sync engine, SSPR and the
// services module must keep running on an installation that never enables access requests.
using (var govScope = app.Services.CreateScope())
{
    var govDb = govScope.ServiceProvider.GetRequiredService<GovernanceDbContext>();
    var govLogger = govScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var govTablesExist = false;
        try
        {
            var conn = govDb.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Gov_AccessRequests') THEN 1 ELSE 0 END";
            govTablesExist = Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
            await conn.CloseAsync();
        }
        catch (Exception ex) { govLogger.LogWarning(ex, "Could not check Gov_AccessRequests table existence"); }

        if (!govTablesExist)
        {
            var creator = govDb.Database.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>();
            creator.CreateTables();
            govLogger.LogInformation("Access governance tables created successfully");
        }
        else
        {
            govLogger.LogInformation("Access governance tables verified");

            // Upgrade path for installations created before a column existed. Same idempotent
            // pattern the services module uses — the tables are created once by EF and evolved
            // here, so a running installation never needs a migration applied by hand.
            try
            {
                var conn = govDb.Database.GetDbConnection();
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Gov_CatalogItems') AND name = 'ApproverNotificationEmail') " +
                    "ALTER TABLE Gov_CatalogItems ADD ApproverNotificationEmail nvarchar(500) NULL";
                await cmd.ExecuteNonQueryAsync();
                await conn.CloseAsync();
            }
            catch (Exception exAlter) { govLogger.LogWarning(exAlter, "Could not evolve Gov_CatalogItems"); }

            // The certification tables arrived after the first three. EF's CreateTables() only runs
            // on an installation that had none of them, so an existing one needs these explicitly —
            // the same idempotent shape the services and account-status modules use.
            try
            {
                var conn = govDb.Database.GetDbConnection();
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();

                cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Gov_Campaigns')
CREATE TABLE Gov_Campaigns (
    Id int IDENTITY(1,1) PRIMARY KEY,
    Name nvarchar(200) NOT NULL,
    Description nvarchar(1000) NULL,
    ScopeGroups nvarchar(2000) NULL,
    ScopeTenantId int NULL,
    ScopeCatalogItemIds nvarchar(1000) NULL,
    ReviewerUsers nvarchar(1000) NULL,
    ReviewerAdGroup nvarchar(500) NULL,
    ReviewerNotificationEmail nvarchar(500) NULL,
    ReviewDays int NOT NULL DEFAULT 14,
    DueUtc datetime2 NULL,
    MaxUndecidedRevokePercent int NOT NULL DEFAULT 50,
    Status nvarchar(20) NOT NULL DEFAULT 'Draft',
    CreatedUtc datetime2 NOT NULL DEFAULT GETUTCDATE(),
    StartedUtc datetime2 NULL,
    ClosedUtc datetime2 NULL,
    ClosingNote nvarchar(2000) NULL
)";
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Gov_CampaignItems')
CREATE TABLE Gov_CampaignItems (
    Id bigint IDENTITY(1,1) PRIMARY KEY,
    CampaignId int NOT NULL,
    SubjectAccount nvarchar(200) NOT NULL,
    SubjectDisplayName nvarchar(500) NULL,
    GroupName nvarchar(500) NOT NULL,
    TenantId int NOT NULL,
    SourceCatalogItemId int NULL,
    Decision nvarchar(20) NOT NULL DEFAULT 'Pending',
    DecidedBy nvarchar(200) NULL,
    DecidedOnBehalfOf nvarchar(200) NULL,
    DecisionSource nvarchar(30) NOT NULL DEFAULT 'Reviewer',
    Comment nvarchar(2000) NULL,
    DecidedUtc datetime2 NULL,
    ExecutionStatus nvarchar(20) NOT NULL DEFAULT 'None',
    ExecutedUtc datetime2 NULL,
    ExecutionError nvarchar(2000) NULL,
    CONSTRAINT FK_Gov_CampaignItems_Campaign FOREIGN KEY (CampaignId)
        REFERENCES Gov_Campaigns(Id) ON DELETE CASCADE
)";
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Gov_ReviewDelegations')
CREATE TABLE Gov_ReviewDelegations (
    Id bigint IDENTITY(1,1) PRIMARY KEY,
    FromUsername nvarchar(200) NOT NULL,
    ToUsername nvarchar(200) NOT NULL,
    StartUtc datetime2 NOT NULL,
    EndUtc datetime2 NOT NULL,
    Reason nvarchar(1000) NULL,
    CreatedUtc datetime2 NOT NULL DEFAULT GETUTCDATE(),
    RevokedUtc datetime2 NULL
)";
                await cmd.ExecuteNonQueryAsync();

                // The tracked non-human population. Keyed on objectGUID rather than name or DN, so
                // that renaming an account or moving it to another OU does not lose its owner and
                // restart its claim window as if it were a new account.
                // Added after the table shipped, so it needs its own upgrade for databases that
                // already created Gov_NhiAccounts without it.
                cmd.CommandText = "IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Gov_NhiAccounts') " +
                                  "AND NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Gov_NhiAccounts') AND name = 'LastNotifiedUtc') " +
                                  "ALTER TABLE Gov_NhiAccounts ADD LastNotifiedUtc datetime2 NULL";
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Gov_NhiAccounts')
CREATE TABLE Gov_NhiAccounts (
    Id bigint IDENTITY(1,1) PRIMARY KEY,
    ObjectGuid nvarchar(50) NOT NULL,
    ServiceId int NOT NULL,
    Account nvarchar(200) NOT NULL,
    DistinguishedName nvarchar(500) NOT NULL,
    DisplayName nvarchar(500) NULL,
    Description nvarchar(1000) NULL,
    FirstSeenUtc datetime2 NOT NULL DEFAULT GETUTCDATE(),
    LastSeenUtc datetime2 NOT NULL DEFAULT GETUTCDATE(),
    Signals nvarchar(500) NULL,
    Privileged bit NOT NULL DEFAULT 0,
    Enabled bit NOT NULL DEFAULT 1,
    DirectoryOwner nvarchar(500) NULL,
    IsSelfAccount bit NOT NULL DEFAULT 0,
    State nvarchar(20) NOT NULL DEFAULT 'Discovered',
    ClaimDueUtc datetime2 NULL,
    OwnerUsername nvarchar(200) NULL,
    OwnerConfirmedUtc datetime2 NULL,
    DisownedBy nvarchar(200) NULL,
    DisownedUtc datetime2 NULL,
    LastAttestedUtc datetime2 NULL,
    LastAttestedBy nvarchar(200) NULL,
    AttestationNote nvarchar(2000) NULL,
    QuarantinedUtc datetime2 NULL,
    QuarantineReason nvarchar(40) NULL,
    QuarantineEffect nvarchar(30) NOT NULL DEFAULT 'None',
    QuarantineError nvarchar(2000) NULL,
    ExemptReason nvarchar(1000) NULL,
    ExemptBy nvarchar(200) NULL,
    ExemptUntilUtc datetime2 NULL,
    LastNotifiedUtc datetime2 NULL,
    RetiredUtc datetime2 NULL
)";
                await cmd.ExecuteNonQueryAsync();

                // Separation of duties: the rules, and every person who has ever held both sides.
                cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Gov_SodPolicies')
CREATE TABLE Gov_SodPolicies (
    Id int IDENTITY(1,1) PRIMARY KEY,
    Name nvarchar(200) NOT NULL,
    Rationale nvarchar(2000) NOT NULL,
    TenantId int NOT NULL,
    DutyAGroups nvarchar(2000) NOT NULL,
    DutyAName nvarchar(200) NOT NULL,
    DutyBGroups nvarchar(2000) NOT NULL,
    DutyBName nvarchar(200) NOT NULL,
    Enforcement nvarchar(20) NOT NULL DEFAULT 'Detect',
    Severity nvarchar(20) NOT NULL DEFAULT 'High',
    IsEnabled bit NOT NULL DEFAULT 1,
    CreatedUtc datetime2 NOT NULL DEFAULT GETUTCDATE(),
    CreatedBy nvarchar(200) NULL
)";
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Gov_SodViolations')
CREATE TABLE Gov_SodViolations (
    Id bigint IDENTITY(1,1) PRIMARY KEY,
    PolicyId int NOT NULL,
    TenantId int NOT NULL,
    SubjectAccount nvarchar(200) NOT NULL,
    SubjectDisplayName nvarchar(500) NULL,
    MatchedA nvarchar(2000) NOT NULL,
    MatchedB nvarchar(2000) NOT NULL,
    DetectedUtc datetime2 NOT NULL DEFAULT GETUTCDATE(),
    LastSeenUtc datetime2 NOT NULL DEFAULT GETUTCDATE(),
    ClearedUtc datetime2 NULL,
    MitigationReason nvarchar(2000) NULL,
    MitigatedBy nvarchar(200) NULL,
    MitigatedUtc datetime2 NULL,
    MitigationExpiresUtc datetime2 NULL,
    CONSTRAINT FK_Gov_SodViolations_Policy FOREIGN KEY (PolicyId)
        REFERENCES Gov_SodPolicies(Id)
)";
                await cmd.ExecuteNonQueryAsync();

                foreach (var ix in new[]
                {
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Gov_SodPolicies_TenantId_IsEnabled') CREATE INDEX IX_Gov_SodPolicies_TenantId_IsEnabled ON Gov_SodPolicies(TenantId, IsEnabled)",
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Gov_SodViolations_Policy_Subject_Cleared') CREATE INDEX IX_Gov_SodViolations_Policy_Subject_Cleared ON Gov_SodViolations(PolicyId, SubjectAccount, ClearedUtc)",
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Gov_SodViolations_SubjectAccount') CREATE INDEX IX_Gov_SodViolations_SubjectAccount ON Gov_SodViolations(SubjectAccount)",
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Gov_SodViolations_ClearedUtc') CREATE INDEX IX_Gov_SodViolations_ClearedUtc ON Gov_SodViolations(ClearedUtc)",
                    // Unique in the database, not only in the reconciler: two runs overlapping
                    // would each look, each find nothing, and each insert a row for one account.
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Gov_NhiAccounts_ServiceId_ObjectGuid') CREATE UNIQUE INDEX IX_Gov_NhiAccounts_ServiceId_ObjectGuid ON Gov_NhiAccounts(ServiceId, ObjectGuid)",
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Gov_NhiAccounts_OwnerUsername') CREATE INDEX IX_Gov_NhiAccounts_OwnerUsername ON Gov_NhiAccounts(OwnerUsername)",
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Gov_NhiAccounts_ServiceId_State') CREATE INDEX IX_Gov_NhiAccounts_ServiceId_State ON Gov_NhiAccounts(ServiceId, State)",
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Gov_NhiAccounts_ClaimDueUtc') CREATE INDEX IX_Gov_NhiAccounts_ClaimDueUtc ON Gov_NhiAccounts(ClaimDueUtc)",
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Gov_NhiAccounts_LastAttestedUtc') CREATE INDEX IX_Gov_NhiAccounts_LastAttestedUtc ON Gov_NhiAccounts(LastAttestedUtc)",
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Gov_NhiAccounts_Account') CREATE INDEX IX_Gov_NhiAccounts_Account ON Gov_NhiAccounts(Account)",
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Gov_Campaigns_Status') CREATE INDEX IX_Gov_Campaigns_Status ON Gov_Campaigns(Status)",
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Gov_Campaigns_DueUtc') CREATE INDEX IX_Gov_Campaigns_DueUtc ON Gov_Campaigns(DueUtc)",
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Gov_CampaignItems_CampaignId_Decision') CREATE INDEX IX_Gov_CampaignItems_CampaignId_Decision ON Gov_CampaignItems(CampaignId, Decision)",
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Gov_CampaignItems_SubjectAccount') CREATE INDEX IX_Gov_CampaignItems_SubjectAccount ON Gov_CampaignItems(SubjectAccount)",
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Gov_CampaignItems_ExecutionStatus') CREATE INDEX IX_Gov_CampaignItems_ExecutionStatus ON Gov_CampaignItems(ExecutionStatus)",
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Gov_ReviewDelegations_ToUsername_EndUtc') CREATE INDEX IX_Gov_ReviewDelegations_ToUsername_EndUtc ON Gov_ReviewDelegations(ToUsername, EndUtc)",
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Gov_ReviewDelegations_FromUsername') CREATE INDEX IX_Gov_ReviewDelegations_FromUsername ON Gov_ReviewDelegations(FromUsername)"
                })
                {
                    cmd.CommandText = ix;
                    await cmd.ExecuteNonQueryAsync();
                }

                await conn.CloseAsync();
            }
            catch (Exception exCert) { govLogger.LogWarning(exCert, "Could not ensure the certification tables"); }
        }
    }
    catch (Exception ex)
    {
        govLogger.LogError(ex, "Access governance database setup failed");
    }
}

// === Encrypt existing plaintext secrets + widen secret columns (idempotent) ===
// Runs after all tables are guaranteed to exist. Safe to run every startup.
using (var secScope = app.Services.CreateScope())
{
    var secLogger = secScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var secretsConnStr = builder.Configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrEmpty(secretsConnStr))
            await SecretsMigrator.MigrateAsync(secretsConnStr, secLogger);
    }
    catch (Exception ex)
    {
        secLogger.LogError(ex, "Secrets encryption migration failed");
    }
}

// === Middleware Pipeline ===

// Real client IP behind a reverse proxy / WAF / load balancer (opt-in).
// SSPR blocks abusive clients by IP. When the app sits behind a proxy, RemoteIpAddress is
// the PROXY's address — so every user shares one IP and a single abuser would block everyone.
// Enabling this resolves the real client from X-Forwarded-For.
//
// ⚠️ X-Forwarded-For is trivially forgeable, so it is honoured ONLY for requests arriving
// from a proxy we explicitly list. Configured on but with no KnownProxies/KnownNetworks =>
// stay OFF rather than trust a spoofable header (which would let a client both evade the
// SSPR block and get an innocent IP blocked).
// Must run before anything that reads the client IP or scheme.
var netCfg = builder.Configuration.GetSection("Network");
if (netCfg.GetValue<bool>("UseForwardedHeaders"))
{
    var fwd = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit = netCfg.GetValue<int?>("ForwardLimit") ?? 1
    };
    // Defaults trust loopback only; replace with the operator's list.
    fwd.KnownProxies.Clear();
    fwd.KnownNetworks.Clear();

    foreach (var ip in netCfg.GetSection("KnownProxies").Get<string[]>() ?? Array.Empty<string>())
        if (IPAddress.TryParse(ip, out var parsed)) fwd.KnownProxies.Add(parsed);

    foreach (var cidr in netCfg.GetSection("KnownNetworks").Get<string[]>() ?? Array.Empty<string>())
    {
        var parts = cidr.Split('/');
        if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var net) && int.TryParse(parts[1], out var bits))
            fwd.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(net, bits));
    }

    if (fwd.KnownProxies.Count == 0 && fwd.KnownNetworks.Count == 0)
    {
        app.Logger.LogWarning(
            "Network:UseForwardedHeaders is enabled but no KnownProxies/KnownNetworks are configured — " +
            "X-Forwarded-For is being IGNORED. Clients could otherwise spoof their IP and evade SSPR blocking. " +
            "List your proxy address(es) to activate it.");
    }
    else
    {
        app.UseForwardedHeaders(fwd);
        app.Logger.LogInformation(
            "Forwarded headers enabled — real client IP resolved from X-Forwarded-For ({Proxies} known proxy/proxies, {Networks} network(s)).",
            fwd.KnownProxies.Count, fwd.KnownNetworks.Count);
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// Registered before UseStaticFiles so the headers also cover css/js/font responses.
app.UseSecurityHeaders(builder.Configuration);

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseCors();
app.UseSession(); // must precede authentication: MFA enrollment reads it during sign-in
app.UseAuthentication(); // ✅ must precede UseAuthorization
app.UseAuthorization();

// === Hangfire Dashboard (Protected) ===
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    DashboardTitle = "IdentitySync Pro - Job Dashboard",
    DisplayStorageConnectionString = false,
    Authorization = new[] { new HangfireDashboardAuthFilter(builder.Configuration, app.Environment) }
});

// === SignalR Hub ===
app.MapHub<SyncHub>("/hubs/sync");

// === Routes ===
app.MapControllers(); // API controllers (SCIM, SMS callback)
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

// === Schedule Recurring Jobs ===
var syncSettings = builder.Configuration.GetSection("SyncSettings");
if (syncSettings.GetValue<bool>("EnableAutoSync"))
{
    RecurringJob.AddOrUpdate<FullSyncJob>(
        "full-sync",
        job => job.ExecuteAsync(ActorNames.Schedule, CancellationToken.None),
        syncSettings["FullSyncSchedule"] ?? "0 2 * * *");

    RecurringJob.AddOrUpdate<DeltaSyncJob>(
        "delta-sync",
        job => job.ExecuteAsync(ActorNames.Schedule, CancellationToken.None),
        syncSettings["DeltaSyncSchedule"] ?? "*/30 * * * *");
}

RecurringJob.AddOrUpdate<HealthCheckJob>(
    "health-check",
    job => job.ExecuteAsync(),
    syncSettings["HealthCheckSchedule"] ?? "*/10 * * * *");

// === Per-tenant sync schedules (multi-source) ===
// Each active tenant with auto-sync enabled gets its own full/delta recurring
// jobs from its cron settings — replacing the legacy global jobs above.
using (var schedScope = app.Services.CreateScope())
{
    var schedDb = schedScope.ServiceProvider.GetRequiredService<AppDbContext>();
    var schedLogger = schedScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try { TenantSyncScheduler.RefreshTenantJobs(schedDb, schedLogger); }
    catch (Exception ex) { schedLogger.LogWarning(ex, "Per-tenant schedule registration failed"); }
}

// === Data Retention (weekly — Sunday 3 AM) ===
RecurringJob.AddOrUpdate<DataRetentionJob>(
    "data-retention",
    job => job.ExecuteAsync(CancellationToken.None),
    "0 3 * * 0"); // Every Sunday at 3:00 AM

// === Access Governance sweep (every 15 minutes) ===
// Frequent because two of the three things it does are time-sensitive in a way a person notices:
// an approval that failed to reach AD is access somebody was told they had, and a time-bound grant
// that outlives its window is exactly the accumulation the window exists to prevent. Expiring an
// overdue decision could wait for the hour; the other two should not.
RecurringJob.AddOrUpdate<AccessGovernanceJob>(
    "access-governance-sweep",
    job => job.ExecuteAsync(CancellationToken.None),
    "*/15 * * * *");

// Once a day, not every fifteen minutes: the spacing between reminders is a setting on the job,
// and a sweep that ran four times an hour would only re-read a decision nobody had had time to act on.
RecurringJob.AddOrUpdate<NhiNotificationJob>(
    "nhi-owner-notifications",
    job => job.ExecuteAsync(CancellationToken.None),
    "0 7 * * *");

// === Register Services Module Recurring Jobs ===
using (var svcJobScope = app.Services.CreateScope())
{
    var svcDb = svcJobScope.ServiceProvider.GetRequiredService<ServicesDbContext>();
    try
    {
        // Close out runs left mid-flight by a previous process.
        //
        // SvcRunLog is written as "Running" when execution starts and updated when it ends. Kill
        // the process in between — a restart, a deploy, a crash — and the row stays "Running"
        // forever, because nothing ever revisits it. That is not cosmetic: RunNow refuses to start
        // while any run is "Running" ("wait until it finishes"), and CancelRun works through an
        // in-memory cancellation registry that a new process starts empty, so it answers "no running
        // operation found". The service becomes permanently unrunnable, and the only way out is
        // editing the database by hand.
        //
        // A process that has just started owns no in-flight runs, so any row still marked Running
        // belongs to an instance that no longer exists. (Single-instance assumption — with several
        // app instances sharing this database, an owner or heartbeat column would be needed instead
        // of closing everything at startup.)
        var orphanedRuns = svcDb.SvcRunLogs.Where(l => l.Status == "Running").ToList();
        if (orphanedRuns.Count > 0)
        {
            var svcOrphanLogger = svcJobScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            foreach (var run in orphanedRuns)
            {
                run.Status = "Interrupted";
                run.EndTime ??= DateTime.UtcNow;
                run.ErrorMessage = string.IsNullOrWhiteSpace(run.ErrorMessage)
                    ? "The application stopped while this run was in progress. Closed at startup; the service can run again."
                    : run.ErrorMessage;

                svcOrphanLogger.LogWarning(
                    "Service run {RunId} (service {ServiceId}, started {Started:u}) was still marked Running from a " +
                    "previous process — closed as Interrupted so the service is runnable again.",
                    run.Id, run.SvcServiceId, run.StartTime);
            }
            svcDb.SaveChanges();
        }

        var enabledServices = svcDb.SvcServices.Where(s => s.IsEnabled && s.ScheduleCron != null).ToList();
        foreach (var svc in enabledServices)
        {
            RecurringJob.AddOrUpdate<SvcSyncJob>(
                $"svc-sync-{svc.Id}",
                job => job.ExecuteAsync(svc.Id, CancellationToken.None),
                svc.ScheduleCron!,
                new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });
        }
        var svcStartLogger = svcJobScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        svcStartLogger.LogInformation("Registered {Count} service sync recurring jobs", enabledServices.Count);

        // A service carrying a schedule it will never run is the same silent state a tenant sync
        // was found in: the cron is stored, the settings page describes it, and no job exists.
        // Named at startup so a suspension that was meant to last an afternoon does not last a term.
        var suspended = svcDb.SvcServices
            .Where(s => !s.IsEnabled && s.ScheduleCron != null)
            .Select(s => new { s.Id, s.Name, s.ScheduleCron })
            .ToList();

        foreach (var s in suspended)
            svcStartLogger.LogWarning(
                "Service '{Name}' (id {Id}) has a schedule [{Cron}] but is suspended — it will NOT run on a timer. " +
                "Resume it from the services list. Manual runs are unaffected.",
                s.Name, s.Id, s.ScheduleCron);
    }
    catch (Exception ex)
    {
        // Services tables may not exist yet on first run — safe to ignore
        var jobLogger = svcJobScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        jobLogger.LogWarning(ex, "Could not register service recurring jobs (tables may not exist yet)");
    }
}

// Stamp which binary is actually serving. Two rounds of diagnosis were spent unable to tell a
// code fault from a stale process still running an older build; this line settles that in one look.
{
    var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
    var asm = System.Reflection.Assembly.GetExecutingAssembly();
    var built = System.IO.File.Exists(asm.Location)
        ? System.IO.File.GetLastWriteTime(asm.Location).ToString("yyyy-MM-dd HH:mm:ss")
        : "unknown";
    startupLogger.LogInformation(
        "IdentitySyncPro starting — assembly built {Built}, path {Path}", built, asm.Location);
}

// === Security posture at startup ===
// Printed on every boot so the effective policy is a fact in the log, not an assumption.
// The API-key lines are the ones that matter: a shipped placeholder left in place used to pass
// every "is it configured?" check while leaving the SCIM endpoint open to a key in the repo.
{
    var secLogger = app.Services.GetRequiredService<ILogger<Program>>();

    secLogger.LogInformation(
        "🔐 Session policy — idle timeout {Minutes} min (sliding), auth cookie HTTPS-only: {Https}",
        idleTimeoutMinutes, requireHttpsCookie);

    secLogger.LogInformation(
        passwordMaxAgeDays > 0
            ? "🔐 Local password policy — maximum age {Days} days (AD users exempt; the domain owns theirs)"
            : "⚠️ Local password expiry is DISABLED (Security:PasswordMaxAgeDays = {Days})",
        passwordMaxAgeDays);

    if (!requireHttpsCookie)
    {
        secLogger.LogWarning(
            "⚠️ Security:RequireHttpsCookie is false — the auth cookie may travel over plain HTTP. " +
            "Set it to true once the site has an HTTPS binding.");
    }

    var headersEnabled = securityConfig.GetValue<bool?>("EnableSecurityHeaders") ?? true;
    if (!headersEnabled)
    {
        secLogger.LogWarning("⚠️ Security:EnableSecurityHeaders is false — CSP and clickjacking headers are NOT being sent.");
    }

    var apiKey = builder.Configuration["ApiSecurity:ApiKey"];
    if (ApiKeyGuard.IsPlaceholderOrMissing(apiKey))
    {
        secLogger.LogWarning(
            "🔒 ApiSecurity:ApiKey is missing or still the shipped placeholder — the SCIM API is BLOCKED " +
            "until a real key is configured. This is intentional: a placeholder key is a published key.");
    }
    else if (ApiKeyGuard.IsWeak(apiKey))
    {
        secLogger.LogWarning(
            "⚠️ ApiSecurity:ApiKey is shorter than {Min} characters. It works, but a longer random key is advised.",
            ApiKeyGuard.RecommendedMinimumLength);
    }

    var hangfireKey = builder.Configuration["ApiSecurity:HangfireApiKey"];
    if (ApiKeyGuard.IsPlaceholderOrMissing(hangfireKey))
    {
        secLogger.LogInformation(
            "ℹ️ ApiSecurity:HangfireApiKey is not configured — the /hangfire dashboard is reachable by " +
            "signed-in Admin users only (the intended path). The query-string key fallback is disabled.");
    }
}

app.Run();
