using Microsoft.EntityFrameworkCore;
using IdentitySyncPro.Core.Models.AccountStatus;
using IdentitySyncPro.Infrastructure.Security;

namespace IdentitySyncPro.Infrastructure.Data
{
    /// <summary>
    /// Separate database context for Account Status operations.
    /// Completely independent from AppDbContext and ServicesDbContext — uses its own table with Acct_ prefix.
    /// Same connection string, different table.
    /// </summary>
    public class AccountStatusDbContext : DbContext
    {
        public AccountStatusDbContext(DbContextOptions<AccountStatusDbContext> options) : base(options) { }

        public DbSet<AccountStatusLog> AccountStatusLogs { get; set; } = null!;
        public DbSet<CustomDomain> CustomDomains { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<AccountStatusLog>(entity =>
            {
                entity.ToTable("Acct_StatusLogs");
                entity.HasKey(e => e.Id);

                // Indexes for common queries
                entity.HasIndex(e => e.SamAccountName);
                entity.HasIndex(e => e.Domain);
                entity.HasIndex(e => e.Timestamp);
                entity.HasIndex(e => e.Action);

                // Property constraints
                entity.Property(e => e.SamAccountName).HasMaxLength(200).IsRequired();
                entity.Property(e => e.DisplayName).HasMaxLength(500);
                entity.Property(e => e.Domain).HasMaxLength(300).IsRequired();
                entity.Property(e => e.Action).HasMaxLength(50).IsRequired();
                entity.Property(e => e.Reason).HasMaxLength(1000).IsRequired();
                entity.Property(e => e.PreviousStatus).HasMaxLength(50);
                entity.Property(e => e.NewStatus).HasMaxLength(50).IsRequired();
                entity.Property(e => e.SmsResult).HasMaxLength(2000);
                entity.Property(e => e.PhoneNumber).HasMaxLength(50);
                entity.Property(e => e.PerformedBy).HasMaxLength(100);
            });

            modelBuilder.Entity<CustomDomain>(entity =>
            {
                entity.ToTable("Acct_CustomDomains");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.DisplayName).HasMaxLength(200).IsRequired();
                entity.Property(e => e.Server).HasMaxLength(500).IsRequired();
                entity.Property(e => e.BaseDN).HasMaxLength(500).IsRequired();
                entity.Property(e => e.Username).HasMaxLength(200);
                // 🔐 Secret — encrypted at rest via EncryptedStringConverter
                entity.Property(e => e.Password).HasMaxLength(1024).HasConversion(new EncryptedStringConverter());
            });
        }
    }
}
