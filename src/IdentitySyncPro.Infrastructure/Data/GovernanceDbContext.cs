using IdentitySyncPro.Core.Models.Governance;
using Microsoft.EntityFrameworkCore;

namespace IdentitySyncPro.Infrastructure.Data
{
    /// <summary>
    /// Access governance: the request catalog, the requests, and the decisions taken on them.
    ///
    /// Its own context with a <c>Gov_</c> prefix, following the same separation as
    /// <see cref="ServicesDbContext"/> and <see cref="AccountStatusDbContext"/> — same connection,
    /// independent tables, so the module can be reasoned about and upgraded on its own.
    /// </summary>
    public class GovernanceDbContext : DbContext
    {
        public GovernanceDbContext(DbContextOptions<GovernanceDbContext> options) : base(options) { }

        public DbSet<GovCatalogItem> CatalogItems { get; set; } = null!;
        public DbSet<GovAccessRequest> AccessRequests { get; set; } = null!;
        public DbSet<GovRequestDecision> RequestDecisions { get; set; } = null!;
        public DbSet<GovCampaign> Campaigns { get; set; } = null!;
        public DbSet<GovCampaignItem> CampaignItems { get; set; } = null!;
        public DbSet<GovReviewDelegation> ReviewDelegations { get; set; } = null!;
        public DbSet<GovNhiAccount> NhiAccounts { get; set; } = null!;
        public DbSet<GovSodPolicy> SodPolicies { get; set; } = null!;
        public DbSet<GovSodViolation> SodViolations { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<GovCatalogItem>(entity =>
            {
                entity.ToTable("Gov_CatalogItems");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.DisplayName).HasMaxLength(200).IsRequired();
                entity.Property(e => e.Description).HasMaxLength(1000);
                entity.Property(e => e.TargetType).HasMaxLength(30).IsRequired();
                entity.Property(e => e.GroupName).HasMaxLength(500).IsRequired();
                entity.Property(e => e.ApproverAdGroup).HasMaxLength(500);
                entity.Property(e => e.ApproverUsers).HasMaxLength(1000);
                entity.Property(e => e.ApproverNotificationEmail).HasMaxLength(500);
                entity.Property(e => e.EligibleRequesterGroup).HasMaxLength(500);

                entity.HasIndex(e => e.TenantId);
                entity.HasIndex(e => e.IsEnabled);
            });

