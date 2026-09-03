using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Infrastructure.Services;
using IdentitySyncPro.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Who the audit trail says acted, and what it is allowed to write down.
    ///
    /// Both halves were broken in the same way: the trail existed but said nothing useful.
    /// <c>PerformedBy</c> defaulted to "System" and not one of the fifty-two call sites passed a
    /// name, so every entry claimed the system did it. And the account status screen took the
    /// operator's name from a text box on the page — a record of who disabled an account that the
    /// browser could set to anything.
    ///
    /// The redaction half is security-critical in the other direction: this decides whether a
    /// password reaches the audit table, where it would sit in plain text and be exported to Excel
    /// by anyone with the audit screen.
    /// </summary>
    public class AuditActorAndRedactionTests
    {
        private sealed class Actor : ICurrentActor
        {
            public string? Username { get; init; }
            public string? IpAddress { get; init; }
        }

        private static AuditService Service(AppDbContextHolder holder, ICurrentActor? actor) =>
            new(holder.Db, Mock.Of<ILogger<AuditService>>(), actor);

        private sealed class AppDbContextHolder : IDisposable
        {
            public Infrastructure.Data.AppDbContext Db { get; } = TestDbContext.Create();
            public void Dispose() => Db.Dispose();
        }

        // ── Who acted ──

        [Fact]
        public async Task TheSignedInUserIsRecordedWithoutTheCallerPassingIt()
        {
            // The whole point: 52 call sites write audit entries and none names the actor.
            using var h = new AppDbContextHolder();
            var svc = Service(h, new Actor { Username = "nasser", IpAddress = "10.1.2.3" });

            await svc.LogAsync("Sync started", "Sync");

            var entry = h.Db.AuditEntries.Single();
            Assert.Equal("nasser", entry.PerformedBy);
            Assert.Equal("10.1.2.3", entry.IpAddress);
        }

        [Fact]
        public async Task BackgroundWorkIsRecordedAsSystem()
        {
            // A Hangfire job has no HttpContext, so "System" here is the truth, not a fallback.
            using var h = new AppDbContextHolder();
            var svc = Service(h, new Actor { Username = null, IpAddress = null });

            await svc.LogAsync("Full sync completed", "Sync");

            Assert.Equal(AuditService.SystemActor, h.Db.AuditEntries.Single().PerformedBy);
        }

        [Fact]
        public async Task AnExplicitNameStillWins()
        {
            // A few callers name a subject other than the signed-in user; that must not be lost.
            using var h = new AppDbContextHolder();
            var svc = Service(h, new Actor { Username = "nasser" });

            await svc.LogAsync("Password reset", "Security", performedBy: "sspr-portal");

            Assert.Equal("sspr-portal", h.Db.AuditEntries.Single().PerformedBy);
        }

        [Fact]
        public async Task AnActorWithNoNameIsNotRecordedAsAnEmptyString()
        {
            // A blank name reads as a missing record rather than as background work.
            using var h = new AppDbContextHolder();
            var svc = Service(h, new Actor { Username = "   " });

            await svc.LogAsync("Something", "System");

            Assert.Equal(AuditService.SystemActor, h.Db.AuditEntries.Single().PerformedBy);
        }

        [Fact]
        public async Task EntriesCanBeFilteredByActorAndTheCountAgrees()
        {
            using var h = new AppDbContextHolder();
            await Service(h, new Actor { Username = "nasser" }).LogAsync("a", "UserAction");
            await Service(h, new Actor { Username = "nasser" }).LogAsync("b", "UserAction");
            await Service(h, new Actor { Username = "someone-else" }).LogAsync("c", "UserAction");

            var svc = Service(h, null);
            var rows = await svc.GetEntriesAsync(performedBy: "nasser");
            var count = await svc.GetEntryCountAsync(performedBy: "nasser");

            // The list and the count must agree — they used to build separate filter chains.
            Assert.Equal(2, rows.Count());
            Assert.Equal(2, count);
        }

        // ── What may be written ──

        private sealed class SaveDomainRequest
        {
            public string Name { get; set; } = "corp.local";
            public string AdServer { get; set; } = "dc01.corp.local";
            public string? AdPassword { get; set; } = "SuperSecret123!";
            public string? ApiKey { get; set; } = "tok_live_abcdef";
            public int AdPort { get; set; } = 389;
        }

        [Fact]
        public void SecretsNeverReachTheAuditTable()
        {
            var summary = AuditArgumentRedactor.Describe(new Dictionary<string, object?>
            {
                ["model"] = new SaveDomainRequest(),
                ["newPassword"] = "AnotherSecret1",
                ["testPassword"] = "Third",
                ["otp"] = "483920"
            });

            Assert.DoesNotContain("SuperSecret123!", summary);
            Assert.DoesNotContain("tok_live_abcdef", summary);
            Assert.DoesNotContain("AnotherSecret1", summary);
            Assert.DoesNotContain("Third", summary);
            Assert.DoesNotContain("483920", summary);
        }

        [Fact]
        public void TheNonSecretContextIsStillRecorded()
        {
            // Control: redaction that swallowed everything would make the log useless.
            var summary = AuditArgumentRedactor.Describe(new Dictionary<string, object?>
            {
                ["model"] = new SaveDomainRequest(),
                ["id"] = 7
            });

            Assert.Contains("corp.local", summary);
            Assert.Contains("dc01.corp.local", summary);
            Assert.Contains("389", summary);
            Assert.Contains("id=7", summary);
            Assert.Contains(AuditArgumentRedactor.Redacted, summary);
        }

        [Theory]
        [InlineData("password")]
        [InlineData("Password")]
        [InlineData("adPassword")]
        [InlineData("NewPassword")]
        [InlineData("pwd")]
        [InlineData("ApiKey")]
        [InlineData("api_key")]
        [InlineData("clientSecret")]
        [InlineData("accessToken")]
        [InlineData("otp")]
        [InlineData("passwordHash")]
        [InlineData("credentials")]
        public void EveryNamingStyleForASecretIsCaught(string name)
        {
            // Substring matching is what makes this hold for names nobody has invented yet.
            Assert.True(AuditArgumentRedactor.IsSecret(name), $"'{name}' should be treated as secret");
        }

        [Theory]
        [InlineData("username")]
        [InlineData("samAccountName")]
        [InlineData("adServer")]
        [InlineData("reason")]
        [InlineData("id")]
        public void OrdinaryFieldsAreNotRedacted(string name)
        {
            Assert.False(AuditArgumentRedactor.IsSecret(name), $"'{name}' should not be treated as secret");
        }

        [Fact]
        public void AFailedRequestIsRecordedAsFailed()
        {
            var summary = AuditArgumentRedactor.Describe(
                new Dictionary<string, object?> { ["id"] = 3 }, error: "LDAP bind failed");

            Assert.Contains("FAILED", summary);
            Assert.Contains("LDAP bind failed", summary);
        }

        [Fact]
        public void ARequestWithNoArgumentsSaysSo()
        {
            // An empty string in Details reads as a truncated record.
            Assert.Equal(AuditArgumentRedactor.NoArguments,
                AuditArgumentRedactor.Describe(new Dictionary<string, object?>()));
        }

        /// <summary>
        /// Details is nvarchar(2000). A value one character too long throws on SaveChanges, and the
        /// audit write is the last thing in the request — losing it loses the whole record.
        /// </summary>
        [Fact]
        public void ALongArgumentCannotOverflowTheColumn()
        {
            var summary = AuditArgumentRedactor.Describe(new Dictionary<string, object?>
            {
                ["blob"] = new string('x', 50_000),
                ["list"] = Enumerable.Range(0, 5000).ToList()
            });

            Assert.True(summary.Length < 2000, $"Details too long: {summary.Length}");
        }

        [Fact]
        public void ACollectionIsCountedNotDumped()
        {
            var summary = AuditArgumentRedactor.Describe(new Dictionary<string, object?>
            {
                ["ids"] = new List<int> { 1, 2, 3 }
            });

            Assert.Contains("3 item(s)", summary);
        }
    }
}
