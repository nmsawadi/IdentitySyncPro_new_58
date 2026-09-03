namespace IdentitySyncPro.Core.Models.Services
{
    /// <summary>
    /// Log entry for each service execution run.
    /// Tracks start/end times, record counts, and overall status.
    /// </summary>
    public class SvcRunLog
    {
        public long Id { get; set; }
        public int SvcServiceId { get; set; }
        public DateTime StartTime { get; set; } = DateTime.UtcNow;
        public DateTime? EndTime { get; set; }

        /// <summary>Run status: "Running", "Completed", "CompletedWithErrors", "Failed"</summary>
        public string Status { get; set; } = "Running";

        public int TotalRecords { get; set; }
        public int UpdatedRecords { get; set; }
        public int FailedRecords { get; set; }
        public int SkippedRecords { get; set; }
        public int NotFoundRecords { get; set; }

        public string? ErrorMessage { get; set; }

        /// <summary>How this run was triggered: "Schedule" or "Manual"</summary>
        public string TriggeredBy { get; set; } = "Manual";

        // Navigation
        public SvcService Service { get; set; } = null!;
        public List<SvcAuditEntry> AuditEntries { get; set; } = new();
    }
}