            modelBuilder.Entity<GovAccessRequest>(entity =>
            {
                entity.ToTable("Gov_AccessRequests");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.SubjectAccount).HasMaxLength(200).IsRequired();
                entity.Property(e => e.SubjectDisplayName).HasMaxLength(500);
                entity.Property(e => e.RequestedBy).HasMaxLength(200).IsRequired();
                entity.Property(e => e.Channel).HasMaxLength(20).IsRequired();
                entity.Property(e => e.Justification).HasMaxLength(2000).IsRequired();
                entity.Property(e => e.Status).HasMaxLength(20).IsRequired();
                entity.Property(e => e.ExecutionStatus).HasMaxLength(20).IsRequired();
                entity.Property(e => e.ExecutionError).HasMaxLength(2000);

                // The three questions the screens actually ask: what is waiting on me, what did I
                // ask for, and what does this account already hold.
                entity.HasIndex(e => e.Status);
                entity.HasIndex(e => e.RequestedBy);
                entity.HasIndex(e => e.SubjectAccount);

                // The two background sweeps: close overdue decisions, revoke lapsed access.
                entity.HasIndex(e => e.DecisionDueUtc);
                entity.HasIndex(e => e.AccessExpiresUtc);

                entity.HasOne(e => e.CatalogItem)
                      .WithMany()
                      .HasForeignKey(e => e.CatalogItemId)
                      .OnDelete(DeleteBehavior.Restrict);   // a decided request outlives the catalog entry that produced it

                entity.HasMany(e => e.Decisions)
                      .WithOne(d => d.Request)
                      .HasForeignKey(d => d.RequestId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<GovCampaign>(entity =>
            {
                entity.ToTable("Gov_Campaigns");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
                entity.Property(e => e.Description).HasMaxLength(1000);
                entity.Property(e => e.ScopeGroups).HasMaxLength(2000);
                entity.Property(e => e.ScopeCatalogItemIds).HasMaxLength(1000);
                entity.Property(e => e.ReviewerUsers).HasMaxLength(1000);
                entity.Property(e => e.ReviewerAdGroup).HasMaxLength(500);
                entity.Property(e => e.ReviewerNotificationEmail).HasMaxLength(500);
                entity.Property(e => e.Status).HasMaxLength(20).IsRequired();
                entity.Property(e => e.ClosingNote).HasMaxLength(2000);

                entity.HasIndex(e => e.Status);
                // The deadline sweep's only query.
                entity.HasIndex(e => e.DueUtc);

                entity.HasMany(e => e.Items)
                      .WithOne(i => i.Campaign)
                      .HasForeignKey(i => i.CampaignId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<GovCampaignItem>(entity =>
            {
                entity.ToTable("Gov_CampaignItems");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.SubjectAccount).HasMaxLength(200).IsRequired();
                entity.Property(e => e.SubjectDisplayName).HasMaxLength(500);
                entity.Property(e => e.GroupName).HasMaxLength(500).IsRequired();
                entity.Property(e => e.Decision).HasMaxLength(20).IsRequired();
                entity.Property(e => e.DecidedBy).HasMaxLength(200);
                entity.Property(e => e.DecidedOnBehalfOf).HasMaxLength(200);
                entity.Property(e => e.DecisionSource).HasMaxLength(30).IsRequired();
                entity.Property(e => e.Comment).HasMaxLength(2000);
                entity.Property(e => e.ExecutionStatus).HasMaxLength(20).IsRequired();
                entity.Property(e => e.ExecutionError).HasMaxLength(2000);

                // "What is left to review", "what did this account hold", "what did we revoke".
                entity.HasIndex(e => new { e.CampaignId, e.Decision });
                entity.HasIndex(e => e.SubjectAccount);
                entity.HasIndex(e => e.ExecutionStatus);
            });

            modelBuilder.Entity<GovReviewDelegation>(entity =>
            {
                entity.ToTable("Gov_ReviewDelegations");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.FromUsername).HasMaxLength(200).IsRequired();
                entity.Property(e => e.ToUsername).HasMaxLength(200).IsRequired();
                entity.Property(e => e.Reason).HasMaxLength(1000);

                // Asked on every review screen load: "whose authority do I currently carry?"
                entity.HasIndex(e => new { e.ToUsername, e.EndUtc });
                entity.HasIndex(e => e.FromUsername);
            });

            modelBuilder.Entity<GovNhiAccount>(entity =>
            {
                entity.ToTable("Gov_NhiAccounts");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.ObjectGuid).HasMaxLength(50).IsRequired();
                entity.Property(e => e.Account).HasMaxLength(200).IsRequired();
                entity.Property(e => e.DistinguishedName).HasMaxLength(500).IsRequired();
                entity.Property(e => e.DisplayName).HasMaxLength(500);
                entity.Property(e => e.Description).HasMaxLength(1000);
                entity.Property(e => e.Signals).HasMaxLength(500);
                entity.Property(e => e.DirectoryOwner).HasMaxLength(500);
                entity.Property(e => e.State).HasMaxLength(20).IsRequired();
                entity.Property(e => e.OwnerUsername).HasMaxLength(200);
                entity.Property(e => e.DisownedBy).HasMaxLength(200);
                entity.Property(e => e.LastAttestedBy).HasMaxLength(200);
                entity.Property(e => e.AttestationNote).HasMaxLength(2000);
                entity.Property(e => e.QuarantineReason).HasMaxLength(40);
                entity.Property(e => e.QuarantineEffect).HasMaxLength(30).IsRequired();
                entity.Property(e => e.QuarantineError).HasMaxLength(2000);
                entity.Property(e => e.ExemptReason).HasMaxLength(1000);
                entity.Property(e => e.ExemptBy).HasMaxLength(200);

                // The identity of the account across runs. Unique per service so the reconciler can
                // find the existing row instead of creating a second history for the same object —
                // and unique in the database rather than only in the code that looks it up, because
                // two runs overlapping would otherwise each insert one.
                entity.HasIndex(e => new { e.ServiceId, e.ObjectGuid }).IsUnique();

                // "What is mine", "what is unowned", "what is due".
                entity.HasIndex(e => e.OwnerUsername);
                entity.HasIndex(e => new { e.ServiceId, e.State });
                entity.HasIndex(e => e.ClaimDueUtc);
                entity.HasIndex(e => e.LastAttestedUtc);
                entity.HasIndex(e => e.Account);
            });

            modelBuilder.Entity<GovSodPolicy>(entity =>
            {
                entity.ToTable("Gov_SodPolicies");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
                entity.Property(e => e.Rationale).HasMaxLength(2000).IsRequired();
                entity.Property(e => e.DutyAName).HasMaxLength(200).IsRequired();
                entity.Property(e => e.DutyBName).HasMaxLength(200).IsRequired();
                entity.Property(e => e.DutyAGroups).HasMaxLength(2000).IsRequired();
                entity.Property(e => e.DutyBGroups).HasMaxLength(2000).IsRequired();
                entity.Property(e => e.Enforcement).HasMaxLength(20).IsRequired();
                entity.Property(e => e.Severity).HasMaxLength(20).IsRequired();
                entity.Property(e => e.CreatedBy).HasMaxLength(200);

                entity.HasIndex(e => new { e.TenantId, e.IsEnabled });
            });

            modelBuilder.Entity<GovSodViolation>(entity =>
            {
                entity.ToTable("Gov_SodViolations");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.SubjectAccount).HasMaxLength(200).IsRequired();
                entity.Property(e => e.SubjectDisplayName).HasMaxLength(500);
                entity.Property(e => e.MatchedA).HasMaxLength(2000).IsRequired();
                entity.Property(e => e.MatchedB).HasMaxLength(2000).IsRequired();
                entity.Property(e => e.MitigationReason).HasMaxLength(2000);
                entity.Property(e => e.MitigatedBy).HasMaxLength(200);

                // One live row per person per policy: the reconciler finds the existing one and
                // extends it rather than writing a new violation every time the scan runs.
                entity.HasIndex(e => new { e.PolicyId, e.SubjectAccount, e.ClearedUtc });
                entity.HasIndex(e => e.SubjectAccount);
                entity.HasIndex(e => e.ClearedUtc);

                entity.HasOne(e => e.Policy)
                      .WithMany()
                      .HasForeignKey(e => e.PolicyId)
                      .OnDelete(DeleteBehavior.Restrict);   // a recorded violation outlives the rule that found it
            });

            modelBuilder.Entity<GovRequestDecision>(entity =>
            {
                entity.ToTable("Gov_RequestDecisions");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.ApproverUsername).HasMaxLength(200).IsRequired();
                entity.Property(e => e.Decision).HasMaxLength(20).IsRequired();
                entity.Property(e => e.Comment).HasMaxLength(2000);

                entity.HasIndex(e => e.RequestId);
                entity.HasIndex(e => e.ApproverUsername);
            });
        }
    }
}
