using Microsoft.EntityFrameworkCore;
using IdentitySyncPro.Core.Models.Audit;
using IdentitySyncPro.Core.Models.Connectors;
using IdentitySyncPro.Core.Models.Metaverse;
using IdentitySyncPro.Core.Models.Resilience;
using IdentitySyncPro.Core.Models.Rules;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Core.Models.Sync;
using IdentitySyncPro.Infrastructure.Security;

namespace IdentitySyncPro.Infrastructure.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<SyncState> SyncStates { get; set; } = null!;
        public DbSet<SyncRun> SyncRuns { get; set; } = null!;
        public DbSet<SyncOperation> SyncOperations { get; set; } = null!;
        public DbSet<AuditEntry> AuditEntries { get; set; } = null!;
        public DbSet<ConnectorConfig> ConnectorConfigs { get; set; } = null!;
        public DbSet<SyncRule> SyncRules { get; set; } = null!;
        public DbSet<AttributeMapping> AttributeMappings { get; set; } = null!;
        public DbSet<TenantSettings> TenantSettings { get; set; } = null!;
        public DbSet<AppSettings> AppSettings { get; set; } = null!;
        public DbSet<TenantAttributeMapping> TenantAttributeMappings { get; set; } = null!;
        public DbSet<TenantGroupRule> TenantGroupRules { get; set; } = null!;
        public DbSet<TenantOURule> TenantOURules { get; set; } = null!;

        // Metaverse & Lifecycle
        public DbSet<MetaverseEntry> MetaverseEntries { get; set; } = null!;
        public DbSet<MetaverseHistory> MetaverseHistory { get; set; } = null!;
        public DbSet<LifecycleRule> LifecycleRules { get; set; } = null!;

        // Rules Engine V2
        public DbSet<SyncRuleV2> SyncRulesV2 { get; set; } = null!;
        public DbSet<SyncRuleFlowMapping> SyncRuleFlowMappings { get; set; } = null!;
        public DbSet<SyncRuleVersion> SyncRuleVersions { get; set; } = null!;

        // Resilience
        public DbSet<QuarantinedIdentity> QuarantinedIdentities { get; set; } = null!;
        public DbSet<DeadLetterEntry> DeadLetterEntries { get; set; } = null!;

        // SMS Center
        public DbSet<SmsProvider> SmsProviders { get; set; } = null!;
        public DbSet<SmsSendLog> SmsSendLogs { get; set; } = null!;
        public DbSet<EmailProvider> EmailProviders { get; set; } = null!;

        // Console users (authentication)
        public DbSet<AppUser> AppUsers { get; set; } = null!;
        public DbSet<MfaSettings> MfaSettings { get; set; } = null!;

        /// <summary>AD domains console sign-in binds against — owned by this module, not borrowed.</summary>
        public DbSet<AuthDomain> AuthDomains { get; set; } = null!;

        // Self-service password reset (standalone module)
        public DbSet<PasswordResetRequest> PasswordResetRequests { get; set; } = null!;
        public DbSet<SsprSettings> SsprSettings { get; set; } = null!;
        public DbSet<SsprDomain> SsprDomains { get; set; } = null!;
        public DbSet<SsprIpBlock> SsprIpBlocks { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // AppUser (console authentication)
            modelBuilder.Entity<AppUser>(entity =>
            {
                entity.ToTable("AppUsers");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Username).IsUnique();
                entity.Property(e => e.Username).HasMaxLength(200).IsRequired();
                entity.Property(e => e.DisplayName).HasMaxLength(200);
                entity.Property(e => e.PasswordHash).HasMaxLength(500);
                entity.Property(e => e.Role).HasMaxLength(30);
                entity.Property(e => e.AuthType).HasMaxLength(30);
                // 🔐 The TOTP secret is a bearer credential — anyone holding it can mint valid
                // codes forever. Encrypted at rest like every other secret in the system.
                entity.Property(e => e.MfaSecret).HasMaxLength(1024).HasConversion(new EncryptedStringConverter());
                // Recovery codes are already PBKDF2 hashes; length only.
                entity.Property(e => e.MfaRecoveryCodes).HasMaxLength(4000);
            });

            // Institution-wide MFA policy (single row)
            modelBuilder.Entity<MfaSettings>(entity =>
            {
                entity.ToTable("MfaSettings");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.RequiredRoles).HasMaxLength(200);
            });

            // Console sign-in domains. No password property: the bind uses the signing-in user's
            // own credentials, so there is no secret here to encrypt.
            modelBuilder.Entity<AuthDomain>(entity =>
            {
                entity.ToTable("AuthDomains");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
                entity.Property(e => e.AdServer).HasMaxLength(300).IsRequired();
            });

            // Self-service password reset requests
            modelBuilder.Entity<PasswordResetRequest>(entity =>
            {
                entity.ToTable("PasswordResetRequests");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.RequestGuid).IsUnique();
                entity.HasIndex(e => new { e.Username, e.CreatedAtUtc });
                entity.HasIndex(e => new { e.ClientIp, e.CreatedAtUtc });
                entity.Property(e => e.RequestGuid).HasMaxLength(40).IsRequired();
                entity.Property(e => e.Username).HasMaxLength(256);
                entity.Property(e => e.PhoneNumber).HasMaxLength(50);
                entity.Property(e => e.OtpHash).HasMaxLength(100);
                entity.Property(e => e.Status).HasMaxLength(20);
                entity.Property(e => e.ClientIp).HasMaxLength(64);
            });

            // SSPR global settings (single row)
            modelBuilder.Entity<SsprSettings>(entity =>
            {
                entity.ToTable("SsprSettings");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.MessageTemplate).HasMaxLength(1000);
                entity.Property(e => e.MessageTemplateEn).HasMaxLength(1000);
                entity.Property(e => e.NewPasswordTemplate).HasMaxLength(1000);
                entity.Property(e => e.NewPasswordTemplateEn).HasMaxLength(1000);
            });

            // SSPR domains (own AD connection + verification attributes)
            modelBuilder.Entity<SsprDomain>(entity =>
            {
                entity.ToTable("SsprDomains");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
                entity.Property(e => e.AdServer).HasMaxLength(300);
                entity.Property(e => e.AdUsername).HasMaxLength(200);
                // 🔐 Secret — encrypted at rest
                entity.Property(e => e.AdPassword).HasMaxLength(1024).HasConversion(new EncryptedStringConverter());
                entity.Property(e => e.AdBaseDN).HasMaxLength(500);
                entity.Property(e => e.NationalIdAttribute).HasMaxLength(100);
                entity.Property(e => e.MobileAttribute).HasMaxLength(100);
            });

            // SSPR per-IP failed-attempt counters / blocks
            modelBuilder.Entity<SsprIpBlock>(entity =>
            {
                entity.ToTable("SsprIpBlocks");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.ClientIp).IsUnique();
                entity.Property(e => e.ClientIp).HasMaxLength(64).IsRequired();
                entity.Property(e => e.LastUsername).HasMaxLength(256);
            });

            // SyncState
            modelBuilder.Entity<SyncState>(entity =>
            {
                entity.ToTable("SyncStates");
                entity.HasKey(e => e.Id);
                // Composite: the same source key may exist under different tenants
                entity.HasIndex(e => new { e.TenantId, e.IdentityId }).IsUnique();
                entity.Property(e => e.IdentityId).IsRequired();
                entity.Property(e => e.CurrentHash).HasMaxLength(100);
                entity.Property(e => e.Status).HasMaxLength(50);
                entity.Property(e => e.ErrorMessage).HasMaxLength(1000);
            });

            // SyncRun
            modelBuilder.Entity<SyncRun>(entity =>
            {
                entity.ToTable("SyncRuns");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.CorrelationId).HasMaxLength(20);
                entity.HasIndex(e => e.CorrelationId);
                entity.Property(e => e.RunType).HasMaxLength(50);
                entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
                entity.Property(e => e.TriggeredBy).HasMaxLength(200);   // holds a username, not just "Schedule"
                entity.HasMany(e => e.Operations)
                      .WithOne(o => o.SyncRun)
                      .HasForeignKey(o => o.SyncRunId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // SyncOperation
            modelBuilder.Entity<SyncOperation>(entity =>
            {
                entity.ToTable("SyncOperations");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.IdentityId);
                entity.HasIndex(e => e.Timestamp);
                entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
                entity.Property(e => e.ChangedFields).HasMaxLength(500);
            });

            // AuditEntry
            modelBuilder.Entity<AuditEntry>(entity =>
            {
                entity.ToTable("AuditEntries");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Timestamp);
                entity.HasIndex(e => e.Category);
                entity.HasIndex(e => e.CorrelationId);
                entity.Property(e => e.Category).HasMaxLength(100);
                entity.Property(e => e.Action).HasMaxLength(500);
                entity.Property(e => e.EntityType).HasMaxLength(100);
                entity.Property(e => e.EntityId).HasMaxLength(100);
                entity.Property(e => e.PerformedBy).HasMaxLength(100);
                entity.Property(e => e.IpAddress).HasMaxLength(50);
                entity.Property(e => e.CorrelationId).HasMaxLength(20);
            });

            // ConnectorConfig
            modelBuilder.Entity<ConnectorConfig>(entity =>
            {
                entity.ToTable("ConnectorConfigs");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
                entity.Property(e => e.ConnectorClass).HasMaxLength(100);
                entity.Property(e => e.LastError).HasMaxLength(2000);
            });

            // SyncRule
            modelBuilder.Entity<SyncRule>(entity =>
            {
                entity.ToTable("SyncRules");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
                entity.Property(e => e.RuleType).HasMaxLength(50);
            });

            // AttributeMapping
            modelBuilder.Entity<AttributeMapping>(entity =>
            {
                entity.ToTable("AttributeMappings");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.SourceAttribute).HasMaxLength(100);
                entity.Property(e => e.TargetAttribute).HasMaxLength(100);
                entity.Property(e => e.TransformExpression).HasMaxLength(500);
                entity.Property(e => e.DefaultValue).HasMaxLength(500);
                entity.HasOne(e => e.SyncRule)
                      .WithMany()
                      .HasForeignKey(e => e.SyncRuleId);
            });

            // Seed default connector configs (generic placeholders — configured per organization from Settings)
            modelBuilder.Entity<ConnectorConfig>().HasData(
                new ConnectorConfig
                {
                    Id = 1,
                    Name = "Source Database (Oracle)",
                    Type = Core.Enums.ConnectorType.Source,
                    ConnectorClass = "Oracle",
                    Enabled = true,
                    ConnectionSettings = System.Text.Json.JsonSerializer.Serialize(new OracleConnectionSettings
                    {
                        DataSource = "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=localhost)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=ORCL)))",
                        UserId = "source_view_user",
                        ViewName = "V_IDENTITY_DATA",
                        CommandTimeout = 300
                    })
                },
                new ConnectorConfig
                {
                    Id = 2,
                    Name = "Active Directory",
                    Type = Core.Enums.ConnectorType.Target,
                    ConnectorClass = "ActiveDirectory",
                    Enabled = true,
                    ConnectionSettings = System.Text.Json.JsonSerializer.Serialize(new ADConnectionSettings
                    {
                        Server = "dc.example.local",
                        Port = 389,
                        BaseDN = "DC=example,DC=local",
                        DefaultPassword = "ChangeMe@2026"
                    })
                }
            );

            // TenantSettings
            modelBuilder.Entity<TenantSettings>(entity =>
            {
                entity.ToTable("TenantSettings");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.TenantName).HasMaxLength(200).IsRequired();
                entity.Property(e => e.Description).HasMaxLength(500);

                // Data Source (generic)
                entity.Property(e => e.SourceProvider).HasMaxLength(50);
                entity.Property(e => e.SourceHost).HasMaxLength(300);
                entity.Property(e => e.SourceDatabase).HasMaxLength(200);
                entity.Property(e => e.SourceUsername).HasMaxLength(100);
                // 🔐 Secret — encrypted at rest via EncryptedStringConverter (widened to hold ciphertext)
                entity.Property(e => e.SourcePassword).HasMaxLength(1024).HasConversion(new EncryptedStringConverter());
                entity.Property(e => e.SourceTableOrView).HasMaxLength(200);
                entity.Ignore(e => e.SourceConnectionString);

                // Active Directory
                entity.Property(e => e.ADServer).HasMaxLength(300);
                entity.Property(e => e.ADUsername).HasMaxLength(200).IsRequired();
                // 🔐 Secret — encrypted at rest
                entity.Property(e => e.ADPassword).HasMaxLength(1024).IsRequired().HasConversion(new EncryptedStringConverter());
                entity.Property(e => e.ADBaseDN).HasMaxLength(500);
                // 🔐 Secret — encrypted at rest
                entity.Property(e => e.ADDefaultPassword).HasMaxLength(1024).HasConversion(new EncryptedStringConverter());
                entity.Property(e => e.TargetProvider).HasMaxLength(30);
                entity.Property(e => e.ScimBaseUrl).HasMaxLength(500);
                // 🔐 Secret — a bearer token is a credential like any password here.
                entity.Property(e => e.ScimBearerToken).HasMaxLength(2048).HasConversion(new EncryptedStringConverter());

                // Application Database
                entity.Property(e => e.DatabaseProvider).HasMaxLength(50);
                entity.Property(e => e.DbHost).HasMaxLength(300);
                entity.Property(e => e.DbName).HasMaxLength(200);
                entity.Property(e => e.DbUsername).HasMaxLength(100);
                // 🔐 Secret — encrypted at rest
                entity.Property(e => e.DbPassword).HasMaxLength(1024).HasConversion(new EncryptedStringConverter());
                // 🔐 Secret — encrypted at rest (nvarchar(max), holds ciphertext)
                entity.Property(e => e.SmsApiPassword).HasConversion(new EncryptedStringConverter());
                entity.Ignore(e => e.SqlConnectionString);

                // Schedule
                entity.Property(e => e.FullSyncMode).HasMaxLength(20);
                entity.Property(e => e.FullSyncTime).HasMaxLength(10);
                entity.Property(e => e.FullSyncDays).HasMaxLength(50);
                entity.Property(e => e.FullSyncSchedule).HasMaxLength(100);
                entity.Property(e => e.DeltaSyncMode).HasMaxLength(20);
                entity.Property(e => e.DeltaSyncTime).HasMaxLength(10);
                entity.Property(e => e.DeltaSyncDays).HasMaxLength(50);
                entity.Property(e => e.DeltaSyncSchedule).HasMaxLength(100);
                entity.Property(e => e.HealthCheckMode).HasMaxLength(20);
                entity.Property(e => e.HealthCheckTime).HasMaxLength(10);
                entity.Property(e => e.HealthCheckSchedule).HasMaxLength(100);

                // Navigation
                entity.HasMany(e => e.AttributeMappings).WithOne(m => m.Tenant).HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Cascade);
                entity.HasMany(e => e.GroupRules).WithOne(g => g.Tenant).HasForeignKey(g => g.TenantId).OnDelete(DeleteBehavior.Cascade);
                entity.HasMany(e => e.OURules).WithOne(o => o.Tenant).HasForeignKey(o => o.TenantId).OnDelete(DeleteBehavior.Cascade);
            });

            // TenantAttributeMapping
            modelBuilder.Entity<TenantAttributeMapping>(entity =>
            {
                entity.ToTable("TenantAttributeMappings");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.SourceColumn).HasMaxLength(200).IsRequired();
                entity.Property(e => e.TargetAttribute).HasMaxLength(200).IsRequired();
                entity.Property(e => e.Transform).HasMaxLength(500);
                entity.Property(e => e.DefaultValue).HasMaxLength(500);
                entity.Property(e => e.Condition).HasMaxLength(1000);
            });

            // TenantGroupRule
            modelBuilder.Entity<TenantGroupRule>(entity =>
            {
                entity.ToTable("TenantGroupRules");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.GroupName).HasMaxLength(300).IsRequired();
                entity.Property(e => e.GroupDN).HasMaxLength(1000);
                entity.Property(e => e.ConditionField).HasMaxLength(100);
                entity.Property(e => e.ConditionOperator).HasMaxLength(20);
                entity.Property(e => e.ConditionValue).HasMaxLength(500);
                entity.Property(e => e.Description).HasMaxLength(500);
            });

            // TenantOURule
            modelBuilder.Entity<TenantOURule>(entity =>
            {
                entity.ToTable("TenantOURules");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.OUTemplate).HasMaxLength(1000).IsRequired();
                entity.Property(e => e.ConditionField).HasMaxLength(100);
                entity.Property(e => e.ConditionOperator).HasMaxLength(20);
                entity.Property(e => e.ConditionValue).HasMaxLength(500);
                entity.Property(e => e.ValueMappings).HasMaxLength(2000);
                entity.Property(e => e.Description).HasMaxLength(500);
            });

            // AppSettings
            modelBuilder.Entity<AppSettings>(entity =>
            {
                entity.ToTable("AppSettings");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Key).IsUnique();
                entity.Property(e => e.Key).HasMaxLength(100).IsRequired();
                entity.Property(e => e.Value).HasMaxLength(2000);
            });

            // SmsProvider
            modelBuilder.Entity<SmsProvider>(entity =>
            {
                entity.ToTable("SmsProviders");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
                entity.Property(e => e.ApiUrl).HasMaxLength(500).IsRequired();
                entity.Property(e => e.ApiUsername).HasMaxLength(200);
                // 🔐 Secrets — encrypted at rest
                entity.Property(e => e.ApiPassword).HasMaxLength(1024).HasConversion(new EncryptedStringConverter());
                entity.Property(e => e.ApiKey).HasMaxLength(1024).HasConversion(new EncryptedStringConverter());
                entity.Property(e => e.SenderName).HasMaxLength(100);
                entity.Property(e => e.HttpMethod).HasMaxLength(10);
                entity.Property(e => e.BodyFormat).HasMaxLength(10);
                entity.Property(e => e.RequestTemplate).HasMaxLength(4000);
                entity.Property(e => e.HeadersJson).HasMaxLength(2000);
                entity.Property(e => e.SuccessBodyContains).HasMaxLength(200);
                entity.Property(e => e.Notes).HasMaxLength(500);
            });

            // SmsSendLog
            modelBuilder.Entity<SmsSendLog>(entity =>
            {
                entity.ToTable("SmsSendLogs");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Account);
                entity.HasIndex(e => e.Status);
                entity.HasIndex(e => e.CreatedAt);
                entity.Property(e => e.Source).HasMaxLength(30).IsRequired();
                entity.Property(e => e.Account).HasMaxLength(256);
                entity.Property(e => e.DisplayName).HasMaxLength(300);
                entity.Property(e => e.PhoneNumber).HasMaxLength(50);
                entity.Property(e => e.Status).HasMaxLength(20).IsRequired();
                entity.Property(e => e.ProviderName).HasMaxLength(200);
                entity.Property(e => e.GatewayResponse).HasMaxLength(2000);
                // 🔐 Rendered message kept only for retry — encrypted at rest (may contain a password)
                entity.Property(e => e.SentMessage).HasConversion(new EncryptedStringConverter());
                entity.Ignore(e => e.IsRetryable);
            });

            // EmailProvider
            modelBuilder.Entity<EmailProvider>(entity =>
            {
                entity.ToTable("EmailProviders");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
                entity.Property(e => e.Mode).HasMaxLength(30).IsRequired();
                entity.Property(e => e.SmtpHost).HasMaxLength(300);
                entity.Property(e => e.Username).HasMaxLength(300);
                // 🔐 Secret — encrypted at rest
                entity.Property(e => e.Password).HasMaxLength(1024).HasConversion(new EncryptedStringConverter());
                entity.Property(e => e.FromEmail).HasMaxLength(300);
                entity.Property(e => e.FromName).HasMaxLength(200);
                entity.Property(e => e.Notes).HasMaxLength(500);
            });

            // Seed default app settings
            modelBuilder.Entity<AppSettings>().HasData(
                new AppSettings { Id = 1, Key = "Language", Value = "ar" }
            );

            // ══════════════════════════════════════
            // METAVERSE & LIFECYCLE
            // ══════════════════════════════════════

            modelBuilder.Entity<MetaverseEntry>(entity =>
            {
                entity.ToTable("MetaverseEntries");
                entity.HasKey(e => e.Id);
                // Composite: the same external id may exist under different tenants
                entity.HasIndex(e => new { e.TenantId, e.ExternalId }).IsUnique();
                entity.HasIndex(e => e.LifecycleState);
                entity.Property(e => e.ExternalId).HasMaxLength(100).IsRequired();
                entity.Property(e => e.IdentityType).HasMaxLength(50);
                entity.Property(e => e.LifecycleState).HasMaxLength(50);
                entity.Property(e => e.CurrentHash).HasMaxLength(100);
            });

            modelBuilder.Entity<MetaverseHistory>(entity =>
            {
                entity.ToTable("MetaverseHistory");
                entity.HasKey(e => e.Id);
                entity.HasOne(e => e.MetaverseEntry).WithMany(e => e.History).HasForeignKey(e => e.MetaverseEntryId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<LifecycleRule>(entity =>
            {
                entity.ToTable("LifecycleRules");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
                entity.HasOne(e => e.Tenant).WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Cascade);
            });

            // ══════════════════════════════════════
            // RULES ENGINE V2
            // ══════════════════════════════════════

            modelBuilder.Entity<SyncRuleV2>(entity =>
            {
                entity.ToTable("SyncRulesV2");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => new { e.TenantId, e.RuleType });
                entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
                entity.Property(e => e.RuleType).HasMaxLength(50);
                entity.HasOne(e => e.Tenant).WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Cascade);
                entity.HasMany(e => e.FlowMappings).WithOne(m => m.SyncRuleV2).HasForeignKey(m => m.SyncRuleV2Id).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<SyncRuleFlowMapping>(entity =>
            {
                entity.ToTable("SyncRuleFlowMappings");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.SourceAttribute).HasMaxLength(200).IsRequired();
                entity.Property(e => e.TargetAttribute).HasMaxLength(200).IsRequired();
            });

            modelBuilder.Entity<SyncRuleVersion>(entity =>
            {
                entity.ToTable("SyncRuleVersions");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => new { e.SyncRuleV2Id, e.VersionNumber }).IsUnique();
                entity.Property(e => e.ChangeNotes).HasMaxLength(500);
                entity.Property(e => e.ChangedBy).HasMaxLength(100);
                entity.HasOne(e => e.SyncRuleV2).WithMany().HasForeignKey(e => e.SyncRuleV2Id).OnDelete(DeleteBehavior.Cascade);
            });

            // ══════════════════════════════════════
            // RESILIENCE
            // ══════════════════════════════════════

            modelBuilder.Entity<QuarantinedIdentity>(entity =>
            {
                entity.ToTable("QuarantinedIdentities");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.IdentityId);
                entity.Property(e => e.Reason).HasMaxLength(500).IsRequired();
                entity.Property(e => e.LastError).HasMaxLength(2000);
            });

            modelBuilder.Entity<DeadLetterEntry>(entity =>
            {
                entity.ToTable("DeadLetterEntries");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.IsReplayed);
                entity.Property(e => e.OperationType).HasMaxLength(50).IsRequired();
                entity.Property(e => e.ErrorMessage).HasMaxLength(2000).IsRequired();
            });
        }
    }
}
