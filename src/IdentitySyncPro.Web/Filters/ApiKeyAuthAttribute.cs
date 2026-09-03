using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace IdentitySyncPro.Web.Filters
{
    /// <summary>
    /// Action filter that validates API Key authentication for API endpoints.
    /// Checks for the key in the X-API-Key header or api_key query parameter.
    /// 
    /// Register as ServiceFilter in DI, then apply:
    ///   [ServiceFilter(typeof(ApiKeyAuthAttribute))]
    /// </summary>
    public class ApiKeyAuthAttribute : IActionFilter
    {
        private const string ApiKeyHeaderName = "X-API-Key";
        private const string ApiKeyQueryParam = "api_key";

        private readonly IConfiguration _configuration;
        private readonly ILogger<ApiKeyAuthAttribute> _logger;

        public ApiKeyAuthAttribute(IConfiguration configuration, ILogger<ApiKeyAuthAttribute> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public void OnActionExecuting(ActionExecutingContext context)
        {
            var configuredKey = _configuration["ApiSecurity:ApiKey"];

            // A key left at its shipped placeholder ("CHANGE-THIS-API-KEY") is a published
            // secret, so it counts as unconfigured — otherwise the endpoint looks protected
            // while accepting a value anyone can read in the source tree.
            if (IdentitySyncPro.Core.Helpers.ApiKeyGuard.IsPlaceholderOrMissing(configuredKey))
            {
                _logger.LogWarning("⚠️ ApiSecurity:ApiKey is not configured (missing or still the shipped placeholder). API access is blocked.");
                context.Result = new JsonResult(new
                {
                    schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:Error" },
                    detail = "API authentication is not configured. Contact administrator.",
                    status = 500
                })
                { StatusCode = 500 };
                return;
            }

            // Check header first, then query parameter
            string? providedKey = null;

            if (context.HttpContext.Request.Headers.TryGetValue(ApiKeyHeaderName, out var headerValue))
            {
                providedKey = headerValue.FirstOrDefault();
            }

            if (string.IsNullOrEmpty(providedKey))
            {
                providedKey = context.HttpContext.Request.Query[ApiKeyQueryParam].FirstOrDefault();
            }

            if (string.IsNullOrEmpty(providedKey))
            {
                _logger.LogWarning("🔒 API request rejected — no API key provided. IP: {IP}, Path: {Path}",
                    context.HttpContext.Connection.RemoteIpAddress,
                    context.HttpContext.Request.Path);

                context.Result = new JsonResult(new
                {
                    schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:Error" },
                    detail = "API key is required. Provide it via X-API-Key header or api_key query parameter.",
                    status = 401
                })
                { StatusCode = 401 };
                return;
            }

            // Constant-time comparison to prevent timing attacks
            // (configuredKey is non-null here — the guard above returns on missing/placeholder)
            if (!CryptographicEquals(providedKey, configuredKey!))
            {
                _logger.LogWarning("🔒 API request rejected — invalid API key. IP: {IP}, Path: {Path}",
                    context.HttpContext.Connection.RemoteIpAddress,
                    context.HttpContext.Request.Path);

                context.Result = new JsonResult(new
                {
                    schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:Error" },
                    detail = "Invalid API key.",
                    status = 401
                })
                { StatusCode = 401 };
                return;
            }
        }

        public void OnActionExecuted(ActionExecutedContext context) { }

        /// <summary>
        /// Constant-time string comparison to prevent timing side-channel attacks.
        /// </summary>
        private static bool CryptographicEquals(string a, string b)
        {
            var aBytes = System.Text.Encoding.UTF8.GetBytes(a);
            var bBytes = System.Text.Encoding.UTF8.GetBytes(b);
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
        }
    }
}
