using IdentitySyncPro.Core.Models.Services;
using IdentitySyncPro.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// The rule that decides whether a service mails its summary.
    ///
    /// The production symptom this exists for: the AD audit service running the
    /// PasswordNeverExpires report found no accounts and mailed an empty table anyway, on every
    /// scheduled run. An empty report trains people to ignore the mail, so the run that finally
    /// does find a privileged-group change goes unread.
    ///
    /// The invariant is one line — no findings, no email — but it has to hold for every service,
    /// which is why they all route through this gate instead of restating it.
    /// </summary>
    public class SvcEmailGateTests
    {
        private static SvcService Service(bool enabled = true, string? recipient = "admin@example.com") => new()
        {
            Id = 3,
            Name = "PasswordNeverExpires report",
            EnableEmailNotification = enabled,
            NotificationEmail = recipient
        };

        private static ILogger Logger() => Mock.Of<ILogger>();

        [Fact]
        public void ARunThatFoundNothingSendsNoEmail()
        {
            // The reported bug, stated directly.
            Assert.False(SvcEmailGate.ShouldSend(Service(), 0, Logger(), "SvcAdAudit"));
        }

        [Fact]
        public void ARunWithFindingsSendsTheEmail()
        {
            // The control: the gate must not have closed the door on everything.
            Assert.True(SvcEmailGate.ShouldSend(Service(), 1, Logger(), "SvcAdAudit"));
            Assert.True(SvcEmailGate.ShouldSend(Service(), 4988, Logger(), "SvcAdAudit"));
        }

        /// <summary>
        /// A negative count is not reachable today, but "&gt; 0" and "!= 0" differ only on this
        /// input — and the one that is wrong sends mail about nothing.
        /// </summary>
        [Fact]
        public void ANegativeCountIsTreatedAsNothingToReport()
        {
            Assert.False(SvcEmailGate.ShouldSend(Service(), -1, Logger(), "SvcAdAudit"));
        }

        [Fact]
        public void NotificationsTurnedOffSendNothingEvenWithFindings()
        {
            Assert.False(SvcEmailGate.ShouldSend(Service(enabled: false), 25, Logger(), "SvcOrphan"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void NoRecipientSendsNothingEvenWithFindings(string? recipient)
        {
            // Whitespace counts as blank: an address field containing a space would otherwise
            // reach the mail server as a recipient and fail there instead of here.
            Assert.False(SvcEmailGate.ShouldSend(Service(recipient: recipient), 25, Logger(), "SvcExpiry"));
        }

        /// <summary>
        /// Each of the three reasons for not sending is fixed somewhere different — the service
        /// form, the address field, or nowhere at all — so the log has to name which one applied.
        /// A silent skip is what makes "no email arrived" unanswerable.
        /// </summary>
        [Theory]
        [InlineData(true, "admin@example.com", 0, LogLevel.Information, "nothing to report")]
        [InlineData(false, "admin@example.com", 25, LogLevel.Information, "turned off")]
        [InlineData(true, null, 25, LogLevel.Warning, "no recipient address")]
        public void EverySkipSaysWhyInTheLog(
            bool enabled, string? recipient, int count, LogLevel expectedLevel, string expectedPhrase)
        {
            var logger = new Mock<ILogger>();
            logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            string? message = null;
            LogLevel? level = null;

            logger.Setup(l => l.Log(
                    It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
                .Callback(new InvocationAction(inv =>
                {
                    level = (LogLevel)inv.Arguments[0];
                    message = inv.Arguments[2]?.ToString();
                }));

            var sent = SvcEmailGate.ShouldSend(
                Service(enabled, recipient), count, logger.Object, "SvcAdAudit");

            Assert.False(sent);
            Assert.Equal(expectedLevel, level);
            Assert.Contains(expectedPhrase, message);
        }

        [Fact]
        public void ASuccessfulSendIsNotLoggedAsASkip()
        {
            var logger = new Mock<ILogger>();
            logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            bool logged = false;

            logger.Setup(l => l.Log(
                    It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
                .Callback(new InvocationAction(_ => logged = true));

            Assert.True(SvcEmailGate.ShouldSend(Service(), 7, logger.Object, "SvcAdAudit"));
            Assert.False(logged);
        }
    }
}
