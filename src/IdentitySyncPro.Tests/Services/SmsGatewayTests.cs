using System.Text.Json;
using IdentitySyncPro.Infrastructure.Services;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Verifies the generic SMS gateway engine shapes requests correctly for any provider:
    /// legacy fallback, JSON/Form/Query formats, placeholder substitution + encoding, and headers.
    /// </summary>
    public class SmsGatewayTests
    {
        private static string ReadBody(HttpRequestMessage req) =>
            req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();

        [Fact]
        public void EmptyTemplate_FallsBackToLegacyJsonPayload()
        {
            var cfg = new SmsGateway.Config
            {
                ApiUrl = "https://gw/send",
                ApiUsername = "u", ApiPassword = "p", SenderName = "Org"
            };

            var req = SmsGateway.BuildRequest(cfg, "966500000000", "hi");

            Assert.Equal(HttpMethod.Post, req.Method);
            var body = ReadBody(req);
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("u", doc.RootElement.GetProperty("userName").GetString());
            Assert.Equal("966500000000", doc.RootElement.GetProperty("mobileNumber").GetString());
            Assert.Equal("hi", doc.RootElement.GetProperty("message").GetString());
        }

        [Fact]
        public void JsonTemplate_EscapesQuotesSoBodyStaysValidJson()
        {
            var cfg = new SmsGateway.Config
            {
                ApiUrl = "https://gw/send",
                BodyFormat = "Json",
                RequestTemplate = "{\"numbers\":\"{recipient}\",\"msg\":\"{message}\"}",
                ApiUsername = "u"
            };

            // Message contains a double quote and newline — must be escaped, not break the JSON.
            var req = SmsGateway.BuildRequest(cfg, "966500000000", "say \"hi\"\nnow");
            var body = ReadBody(req);

            using var doc = JsonDocument.Parse(body); // throws if escaping is wrong
            Assert.Equal("say \"hi\"\nnow", doc.RootElement.GetProperty("msg").GetString());
        }

        [Fact]
        public void QueryFormat_AppendsUrlEncodedQueryAndUsesGet()
        {
            var cfg = new SmsGateway.Config
            {
                ApiUrl = "https://gw/send",
                HttpMethod = "GET",
                BodyFormat = "Query",
                RequestTemplate = "numbers={recipient}&msg={message}&sender={sender}",
                SenderName = "My Org"
            };

            var req = SmsGateway.BuildRequest(cfg, "966500000000", "a b&c");

            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Null(req.Content);
            // Assert the escaped wire form (ToString() unescapes for display).
            var query = req.RequestUri!.GetComponents(UriComponents.Query, UriFormat.UriEscaped);
            Assert.Contains("numbers=966500000000", query);
            Assert.Contains("msg=a%20b%26c", query);   // space + ampersand encoded (not split into params)
            Assert.Contains("sender=My%20Org", query);
        }

        [Fact]
        public void FormFormat_UsesUrlEncodedBody()
        {
            var cfg = new SmsGateway.Config
            {
                ApiUrl = "https://gw/send",
                BodyFormat = "Form",
                RequestTemplate = "Recipient={recipient}&Body={message}"
            };

            var req = SmsGateway.BuildRequest(cfg, "966500000000", "x&y");
            Assert.Equal("application/x-www-form-urlencoded", req.Content!.Headers.ContentType!.MediaType);
            Assert.Contains("Body=x%26y", ReadBody(req));
        }

        [Fact]
        public void Headers_ApplyWithPlaceholderSubstitution()
        {
            var cfg = new SmsGateway.Config
            {
                ApiUrl = "https://gw/send",
                BodyFormat = "Json",
                RequestTemplate = "{\"body\":\"{message}\"}",
                HeadersJson = "{\"Authorization\":\"Bearer {apikey}\"}",
                ApiKey = "secret-token"
            };

            var req = SmsGateway.BuildRequest(cfg, "966500000000", "hi");
            Assert.True(req.Headers.TryGetValues("Authorization", out var vals));
            Assert.Equal("Bearer secret-token", vals!.Single());
        }

        // ═══════════════════════════════════════
        // UCS-2 — gateways that flag Unicode with a message type (RML type=2)
        // ═══════════════════════════════════════

        [Fact]
        public void MessageUcs2_EncodesEachCodeUnitAsFourHexDigits()
        {
            var cfg = new SmsGateway.Config
            {
                ApiUrl = "https://gw/send", HttpMethod = "GET", BodyFormat = "Query",
                RequestTemplate = "message={message_ucs2}"
            };

            // 'A' = U+0041, 'ب' = U+0628 — the gateway wants them concatenated, not URL-escaped.
            var req = SmsGateway.BuildRequest(cfg, "966500000000", "Aب");
            Assert.Equal("https://gw/send?message=00410628", req.RequestUri!.ToString());
        }

        [Fact]
        public void MessageUcs2_OutputIsAsciiSoEncodingLeavesItIntact()
        {
            // The hex form is pure [0-9A-F]; URL-encoding must not alter it (no double encoding).
            var cfg = new SmsGateway.Config
            {
                ApiUrl = "https://gw/send", HttpMethod = "GET", BodyFormat = "Query",
                RequestTemplate = "m={message_ucs2}"
            };

            var arabic = "مرحبا";
            var req = SmsGateway.BuildRequest(cfg, "966500000000", arabic);
            var sent = req.RequestUri!.Query.Split("m=")[1];

            Assert.Equal(arabic.Length * 4, sent.Length);
            Assert.DoesNotContain("%", sent);
            Assert.All(sent, c => Assert.True(Uri.IsHexDigit(c), $"'{c}' is not a hex digit"));
        }

        [Fact]
        public void MessageUcs2_AndPlainMessage_DoNotCollide()
        {
            // Guards the substitution order: {message} must not eat the prefix of {message_ucs2}.
            var cfg = new SmsGateway.Config
            {
                ApiUrl = "https://gw/send", BodyFormat = "Form",
                RequestTemplate = "plain={message}&hex={message_ucs2}"
            };

            var body = ReadBody(SmsGateway.BuildRequest(cfg, "966500000000", "AB"));
            Assert.Equal("plain=AB&hex=00410042", body);
        }

        [Fact]
        public void RmlConnectPreset_ProducesTheExpectedRequestLine()
        {
            // Mirrors the rmlConnect preset in wwwroot/js/sms-presets.js end to end.
            var cfg = new SmsGateway.Config
            {
                ApiUrl = "https://ksa-api.rmlconnect.net/bulksms/bulksms",
                HttpMethod = "GET", BodyFormat = "Query",
                RequestTemplate = "username={username}&password={password}&type=2&dlr=1" +
                                  "&destination={recipient}&source={sender}&message={message_ucs2}",
                ApiUsername = "user1", ApiPassword = "pass1", SenderName = "MyOrg"
            };

            var req = SmsGateway.BuildRequest(cfg, "966500000000", "Aب");

            Assert.Equal(System.Net.Http.HttpMethod.Get, req.Method);
            Assert.Equal(
                "https://ksa-api.rmlconnect.net/bulksms/bulksms?username=user1&password=pass1" +
                "&type=2&dlr=1&destination=966500000000&source=MyOrg&message=00410628",
                req.RequestUri!.ToString());
        }
    }
}
