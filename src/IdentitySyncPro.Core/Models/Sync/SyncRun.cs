using IdentitySyncPro.Core.Enums;

namespace IdentitySyncPro.Core.Models.Sync
{
    /// <summary>
    /// Represents a complete sync execution run with aggregate statistics.
    /// </summary>
    public class SyncRun
    {
        public int Id { get; set; }

        /// <summary>Unique correlation ID for tracing this sync run across all logs and audit entries.</summary>
        public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N")[..12];

        /// <summary>The tenant this run executed for (null for legacy/global runs).</summary>
        public int? TenantId { get; set; }

        public string RunType { get; set; } = "Full"; // Full, Delta, Manual
        public SyncRunStatus Status { get; set; } = SyncRunStatus.Pending;
        public DateTime StartTime { get; set; } = DateTime.UtcNow;
        public DateTime? EndTime { get; set; }
        public int TotalProcessed { get; set; }
        public int TotalCreated { get; set; }
        public int TotalUpdated { get; set; }
        public int TotalSkipped { get; set; }
        public int TotalFailed { get; set; }
        public int TotalNoChange { get; set; }
        public int TotalAlreadyExisted { get; set; }
        public string? ErrorMessage { get; set; }
        public string? TriggeredBy { get; set; } = "System";
        public int BatchSize { get; set; } = 1000;

        public TimeSpan? Duration => EndTime.HasValue ? EndTime.Value - StartTime : null;

        // Navigation
        public ICollection<SyncOperation> Operations { get; set; } = new List<SyncOperation>();
    }
}
