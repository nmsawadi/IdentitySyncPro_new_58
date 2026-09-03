using IdentitySyncPro.Core.Models.Audit;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// How a run says who started it.
    ///
    /// Before this, <c>TriggeredBy</c> was the string "Manual" on every run — the Hangfire job
    /// passed that literal too, so the column neither distinguished a scheduled run from a
    /// human one nor named the human. The convention now is: a person's run stores their
    /// username, everything else stores a token.
    ///
    /// The display rule carries the weight. Anything unrecognised must come back unchanged,
    /// because "unrecognised" is precisely the case that holds a username — mapping it to some
    /// label would hide the one thing the column exists to show.
    /// </summary>
    public class ActorNamesTests
    {
        [Theory]
        [InlineData(ActorNames.Schedule, "الجدولة", "Schedule")]
        [InlineData(ActorNames.System, "النظام", "System")]
        [InlineData(ActorNames.LifecycleEngine, "محرك دورة الحياة", "Lifecycle engine")]
        [InlineData(ActorNames.BulkPipeline, "معالجة جماعية", "Bulk pipeline")]
        public void AutomatedOriginsGetALabelInBothLanguages(string token, string arabic, string english)
        {
            Assert.Equal(arabic, ActorNames.Describe(token, isArabic: true));
            Assert.Equal(english, ActorNames.Describe(token, isArabic: false));
        }

        /// <summary>
        /// The case the whole change exists for: a username must survive display untouched.
        /// </summary>
        [Theory]
        [InlineData("nasser")]
        [InlineData("CORP\\nasser")]
        [InlineData("nasser@corp.local")]
        [InlineData("admin")]
        public void AUsernameIsShownExactlyAsStored(string username)
        {
            Assert.Equal(username, ActorNames.Describe(username, isArabic: true));
            Assert.Equal(username, ActorNames.Describe(username, isArabic: false));
            Assert.True(ActorNames.IsUser(username));
        }

        [Theory]
        [InlineData(ActorNames.Schedule)]
        [InlineData(ActorNames.System)]
        [InlineData(ActorNames.LifecycleEngine)]
        [InlineData(ActorNames.BulkPipeline)]
        [InlineData(ActorNames.LegacyManual)]
        [InlineData(null)]
        [InlineData("")]
        public void AutomatedAndUnknownOriginsAreNotPeople(string? stored)
        {
            Assert.False(ActorNames.IsUser(stored));
        }

        /// <summary>
        /// Rows written before the username was recorded must not read as though somebody had
        /// been identified — "Manual" alone invites exactly that reading.
        /// </summary>
        [Fact]
        public void HistoricRowsSayThatTheUserWasNotRecorded()
        {
            var arabic = ActorNames.Describe(ActorNames.LegacyManual, isArabic: true);
            var english = ActorNames.Describe(ActorNames.LegacyManual, isArabic: false);

            Assert.Contains("غير مسجَّل", arabic);
            Assert.Contains("not recorded", english);
        }

        [Fact]
        public void AnEmptyValueIsReportedAsUnknownRatherThanBlank()
        {
            Assert.Equal("غير معروف", ActorNames.Describe(null, isArabic: true));
            Assert.Equal("Unknown", ActorNames.Describe("   ", isArabic: false));
        }

        // ── Job payloads ──

        [Fact]
        public void AJobPayloadWithNoActorIsTreatedAsScheduled()
        {
            // Only the recurring registrations can be old enough to lack the argument; a manual
            // run always carries the username that enqueued it.
            Assert.Equal(ActorNames.Schedule, ActorNames.OrSchedule(null));
            Assert.Equal(ActorNames.Schedule, ActorNames.OrSchedule("  "));
        }

        [Fact]
        public void AJobPayloadCarryingAUsernameKeepsIt()
        {
            Assert.Equal("nasser", ActorNames.OrSchedule("nasser"));
        }

        // ── Column width ──

        /// <summary>
        /// TriggeredBy is written when a run ENDS. One character too many throws on SaveChanges
        /// and takes the run's final status with it, so a long name must be truncated rather than
        /// allowed to reach the database.
        /// </summary>
        [Fact]
        public void AnOverlongNameIsTruncatedRatherThanLosingTheRun()
        {
            var clamped = ActorNames.Clamp(new string('x', 5000));

            Assert.Equal(ActorNames.MaxLength, clamped.Length);
        }

        [Fact]
        public void AnOrdinaryNameIsUnchangedByClamping()
        {
            // Control: clamping must not mangle the normal case.
            Assert.Equal("nasser", ActorNames.Clamp("nasser"));
            Assert.Equal("nasser", ActorNames.Clamp("  nasser  "));
        }

        [Fact]
        public void ClampingAnAbsentNameYieldsSystem()
        {
            // An unauthenticated or background caller is System, not an empty string.
            Assert.Equal(ActorNames.System, ActorNames.Clamp(null));
            Assert.Equal(ActorNames.System, ActorNames.Clamp("   "));
        }
    }
}
