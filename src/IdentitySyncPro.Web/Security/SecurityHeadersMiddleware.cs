namespace IdentitySyncPro.Web.Security
{
    /// <summary>
    /// Adds the standard browser-side hardening headers to every response.
    ///
    /// The Content-Security-Policy here is deliberately **permissive about inline code**:
    /// the UI carries inline &lt;script&gt; blocks and ~100 inline event handlers
    /// (onclick/onchange/...), plus inline style attributes throughout. A nonce-based or
    /// strict-dynamic policy would blank the entire application. What this policy still buys —
    /// and what an auditor checks for — is the part that costs nothing here:
    /// frame-ancestors (clickjacking), object-src none (plugin injection), base-uri and
    /// form-action (base-tag hijacking and form exfiltration), and a closed default-src.
    ///
    /// Everything is switchable from configuration rather than code, because the CDN allowlist
    /// is an environment fact (an air-gapped install self-hosts the libraries) and because an
    /// operator who hits a blocked resource in production needs a one-line fix, not a redeploy.
    /// </summary>
    public class SecurityHeadersMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly string _csp;
        private readonly bool _reportOnly;
        private readonly string _permissionsPolicy;

        public SecurityHeadersMiddleware(RequestDelegate next, IConfiguration configuration)
        {
            _next = next;

            var section = configuration.GetSection("Security");
            _reportOnly = section.GetValue<bool?>("ContentSecurityPolicyReportOnly") ?? false;

            // Space-separated extra origins, e.g. an internal mirror of the CDN.
            var extra = (section.GetValue<string?>("CspExtraHosts") ?? string.Empty).Trim();
            var extraSuffix = string.IsNullOrEmpty(extra) ? string.Empty : " " + extra;

            // Every front-end library (Bootstrap, Bootstrap Icons, Chart.js, SignalR) and both
            // web fonts are served from wwwroot/lib, so no external origin is allowed at all.
            // Re-introducing a CDN reference in a view will now be blocked by the browser rather
            // than quietly re-creating the outbound-internet dependency this policy exists to
            // prevent. `CspExtraHosts` remains for an installation that deliberately adds one.
            _csp = string.Join("; ", new[]
            {
                "default-src 'self'",
                // 'unsafe-inline' is required by the existing views — see the class remarks.
                $"script-src 'self' 'unsafe-inline'{extraSuffix}",
                $"style-src 'self' 'unsafe-inline'{extraSuffix}",
                $"font-src 'self' data:{extraSuffix}",
                "img-src 'self' data: blob:",
                // 'self' covers the SignalR hub: since CSP3, a same-origin ws://ws s:// connection
                // matches 'self', so no scheme entry is needed. The bare `ws: wss:` that used to
                // be here did NOT mean "same-origin either way" — a bare scheme allows **any
                // host** on that scheme, so the policy permitted a socket to wss://attacker.tld.
                // Flagged as a wildcard directive by ZAP on 2026-08-10.
                "connect-src 'self'",
                "frame-ancestors 'self'",
                "base-uri 'self'",
                "form-action 'self'",
                "object-src 'none'"
            });

            // Mirrors the "disable unused features (camera/microphone)" line in the requirements.
            _permissionsPolicy = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Headers must be attached before the response starts — once a byte is written
            // (static files, SignalR upgrade) the collection is read-only and appending throws.
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;

                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "SAMEORIGIN";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Permissions-Policy"] = _permissionsPolicy;

                var cspHeader = _reportOnly
                    ? "Content-Security-Policy-Report-Only"
                    : "Content-Security-Policy";

                // Never overwrite a policy a downstream component already set.
                if (!headers.ContainsKey("Content-Security-Policy") &&
                    !headers.ContainsKey("Content-Security-Policy-Report-Only"))
                {
                    headers[cspHeader] = _csp;
                }

                return Task.CompletedTask;
            });

            await _next(context);
        }
    }

    public static class SecurityHeadersMiddlewareExtensions
    {
        /// <summary>
        /// Registers the hardening headers. Honours <c>Security:EnableSecurityHeaders</c>
        /// (default true) so a deployment that hits an unforeseen conflict can disable it
        /// without redeploying binaries.
        /// </summary>
        public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, IConfiguration configuration)
        {
            var enabled = configuration.GetSection("Security").GetValue<bool?>("EnableSecurityHeaders") ?? true;
            return enabled ? app.UseMiddleware<SecurityHeadersMiddleware>() : app;
        }
    }
}
