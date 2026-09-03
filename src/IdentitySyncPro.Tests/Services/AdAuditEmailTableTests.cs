using System.Text.RegularExpressions;
using IdentitySyncPro.Infrastructure.Services;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// The table inside the AD audit report email.
    ///
    /// The failure this guards against does not look like a failure: a row with one cell more or
    /// fewer than its header renders as a flawless table in which every column after the mismatch
    /// shows the value belonging to its neighbour. Nothing throws, the mail arrives, and the reader
    /// takes an OU for a display name. So the assertions never use a fixed column index — they look
    /// the header up by name and read the cell underneath it, which is the only way a reordering
    /// gets caught.
    /// </summary>
    public class AdAuditEmailTableTests
    {
        private static SvcAdAuditExecutor.Finding PwdFinding(
            string sam = "svc_backup",
            string? display = "Backup Service",
            string? dn = "CN=svc_backup,OU=Service Accounts,OU=IT,DC=corp,DC=local") =>
            new("PwdNeverExpires", sam, dn, display, "pwdLastSet: 2019-04-02");

        private static List<string> Cells(string rowHtml) =>
            Regex.Matches(rowHtml, "<t[dh][^>]*>(.*?)</t[dh]>", RegexOptions.Singleline)
                 .Select(m => m.Groups[1].Value).ToList();

        private static List<string> FirstRowCells(string rowsHtml) =>
            Cells(Regex.Match(rowsHtml, "<tr>.*?</tr>", RegexOptions.Singleline).Value);

        /// <summary>The columns the report was asked for, in the order they were asked for.</summary>
        [Fact]
        public void ThePasswordNeverExpiresReportHasTheFiveRequestedColumns()
        {
            Assert.Equal(
                new[] { "حساب AD", "الاسم", "النوع", "تفاصيل", "الموقع" },
                SvcAdAuditExecutor.HeaderLabels("PasswordNeverExpires"));
        }

        [Fact]
        public void EveryRowHasExactlyOneCellPerHeader()
        {
            var headers = SvcAdAuditExecutor.HeaderLabels("PasswordNeverExpires");
            var rows = SvcAdAuditExecutor.BuildRows(new[]
            {
                PwdFinding(),
                PwdFinding("admin2", "Second Admin", "CN=admin2,OU=Admins,DC=corp,DC=local"),
                PwdFinding("noDn", null, null)   // a finding with neither name nor DN
            });

            var rowMatches = Regex.Matches(rows, "<tr>.*?</tr>", RegexOptions.Singleline);
            Assert.Equal(3, rowMatches.Count);
            foreach (Match row in rowMatches)
                Assert.Equal(headers.Length, Cells(row.Value).Count);
        }

        [Fact]
        public void EachValueSitsUnderItsOwnHeader()
        {
            var headers = SvcAdAuditExecutor.HeaderLabels("PasswordNeverExpires");
            var cells = FirstRowCells(SvcAdAuditExecutor.BuildRows(new[] { PwdFinding() }));

            string Under(string header) => cells[Array.IndexOf(headers, header)];

            Assert.Equal("svc_backup", Under("حساب AD"));
            Assert.Equal("Backup Service", Under("الاسم"));
            Assert.Contains("كلمة مرور لا تنتهي", Under("النوع"));
            Assert.Equal("pwdLastSet: 2019-04-02", Under("تفاصيل"));
            Assert.Equal("OU=Service Accounts,OU=IT,DC=corp,DC=local", Under("الموقع"));
        }

        /// <summary>
        /// The type column carries the Arabic label AND the programmatic name: the label is what
        /// the reader understands, the raw name is what the audit-log filter and support threads
        /// use. Dropping either one costs more than the line it takes.
        /// </summary>
        [Fact]
        public void TheTypeColumnCarriesTheArabicLabelAndTheRawActionName()
        {
            var headers = SvcAdAuditExecutor.HeaderLabels("PasswordNeverExpires");
            var cells = FirstRowCells(SvcAdAuditExecutor.BuildRows(new[]
            {
                new SvcAdAuditExecutor.Finding("PwdNeverExpiresRemoved", "nasser-test2", "CN=nasser-test2,OU=test1,DC=nu", "Nasser M Sawadi", "pwdLastSet: 2023-12-04")
            }));
            var type = cells[Array.IndexOf(headers, "النوع")];

            Assert.Contains("أُزيل «لا تنتهي»", type);
            Assert.Contains("PwdNeverExpiresRemoved", type);
        }

        /// <summary>
        /// The wording has to match the service results screen. The same run read in two places
        /// reading as two different things is how a reader stops trusting either.
        /// </summary>
        [Theory]
        [InlineData("PwdNeverExpires", "كلمة مرور لا تنتهي")]
        [InlineData("PwdNeverExpiresRemoved", "أُزيل «لا تنتهي»")]
        [InlineData("PwdNeverExpiresExcluded", "مستثنى من الإزالة")]
        [InlineData("PwdNeverExpiresFailed", "فشل الإزالة")]
        [InlineData("PrivilegedMember", "عضو إداري")]
        [InlineData("PrivilegedNew", "عضو إداري جديد")]
        [InlineData("PrivilegedRemoved", "أُزيل من الإداريين")]
        [InlineData("DuplicateAccount", "حساب مكرّر")]
        [InlineData("LockedAccount", "حساب مقفل")]
        [InlineData("AccessMember", "عضو مجموعة")]
        public void EveryActionTheReportsProduceHasALabel(string action, string expected)
        {
            Assert.Equal(expected, SvcAdAuditExecutor.ActionLabel(action));
        }

        /// <summary>
        /// An action nobody labelled shows its own name. Inventing Arabic for it would misreport,
        /// and blanking the cell would read as missing data — both worse than the bare name.
        /// </summary>
        [Fact]
        public void AnUnlabelledActionFallsBackToItsRawName()
        {
            var headers = SvcAdAuditExecutor.HeaderLabels("PasswordNeverExpires");
            var cells = FirstRowCells(SvcAdAuditExecutor.BuildRows(new[]
            {
                new SvcAdAuditExecutor.Finding("SomeFutureAction", "user1", "CN=user1,OU=Staff,DC=corp", "User One", null)
            }));

            Assert.Null(SvcAdAuditExecutor.ActionLabel("SomeFutureAction"));
            Assert.Equal("SomeFutureAction", cells[Array.IndexOf(headers, "النوع")]);
        }

        /// <summary>
        /// The location column answers "where does this account live". The full DN would only
        /// repeat the account name already in the first column, so the leaf is stripped.
        /// </summary>
        [Fact]
        public void TheLocationColumnShowsTheContainingOuNotTheWholeDn()
        {
            var headers = SvcAdAuditExecutor.HeaderLabels("PasswordNeverExpires");
            var cells = FirstRowCells(SvcAdAuditExecutor.BuildRows(new[] { PwdFinding() }));
            var location = cells[Array.IndexOf(headers, "الموقع")];

            Assert.DoesNotContain("CN=svc_backup", location);
            Assert.StartsWith("OU=Service Accounts", location);
        }

        /// <summary>
        /// Findings without a DN are real — a group that would not resolve, a member gone since the
        /// previous run. An empty cell there reads as missing data rather than as "not applicable".
        /// </summary>
        [Fact]
        public void AFindingWithNoDnGetsADashNotABlankCell()
        {
            var headers = SvcAdAuditExecutor.HeaderLabels("PrivilegedGroups");
            var cells = FirstRowCells(SvcAdAuditExecutor.BuildRows(new[]
            {
                new SvcAdAuditExecutor.Finding("PrivilegedRemoved", "olduser", null, "Domain Admins", "REMOVED since last run")
            }));

            Assert.Equal("—", cells[Array.IndexOf(headers, "الموقع")]);
        }

        /// <summary>
        /// Same reasoning for the other optional fields: an account with no displayName, or a
        /// finding that carries no detail, gets a dash. Only a rendered sample showed this — the
        /// code read as correct because the null simply interpolated to nothing.
        /// </summary>
        [Fact]
        public void AMissingNameOrDetailGetsADashNotABlankCell()
        {
            var headers = SvcAdAuditExecutor.HeaderLabels("AccessCertification");
            var cells = FirstRowCells(SvcAdAuditExecutor.BuildRows(new[]
            {
                new SvcAdAuditExecutor.Finding("AccessMember", "user1", "CN=user1,OU=Staff,DC=corp", null, null)
            }));

            Assert.Equal("—", cells[Array.IndexOf(headers, "المجموعة")]);
            Assert.Equal("—", cells[Array.IndexOf(headers, "تفاصيل")]);
        }

        /// <summary>
        /// The second column holds a different thing in each report, so its header has to follow.
        /// Labelling a duplicate's shared value "الاسم" would be a wrong label over correct data.
        /// </summary>
        [Theory]
        [InlineData("PasswordNeverExpires", "الاسم")]
        [InlineData("LockedAccounts", "الاسم")]
        [InlineData("DuplicateAccounts", "القيمة المكررة")]
        [InlineData("PrivilegedGroups", "المجموعة")]
        [InlineData("AccessCertification", "المجموعة")]
        public void TheSecondColumnIsLabelledForTheReportItBelongsTo(string reportType, string expected)
        {
            Assert.Equal(expected, SvcAdAuditExecutor.HeaderLabels(reportType)[1]);
        }

        [Fact]
        public void TheHeaderCellsMatchTheLabelsAndAreAllRendered()
        {
            var html = SvcAdAuditExecutor.BuildHeaderCells("PasswordNeverExpires");
            var rendered = Cells(html).Select(c => c.Replace("&nbsp;", " "));

            Assert.Equal(SvcAdAuditExecutor.HeaderLabels("PasswordNeverExpires"), rendered);
        }

        /// <summary>
        /// "حساب AD" sets a Latin run against an Arabic one, and mail clients were swallowing the
        /// space at that boundary — the header arrived as "حسابAD". A plain space is not enough
        /// there, so the separator has to be non-breaking.
        /// </summary>
        [Fact]
        public void TheArabicLatinHeaderKeepsItsSpace()
        {
            var html = SvcAdAuditExecutor.BuildHeaderCells("PasswordNeverExpires");

            Assert.Contains("حساب&nbsp;AD", html);
            Assert.DoesNotContain("حسابAD", html);
        }

        /// <summary>
        /// Long reports are capped so the mail stays readable; the run's own audit log holds the
        /// rest. The cap must not silently drop the header/row correspondence.
        /// </summary>
        [Fact]
        public void ALongReportIsCappedAndStillWellFormed()
        {
            var many = Enumerable.Range(1, SvcAdAuditExecutor.RowCap + 50)
                .Select(i => PwdFinding($"user{i:D4}", $"User {i}", $"CN=user{i:D4},OU=Staff,DC=corp,DC=local"));

            var rows = SvcAdAuditExecutor.BuildRows(many);
            var rowMatches = Regex.Matches(rows, "<tr>.*?</tr>", RegexOptions.Singleline);

            Assert.Equal(SvcAdAuditExecutor.RowCap, rowMatches.Count);
            foreach (Match row in rowMatches)
                Assert.Equal(5, Cells(row.Value).Count);
        }
    }
}
