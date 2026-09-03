using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace IdentitySyncPro.Web.Filters
{
    /// <summary>
    /// Users flagged with a pending forced password change (pwd_change claim)
    /// are redirected to the change-password page from anywhere else in the app.
    /// </summary>
    public class MustChangePasswordFilter : IActionFilter
    {
        public void OnActionExecuting(ActionExecutingContext context)
        {
            var user = context.HttpContext.User;
            if (user?.Identity?.IsAuthenticated != true) return;
            if (!user.HasClaim("pwd_change", "1")) return;

            var controller = context.RouteData.Values["controller"]?.ToString();
            if (string.Equals(controller, "Account", StringComparison.OrdinalIgnoreCase)) return;

            context.Result = new RedirectToActionResult("ChangePassword", "Account", null);
        }

        public void OnActionExecuted(ActionExecutedContext context) { }
    }
}
