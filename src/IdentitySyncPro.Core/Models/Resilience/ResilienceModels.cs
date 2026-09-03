namespace IdentitySyncPro.Core.Models.Resilience
{
    /// <summary>
    /// Identity quarantined due to repeated sync failures.
    /// Isolated from normal processing until reviewed by admin.
    /// </summary>
    public class QuarantinedIdentity
    {
        public int Id { get; set; }
        public int IdentityId { get; set; }
        public string Reason { get; set; } = string.Empty;
        public int FailureCount { get; set; }
        public string? LastError { get; set; }
        public string? FailedOperation { get; set; }
        public DateTime QuarantinedDate { get; set; } = DateTime.UtcNow;
        public DateTime? ReviewedDate { get; set; }
        public string? ReviewedBy { get; set; }
        public bool IsResolved { get; set; }
        public string? ResolutionNotes { get; set; }
    }

    /// <summary>
    /// Failed operation saved for manual replay.
    /// Acts as a dead letter queue for operations that exceeded retry limits.
    /// </summary>
    public class DeadLetterEntry
    {
        public long Id { get; set; }
        public int IdentityId { get; set; }
        public string OperationType { get; set; } = string.Empty;
        public string? Payload { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public int RetryCount { get; set; }
        public DateTime FailedDate { get; set; } = DateTime.UtcNow;
        public DateTime? ReplayedDate { get; set; }
        public bool IsReplayed { get; set; }
        public string? ReplayResult { get; set; }
    }

    /// <summary>
    /// Component health status snapshot.
    /// </summary>
    public class ComponentHealth
    {
        public string Name { get; set; } = string.Empty;
        public string Status { get; set; } = "Unknown";
        public string? Details { get; set; }
        public DateTime LastChecked { get; set; } = DateTime.UtcNow;
        public int ResponseTimeMs { get; set; }
        public int ConsecutiveFailures { get; set; }
        public bool CircuitOpen { get; set; }
    }
}
