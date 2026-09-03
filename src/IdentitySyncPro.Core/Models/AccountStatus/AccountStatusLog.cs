namespace IdentitySyncPro.Core.Models.AccountStatus
{
    /// <summary>
    /// Represents a single account enable/disable operation log entry.
    /// Stored in a completely separate table (Acct_StatusLogs) — independent from IAM and Services.
    /// Multiple entries per user are expected (full audit trail).
    /// </summary>
    public class AccountStatusLog
    {
        public long Id { get; set; }

        /// <summary>sAMAccountName of the AD user</summary>
        public string SamAccountName { get; set; } = string.Empty;

        /// <summary>Display name from AD description attribute (Arabic name)</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>The AD domain used (e.g., dc.example.local)</summary>
        public string Domain { get; set; } = string.Empty;

        /// <summary>"Disable" or "Enable"</summary>
        public string Action { get; set; } = string.Empty;

        /// <summary>Reason for the action (entered by the operator)</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>Previous account status ("Enabled" or "Disabled")</summary>
        public string? PreviousStatus { get; set; }

        /// <summary>New account status after the action</summary>
        public string NewStatus { get; set; } = string.Empty;

        /// <summary>Whether an SMS was sent</summary>
        public bool SmsSent { get; set; }

        /// <summary>SMS API response or error message</summary>
        public string? SmsResult { get; set; }

        /// <summary>Phone number used for SMS (from AD mobile attribute)</summary>
        public string? PhoneNumber { get; set; }

        /// <summary>Who performed this operation</summary>
        public string PerformedBy { get; set; } = "System";

        /// <summary>Timestamp of the operation</summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}
