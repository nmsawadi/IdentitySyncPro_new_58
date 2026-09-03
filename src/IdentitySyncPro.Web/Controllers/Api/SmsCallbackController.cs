using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Web.Controllers.Api
{
    /// <summary>
    /// API Controller for receiving SMS delivery callbacks from the SMS gateway.
    /// Endpoint: POST /api/sms/callback
    /// </summary>
    [ApiController]
    [Route("api/sms")]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous] // external gateway callback — no console cookie
    [IgnoreAntiforgeryToken]
    public class SmsCallbackController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ILogger<SmsCallbackController> _logger;

        public SmsCallbackController(AppDbContext db, ILogger<SmsCallbackController> logger)
        {
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// Receives delivery status callbacks from the SMS gateway.
        /// Called by the SMS provider when a message is delivered, failed, or rejected.
        /// </summary>
        /// <remarks>
        /// Expected payload:
        /// {
        ///   "messageId": "MSG-2026-0514-00001",
        ///   "mobileNumber": "966501234567",
        ///   "status": "delivered",
        ///   "deliveredAt": "2026-05-14T16:05:12Z",
        ///   "errorCode": null
        /// }
        /// </remarks>
        [HttpPost("callback")]
        public async Task<IActionResult> DeliveryCallback([FromBody] SmsDeliveryCallback callback)
        {
            if (callback == null || string.IsNullOrWhiteSpace(callback.MessageId))
            {
                return BadRequest(new { success = false, error = "Invalid callback payload" });
            }

            _logger.LogInformation(
                "SMS Delivery Callback: MessageId={MessageId}, Status={Status}, Phone={Phone}",
                callback.MessageId, callback.Status, MaskPhone(callback.MobileNumber));

            // Log the delivery status
            try
            {
                _db.AuditEntries.Add(new Core.Models.Audit.AuditEntry
                {
                    Category = "SMS",
                    Action = $"Delivery {callback.Status}: {callback.MessageId}",
                    EntityType = "SMS",
                    EntityId = callback.MessageId,
                    Severity = callback.Status == "delivered"
                        ? Core.Enums.AuditSeverity.Info
                        : Core.Enums.AuditSeverity.Warning,
                    Details = $"Phone: {MaskPhone(callback.MobileNumber)}, " +
                              $"Status: {callback.Status}, " +
                              $"ErrorCode: {callback.ErrorCode ?? "none"}",
                    PerformedBy = "SMS-Gateway",
                    Timestamp = DateTime.UtcNow
                });

                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log SMS delivery callback for {MessageId}", callback.MessageId);
            }

            return Ok(new
            {
                success = true,
                messageId = callback.MessageId,
                received = DateTime.UtcNow
            });
        }

        /// <summary>
        /// Health check endpoint for SMS callback verification.
        /// SMS providers may ping this to verify the callback URL is active.
        /// </summary>
        [HttpGet("callback/health")]
        public IActionResult HealthCheck()
        {
            return Ok(new
            {
                status = "active",
                service = "IdentitySyncPro SMS Callback",
                timestamp = DateTime.UtcNow
            });
        }

        private static string MaskPhone(string phone) =>
            string.IsNullOrEmpty(phone) ? "****" :
            phone.Length > 4 ? new string('*', phone.Length - 4) + phone[^4..] : "****";
    }

    /// <summary>SMS gateway delivery callback payload.</summary>
    public class SmsDeliveryCallback
    {
        public string MessageId { get; set; } = string.Empty;
        public string MobileNumber { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty; // delivered, failed, rejected
        public DateTime? DeliveredAt { get; set; }
        public string? ErrorCode { get; set; }
    }
}
