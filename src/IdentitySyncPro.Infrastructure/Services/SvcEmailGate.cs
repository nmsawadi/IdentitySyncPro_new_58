using IdentitySyncPro.Core.Models.Services;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// The one question every AD service asks before sending its notification email:
    /// is there actually anything to report?
    ///
    /// Why it exists: five of the six services spelled the rule out inline as
    /// <c>count &gt; 0 &amp;&amp; EnableEmailNotification &amp;&amp; recipient != ""</c>, and the sixth
    /// — the AD audit reports — checked only the last two. So a PasswordNeverExpires run that
    /// found nothing still mailed an empty table on every schedule. Six copies of one rule is
    /// precisely how the sixth came to be missing a third of it, so the rule lives here now and
    /// each service states its count.
    ///
    /// The reason for NOT sending is always logged. "No email arrived" otherwise looks identical
    /// whether notifications are off, the recipient is blank, or the run simply had nothing to
    /// say — and those three are fixed in three different places.
    /// </summary>
    public static class SvcEmailGate
    {
        /// <param name="findingCount">
        /// How many things this run has to report. Zero means the email would carry an empty
        /// table, so none is sent.
        /// </param>
        /// <param name="module">Log prefix of the calling service, e.g. <c>SvcAdAudit</c>.</param>
        public static bool ShouldSend(SvcService service, int findingCount, ILogger logger, string module)
        {
            if (!service.EnableEmailNotification)
            {
                logger.LogInformation(
                    "{Module}['{Service}']: email notification is turned off for this service — no email sent.",
                    module, service.Name);
                return false;
            }

            if (string.IsNullOrWhiteSpace(service.NotificationEmail))
            {
                // A warning, not information: notifications were switched on deliberately and are
                // silently going nowhere.
                logger.LogWarning(
                    "{Module}['{Service}']: email notification is ON but no recipient address is configured — no email sent.",
                    module, service.Name);
                return false;
            }

            if (findingCount <= 0)
            {
                logger.LogInformation(
                    "{Module}['{Service}']: nothing to report this run — no email sent. The run itself is recorded in the service audit log.",
                    module, service.Name);
                return false;
            }

            return true;
        }
    }
}
