namespace IdentitySyncPro.Core.Models.Settings
{
    /// <summary>
    /// A configurable SMS gateway. Rather than hardcoding one vendor's request shape, the
    /// provider describes ANY HTTP SMS API generically: method, body format, a request
    /// template with standard placeholders, optional headers/auth, and a success rule.
    /// This makes the notification center compatible with any provider by configuration.
    ///
    /// Supported placeholders (in <see cref="RequestTemplate"/>, <see cref="HeadersJson"/> and the URL):
    ///   {username} {password} {apikey} {sender} {recipient} {message}
    /// </summary>
    public class SmsProvider
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string ApiUrl { get; set; } = string.Empty;

        // --- Credentials (secrets encrypted at rest) ---
        public string ApiUsername { get; set; } = string.Empty;
        public string ApiPassword { get; set; } = string.Empty;
        /// <summary>API key / bearer token for token-based gateways (Taqnyat, Unifonic, 4Jawaly...).</summary>
        public string? ApiKey { get; set; }

        public string SenderName { get; set; } = string.Empty;

        // --- Generic HTTP request shape ---
        /// <summary>"POST" (default) or "GET".</summary>
        public string HttpMethod { get; set; } = "POST";

        /// <summary>
        /// How the request payload is encoded:
        ///   "Json"  → JSON body (application/json)
        ///   "Form"  → application/x-www-form-urlencoded body
        ///   "Query" → appended to the URL as a query string (typical for GET gateways)
        /// </summary>
        public string BodyFormat { get; set; } = "Json";

        /// <summary>
        /// The body (or query string) template with placeholders. When empty, a legacy default
        /// JSON payload is sent — {"userName","password","senderName","mobileNumber","message"} —
        /// so existing providers keep working unchanged.
        /// </summary>
        public string? RequestTemplate { get; set; }

        /// <summary>Optional extra HTTP headers as a JSON object, e.g. {"Authorization":"Bearer {apikey}"}. Placeholders allowed.</summary>
        public string? HeadersJson { get; set; }

        /// <summary>
        /// Success rule: when set, the send counts as successful only if the HTTP status is 2xx
        /// AND the response body contains this text (e.g. "1701", "\"success\":true", "\"code\":\"1\"").
        /// When empty, HTTP 2xx alone means success.
        /// </summary>
        public string? SuccessBodyContains { get; set; }

        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string? Notes { get; set; }
    }
}
