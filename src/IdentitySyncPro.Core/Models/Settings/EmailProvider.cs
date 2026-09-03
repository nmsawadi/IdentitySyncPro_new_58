namespace IdentitySyncPro.Core.Models.Settings
{
    /// <summary>
    /// SMTP transport configuration for outbound email, managed from the Notifications Center.
    /// Supports two delivery methods via <see cref="Mode"/>:
    ///   • "Authenticated" — SMTP AUTH with username + password (e.g. smtp.office365.com:587).
    ///   • "DirectSend"    — anonymous relay via the tenant MX endpoint (e.g.
    ///                        {tenant}.mail.protection.outlook.com:25), internal recipients only.
    /// The active row (IsActive = true) is the one used by <c>EmailService</c>.
    /// </summary>
    public class EmailProvider
    {
        public int Id { get; set; }

        public string Name { get; set; } = "Default";

        /// <summary>"Authenticated" or "DirectSend".</summary>
        public string Mode { get; set; } = "Authenticated";

        public string SmtpHost { get; set; } = string.Empty;
        public int SmtpPort { get; set; } = 587;

        /// <summary>Used only in Authenticated mode.</summary>
        public string? Username { get; set; }

        /// <summary>Used only in Authenticated mode. Stored encrypted at rest.</summary>
        public string? Password { get; set; }

        public string? FromEmail { get; set; }
        public string FromName { get; set; } = "IdentitySyncPro";

        public bool EnableSsl { get; set; } = true;
        public bool IsActive { get; set; } = true;

        public string? Notes { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
