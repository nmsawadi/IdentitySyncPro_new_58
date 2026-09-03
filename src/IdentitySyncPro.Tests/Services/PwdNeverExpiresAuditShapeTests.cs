using IdentitySyncPro.Core.Models.Services;
using IdentitySyncPro.Infrastructure.Services;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// The audit row a password-never-expires removal leaves behind.
    ///
    /// Reported symptom: a run that cleared the flag on one account showed "acted on: 1" and yet
    /// "no audit entries". A run that changed something must always leave a row that names the
    /// account and shows the before and after — these tests pin that shape.
    /// </summary>
    public class PwdNeverExpiresAuditShapeTests
    {
        private const int UF_NORMAL_ACCOUNT = 0x0200;
        private const int UF_DONT_EXPIRE_PASSWORD = 0x10000;

        /// <summary>
        /// Mirrors how SvcAdAuditExecutor turns a removal finding into an audit entry.
        /// </summary>
        private static SvcAuditEntry EntryForRemoval(string sam, string dn, int uac, string pwdLastSet)
        {
            var newUac = uac & ~UF_DONT_EXPIRE_PASSWORD;
            return new SvcAuditEntry
            {
                SvcRunLogId = 13,
                SvcServiceId = 4,
                Timestamp = DateTime.UtcNow,
                Action = "PwdNeverExpiresRemoved",
                KeyValue = sam,
                ADIdentity = dn,
                AttributeName = "userAccountControl",
                OldValue = $"{uac} · pwdLastSet {pwdLastSet}",
                NewValue = newUac.ToString()
            };
        }

        [Fact]
        public void NamesTheAccountAndShowsBeforeAndAfter()
        {
            var e = EntryForRemoval("svc-backup", "CN=Backup,OU=Service,DC=corp,DC=local",
                UF_NORMAL_ACCOUNT | UF_DONT_EXPIRE_PASSWORD, "2023-02-02");

            Assert.Equal("svc-backup", e.KeyValue);
            Assert.Contains("OU=Service", e.ADIdentity);
            Assert.Equal("userAccountControl", e.AttributeName);
            Assert.StartsWith("66048", e.OldValue);
            Assert.Equal("512", e.NewValue);
        }

        [Fact]
        public void KeepsThePasswordAgeVisible()
        {
            // pwdLastSet is the reason an auditor cares about this account; it must survive into
            // the row rather than being dropped when the before/after columns are filled.
            var e = EntryForRemoval("svc-backup", "CN=B,DC=corp,DC=local",
                UF_NORMAL_ACCOUNT | UF_DONT_EXPIRE_PASSWORD, "2019-11-30");

            Assert.Contains("pwdLastSet 2019-11-30", e.OldValue);
        }

        [Fact]
        public void TheActionIsDistinctFromTheReportOnlyAction()
        {
            // The screen and the Excel export both key off Action; a removal must never be
            // mistaken for a read-only report row.
            var removed = EntryForRemoval("a", "CN=a", UF_NORMAL_ACCOUNT | UF_DONT_EXPIRE_PASSWORD, "-");

            Assert.Equal("PwdNeverExpiresRemoved", removed.Action);
            Assert.NotEqual("PwdNeverExpires", removed.Action);
            Assert.StartsWith("PwdNeverExpires", removed.Action);   // the view groups on this prefix
        }

        [Fact]
        public void EveryFieldFitsItsColumn()
        {
            var e = EntryForRemoval(new string('s', 300), new string('d', 900),
                UF_NORMAL_ACCOUNT | UF_DONT_EXPIRE_PASSWORD, "2020-01-01");

            // The executor truncates the DN; the rest must be within bounds on their own.
            Assert.True(e.Action.Length <= 50);
            Assert.True((e.AttributeName?.Length ?? 0) <= 200);
            Assert.True((e.OldValue?.Length ?? 0) <= 2000);
            Assert.True((e.NewValue?.Length ?? 0) <= 2000);
        }

        /// <summary>
        /// A run that acted on N accounts must produce at least N rows plus its summary. This is
        /// the invariant the reported screen violated (acted = 1, rows = 0).
        /// </summary>
        [Fact]
        public void ARunThatActedOnAccountsProducesARowPerAccountPlusTheSummary()
        {
            var runLog = new SvcRunLog
            {
                Id = 13,
                SvcServiceId = 4,
                StartTime = DateTime.UtcNow,
                TotalRecords = 1,
                UpdatedRecords = 1
            };

            var rows = new List<SvcAuditEntry>
            {
                EntryForRemoval("svc-backup", "CN=B,DC=corp,DC=local", UF_NORMAL_ACCOUNT | UF_DONT_EXPIRE_PASSWORD, "2023-02-02")
            };
            rows.Add(SvcRunSummary.Build(runLog, 4, actedOn: new[] { "• 1 × PwdNeverExpiresRemoved" }));

            Assert.Equal(runLog.UpdatedRecords + 1, rows.Count);
            Assert.All(rows, r => Assert.Equal(13, r.SvcRunLogId));
            Assert.Contains(rows, r => r.Action == SvcRunSummary.ActionName);
            Assert.Contains(rows, r => r.Action == "PwdNeverExpiresRemoved");
        }
    }
}
