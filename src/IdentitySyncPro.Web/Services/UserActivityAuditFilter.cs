using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc.Filters;

namespace IdentitySyncPro.Web.Services
{
    /// <summary>
    /// Records every state-changing request in the console — whoever made it, from whichever screen.
    ///
    /// Why a filter rather than fifty call sites: modules audit their own work in detail (sync
    /// operations, service runs, account status changes), but only where somebody remembered to add
    /// a line, and a screen added later inherits nothing. Registered globally, this covers every
    /// controller that exists and every one added afterwards, so "which screens are covered" stops
    /// being a question anyone has to keep answering.
    ///
    /// It does not replace the detailed logs. This answers "who did what, from where, and when";
    /// the module logs answer "exactly what changed". The coarse one is the only one that can be
    /// guaranteed complete.
    /// </summary>
    public class UserActivityAuditFilter : IAsyncActionFilter
    {
        /// <summary>
        /// Endpoints not recorded here. Kept deliberately short — anything absent from this set IS
        /// logged, so coverage is the default and an omission has to be argued for.
        /// </summary>
        private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        {
            "Home/SetLanguage",   // a UI preference, several per session, changes no data
            "Account/Login",      // AuthService already audits success, failure and lockout
            "Account/Logout"
        };

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var request = context.HttpContext.Request;
            var routes = context.ActionDescriptor.RouteValues;
            var controller = routes.TryGetValue("controller", out var c) && c != null ? c : "?";
            var action = routes.TryGetValue("action", out var a) && a != null ? a : "?";
            var key = $"{controller}/{action}";

            var stateChanging = !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method);
            if (!stateChanging || Skipped.Contains(key))
            {
                await next();
                return;
            }

            var arguments = new Dictionary<string, object?>(context.ActionArguments);

            var executed = await next();

            // Audited AFTER the action, so a request that threw is recorded as failed rather than
            // as something that happened. Writing it first would log attempts as accomplishments.
            var audit = context.HttpContext.RequestServices.GetService<IAuditService>();
            if (audit == null) return;

            var error = executed.Exception?.Message;

            await audit.LogAsync(
                action: key,
                category: "UserAction",
                severity: error != null ? AuditSeverity.Error : AuditSeverity.Info,
                entityType: controller,
                details: AuditArgumentRedactor.Describe(arguments, error));
        }
    }
}
