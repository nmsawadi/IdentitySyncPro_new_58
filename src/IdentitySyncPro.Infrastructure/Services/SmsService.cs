using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IdentitySyncPro.Core.Helpers;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// SMS notification service for sending credentials to new identities.
    /// Sends username + password via SMS API after account creation.
    /// </summary>
    public interface ISmsService
    {
        Task<SmsResult> SendCredentialsAsync(SmsRequest request);
    }

    public class SmsService : ISmsService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<SmsService> _logger;

        public SmsService(IHttpClientFactory httpClientFactory, ILogger<SmsService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public async Task<SmsResult> SendCredentialsAsync(SmsRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.PhoneNumber))
                {
                    return new SmsResult { Success = false, Error = "No phone number provided" };
                }

                if (string.IsNullOrWhiteSpace(request.ApiUrl))
                {
                    return new SmsResult { Success = false, Error = "SMS API URL not configured" };
                }

                // Build the message from template
                var message = RenderMessage(request);

                var client = _httpClientFactory.CreateClient("SmsClient");
                client.Timeout = TimeSpan.FromSeconds(30);

                // Generic gateway engine: shapes the request from the provider's config
                // (method/format/template/headers/success rule). Empty template = legacy JSON.
                var result = await SmsGateway.SendAsync(client, request.ToGatewayConfig(),
                    NormalizePhoneNumber(request.PhoneNumber), message);

                if (result.Success)
                {
                    _logger.LogInformation("SMS sent successfully to {Phone} for identity {IdentityId}",
                        MaskPhone(request.PhoneNumber), request.IdentityId);
                    return new SmsResult { Success = true, Response = result.ResponseBody };
                }

                _logger.LogWarning("SMS send failed for identity {IdentityId}: {Error}",
                    request.IdentityId, result.Error);
                return new SmsResult { Success = false, Error = result.Error };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send SMS to identity {IdentityId}", request.IdentityId);
                return new SmsResult { Success = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// Renders the final message text from a request's template + tokens. Exposed so callers can
        /// capture the exact text that was sent (e.g. to store it for a later retry). Passing an
        /// already-rendered message back through is a no-op (no tokens left to replace).
        /// </summary>
        public static string RenderMessage(SmsRequest request) =>
            (request.MessageTemplate ?? string.Empty)
                .Replace("{USERNAME}", request.Username ?? "")
                .Replace("{PASSWORD}", request.Password ?? "")
                .Replace("{DISPLAY_NAME}", request.DisplayName ?? "")
                .Replace("{IDENTITY_ID}", request.IdentityId ?? "");

        private static string NormalizePhoneNumber(string phone) => PhoneHelper.NormalizePhone(phone);

        private static string MaskPhone(string phone) => PhoneHelper.MaskPhone(phone);
    }

    public class SmsRequest
    {
        // --- Provider connection (generic gateway config) ---
        public string ApiUrl { get; set; } = string.Empty;
        public string ApiUsername { get; set; } = string.Empty;
        public string ApiPassword { get; set; } = string.Empty;
        public string? ApiKey { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public string HttpMethod { get; set; } = "POST";
        public string BodyFormat { get; set; } = "Json";
        public string? RequestTemplate { get; set; }
        public string? HeadersJson { get; set; }
        public string? SuccessBodyContains { get; set; }

        // --- Message data ---
        public string PhoneNumber { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string IdentityId { get; set; } = string.Empty;
        public string MessageTemplate { get; set; } = string.Empty;

        /// <summary>Copy a stored SmsProvider's connection fields onto a request.</summary>
        public SmsRequest WithProvider(Core.Models.Settings.SmsProvider p)
        {
            ApiUrl = p.ApiUrl;
            ApiUsername = p.ApiUsername;
            ApiPassword = p.ApiPassword;
            ApiKey = p.ApiKey;
            SenderName = p.SenderName;
            HttpMethod = p.HttpMethod;
            BodyFormat = p.BodyFormat;
            RequestTemplate = p.RequestTemplate;
            HeadersJson = p.HeadersJson;
            SuccessBodyContains = p.SuccessBodyContains;
            return this;
        }

        public SmsGateway.Config ToGatewayConfig() => new()
        {
            ApiUrl = ApiUrl,
            HttpMethod = HttpMethod,
            BodyFormat = BodyFormat,
            RequestTemplate = RequestTemplate,
            HeadersJson = HeadersJson,
            SuccessBodyContains = SuccessBodyContains,
            ApiUsername = ApiUsername,
            ApiPassword = ApiPassword,
            ApiKey = ApiKey,
            SenderName = SenderName
        };
    }

    public class SmsResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string? Response { get; set; }
    }
}
