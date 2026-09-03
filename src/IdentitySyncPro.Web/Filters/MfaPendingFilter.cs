using IdentitySyncPro.Web.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace IdentitySyncPro.Web.Filters
{
    /// <summary>
    /// Confines a principal that passed the password but not the second factor to the MFA
    /// screens and sign-out.
    ///
    /// This is the second of two independent barriers, not the only one: the pending principal
    /// is issued without any role claim, so every <c>[Authorize(Roles = ...)]</c> screen already
    /// rejects it. The filter closes the remainder — pages that require only an authenticated
    /// user — and is registered globally so a controller added later is covered without anyone
    /// remembering to.
    ///
    /// Ordered before <see cref="MustChangePasswordFilter"/>: proving who you are comes before
    /// being asked to rotate a password.
    /// </summary>
    public class MfaPendingFilter : IActionFilter
    {
        /// <summary>Actions on AccountController a half-authenticated user may still reach.</summary>
        private static readonly HashSet<string> AllowedAccountActions = new(StringComparer.OrdinalIgnoreCase)
        {
            nameof(AccountController.Mfa),
            nameof(AccountController.MfaSetup),
            nameof(AccountController.MfaRecoveryCodes),
            nameof(AccountController.MfaRecoveryCodesAck),
            nameof(AccountController.Logout),
            nameof(AccountController.Login),
            nameof(AccountController.AccessDenied)
        };

        public void OnActionExecuting(ActionExecutingContext context)
        {
            var user = context.HttpContext.User;
            if (user?.Identity?.IsAuthenticated != true) return;
            if (!user.HasClaim(AccountController.MfaPendingClaim, "1")) return;

            var controller = context.RouteData.Values["controller"]?.ToString();
            var action = context.RouteData.Values["action"]?.ToString();

            if (string.Equals(controller, "Account", StringComparison.OrdinalIgnoreCase) &&
                action != null && AllowedAccountActions.Contains(action))
            {
                return;
            }

            context.Result = new RedirectToActionResult(nameof(AccountController.Mfa), "Account", null);
        }

        public void OnActionExecuted(ActionExecutedContext context) { }
    }
}
