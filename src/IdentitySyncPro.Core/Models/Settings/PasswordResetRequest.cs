namespace IdentitySyncPro.Core.Models.Settings
{
    /// <summary>
    /// One self-service password reset attempt (public portal).
    /// The OTP itself is never stored — only its SHA256 hash.
    /// The user is identified by AD account (username), verified directly
    /// against the domain by national ID.
    /// </summary>
    public class PasswordResetRequest
    {
        public int Id { get; set; }

        /// <summary>Opaque handle carried between the two portal steps.</summary>
        public string RequestGuid { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>The SSPR domain the account belongs to.</summary>
        public int SsprDomainId { get; set; }

        /// <summary>AD account name entered by the user (sAMAccountName).</summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>Phone the OTP was sent to (pulled from AD).</summary>
        public string PhoneNumber { get; set; } = string.Empty;

        /// <summary>SHA256 of the one-time code.</summary>
        public string OtpHash { get; set; } = string.Empty;

        public DateTime ExpiresAtUtc { get; set; }
        public int Attempts { get; set; }
        public bool IsUsed { get; set; }

        /// <summary>Pending | Completed | Expired | Blocked</summary>
        public string Status { get; set; } = "Pending";

        public string? ClientIp { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
