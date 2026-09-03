using ClosedXML.Excel;
using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Models.Metaverse;
using IdentitySyncPro.Core.Models.Sync;
using IdentitySyncPro.Infrastructure.Services;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Guards the Tenant column in Excel exports. Adding it shifted every following column by
    /// one, and a mis-shift produces a file whose values sit under the wrong headers — wrong
    /// but perfectly well-formed, so nothing else would catch it.
    /// </summary>
    public class ExcelExportTenantTests
    {
        private static readonly Dictionary<int, string> TenantNames = new()
        {
            { 1, "Employees" },
            { 2, "Contractors" }
        };

        private static IXLWorksheet FirstSheet(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            var wb = new XLWorkbook(ms);
            return wb.Worksheet(1);
        }

        /// <summary>Header text → its 1-based column index, so assertions survive reordering.</summary>
        private static int Col(IXLWorksheet ws, string header)
        {
            for (int c = 1; c <= 40; c++)
                if (ws.Cell(1, c).GetString() == header) return c;
            throw new Xunit.Sdk.XunitException($"Header '{header}' not found in exported sheet.");
        }

        [Fact]
        public void SyncRunsExport_PutsEachValueUnderItsOwnHeader()
        {
            var runs = new List<SyncRun>
            {
                new() {
                    Id = 1, TenantId = 2, RunType = "Delta", Status = SyncRunStatus.Completed,
                    TotalCreated = 5, TotalUpdated = 6, TotalFailed = 7, TotalNoChange = 8,
                    TotalSkipped = 9, TotalProcessed = 35, TriggeredBy = "scheduler",
                    StartTime = new DateTime(2026, 7, 19, 10, 0, 0, DateTimeKind.Utc)
                }
            };

            var ws = FirstSheet(new ExcelExportService().ExportSyncRuns(runs, isArabic: false, TenantNames));

            Assert.Equal("Contractors", ws.Cell(2, Col(ws, "Tenant")).GetString());
            Assert.Equal("Delta", ws.Cell(2, Col(ws, "Type")).GetString());
            Assert.Equal("Completed", ws.Cell(2, Col(ws, "Status")).GetString());
            // The counters are the easiest thing to shift by one and never notice.
            Assert.Equal(5, ws.Cell(2, Col(ws, "Created")).GetValue<int>());
            Assert.Equal(6, ws.Cell(2, Col(ws, "Updated")).GetValue<int>());
            Assert.Equal(7, ws.Cell(2, Col(ws, "Failed")).GetValue<int>());
            Assert.Equal(8, ws.Cell(2, Col(ws, "No Change")).GetValue<int>());
            Assert.Equal(9, ws.Cell(2, Col(ws, "Skipped")).GetValue<int>());
            Assert.Equal(35, ws.Cell(2, Col(ws, "Total")).GetValue<int>());
            Assert.Equal("scheduler", ws.Cell(2, Col(ws, "Triggered By")).GetString());
        }

        [Fact]
        public void MetaverseExport_PutsEachValueUnderItsOwnHeader()
        {
            var entries = new List<MetaverseEntry>
            {
                new() {
                    Id = 1, TenantId = 1, ExternalId = "1001", LifecycleState = "Active",
                    SourceStatusCode = 42, SourceStatusDesc = "On duty", ADAccountEnabled = true,
                    LastImportDate = new DateTime(2026, 7, 19, 9, 0, 0, DateTimeKind.Utc),
                    StateChangedDate = new DateTime(2026, 7, 18, 9, 0, 0, DateTimeKind.Utc)
                }
            };

            var ws = FirstSheet(new ExcelExportService().ExportMetaverseEntries(entries, isArabic: false, TenantNames));

            Assert.Equal("1001", ws.Cell(2, Col(ws, "External ID")).GetString());
            Assert.Equal("Employees", ws.Cell(2, Col(ws, "Tenant")).GetString());
            Assert.Equal("Active", ws.Cell(2, Col(ws, "Lifecycle State")).GetString());
            Assert.Equal(42, ws.Cell(2, Col(ws, "Status Code")).GetValue<int>());
            Assert.Equal("On duty", ws.Cell(2, Col(ws, "Status Desc")).GetString());
            Assert.Equal("Yes", ws.Cell(2, Col(ws, "AD Enabled")).GetString());
        }

        [Fact]
        public void UnknownAndMissingTenants_DegradeInsteadOfThrowing()
        {
            // TenantId 99 has no name; a null TenantId is a row predating multi-tenancy.
            var runs = new List<SyncRun>
            {
                new() { Id = 1, TenantId = 99, RunType = "Full", Status = SyncRunStatus.Completed },
                new() { Id = 2, TenantId = null, RunType = "Full", Status = SyncRunStatus.Completed }
            };

            var ws = FirstSheet(new ExcelExportService().ExportSyncRuns(runs, isArabic: false, TenantNames));
            var tenantCol = Col(ws, "Tenant");

            Assert.Equal("Tenant #99", ws.Cell(2, tenantCol).GetString());
            Assert.Equal("—", ws.Cell(3, tenantCol).GetString());
        }

        [Fact]
        public void ReportSummary_StatesItsTenantScope()
        {
            var svc = new ExcelExportService();
            var empty = new List<(string, int, int, int)>();
            var noStatus = new List<(string?, int)>();
            var noErrors = new List<(string?, int)>();

            var scoped = FirstSheet(svc.ExportReportSummary(1, 1, 0, 10, empty, noStatus, noErrors, false, "Contractors"));
            Assert.Equal("Scope (Tenant)", scoped.Cell(2, 1).GetString());
            Assert.Equal("Contractors", scoped.Cell(2, 2).GetString());
            // Totals must not be knocked out of place by the scope row above them.
            Assert.Equal(10, scoped.Cell(6, 2).GetValue<int>());

            // No tenant selected = the file says so explicitly rather than leaving it blank.
            var all = FirstSheet(svc.ExportReportSummary(1, 1, 0, 10, empty, noStatus, noErrors, false));
            Assert.Equal("All Tenants", all.Cell(2, 2).GetString());
        }
    }
}
