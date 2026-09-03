using Microsoft.EntityFrameworkCore;
using IdentitySyncPro.Core.Models.Services;
using IdentitySyncPro.Infrastructure.Security;

namespace IdentitySyncPro.Infrastructure.Data
{
    /// <summary>
    /// Separate database context for the Services module.
    /// Completely independent from AppDbContext — uses its own tables with Svc_ prefix.
    /// Same connection string, different tables.
    /// </summary>
    public class ServicesDbContext : DbContext
    {
        public ServicesDbContext(DbContextOptions<ServicesDbContext> options) : base(options) { }

        public DbSet<SvcService> SvcServices { get; set; } = null!;
        public DbSet<SvcFieldMapping> SvcFieldMappings { get; set; } = null!;
        public DbSet<SvcRunLog> SvcRunLogs { get; set; } = null!;
        public DbSet<SvcAuditEntry> SvcAuditEntries { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // ══════════════════════════════════════
            // SvcService
            // ══════════════════════════════════════
            modelBuilder.Entity<SvcService>(entity =>
            {
                entity.ToTable("Svc_Services");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
                entity.Property(e => e.Description).HasMaxLength(500);

                // Source
                entity.Property(e => e.SourceProvider).HasMaxLength(50).IsRequired();
                entity.Property(e => e.SourceHost).HasMaxLength(300);
                entity.Property(e => e.SourceDatabase).HasMaxLength(200);
                entity.Property(e => e.SourceUsername).HasMaxLength(100);
                // 🔐 Secret — encrypted at rest via EncryptedStringConverter
                entity.Property(e => e.SourcePassword).HasMaxLength(1024).HasConversion(new EncryptedStringConverter());
                entity.Property(e => e.SourceTableOrView).HasMaxLength(200);

                // AD Target
                entity.Property(e => e.ADServer).HasMaxLength(300);
                entity.Property(e => e.ADUsername).HasMaxLength(200);
                // 🔐 Secret — encrypted at rest
                entity.Property(e => e.ADPassword).HasMaxLength(1024).HasConversion(new EncryptedStringConverter());
                entity.Property(e => e.ADBaseDN).HasMaxLength(500);
                entity.Property(e => e.OffboardingSearchOU).HasMaxLength(500);
                entity.Property(e => e.OffboardingExclusionGroup).HasMaxLength(500);
                entity.Property(e => e.EmptyCheckAttributes).HasMaxLength(500);
                entity.Property(e => e.ADSearchAttribute).HasMaxLength(100);

                // Key
                entity.Property(e => e.KeySourceColumn).HasMaxLength(200);

                // Schedule
                entity.Property(e => e.ScheduleMode).HasMaxLength(20);
                entity.Property(e => e.ScheduleTime).HasMaxLength(10);
                entity.Property(e => e.ScheduleDays).HasMaxLength(50);
                entity.Property(e => e.ScheduleCron).HasMaxLength(100);

                // Status
                entity.Property(e => e.LastRunStatus).HasMaxLength(50);

                // Navigation
                entity.HasMany(e => e.FieldMappings)
                      .WithOne(m => m.Service)
                      .HasForeignKey(m => m.SvcServiceId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasMany(e => e.RunLogs)
                      .WithOne(r => r.Service)
                      .HasForeignKey(r => r.SvcServiceId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // ══════════════════════════════════════
            // SvcFieldMapping
            // ══════════════════════════════════════
            modelBuilder.Entity<SvcFieldMapping>(entity =>
            {
                entity.ToTable("Svc_FieldMappings");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.SourceColumn).HasMaxLength(200).IsRequired();
                entity.Property(e => e.TargetAttribute).HasMaxLength(200).IsRequired();
                entity.HasIndex(e => new { e.SvcServiceId, e.SortOrder });
            });

            // ══════════════════════════════════════
            // SvcRunLog
            // ══════════════════════════════════════
            modelBuilder.Entity<SvcRunLog>(entity =>
            {
                entity.ToTable("Svc_RunLogs");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.SvcServiceId);
                entity.HasIndex(e => e.StartTime);
                entity.Property(e => e.Status).HasMaxLength(50);
                entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
                entity.Property(e => e.TriggeredBy).HasMaxLength(200);   // holds a username, not just "Schedule"

                entity.HasMany(e => e.AuditEntries)
                      .WithOne(a => a.RunLog)
                      .HasForeignKey(a => a.SvcRunLogId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // ══════════════════════════════════════
            // SvcAuditEntry
            // ══════════════════════════════════════
            modelBuilder.Entity<SvcAuditEntry>(entity =>
            {
                entity.ToTable("Svc_AuditEntries");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.SvcRunLogId);
                entity.HasIndex(e => e.SvcServiceId);
                entity.HasIndex(e => e.Timestamp);
                entity.HasIndex(e => e.KeyValue);
                entity.Property(e => e.Action).HasMaxLength(50).IsRequired();
                entity.Property(e => e.KeyValue).HasMaxLength(200).IsRequired();
                entity.Property(e => e.ADIdentity).HasMaxLength(500);
                entity.Property(e => e.AttributeName).HasMaxLength(200);
                entity.Property(e => e.OldValue).HasMaxLength(2000);
                entity.Property(e => e.NewValue).HasMaxLength(2000);
                entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
            });
        }
    }
}
