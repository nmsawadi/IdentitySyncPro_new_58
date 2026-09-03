using IdentitySyncPro.Web.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace IdentitySyncPro.Web.Filters
{
    /// <summary>
    /// Keeps the employee portal and the console apart, in both directions.
    ///
    /// <b>This is the second of two independent barriers, not the only one.</b> A portal principal
    /// is issued with no role claim, so every <c>[Authorize(Roles = ...)]</c> screen already turns
    /// it away without knowing the portal exists. What barrier one cannot see are the console
    /// screens that ask only for an authenticated user — <see cref="AccessRequestsController"/>
    /// among them, deliberately, because approving is not a console role. Those are what this
    /// closes.
    ///
    /// Registered globally so a controller added next year is covered without anybody remembering
    /// to think about it.
    ///
    /// The reverse direction is a correctness guard rather than a security one: a console user
    /// reaching the portal would file requests whose subject is their console username, which is
    /// frequently not a directory account at all. Those requests would be raised for an account
    /// that does not exist and fail at the first membership check.
    /// </summary>
    public class PortalGuardFilter : IActionFilter
    {
        public void OnActionExecuting(ActionExecutingContext context)
        {
            var user = context.HttpContext.User;
            if (user?.Identity?.IsAuthenticated != true) return;

            var controller = context.RouteData.Values["controller"]?.ToString();
            var onPortal = string.Equals(controller, "Portal", StringComparison.OrdinalIgnoreCase);
            var isPortalUser = user.HasClaim(PortalController.PortalClaim, "1");

            if (isPortalUser && !onPortal)
            {
                // Sent back to the portal rather than to Access Denied: from where they stand this
                // is not a refusal, it is a page that was never theirs to be on.
                context.Result = new RedirectToActionResult(nameof(PortalController.Index), "Portal", null);
                return;
            }

            if (!isPortalUser && onPortal)
            {
                // Sign-out is exempt on both sides — a person must always be able to leave, and
                // bouncing them away from the button would be a small trap of its own.
                var action = context.RouteData.Values["action"]?.ToString();
                if (string.Equals(action, nameof(PortalController.Logout), StringComparison.OrdinalIgnoreCase))
                    return;

                context.Result = new RedirectToActionResult("Index", "Dashboard", null);
            }
        }

        public void OnActionExecuted(ActionExecutedContext context) { }
    }
}
