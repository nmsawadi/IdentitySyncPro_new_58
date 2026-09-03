namespace IdentitySyncPro.Core.Interfaces
{
    /// <summary>
    /// Who is performing the operation being audited.
    ///
    /// Exists so the audit trail can name the actor without every caller having to remember to pass
    /// it — fifty-two call sites wrote audit entries and not one supplied a name, so every entry
    /// read "System" regardless of who acted.
    ///
    /// Infrastructure has no ASP.NET dependency, hence the abstraction rather than
    /// <c>IHttpContextAccessor</c> directly. Outside a request — Hangfire jobs, schedulers, startup
    /// — both members are null, and "System" is then the truthful answer rather than a fallback.
    /// </summary>
    public interface ICurrentActor
    {
        /// <summary>Signed-in username, or null when no person is driving the operation.</summary>
        string? Username { get; }

        /// <summary>Client address of the current request, or null outside one.</summary>
        string? IpAddress { get; }
    }
}
