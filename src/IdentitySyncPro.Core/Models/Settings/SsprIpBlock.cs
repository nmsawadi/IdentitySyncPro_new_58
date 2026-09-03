namespace IdentitySyncPro.Core.Models.Settings
{
    /// <summary>
    /// Tracks wrong username / national-ID attempts coming from a single client IP,
    /// and blocks that IP once the configured threshold is reached.
    ///
    /// One row per IP (reused across blocks rather than inserting a row per attempt).
    /// The failure counter is reset automatically once the counting window
    /// (<see cref="SsprSettings.IpBlockDurationHours"/>) elapses without a new failure,
    /// and on a successful identity verification.
    ///
    /// ⚠️ Blocking is per IP. Users behind a shared NAT/proxy share one IP, so a block
    /// can affect everyone behind it — keep the threshold sensible and use the admin
    /// unblock action when needed.
    /// </summary>
    public class SsprIpBlock
    {
        public int Id { get; set; }

        /// <summary>Client IP this counter belongs to.</summary>
        public string ClientIp { get; set; } = string.Empty;

        /// <summary>Consecutive failed identity attempts within the current window.</summary>
        public int FailedCount { get; set; }

        /// <summary>First failure of the current window.</summary>
        public DateTime FirstFailureUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Most recent failure (drives window expiry).</summary>
        public DateTime LastFailureUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Blocked until this moment; null / past = not blocked.</summary>
        public DateTime? BlockedUntilUtc { get; set; }

        /// <summary>Last username tried from this IP — for the audit trail.</summary>
        public string? LastUsername { get; set; }
    }
}
