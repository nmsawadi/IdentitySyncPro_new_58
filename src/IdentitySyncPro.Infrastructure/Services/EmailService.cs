using System.Net;
using System.Net.Mail;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Email notification service for sending offboarding alerts to administrators.
    /// Reads the active SMTP transport from the Notifications Center (EmailProviders table),
    /// falling back to the legacy appsettings "EmailSettings" section when none is configured.
    /// </summary>
    public interface IEmailService
    {
        /// <summary>Send using the active provider (Notifications Center) or appsettings fallback.</summary>
        Task<EmailResult> SendAsync(EmailMessage message);

        /// <summary>Send using a specific provider — used by the "Test" button before activating it.</summary>
        Task<EmailResult> SendViaAsync(Core.Models.Settings.EmailProvider provider, EmailMessage message);
    }

    public class EmailService : IEmailService
    {
        private readonly IConfiguration _configuration;
        private readonly AppDbContext _db;
        private readonly ILogger<EmailService> _logger;

        public EmailService(IConfiguration configuration, AppDbContext db, ILogger<EmailService> logger)
        {
            _configuration = configuration;
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// How long a send may take before it is abandoned.
        ///
        /// It has to be bounded by something the caller controls, because the obvious bound does
        /// not work: <c>SmtpClient.Timeout</c> is ignored by <c>SendMailAsync</c>.
        /// </summary>
        private const int SendTimeoutSeconds = 30;

        public async Task<EmailResult> SendAsync(EmailMessage message)
        {
            // Prefer the active provider from the Notifications Center; else legacy appsettings.
            var provider = await _db.EmailProviders.FirstOrDefaultAsync(e => e.IsActive);
            if (provider != null)
                return await SendViaAsync(provider, message);

            var smtp = _configuration.GetSection("EmailSettings");
            var username = smtp["SmtpUsername"];
            return await SendCoreAsync(
                host: smtp["SmtpHost"],
                port: int.TryParse(smtp["SmtpPort"], out var p) ? p : 587,
                authenticated: !string.IsNullOrWhiteSpace(username),
                username: username,
                password: smtp["SmtpPassword"],
                fromEmail: smtp["FromEmail"] ?? username,
                fromName: smtp["FromName"] ?? "IdentitySyncPro",
                enableSsl: smtp["EnableSsl"]?.ToLower() != "false",
                message: message);
        }

        public Task<EmailResult> SendViaAsync(Core.Models.Settings.EmailProvider provider, EmailMessage message)
        {
            var authenticated = provider.Mode == "Authenticated";
            return SendCoreAsync(
                host: provider.SmtpHost,
                port: provider.SmtpPort,
                authenticated: authenticated,
                username: authenticated ? provider.Username : null,
                password: authenticated ? provider.Password : null,
                fromEmail: provider.FromEmail ?? provider.Username,
                fromName: provider.FromName ?? "IdentitySyncPro",
                enableSsl: provider.EnableSsl,
                message: message);
        }

        private async Task<EmailResult> SendCoreAsync(
            string? host, int port, bool authenticated, string? username, string? password,
            string? fromEmail, string? fromName, bool enableSsl, EmailMessage message)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(host))
                    return new EmailResult { Success = false, Error = "No active email provider configured (Notifications Center → Email)" };
                if (string.IsNullOrWhiteSpace(fromEmail))
                    return new EmailResult { Success = false, Error = "From address is not configured" };

                using var client = new SmtpClient(host, port)
                {
                    EnableSsl = enableSsl,
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    // Honoured by the synchronous Send only — see the cancellation token below,
                    // which is what actually bounds the asynchronous path used here.
                    Timeout = SendTimeoutSeconds * 1000
                };

                if (authenticated && !string.IsNullOrWhiteSpace(username))
                {
                    client.Credentials = new NetworkCredential(username, password);
                }

                using var mailMessage = new MailMessage
                {
                    From = new MailAddress(fromEmail, fromName ?? "IdentitySyncPro"),
                    Subject = message.Subject,
                    Body = message.Body,
                    IsBodyHtml = message.IsHtml
                };

                // Support multiple recipients separated by ; or ,
                var recipients = message.To.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var to in recipients)
                {
                    mailMessage.To.Add(to.Trim());
                }

                // ⛔ SmtpClient.Timeout does NOT apply to SendMailAsync. Setting it reads as a
                // thirty-second bound and provides none: the asynchronous path waits on the socket
                // for as long as the network will let it, which against an unreachable mail server
                // is forever.
                //
                // That cost the whole background pipeline once. The notification was moved onto a
                // Hangfire worker so an unreachable mail server could not hang the request page —
                // and three of them then sat in Processing indefinitely, consuming both workers.
                // Every sweep stopped: campaigns never closed, revocations never ran, requests
                // never expired. Nothing failed; everything simply stopped, and the job list looked
                // busy the entire time.
                //
                // Moving the wait was never the fix. Bounding it is.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(SendTimeoutSeconds));
                await client.SendMailAsync(mailMessage, timeout.Token);

                _logger.LogInformation("Email sent to {To}: {Subject}", message.To, message.Subject);
                return new EmailResult { Success = true };
            }
            catch (OperationCanceledException)
            {
                _logger.LogError("Email to {To} timed out after {Seconds}s — the mail server did not answer",
                    message.To, SendTimeoutSeconds);
                return new EmailResult
                {
                    Success = false,
                    Error = $"لم يستجب خادم البريد خلال {SendTimeoutSeconds} ثانية / "
                          + $"The mail server did not answer within {SendTimeoutSeconds}s."
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email to {To}", message.To);
                // SmtpException's own message is a generic "Failure sending mail." —
                // the real cause (unknown host, connection refused, auth rejected) is in the inner exceptions.
                var error = ex.Message;
                for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                    error += $" → {inner.Message}";
                return new EmailResult { Success = false, Error = error };
            }
        }
    }

    public class EmailMessage
    {
        public string To { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public bool IsHtml { get; set; } = true;
    }

    public class EmailResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
    }
}
