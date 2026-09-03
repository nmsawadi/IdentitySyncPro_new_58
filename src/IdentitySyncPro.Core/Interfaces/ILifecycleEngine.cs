using IdentitySyncPro.Core.Models.Metaverse;

namespace IdentitySyncPro.Core.Interfaces
{
    public interface ILifecycleEngine
    {
        // tenantId scopes the operation to one tenant's rules/metaverse/connectors.
        // Null = first active tenant (legacy single-tenant behavior).
        Task<MetaverseEntry> ImportToMetaverseAsync(int identityId, int? tenantId = null, CancellationToken ct = default);
        /// <summary>
        /// Applies the tenant's lifecycle rules. These rules act on AD directly (moving the
        /// account, adding or removing groups), so <paramref name="dryRun"/> must be honoured
        /// here and not only further down the pipeline.
        /// </summary>
        Task<LifecycleActionResult> ApplyLifecycleRulesAsync(MetaverseEntry entry, bool dryRun = false, CancellationToken ct = default);
        Task<LifecycleActionResult> ExportFromMetaverseAsync(MetaverseEntry entry, CancellationToken ct = default);
        Task<LifecycleActionResult> ProcessIdentityAsync(int identityId, bool dryRun = false, int? tenantId = null, CancellationToken ct = default);
        /// <summary>Lifecycle stats. tenantId null = totals across every tenant.</summary>
        Task<LifecycleStats> GetStatsAsync(int? tenantId = null, CancellationToken ct = default);

        Task<int> BulkImportPendingAsync(int? tenantId = null, Action<int, int>? onProgress = null, CancellationToken ct = default);
        Task<int> BulkApplyRulesAsync(int? tenantId = null, Action<int, int>? onProgress = null, CancellationToken ct = default);
        Task<int> BulkExportAsync(object scopeFactory, int? tenantId = null, Action<int, int>? onProgress = null, CancellationToken ct = default);
        Task ProcessAllPendingPipelineAsync(object scopeFactory, int? tenantId = null, Action<string, int, int>? onStageProgress = null, CancellationToken ct = default);
    }

    /// <summary>
    /// Result of a lifecycle action execution.
    /// </summary>
    public class LifecycleActionResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string? PreviousState { get; set; }
        public string? NewState { get; set; }
        public string? ActionsTaken { get; set; }
        public int DurationMs { get; set; }
    }

    /// <summary>
    /// Lifecycle statistics for dashboard display.
    /// </summary>
    public class LifecycleStats
    {
        public int TotalIdentities { get; set; }
        public int PendingCount { get; set; }
        public int ActiveCount { get; set; }
        public int SuspendedCount { get; set; }
        public int DeprovisionedCount { get; set; }
        public int RecentTransitions { get; set; }
        public int RulesCount { get; set; }
        public int EnabledRulesCount { get; set; }
    }
}
