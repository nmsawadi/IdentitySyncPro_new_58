using IdentitySyncPro.Infrastructure.Services;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Guards the schedule builder — the monthly mode and the custom expression.
    ///
    /// The bug this replaces was silent in the worst way: the dropdown offered "custom", no field
    /// carried an expression to it, and <c>customCron ?? "0 2 * * *"</c> turned every such service
    /// into a nightly one. Nothing threw, nothing logged, and the only trace was a read-only cron
    /// box on a screen nobody rereads after choosing "custom".
    /// </summary>
    public class CronScheduleTests
    {
        // ══════════════════════════════════════
        // MONTHLY
        // ══════════════════════════════════════

        [Theory]
        [InlineData("02:00", 1, "0 2 1 * *")]
        [InlineData("23:30", 15, "30 23 15 * *")]
        [InlineData("06:05", 28, "5 6 28 * *")]
        public void Monthly_PutsTheDayInTheDayOfMonthField(string time, int day, string expected)
        {
            Assert.Equal(expected, CronBuilder.Build("monthly", time, null, null, null, day));
        }

        /// <summary>
        /// Cron has no "last day", so day 31 does not fire in February, April, June, September or
        /// November. A schedule that skips five months a year while the screen shows it as
        /// configured is exactly the failure this codebase refuses to ship, so the day is clamped
        /// into a range where every month has one.
        /// </summary>
        [Theory]
        [InlineData(29)]
        [InlineData(31)]
        [InlineData(99)]
        public void Monthly_NeverProducesADayThatSomeMonthsLack(int day)
        {
            var field = CronBuilder.Build("monthly", "02:00", null, null, null, day).Split(' ')[2];
            Assert.True(int.Parse(field) <= CronBuilder.MaxDayOfMonth);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void Monthly_NeverProducesADayBelowOne(int day)
        {
            var field = CronBuilder.Build("monthly", "02:00", null, null, null, day).Split(' ')[2];
            Assert.True(int.Parse(field) >= CronBuilder.MinDayOfMonth);
        }

        [Fact]
        public void Monthly_WithoutADay_DefaultsToTheFirst()
        {
            Assert.Equal("0 2 1 * *", CronBuilder.Build("monthly", "02:00", null, null));
        }

        /// <summary>A monthly schedule must read as one on the services list, not as a raw expression.</summary>
        [Fact]
        public void Monthly_IsDescribedInWords()
        {
            Assert.Equal("Monthly on day 15 at 02:30", CronBuilder.Describe("30 2 15 * *", isArabic: false));
            Assert.Contains("15", CronBuilder.Describe("30 2 15 * *", isArabic: true));
        }

        /// <summary>The other modes must still describe as they did — a monthly branch placed too early would swallow them.</summary>
        [Theory]
        [InlineData("0 2 * * *", "Daily at 02:00")]
        [InlineData("0 2 * * 5", "Weekly (Fri) at 02:00")]
        [InlineData("*/30 * * * *", "Every 30 minutes")]
        [InlineData("0 */6 * * *", "Every 6 hours")]
        public void OtherModes_KeepTheirDescriptions(string cron, string expected)
        {
            Assert.Equal(expected, CronBuilder.Describe(cron, isArabic: false));
        }

        // ══════════════════════════════════════
        // CUSTOM — THE SILENT FALLBACK
        // ══════════════════════════════════════

        [Fact]
        public void Custom_UsesTheExpressionItWasGiven()
        {
            Assert.Equal("0 3 1 */3 *", CronBuilder.Build("custom", null, null, null, "0 3 1 */3 *"));
        }

        [Fact]
        public void Custom_IsTrimmed()
        {
            Assert.Equal("0 2 1 * *", CronBuilder.Build("custom", null, null, null, "  0 2 1 * *  "));
        }

        /// <summary>
        /// The heart of it: an unusable custom expression must refuse, not quietly become daily.
        /// A schedule that is not the one you chose has to fail while you are still looking at it.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("0 2 * *")]            // four fields
        [InlineData("0 2 * * * *")]        // six fields
        [InlineData("every night")]        // not an expression at all
        [InlineData("0 25 * * *")]         // hour out of range
        [InlineData("70 2 * * *")]         // minute out of range
        [InlineData("0 2 0 * *")]          // day-of-month starts at 1
        [InlineData("0 2 * 13 *")]         // month out of range
        [InlineData("0 2 * * 9")]          // weekday out of range
        [InlineData("0 2 * * $")]          // stray character
        public void Custom_RefusesAnythingUnusable(string? expression)
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => CronBuilder.Build("custom", null, null, null, expression));
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }

        /// <summary>The refusal must never be the daily default wearing a different name.</summary>
        [Fact]
        public void Custom_NeverSilentlyReturnsTheDailyDefault()
        {
            foreach (var bad in new string?[] { null, "", "0 2 * *", "every night" })
                Assert.Throws<InvalidOperationException>(
                    () => CronBuilder.Build("custom", "02:00", null, null, bad));
        }

        [Theory]
        [InlineData("0 2 1 * *")]
        [InlineData("*/15 * * * *")]
        [InlineData("0 0 1,15 * *")]
        [InlineData("0 2 * * 1-5")]
        [InlineData("0 2 1 */3 *")]
        [InlineData("0 2 ? * MON")]        // names are handed on to Hangfire's own parser
        [InlineData("0 2 L * *")]          // as are markers this check does not model
        public void Validate_AcceptsRealExpressions(string expression)
        {
            Assert.Null(CronBuilder.Validate(expression));
        }

        /// <summary>The message has to name the problem — "invalid cron" tells an operator nothing they can fix.</summary>
        [Fact]
        public void Validate_SaysWhatIsWrong()
        {
            Assert.Contains("5 fields", CronBuilder.Validate("0 2 * *"));
            Assert.Contains("hour", CronBuilder.Validate("0 25 * * *"));
            Assert.Contains("day of month", CronBuilder.Validate("0 2 0 * *"));
        }

        // ══════════════════════════════════════
        // THE MODES THAT ALREADY WORKED
        // ══════════════════════════════════════

        [Theory]
        [InlineData("daily", "02:00", null, null, "0 2 * * *")]
        [InlineData("weekly", "03:15", "1,3,5", null, "15 3 * * 1,3,5")]
        [InlineData("interval", null, null, 30, "*/30 * * * *")]
        [InlineData("interval", null, null, 120, "0 */2 * * *")]
        [InlineData("nonsense", null, null, null, "0 2 * * *")]
        public void ExistingModes_AreUnchanged(string mode, string? time, string? days, int? interval, string expected)
        {
            Assert.Equal(expected, CronBuilder.Build(mode, time, days, interval));
        }
    }
}
