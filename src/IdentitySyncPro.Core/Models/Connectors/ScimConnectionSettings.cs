namespace IdentitySyncPro.Core.Models.Connectors
{
    /// <summary>
    /// What a SCIM 2.0 target needs to be reached.
    ///
    /// Deliberately small. SCIM standardises the paths (<c>/Users</c>, <c>/Groups</c>) and the
    /// payloads, so the only per-installation facts are where the service lives, how to prove who
    /// is calling, and how long to wait.
    /// </summary>
    public class ScimConnectionSettings
    {
        /// <summary>Service root, e.g. https://idp.example.edu/scim/v2 — no resource path.</summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>Bearer token. Held in memory only; the stored copy is encrypted at rest.</summary>
        public string? BearerToken { get; set; }

        /// <summary>Accept an internal-CA or self-signed certificate, for a service inside an isolated network.</summary>
        public bool AllowUntrustedCertificate { get; set; }

        /// <summary>
        /// Per-request ceiling.
        ///
        /// Not optional and not unbounded. An unbounded wait on a network socket once stopped this
        /// system's entire background pipeline without reporting anything — the workers were held
        /// on a mail server that never answered, and every sweep simply stopped while the job list
        /// looked busy. No HTTP client is added here without a limit.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>Name shown in logs and on the connection-test screen.</summary>
        public string DisplayName { get; set; } = "SCIM";
    }
}
