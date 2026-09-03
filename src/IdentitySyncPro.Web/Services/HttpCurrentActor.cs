using IdentitySyncPro.Core.Interfaces;

namespace IdentitySyncPro.Web.Services
{
    /// <summary>
    /// Resolves the acting user from the current request.
    ///
    /// Everything comes from the authentication cookie, never from anything the client sends. That
    /// distinction is the whole point: the account status screen used to store an operator name
    /// typed into a text box, so the record of who disabled an account was whatever the browser
    /// chose to submit.
    ///
    /// Outside a request — Hangfire jobs, the scheduler, startup — there is no HttpContext and both
    /// members are null, which the audit service records as "System".
    /// </summary>
    public class HttpCurrentActor : ICurrentActor
    {
        private readonly IHttpContextAccessor _accessor;

        public HttpCurrentActor(IHttpContextAccessor accessor) => _accessor = accessor;

        public string? Username
        {
            get
            {
                var identity = _accessor.HttpContext?.User?.Identity;
                return identity is { IsAuthenticated: true } && !string.IsNullOrWhiteSpace(identity.Name)
                    ? identity.Name
                    : null;
            }
        }

        /// <summary>
        /// Honours the forwarded-header configuration already applied by the pipeline, so a
        /// deployment behind a proxy records the real client rather than the proxy — the same
        /// concern the SSPR IP blocking documents.
        /// </summary>
        public string? IpAddress => _accessor.HttpContext?.Connection?.RemoteIpAddress?.ToString();
    }
}
