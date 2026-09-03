using Hangfire.Dashboard;

namespace IdentitySyncPro.Web.Filters
{
    /// <summary>
    /// Authorization filter for the Hangfire Dashboard.
    /// 
    /// Development: Allows all requests (for local debugging).
    /// Production:  Requires HangfireApiKey via query string (?key=xxx) or cookie.
    /// 
    /// Usage in Program.cs:
    ///   app.UseHangfireDashboard("/hangfire", new DashboardOptions
    ///   {
    ///       Authorization = new[] { new HangfireDashboardAuthFilter(configuration, env) }
    ///   });
    /// </summary>
    public class HangfireDashboardAuthFilter : IDashboardAuthorizationFilter
    {
        private const string CookieName = "HangfireAuth";
        private const string QueryParamName = "key";

        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _environment;

        public HangfireDashboardAuthFilter(IConfiguration configuration, IWebHostEnvironment environment)
        {
            _configuration = configuration;
            _environment = environment;
        }

        public bool Authorize(DashboardContext context)
        {
            var httpContext = context.GetHttpContext();

            // ⛔ An employee-portal principal is never an operator, in any environment.
            //
            // Checked before the development bypass, and deliberately. In production the Admin-role
            // test below already refuses a portal principal, because it is issued without any role
            // claim — but the bypass is unconditional, and the portal changed what that costs:
            // before it existed, reaching this host meant being on the console, and the only people
            // who could authenticate were the handful with console accounts. Now every identity in
            // the directory can sign in, and a lab left running in Development would hand each of
            // them the job dashboard.
            //
            // Found by signing into the portal as an employee and simply requesting /hangfire. The
            // MVC filter that confines a portal principal cannot cover this: the dashboard is
            // middleware, so no action filter ever runs for it.
            if (httpContext.User?.HasClaim(Controllers.PortalController.PortalClaim, "1") == true)
                return false;

            // Development: allow all
            if (_environment.IsDevelopment())
                return true;

            // ✅ Primary path: a console user signed in with the Admin role
            if (httpContext.User?.Identity?.IsAuthenticated == true &&
                httpContext.User.IsInRole("Admin"))
                return true;

            // Legacy fallback: configured API key (constant-time comparison).
            // A placeholder key counts as unconfigured — accepting "CHANGE-THIS-HANGFIRE-KEY"
            // would hand the job dashboard to anyone who has read the repository. Blocking it
            // locks nobody out: the Admin-cookie path above is the intended way in.
            var configuredKey = _configuration["ApiSecurity:HangfireApiKey"];
            if (IdentitySyncPro.Core.Helpers.ApiKeyGuard.IsPlaceholderOrMissing(configuredKey))
                return false; // Block if neither Admin login nor a real key

            var queryKey = httpContext.Request.Query[QueryParamName].FirstOrDefault();
            if (!string.IsNullOrEmpty(queryKey) && FixedTimeEquals(queryKey, configuredKey!))
            {
                httpContext.Response.Cookies.Append(CookieName, configuredKey!, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Strict,
                    MaxAge = TimeSpan.FromHours(8)
                });
                return true;
            }

            var cookieKey = httpContext.Request.Cookies[CookieName];
            return !string.IsNullOrEmpty(cookieKey) && FixedTimeEquals(cookieKey, configuredKey!);
        }

        private static bool FixedTimeEquals(string a, string b) =>
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(a),
                System.Text.Encoding.UTF8.GetBytes(b));
    }
}
