using ClosedXML.Excel;
using IdentitySyncPro.Core.Models.Services;
using IdentitySyncPro.Infrastructure.Services;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// The service audit-log Excel export.
    ///
    /// This export existed but was reachable from nowhere — no controller action, no button — so
    /// nothing had ever produced a file from it. These tests cover the workbook itself now that it
    /// is wired up: a file whose values sit under the wrong headers is well-formed and looks
    /// correct, which is why the columns are asserted by header name rather than by position.
    /// </summary>
    public class ServiceAuditExportTests
    {
        private static IXLWorksheet Sheet(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            var wb = new XLWorkbook(ms);
            return wb.Worksheet(1);
        }

        /// <summary>Header text → its 1-based column index, so assertions survive reordering.</summary>
        private static int Col(IXLWorksheet ws, string header)
        {
            for (int c = 1; c <= 20; c++)
                if (ws.Cell(1, c).GetString().Trim() == header) return c;
            throw new Xunit.Sdk.XunitException($"Header '{header}' not found");
        }

        private static List<SvcAuditEntry> SampleEntries() => new()
        {
            new SvcAuditEntry
            {
                Timestamp = new DateTime(2026, 7, 25, 9, 30, 0, DateTimeKind.Utc),
                Action = "InactiveDisabled",
                KeyValue = "maalhareth",
                ADIdentity = "CN=Mohammed,OU=Employees,DC=corp,DC=local",
                AttributeName = "lastLogonTimestamp",
                OldValue = "512",
                NewValue = "2025-01-14"
            },
            new SvcAuditEntry
            {
                Timestamp = new DateTime(2026, 7, 25, 9, 31, 0, DateTimeKind.Utc),
                Action = "PwdNeverExpiresRemoved",
                KeyValue = "svc-backup",
                ADIdentity = "CN=Backup,OU=Service,DC=corp,DC=local",
                AttributeName = "userAccountControl",
                NewValue = "66048 → 512 (pwdLastSet: 2023-02-02)"
            },
            new SvcAuditEntry
            {
                Timestamp = new DateTime(2026, 7, 25, 9, 32, 0, DateTimeKind.Utc),
                Action = "PwdNeverExpiresFailed",
                KeyValue = "svc-legacy",
                ADIdentity = "CN=Legacy,OU=Service,DC=corp,DC=local",
                ErrorMessage = "insufficient access rights"
            }
        };

        [Fact]
        public void ProducesAReadableWorkbookWithEveryRow()
        {
            var ws = Sheet(new ExcelExportService().ExportServiceAudit(SampleEntries(), "Inactive Accounts", isArabic: false));

            // Header row + one row per entry.
            Assert.Equal("1", ws.Cell(2, 1).GetString());
            Assert.Equal("maalhareth", ws.Cell(2, Col(ws, "Key")).GetString());
            Assert.Equal("svc-backup", ws.Cell(3, Col(ws, "Key")).GetString());
            Assert.Equal("svc-legacy", ws.Cell(4, Col(ws, "Key")).GetString());
        }

        [Fact]
        public void PutsEachValueUnderItsOwnHeader()
        {
            var ws = Sheet(new ExcelExportService().ExportServiceAudit(SampleEntries(), "Inactive Accounts", isArabic: false));

            Assert.Equal("InactiveDisabled", ws.Cell(2, Col(ws, "Action")).GetString());
            Assert.Equal("lastLogonTimestamp", ws.Cell(2, Col(ws, "Attribute")).GetString());
            Assert.Equal("512", ws.Cell(2, Col(ws, "Old Value")).GetString());
            Assert.Equal("2025-01-14", ws.Cell(2, Col(ws, "New Value")).GetString());
            Assert.Contains("OU=Employees", ws.Cell(2, Col(ws, "AD Identity")).GetString());
        }

        [Fact]
        public void CarriesTheRemovalAndFailureActions()
        {
            // The two actions added with the "remove password-never-expires" mode. A run that
            // failed on some accounts has to be distinguishable in the file, not just on screen.
            var ws = Sheet(new ExcelExportService().ExportServiceAudit(SampleEntries(), "Pwd Audit", isArabic: false));

            Assert.Equal("PwdNeverExpiresRemoved", ws.Cell(3, Col(ws, "Action")).GetString());
            Assert.Contains("66048", ws.Cell(3, Col(ws, "New Value")).GetString());

            Assert.Equal("PwdNeverExpiresFailed", ws.Cell(4, Col(ws, "Action")).GetString());
            Assert.Equal("insufficient access rights", ws.Cell(4, Col(ws, "Error")).GetString());
        }

        [Fact]
        public void ArabicHeadersAreUsedWhenRequested()
        {
            var ws = Sheet(new ExcelExportService().ExportServiceAudit(SampleEntries(), "تعطيل غير المستخدمة", isArabic: true));

            Assert.Equal("maalhareth", ws.Cell(2, Col(ws, "المفتاح")).GetString());
            Assert.Equal("InactiveDisabled", ws.Cell(2, Col(ws, "الإجراء")).GetString());
        }

        [Fact]
        public void AnEmptyResultStillProducesAValidFile()
        {
            // Exporting a filter that matched nothing must yield a readable empty sheet rather
            // than a corrupt download.
            var ws = Sheet(new ExcelExportService().ExportServiceAudit(new List<SvcAuditEntry>(), "Empty", isArabic: false));

            Assert.Equal("Key", ws.Cell(1, Col(ws, "Key")).GetString());
            Assert.True(string.IsNullOrEmpty(ws.Cell(2, 1).GetString()));
        }

        /// <summary>
        /// Excel refuses a worksheet name over 31 characters, and ClosedXML throws rather than
        /// truncating. Real service names are descriptive — "تعطيل الحسابات غير المستخدمة" alone is
        /// 28 characters — so before the name was shortened, clicking Export returned a server
        /// error for exactly the services most likely to be exported.
        /// </summary>
        [Theory]
        [InlineData("تعطيل الحسابات غير المستخدمة من عدد أشهر محددة")]
        [InlineData("Disable Inactive Accounts After N Months — Production")]
        [InlineData("A")]
        public void LongServiceNamesStillExport(string serviceName)
        {
            var svc = new ExcelExportService();

            foreach (var arabic in new[] { true, false })
            {
                var ws = Sheet(svc.ExportServiceAudit(SampleEntries(), serviceName, arabic));
                Assert.True(ws.Name.Length <= 31, $"sheet name too long: '{ws.Name}'");
                Assert.Equal("maalhareth", ws.Cell(2, Col(ws, arabic ? "المفتاح" : "Key")).GetString());

                var wsLogs = Sheet(svc.ExportServiceLogs(new List<SvcRunLog>(), serviceName, arabic));
                Assert.True(wsLogs.Name.Length <= 31, $"logs sheet name too long: '{wsLogs.Name}'");
            }
        }

        [Theory]
        [InlineData("Sync: Prod/Test")]
        [InlineData("A[B]C*D?E")]
        public void ServiceNamesWithCharactersExcelForbidsStillExport(string serviceName)
        {
            // : \ / ? * [ ] are illegal in a worksheet name — and nothing stops an administrator
            // from using them in a service name.
            var ws = Sheet(new ExcelExportService().ExportServiceAudit(SampleEntries(), serviceName, isArabic: false));

            Assert.DoesNotContain(':', ws.Name);
            Assert.DoesNotContain('/', ws.Name);
            Assert.DoesNotContain('[', ws.Name);
            Assert.Equal("maalhareth", ws.Cell(2, Col(ws, "Key")).GetString());
        }

        [Fact]
        public void NullOptionalFieldsDoNotBreakTheExport()
        {
            var entries = new List<SvcAuditEntry>
            {
                new() { Timestamp = DateTime.UtcNow, Action = "PwdNeverExpires", KeyValue = "user1" }
            };

            var ws = Sheet(new ExcelExportService().ExportServiceAudit(entries, "Report", isArabic: false));

            Assert.Equal("user1", ws.Cell(2, Col(ws, "Key")).GetString());
            Assert.Equal("", ws.Cell(2, Col(ws, "Error")).GetString());
        }
    }
}
