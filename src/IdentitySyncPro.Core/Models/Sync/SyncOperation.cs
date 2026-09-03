using IdentitySyncPro.Core.Enums;

namespace IdentitySyncPro.Core.Models.Sync
{
    /// <summary>
    /// Represents a single sync operation (create/update) for one identity.
    /// Equivalent to the SyncLog table entries.
    /// </summary>
    public class SyncOperation
    {
        public long Id { get; set; }
        public int SyncRunId { get; set; }
        public int IdentityId { get; set; }
        public OperationType Operation { get; set; }
        public SyncOperationStatus Status { get; set; }
        public string? ErrorMessage { get; set; }
        public string? ChangedFields { get; set; }
        public int DurationMs { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>Lifecycle actions taken during a single sync — transient, not stored (lifecycle history has its own tables).</summary>
        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public string? LifecycleAction { get; set; }

        // Navigation
        public SyncRun? SyncRun { get; set; }
    }
}
