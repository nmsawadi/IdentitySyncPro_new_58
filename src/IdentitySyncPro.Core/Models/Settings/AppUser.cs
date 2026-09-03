namespace IdentitySyncPro.Core.Models.Settings
{
    /// <summary>
    /// An application (console) user. Authentication is local by default
    /// (PBKDF2 password hash) or delegated to Active Directory per user.
    /// </summary>
    public class AppUser
    {
        public int Id { get; set; }

        /// <summary>Login name. For AD users this is the account (sAMAccountName or UPN).</summary>
        public string Username { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        /// <summary>PBKDF2 hash (null for ActiveDirectory-authenticated users).</summary>
        public string? PasswordHash { get; set; }

        /// <summary>Admin | Operator | Viewer</summary>
        public string Role { get; set; } = AppUserRoles.Viewer;

        /// <summary>Local | ActiveDirectory</summary>
        public string AuthType { get; set; } = AppUserAuthTypes.Local;

        public bool IsActive { get; set; } = true;

        /// <summary>Force a password change at next login (seeded/reset accounts).</summary>
        public bool MustChangePassword { get; set; }

        /// <summary>
        /// When this LOCAL user's password was last set. Drives the maximum-password-age policy.
        ///
        /// Null means the account predates age tracking. It is deliberately NOT treated as
        /// "infinitely old": the real last-change date was never recorded, and <c>CreatedUtc</c>
        /// is not a substitute (resetting a password never touched it), so inferring an old date
        /// would force a change on people who changed theirs yesterday. Existing rows are stamped
        /// with the upgrade time instead — everyone gets one full period from that point.
        ///
        /// Always null for Active Directory users: their password lives in the domain and its
        /// age is the domain's policy to enforce, not this application's.
        /// </summary>
        public DateTime? PasswordChangedUtc { get; set; }

        // ── Multi-factor authentication (TOTP) ────────────────────────────────
        // Applies to LOCAL and AD users alike: MFA is a second factor on top of whatever
        // proved the first one, so a domain-authenticated administrator is covered too.

        /// <summary>Base32 TOTP secret. Encrypted at rest — it is a bearer credential.</summary>
        public string? MfaSecret { get; set; }

        /// <summary>When enrollment completed. Null = not enrolled.</summary>
        public DateTime? MfaEnabledUtc { get; set; }

        /// <summary>
        /// Last accepted time step. A valid code stays valid for the whole drift window, so
        /// without remembering the spent step a captured code could simply be replayed.
        /// </summary>
        public long? MfaLastUsedTimeStep { get; set; }

        /// <summary>
        /// Newline-separated PBKDF2 hashes of single-use recovery codes.
        ///
        /// Not a convenience: TOTP binds access to one device, and the last administrator losing
        /// that phone would otherwise mean nobody can ever sign in again. Stored hashed because a
        /// recovery code is a password equivalent.
        /// </summary>
        public string? MfaRecoveryCodes { get; set; }

        public bool IsMfaEnrolled => MfaEnabledUtc.HasValue && !string.IsNullOrEmpty(MfaSecret);

        public int FailedLoginAttempts { get; set; }
        public DateTime? LockoutUntilUtc { get; set; }
        public DateTime? LastLoginUtc { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }

    public static class AppUserRoles
    {
        public const string Admin = "Admin";
        public const string Operator = "Operator";
        public const string Viewer = "Viewer";

        /// <summary>Roles allowed to run sync/lifecycle/service operations.</summary>
        public const string AdminOrOperator = Admin + "," + Operator;

        public static readonly string[] All = { Admin, Operator, Viewer };
    }

    public static class AppUserAuthTypes
    {
        public const string Local = "Local";
        public const string ActiveDirectory = "ActiveDirectory";
    }
}
