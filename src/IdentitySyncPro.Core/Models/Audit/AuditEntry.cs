using IdentitySyncPro.Core.Enums;

namespace IdentitySyncPro.Core.Models.Audit
{
    /// <summary>
    /// Comprehensive audit trail entry for all system operations.
    /// </summary>
    public class AuditEntry
    {
        public long Id { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public AuditSeverity Severity { get; set; } = AuditSeverity.Info;
        public string Category { get; set; } = "System";
        public string Action { get; set; } = string.Empty;
        public string? EntityType { get; set; }
        public string? EntityId { get; set; }
        public string? OldValues { get; set; }
        public string? NewValues { get; set; }
        public string? Details { get; set; }
        public string? PerformedBy { get; set; } = "System";
        public string? IpAddress { get; set; }

        /// <summary>Correlation ID for tracing this entry back to a specific sync run or operation.</summary>
        public string? CorrelationId { get; set; }
    }
}
