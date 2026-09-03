namespace IdentitySyncPro.Core.Models.Settings
{
    /// <summary>
    /// One record per outbound SMS attempt, unified across features so operators can review who
    /// received a message and retry the failures. Covers:
    ///   • Sync — identity credentials SMS on account creation.
    ///   • Offboarding — employee notification SMS (Services module).
    /// </summary>
    public class SmsSendLog
    {
        public int Id { get; set; }

        /// <summary>Which feature sent it: "Sync" or "Offboarding".</summary>
        public string Source { get; set; } = "Sync";

        /// <summary>Numeric id when available (identity id / employee id), else 0.</summary>
        public int IdentityId { get; set; }

        /// <summary>AD account / username — the primary identifier shown and filtered on.</summary>
        public string? Account { get; set; }

        /// <summary>Recipient name (identity or employee).</summary>
        public string? DisplayName { get; set; }

        /// <summary>Destination number (masked in the UI).</summary>
        public string? PhoneNumber { get; set; }

        /// <summary>Success | Failed | Skipped</summary>
        public string Status { get; set; } = "Failed";

        /// <summary>Name of the SMS provider used (or blank when skipped).</summary>
        public string? ProviderName { get; set; }

        /// <summary>Gateway response body on success, or the error/reason on failure/skip.</summary>
        public string? GatewayResponse { get; set; }

        /// <summary>
        /// The fully-rendered message text — kept so a failed send can be retried verbatim.
        /// Stored ENCRYPTED at rest (identity credentials messages contain the password) and cleared
        /// once the SMS is delivered successfully.
        /// </summary>
        public string? SentMessage { get; set; }

        /// <summary>Sync run this attempt belonged to (null for offboarding / manual retries).</summary>
        public int? SyncRunId { get; set; }

        public int RetryCount { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastAttemptAt { get; set; }

        /// <summary>True when the send failed and the message is still available to retry.</summary>
        public bool IsRetryable => Status == "Failed" && !string.IsNullOrEmpty(SentMessage);
    }
}
