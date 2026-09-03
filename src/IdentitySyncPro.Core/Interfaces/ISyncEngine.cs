using IdentitySyncPro.Core.Models.Sync;

namespace IdentitySyncPro.Core.Interfaces
{
    /// <summary>
    /// Main sync engine orchestrator interface.
    /// </summary>
    public interface ISyncEngine
    {
        /// <summary>
        /// Execute a full sync run (all records from source).
        /// When <paramref name="tenantId"/> is null, all active tenants run sequentially —
        /// each with its own source connection, mappings, and rules.
        /// Returns the last tenant's run.
        /// </summary>
        Task<SyncRun> RunFullSyncAsync(int batchSize = 1000, bool dryRun = false, int? tenantId = null, CancellationToken ct = default, string? triggeredBy = null);

        /// <summary>
        /// Execute a delta sync (only changed records).
        /// When <paramref name="tenantId"/> is null, all active tenants run sequentially.
        /// </summary>
        Task<SyncRun> RunDeltaSyncAsync(int batchSize = 1000, bool dryRun = false, int? tenantId = null, CancellationToken ct = default, string? triggeredBy = null);

        /// <summary>
        /// Sync a single identity by its source key.
        /// When <paramref name="tenantId"/> is null, the first active tenant is used.
        /// </summary>
        Task<SyncOperation> SyncSingleAsync(int identityId, bool dryRun = false, int? tenantId = null, CancellationToken ct = default, string? triggeredBy = null);

        /// <summary>
        /// Get the current sync engine status.
        /// </summary>
        bool IsRunning { get; }

        /// <summary>
        /// Cancel a running sync.
        /// </summary>
        void CancelCurrentSync();

        /// <summary>
        /// Event for progress reporting.
        /// </summary>
        event Action<SyncProgressInfo>? OnProgress;
    }

    /// <summary>
    /// Sync progress information for real-time monitoring.
    /// </summary>
    public class SyncProgressInfo
    {
        public int TotalRecords { get; set; }
        public int ProcessedRecords { get; set; }
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public string CurrentOperation { get; set; } = string.Empty;
        public string? CurrentIdentityId { get; set; }
        public double ProgressPercentage => TotalRecords > 0 ? (double)ProcessedRecords / TotalRecords * 100 : 0;
        public TimeSpan Elapsed { get; set; }
        public TimeSpan? EstimatedRemaining { get; set; }

        /// <summary>Current phase number (1-based). For Full Sync: 1=Preparation, 2=New Identities, 3=Existing Identities.</summary>
        public int CurrentPhase { get; set; }
        /// <summary>Total number of phases in this sync type. Full=3, Delta=2.</summary>
        public int TotalPhases { get; set; }
        /// <summary>Localized description of the current phase.</summary>
        public string PhaseDescription { get; set; } = string.Empty;
        /// <summary>Progress percentage within the current phase (0-100).</summary>
        public double PhaseProgress { get; set; }
    }
}
