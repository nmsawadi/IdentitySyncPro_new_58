using IdentitySyncPro.Infrastructure.Services;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// A full sync was set to run every 300 minutes and did not run for a day. Everything looked
    /// right: the interval was stored as 300, the cron as "0 */5 * * *", and the settings page
    /// reported the save succeeded. What was missing was EnableAutoSync, which the scheduler also
    /// requires — so no recurring job was ever registered, and nothing anywhere said so.
    ///
    /// These pin the two halves of that: the interval-to-cron conversion, which was correct all
    /// along and must stay correct, and the boundary at 60 minutes where the expression changes
    /// shape.
    /// </summary>
    public class ScheduleRegistrationTests
    {
        [Theory]
        // Whole hours become an hourly step. 300 minutes is the case from production.
        [InlineData(300, "0 */5 * * *")]
        [InlineData(60, "0 */1 * * *")]
        [InlineData(120, "0 */2 * * *")]
        [InlineData(720, "0 */12 * * *")]
        // Below an hour, and anything not a whole number of hours, stays a minute step.
        [InlineData(30, "*/30 * * * *")]
        [InlineData(15, "*/15 * * * *")]
        [InlineData(45, "*/45 * * * *")]
        public void AnIntervalBecomesTheExpectedCron(int minutes, string expected)
        {
            Assert.Equal(expected, CronBuilder.Build("interval", null, null, minutes));
        }

        [Fact]
        public void FiveHoursMeansTheHourBoundaries_NotFiveHoursFromNow()
        {
            // The reading that causes "it did not run": an interval schedule fires on the clock,
            // so enabling it at 15:30 means nothing happens until 20:00, not until 20:30.
            Assert.Equal("0 */5 * * *", CronBuilder.Build("interval", null, null, 300));
        }

        [Theory]
        [InlineData("0 */5 * * *", "كل 5 ساعة")]
        [InlineData("*/30 * * * *", "كل 30 دقيقة")]
        public void TheDescriptionMatchesWhatWasBuilt(string cron, string expectedArabic)
        {
            // The settings page shows this text. It described the schedule correctly the whole time
            // the schedule was doing nothing, which is why the description alone proves nothing
            // about whether a job is registered.
            Assert.Equal(expectedArabic, CronBuilder.Describe(cron, isArabic: true));
        }

        [Fact]
        public void AnUnknownModeFallsBackToADailyExpression_NotToAnInvalidOne()
        {
            // A bad mode must not produce something Hangfire rejects at registration time, which
            // would fail silently in exactly the same way.
            Assert.Equal("0 2 * * *", CronBuilder.Build("nonsense", null, null, null));
        }
    }
}
