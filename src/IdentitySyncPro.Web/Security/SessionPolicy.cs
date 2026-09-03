namespace IdentitySyncPro.Web.Security
{
    /// <summary>
    /// The effective idle-session window, resolved once at startup and injected into the layout.
    ///
    /// The view needs it so the keep-alive ping period is derived from the real setting instead
    /// of a second copy of the number in JavaScript — two copies drift, and the failure mode is
    /// users being signed out mid-form with no visible cause.
    /// </summary>
    /// <param name="IdleTimeoutMinutes">Sliding inactivity window applied to the auth cookie.</param>
    public sealed record SessionPolicy(int IdleTimeoutMinutes);
}
