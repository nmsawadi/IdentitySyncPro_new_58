namespace IdentitySyncPro.Core.Models.Settings
{
    /// <summary>
    /// Maximum password age for LOCAL console users, resolved once at startup from configuration.
    ///
    /// It is a policy value, not a constant: the required interval differs between institutions,
    /// and an installation with a compensating control may legitimately disable it. Hardcoding 90
    /// would force a code change to express a decision that belongs to whoever runs the system.
    /// </summary>
    public sealed class PasswordPolicy
    {
        /// <summary>Common regulatory baseline, used when nothing is configured.</summary>
        public const int DefaultMaxAgeDays = 90;

        /// <summary>Days a password stays valid. Zero (or less) disables expiry entirely.</summary>
        public int MaxAgeDays { get; }

        public PasswordPolicy(int maxAgeDays = DefaultMaxAgeDays) => MaxAgeDays = maxAgeDays;

        public bool IsEnabled => MaxAgeDays > 0;

        /// <summary>
        /// Whether a local user's password has reached the maximum age.
        ///
        /// Static and side-effect free so the rule can be tested directly — the alternative is
        /// asserting on it only through a full authentication round trip, where a wrong answer
        /// looks like a login bug.
        /// </summary>
        /// <param name="passwordChangedUtc">When the password was last set; null = not tracked yet.</param>
        /// <param name="isLocalUser">AD users are exempt — the domain owns their password.</param>
        /// <param name="nowUtc">Current time, injected for deterministic tests.</param>
        public bool IsExpired(DateTime? passwordChangedUtc, bool isLocalUser, DateTime nowUtc)
        {
            if (!IsEnabled) return false;
            if (!isLocalUser) return false;

            // Never tracked: treat as fresh. Startup stamps existing rows, so this only covers a
            // row written by something that bypassed the application entirely.
            if (passwordChangedUtc == null) return false;

            return (nowUtc - passwordChangedUtc.Value).TotalDays >= MaxAgeDays;
        }

        /// <summary>
        /// Whole days left before expiry; null when expiry does not apply to this user.
        /// Negative values mean the password is already overdue.
        /// </summary>
        public int? DaysRemaining(DateTime? passwordChangedUtc, bool isLocalUser, DateTime nowUtc)
        {
            if (!IsEnabled || !isLocalUser || passwordChangedUtc == null) return null;
            return (int)Math.Ceiling(MaxAgeDays - (nowUtc - passwordChangedUtc.Value).TotalDays);
        }
    }
}
