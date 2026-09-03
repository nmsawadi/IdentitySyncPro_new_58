namespace IdentitySyncPro.Core.Models.Sync
{
    /// <summary>
    /// Tracks the sync state for each identity identity.
    /// Equivalent to the IdentitySyncState SQL table.
    /// </summary>
    public class SyncState
    {
        public int Id { get; set; }

        /// <summary>The tenant (source) this state belongs to. Allows the same
        /// source key to exist under different tenants without collision.</summary>
        public int TenantId { get; set; }

        public int IdentityId { get; set; }
        public string CurrentHash { get; set; } = string.Empty;
        public bool CreatedInAD { get; set; }
        public string Status { get; set; } = "Pending";
        public string? ErrorMessage { get; set; }
        /// <summary>
        /// Stores the identity's last known StatusCode from Oracle.
        /// Used to detect status changes during sync (e.g., Active→Suspended, Suspended→Active)
        /// and trigger lifecycle rules only when the status actually changes.
        /// </summary>
        public int? LastStatusCode { get; set; }
        public DateTime LastSyncDate { get; set; } = DateTime.UtcNow;
        public DateTime LastModified { get; set; } = DateTime.UtcNow;
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    }
}
