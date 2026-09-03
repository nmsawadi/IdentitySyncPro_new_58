namespace IdentitySyncPro.Core.Models.Settings
{
    /// <summary>
    /// Institution-wide multi-factor policy — a single row, edited from the Users screen.
    ///
    /// Deliberately in the database rather than appsettings: turning MFA on or off is an
    /// operational decision an administrator makes at runtime, and the requirement is that it be
    /// switchable without a redeploy. It also gives an unambiguous break-glass path — one UPDATE
    /// statement re-opens the console if every enrolled device is lost at once (see RUNBOOK).
    /// </summary>
    public class MfaSettings
    {
        public int Id { get; set; }

        /// <summary>
        /// Master switch. Off by default so an upgrade never locks anyone out of a running
        /// system — enabling it is an explicit act, taken when someone is ready to enroll.
        /// </summary>
        public bool IsEnabled { get; set; }

        /// <summary>
        /// Comma-separated roles that must use MFA. Defaults to Admin, matching the requirement
        /// ("highly privileged or highly sensitive accounts") rather than forcing it on viewers.
        /// </summary>
        public string RequiredRoles { get; set; } = AppUserRoles.Admin;

        /// <summary>
        /// When true a user in scope who has not enrolled is walked through setup at sign-in.
        /// When false they are allowed through until they enroll voluntarily — a grace mode for
        /// rolling MFA out to an existing team without locking everyone out on the same morning.
        /// </summary>
        public bool EnforceEnrollment { get; set; } = true;

        public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Whether a user holding this role must complete MFA.</summary>
        public bool AppliesToRole(string? role)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(role)) return false;

            return (RequiredRoles ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
        }
    }
}
