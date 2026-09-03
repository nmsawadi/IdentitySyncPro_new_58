using System.Text;
using System.Text.Json;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Builds and sends an HTTP request to ANY SMS gateway based on a provider's generic
    /// configuration (method + body format + template + headers + success rule). This is the
    /// single place that turns provider config into an actual HTTP call, so every caller —
    /// sync, SSPR, retry, services, account status, and the test button — behaves identically.
    /// </summary>
    public static class SmsGateway
    {
        /// <summary>Everything needed to shape one gateway request. Mirrors the SmsProvider fields.</summary>
        public sealed class Config
        {
            public string ApiUrl { get; init; } = "";
            public string HttpMethod { get; init; } = "POST";
            public string BodyFormat { get; init; } = "Json";
            public string? RequestTemplate { get; init; }
            public string? HeadersJson { get; init; }
            public string? SuccessBodyContains { get; init; }
            public string ApiUsername { get; init; } = "";
            public string ApiPassword { get; init; } = "";
            public string? ApiKey { get; init; }
            public string SenderName { get; init; } = "";
        }

        public sealed record Result(bool Success, string ResponseBody, string? Error);

        public static async Task<Result> SendAsync(HttpClient client, Config cfg, string recipient, string message, CancellationToken ct = default)
        {
            var request = BuildRequest(cfg, recipient, message);

            var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            // Success = HTTP 2xx AND (no success-token configured OR the token is present).
            var tokenOk = string.IsNullOrEmpty(cfg.SuccessBodyContains)
                          || body.Contains(cfg.SuccessBodyContains, StringComparison.OrdinalIgnoreCase);

            if (response.IsSuccessStatusCode && tokenOk)
                return new Result(true, body, null);

            var error = !response.IsSuccessStatusCode
                ? $"API returned {(int)response.StatusCode}: {body}"
                : $"Success token '{cfg.SuccessBodyContains}' not found in response: {body}";
            return new Result(false, body, error);
        }

        /// <summary>Build the HttpRequestMessage. Public so the caller/test can preview it if needed.</summary>
        public static HttpRequestMessage BuildRequest(Config cfg, string recipient, string message)
        {
            var method = string.Equals(cfg.HttpMethod?.Trim(), "GET", StringComparison.OrdinalIgnoreCase)
                ? System.Net.Http.HttpMethod.Get
                : System.Net.Http.HttpMethod.Post;

            var url = cfg.ApiUrl;
            HttpRequestMessage req;

            // Backward compatible: no template → the original fixed JSON payload.
            if (string.IsNullOrWhiteSpace(cfg.RequestTemplate))
            {
                var payload = JsonSerializer.Serialize(new
                {
                    userName = cfg.ApiUsername,
                    password = cfg.ApiPassword,
                    senderName = cfg.SenderName,
                    mobileNumber = recipient,
                    message
                });
                req = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, url)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
            }
            else
            {
                var format = (cfg.BodyFormat ?? "Json").Trim();
                if (format.Equals("Query", StringComparison.OrdinalIgnoreCase))
                {
                    var qs = Substitute(cfg.RequestTemplate!, cfg, recipient, message, Uri.EscapeDataString);
                    url = url.Contains('?') ? $"{url}&{qs}" : $"{url}?{qs}";
                    req = new HttpRequestMessage(method, url);
                }
                else if (format.Equals("Form", StringComparison.OrdinalIgnoreCase))
                {
                    var formBody = Substitute(cfg.RequestTemplate!, cfg, recipient, message, Uri.EscapeDataString);
                    req = new HttpRequestMessage(method, url)
                    {
                        Content = new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded")
                    };
                }
                else // Json
                {
                    var jsonBody = Substitute(cfg.RequestTemplate!, cfg, recipient, message, JsonEscape);
                    req = new HttpRequestMessage(method, url)
                    {
                        Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
                    };
                }
            }

            ApplyHeaders(req, cfg, recipient, message);
            return req;
        }

        private static void ApplyHeaders(HttpRequestMessage req, Config cfg, string recipient, string message)
        {
            if (string.IsNullOrWhiteSpace(cfg.HeadersJson)) return;
            try
            {
                var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(cfg.HeadersJson!);
                if (headers == null) return;
                foreach (var (key, rawVal) in headers)
                {
                    var val = Substitute(rawVal, cfg, recipient, message, s => s); // headers: no encoding
                    // Content-Type must be set on the content, not the request headers.
                    if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && req.Content != null)
                        req.Content.Headers.TryAddWithoutValidation("Content-Type", val);
                    else
                        req.Headers.TryAddWithoutValidation(key, val);
                }
            }
            catch { /* malformed headers JSON → ignore, don't break the send */ }
        }

        private static string Substitute(string template, Config cfg, string recipient, string message, Func<string, string> enc) =>
            template
                // {message_ucs2} is substituted BEFORE {message}. The two do not actually collide
                // (a literal "{message}" needs the closing brace straight after "message"), but
                // ordering it first keeps that from becoming a subtle trap if names ever change.
                .Replace("{message_ucs2}", enc(ToUnicodeHex(message ?? "")))
                .Replace("{username}", enc(cfg.ApiUsername ?? ""))
                .Replace("{password}", enc(cfg.ApiPassword ?? ""))
                .Replace("{apikey}", enc(cfg.ApiKey ?? ""))
                .Replace("{sender}", enc(cfg.SenderName ?? ""))
                .Replace("{recipient}", enc(recipient ?? ""))
                .Replace("{message}", enc(message ?? ""));

        /// <summary>
        /// Encodes text as a UCS-2 hex string: every UTF-16 code unit becomes 4 big-endian hex
        /// digits ('A' → "0041", 'م' → "0645"). Required by gateways that flag Unicode with a
        /// message type (RML Connect's type=2, among others) — for those, URL-encoded UTF-8 is
        /// NOT interchangeable and Arabic arrives corrupted.
        ///
        /// The output is pure ASCII [0-9A-F], so the caller's URL/JSON encoding leaves it
        /// untouched — there is no double-encoding hazard.
        /// </summary>
        private static string ToUnicodeHex(string message)
        {
            var sb = new StringBuilder(message.Length * 4);
            foreach (var ch in message)
                sb.Append(((int)ch).ToString("X4"));
            return sb.ToString();
        }

        /// <summary>Escape a value for embedding inside a JSON string literal (no surrounding quotes).</summary>
        private static string JsonEscape(string value)
        {
            var s = JsonSerializer.Serialize(value); // returns a quoted, fully-escaped string
            return s.Length >= 2 ? s[1..^1] : s;      // strip the surrounding quotes
        }
    }
}
