namespace IdentitySyncPro.Core.Models.Services
{
    /// <summary>
    /// Detailed audit trail for each individual record operation within a service run.
    /// Records what was searched, what was found, what changed, and any errors.
    /// </summary>
    public class SvcAuditEntry
    {
        public long Id { get; set; }
        public long SvcRunLogId { get; set; }
        public int SvcServiceId { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>Action performed: "Update", "Skip", "NotFound", "Error", "NoChange"</summary>
        public string Action { get; set; } = string.Empty;

        /// <summary>The key value used to search in AD (e.g., employee number)</summary>
        public string KeyValue { get; set; } = string.Empty;

        /// <summary>The AD user identity found (DN or sAMAccountName), null if not found</summary>
        public string? ADIdentity { get; set; }

        /// <summary>The AD attribute that was updated</summary>
        public string? AttributeName { get; set; }

        /// <summary>Previous value of the attribute in AD</summary>
        public string? OldValue { get; set; }

        /// <summary>New value written to AD</summary>
        public string? NewValue { get; set; }

        /// <summary>Error message if the operation failed</summary>
        public string? ErrorMessage { get; set; }

        // Navigation
        public SvcRunLog RunLog { get; set; } = null!;
    }
}
