using IdentitySyncPro.Core.Models.Audit;
using ClosedXML.Excel;
using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Models.Metaverse;
using IdentitySyncPro.Core.Models.Services;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Core.Models.Sync;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Centralized Excel export service for all report types.
    /// Uses ClosedXML for xlsx generation with consistent styling.
    /// </summary>
    public class ExcelExportService
    {
        // ═══════════════════════════════════════
        // SYNC OPERATIONS (from Sync Details page)
        // ═══════════════════════════════════════
        public byte[] ExportSyncOperations(List<SyncOperation> operations, int runId, bool isArabic = true)
        {
            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add(isArabic ? $"عمليات المزامنة #{runId}" : $"Sync Run #{runId}");

            var headers = isArabic
                ? new[] { "#", "الوقت", "رقم الهوية", "العملية", "الحالة", "التغييرات", "المدة (ms)", "الخطأ" }
                : new[] { "#", "Time", "Identity ID", "Operation", "Status", "Changes", "Duration (ms)", "Error" };

            WriteHeaders(ws, headers);

            for (int i = 0; i < operations.Count; i++)
            {
                var op = operations[i];
                var r = i + 2;
                ws.Cell(r, 1).Value = i + 1;
                ws.Cell(r, 2).Value = op.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                ws.Cell(r, 3).Value = op.IdentityId;
                ws.Cell(r, 4).Value = op.Operation.ToString();
                ws.Cell(r, 5).Value = op.Status.ToString();
                ws.Cell(r, 6).Value = op.ChangedFields ?? "";
                ws.Cell(r, 7).Value = op.DurationMs;
                ws.Cell(r, 8).Value = op.ErrorMessage ?? "";

                StyleStatusCell(ws.Cell(r, 5), op.Status.ToString());
                AlternateRowColor(ws, r, i, headers.Length);
            }

            return FinalizeWorkbook(workbook, ws, isArabic);
        }

        // ═══════════════════════════════════════
        // SYNC RUNS (from Sync Index page)
        // ═══════════════════════════════════════
        /// <param name="tenantNames">
        /// Tenant id → name. Supply it to emit a Tenant column, so a file covering several
        /// tenants stays interpretable once it leaves the screen it was exported from.
        /// </param>
        public byte[] ExportSyncRuns(List<SyncRun> runs, bool isArabic = true, IReadOnlyDictionary<int, string>? tenantNames = null)
        {
            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add(isArabic ? "عمليات المزامنة" : "Sync Runs");

            var headers = isArabic
                ? new[] { "#", "الجهة", "النوع", "الحالة", "تم إنشاؤها", "تم تحديثها", "فاشلة", "بدون تغيير", "تم تخطيها", "إجمالي", "المدة", "بواسطة", "وقت البدء", "وقت الانتهاء" }
                : new[] { "#", "Tenant", "Type", "Status", "Created", "Updated", "Failed", "No Change", "Skipped", "Total", "Duration", "Triggered By", "Start Time", "End Time" };

            WriteHeaders(ws, headers);

            for (int i = 0; i < runs.Count; i++)
            {
                var run = runs[i];
                var r = i + 2;
                ws.Cell(r, 1).Value = i + 1;
                ws.Cell(r, 2).Value = TenantLabel(run.TenantId, tenantNames, isArabic);
                ws.Cell(r, 3).Value = run.RunType;
                ws.Cell(r, 4).Value = run.Status.ToString();
                ws.Cell(r, 5).Value = run.TotalCreated;
                ws.Cell(r, 6).Value = run.TotalUpdated;
                ws.Cell(r, 7).Value = run.TotalFailed;
                ws.Cell(r, 8).Value = run.TotalNoChange;
                ws.Cell(r, 9).Value = run.TotalSkipped;
                ws.Cell(r, 10).Value = run.TotalProcessed;
                ws.Cell(r, 11).Value = run.Duration?.ToString(@"hh\:mm\:ss") ?? "";
                ws.Cell(r, 12).Value = ActorNames.Describe(run.TriggeredBy, isArabic);   // same wording as the screen
                ws.Cell(r, 13).Value = run.StartTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                ws.Cell(r, 14).Value = run.EndTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "";

                StyleStatusCell(ws.Cell(r, 4), run.Status.ToString());
                AlternateRowColor(ws, r, i, headers.Length);
            }

            return FinalizeWorkbook(workbook, ws, isArabic);
        }

        /// <summary>
        /// Human-readable tenant label. Falls back to the raw id when the name is unknown, and
        /// to a dash for rows predating multi-tenancy (SyncRun.TenantId is nullable).
        /// </summary>
        private static string TenantLabel(int? tenantId, IReadOnlyDictionary<int, string>? tenantNames, bool isArabic)
        {
            if (tenantId == null) return "—";
            if (tenantNames != null && tenantNames.TryGetValue(tenantId.Value, out var name)) return name;
            return isArabic ? $"جهة #{tenantId}" : $"Tenant #{tenantId}";
        }

        // ═══════════════════════════════════════
        // SMS SEND LOG
        // ═══════════════════════════════════════
        public byte[] ExportSmsLogs(List<SmsSendLog> logs, bool isArabic = true)
        {
            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add(isArabic ? "سجل الرسائل" : "SMS Log");

            var headers = isArabic
                ? new[] { "#", "المصدر", "الحساب", "الاسم", "الجوال", "الحالة", "المزود", "الرد / الخطأ", "محاولات", "الوقت" }
                : new[] { "#", "Source", "Account", "Name", "Mobile", "Status", "Provider", "Response / Error", "Retries", "Time" };

            WriteHeaders(ws, headers);

            for (int i = 0; i < logs.Count; i++)
            {
                var l = logs[i];
                var r = i + 2;
                ws.Cell(r, 1).Value = i + 1;
                ws.Cell(r, 2).Value = l.Source;
                ws.Cell(r, 3).Value = l.Account ?? "";
                ws.Cell(r, 4).Value = l.DisplayName ?? "";
                ws.Cell(r, 5).Value = string.IsNullOrEmpty(l.PhoneNumber) ? "" : PhoneHelper.MaskPhone(l.PhoneNumber);
                ws.Cell(r, 6).Value = l.Status;
                ws.Cell(r, 7).Value = l.ProviderName ?? "";
                ws.Cell(r, 8).Value = l.GatewayResponse ?? "";
                ws.Cell(r, 9).Value = l.RetryCount;
                ws.Cell(r, 10).Value = l.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

                StyleStatusCell(ws.Cell(r, 6), l.Status);
                AlternateRowColor(ws, r, i, headers.Length);
            }

            return FinalizeWorkbook(workbook, ws, isArabic);
        }

        // ═══════════════════════════════════════
        // SELF-SERVICE PASSWORD RESET LOG
        // ═══════════════════════════════════════
        public byte[] ExportPasswordResetLog(
            List<IdentitySyncPro.Core.Models.Settings.PasswordResetRequest> rows,
            Dictionary<int, string> domainNames, bool isArabic = true)
        {
            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add(isArabic ? "سجل إعادة التعيين" : "Password Reset Log");

            var headers = isArabic
                ? new[] { "#", "الوقت", "اسم المستخدم", "الدومين", "الجوال", "المحاولات", "IP", "الحالة" }
                : new[] { "#", "Time", "Username", "Domain", "Mobile", "Attempts", "IP", "Status" };

            WriteHeaders(ws, headers);

            for (int i = 0; i < rows.Count; i++)
            {
                var x = rows[i];
                var r = i + 2;
                ws.Cell(r, 1).Value = i + 1;
                ws.Cell(r, 2).Value = x.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                ws.Cell(r, 3).Value = x.Username;
                ws.Cell(r, 4).Value = domainNames.TryGetValue(x.SsprDomainId, out var dn) ? dn : "";
                ws.Cell(r, 5).Value = string.IsNullOrEmpty(x.PhoneNumber) ? "" : PhoneHelper.MaskPhone(x.PhoneNumber);
                ws.Cell(r, 6).Value = x.Attempts;
                ws.Cell(r, 7).Value = x.ClientIp ?? "";
                ws.Cell(r, 8).Value = x.Status;

                StyleStatusCell(ws.Cell(r, 8), x.Status);
                AlternateRowColor(ws, r, i, headers.Length);
            }

            return FinalizeWorkbook(workbook, ws, isArabic);
        }

        /// <summary>
        /// A worksheet name Excel will accept: at most 31 characters and none of the reserved
        /// ones (<c>: \ / ? * [ ]</c>).
        ///
        /// Service names reach these exports from a free-text field, and ClosedXML throws on an
        /// over-long or illegal name — so a service called "تعطيل الحسابات غير المستخدمة" made the
        /// export fail outright rather than produce a file with a shortened tab title.
        /// </summary>
        private static string SheetName(string prefix, string serviceName)
        {
            var cleaned = new string((serviceName ?? "").Where(c => !"\\/?*[]:".Contains(c)).ToArray()).Trim();
            var name = string.IsNullOrWhiteSpace(cleaned) ? prefix.Trim() : $"{prefix}{cleaned}";
            return name.Length <= 31 ? name : name[..31].Trim();
        }

        // ═══════════════════════════════════════
        // SERVICES RUN LOGS
        // ═══════════════════════════════════════
        public byte[] ExportServiceLogs(List<SvcRunLog> logs, string serviceName, bool isArabic = true)
        {
            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add(SheetName(isArabic ? "سجلات " : "Logs - ", serviceName));

            var headers = isArabic
                ? new[] { "#", "الحالة", "الإجمالي", "تم التحديث", "فشلت", "تم تخطيها", "لم يوجد", "وقت البدء", "وقت الانتهاء", "الخطأ" }
                : new[] { "#", "Status", "Total", "Updated", "Failed", "Skipped", "Not Found", "Start Time", "End Time", "Error" };

            WriteHeaders(ws, headers);

            for (int i = 0; i < logs.Count; i++)
            {
                var log = logs[i];
                var r = i + 2;
                ws.Cell(r, 1).Value = i + 1;
                ws.Cell(r, 2).Value = log.Status;
                ws.Cell(r, 3).Value = log.TotalRecords;
                ws.Cell(r, 4).Value = log.UpdatedRecords;
                ws.Cell(r, 5).Value = log.FailedRecords;
                ws.Cell(r, 6).Value = log.SkippedRecords;
                ws.Cell(r, 7).Value = log.NotFoundRecords;
                ws.Cell(r, 8).Value = log.StartTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                ws.Cell(r, 9).Value = log.EndTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "";
                ws.Cell(r, 10).Value = log.ErrorMessage ?? "";

                StyleStatusCell(ws.Cell(r, 2), log.Status);
                AlternateRowColor(ws, r, i, headers.Length);
            }

            return FinalizeWorkbook(workbook, ws, isArabic);
        }

        // ═══════════════════════════════════════
        // SERVICES AUDIT LOG
        // ═══════════════════════════════════════
        public byte[] ExportServiceAudit(List<SvcAuditEntry> entries, string serviceName, bool isArabic = true)
        {
            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add(SheetName(isArabic ? "تدقيق - " : "Audit - ", serviceName));

            var headers = isArabic
                ? new[] { "#", "الوقت", "المفتاح", "الحساب", "الإجراء", "الحقل", "القيمة القديمة", "القيمة الجديدة", "الخطأ" }
                : new[] { "#", "Time", "Key", "AD Identity", "Action", "Attribute", "Old Value", "New Value", "Error" };

            WriteHeaders(ws, headers);

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var r = i + 2;
                ws.Cell(r, 1).Value = i + 1;
                ws.Cell(r, 2).Value = e.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                ws.Cell(r, 3).Value = e.KeyValue ?? "";
                ws.Cell(r, 4).Value = e.ADIdentity ?? "";
                ws.Cell(r, 5).Value = e.Action ?? "";
                ws.Cell(r, 6).Value = e.AttributeName ?? "";
                ws.Cell(r, 7).Value = e.OldValue ?? "";
                ws.Cell(r, 8).Value = e.NewValue ?? "";
                ws.Cell(r, 9).Value = e.ErrorMessage ?? "";

                AlternateRowColor(ws, r, i, headers.Length);
            }

            return FinalizeWorkbook(workbook, ws, isArabic);
        }

        // ═══════════════════════════════════════
        // METAVERSE / LIFECYCLE ENTRIES
        // ═══════════════════════════════════════
        /// <param name="tenantNames">Tenant id → name; see <see cref="ExportSyncRuns"/>.</param>
        public byte[] ExportMetaverseEntries(List<MetaverseEntry> entries, bool isArabic = true, IReadOnlyDictionary<int, string>? tenantNames = null)
        {
            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add(isArabic ? "مخزن الهويات" : "Metaverse Identities");

            var headers = isArabic
                ? new[] { "#", "رقم الهوية", "الجهة", "حالة دورة الحياة", "كود الحالة", "وصف الحالة", "حساب AD", "آخر استيراد", "آخر تصدير", "تاريخ تغيير الحالة" }
                : new[] { "#", "External ID", "Tenant", "Lifecycle State", "Status Code", "Status Desc", "AD Enabled", "Last Import", "Last Export", "State Changed" };

            WriteHeaders(ws, headers);

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var r = i + 2;
                ws.Cell(r, 1).Value = i + 1;
                ws.Cell(r, 2).Value = e.ExternalId;
                ws.Cell(r, 3).Value = TenantLabel(e.TenantId, tenantNames, isArabic);
                ws.Cell(r, 4).Value = e.LifecycleState;
                ws.Cell(r, 5).Value = e.SourceStatusCode;
                ws.Cell(r, 6).Value = e.SourceStatusDesc ?? "";
                ws.Cell(r, 7).Value = e.ADAccountEnabled ? (isArabic ? "نعم" : "Yes") : (isArabic ? "لا" : "No");
                ws.Cell(r, 8).Value = e.LastImportDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                ws.Cell(r, 9).Value = e.LastExportDate?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
                ws.Cell(r, 10).Value = e.StateChangedDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

                StyleStatusCell(ws.Cell(r, 4), e.LifecycleState);
                AlternateRowColor(ws, r, i, headers.Length);
            }

            return FinalizeWorkbook(workbook, ws, isArabic);
        }

        // ═══════════════════════════════════════
        // REPORTS — Summary Export
        // ═══════════════════════════════════════
        public byte[] ExportReportSummary(
            int totalRuns, int successfulRuns, int failedRuns, int totalOps,
            List<(string Date, int Created, int Updated, int Failed)> monthlyData,
            List<(string? Status, int Count)> statusDist,
            List<(string? Error, int Count)> topErrors,
            bool isArabic = true,
            string? scopeLabel = null)
        {
            using var workbook = new XLWorkbook();

            // Sheet 1: Summary
            var wsSummary = workbook.Worksheets.Add(isArabic ? "الملخص" : "Summary");
            wsSummary.Cell(1, 1).Value = isArabic ? "المقياس" : "Metric";
            wsSummary.Cell(1, 2).Value = isArabic ? "القيمة" : "Value";
            StyleHeaderRow(wsSummary, 2);

            // State the scope in the file itself — a single-tenant export is otherwise
            // indistinguishable from an all-tenant one once it leaves the screen.
            wsSummary.Cell(2, 1).Value = isArabic ? "النطاق (الجهة)" : "Scope (Tenant)";
            wsSummary.Cell(2, 2).Value = scopeLabel ?? (isArabic ? "كل الجهات" : "All Tenants");
            wsSummary.Cell(3, 1).Value = isArabic ? "إجمالي عمليات المزامنة" : "Total Sync Runs";
            wsSummary.Cell(3, 2).Value = totalRuns;
            wsSummary.Cell(4, 1).Value = isArabic ? "عمليات ناجحة" : "Successful Runs";
            wsSummary.Cell(4, 2).Value = successfulRuns;
            wsSummary.Cell(5, 1).Value = isArabic ? "عمليات فاشلة" : "Failed Runs";
            wsSummary.Cell(5, 2).Value = failedRuns;
            wsSummary.Cell(6, 1).Value = isArabic ? "إجمالي العمليات" : "Total Operations";
            wsSummary.Cell(6, 2).Value = totalOps;
            wsSummary.Columns().AdjustToContents();
            if (isArabic) wsSummary.RightToLeft = true;

            // Sheet 2: Monthly Trend
            var wsMonthly = workbook.Worksheets.Add(isArabic ? "النشاط الشهري" : "Monthly Activity");
            var mHeaders = isArabic
                ? new[] { "التاريخ", "إنشاء", "تحديث", "فشل" }
                : new[] { "Date", "Created", "Updated", "Failed" };
            WriteHeaders(wsMonthly, mHeaders);

            for (int i = 0; i < monthlyData.Count; i++)
            {
                var d = monthlyData[i];
                var r = i + 2;
                wsMonthly.Cell(r, 1).Value = d.Date;
                wsMonthly.Cell(r, 2).Value = d.Created;
                wsMonthly.Cell(r, 3).Value = d.Updated;
                wsMonthly.Cell(r, 4).Value = d.Failed;
                AlternateRowColor(wsMonthly, r, i, mHeaders.Length);
            }
            wsMonthly.Columns().AdjustToContents();
            if (isArabic) wsMonthly.RightToLeft = true;

            // Sheet 3: Status Distribution
            var wsStatus = workbook.Worksheets.Add(isArabic ? "توزيع الحالات" : "Status Distribution");
            var sHeaders = isArabic ? new[] { "الحالة", "العدد" } : new[] { "Status", "Count" };
            WriteHeaders(wsStatus, sHeaders);
            for (int i = 0; i < statusDist.Count; i++)
            {
                wsStatus.Cell(i + 2, 1).Value = statusDist[i].Status ?? "Unknown";
                wsStatus.Cell(i + 2, 2).Value = statusDist[i].Count;
            }
            wsStatus.Columns().AdjustToContents();
            if (isArabic) wsStatus.RightToLeft = true;

            // Sheet 4: Top Errors
            var wsErrors = workbook.Worksheets.Add(isArabic ? "أكثر الأخطاء" : "Top Errors");
            var eHeaders = isArabic ? new[] { "#", "رسالة الخطأ", "التكرارات" } : new[] { "#", "Error Message", "Count" };
            WriteHeaders(wsErrors, eHeaders);
            for (int i = 0; i < topErrors.Count; i++)
            {
                wsErrors.Cell(i + 2, 1).Value = i + 1;
                wsErrors.Cell(i + 2, 2).Value = topErrors[i].Error ?? "";
                wsErrors.Cell(i + 2, 3).Value = topErrors[i].Count;
            }
            wsErrors.Columns().AdjustToContents();
            if (isArabic) wsErrors.RightToLeft = true;

            using var ms = new MemoryStream();
            workbook.SaveAs(ms);
            return ms.ToArray();
        }

        // ═══════════════════════════════════════
        // SHARED HELPERS
        // ═══════════════════════════════════════

        private static void WriteHeaders(IXLWorksheet ws, string[] headers)
        {
            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cell(1, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#6366f1");
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }
        }

        private static void StyleHeaderRow(IXLWorksheet ws, int colCount)
        {
            for (int i = 1; i <= colCount; i++)
            {
                var cell = ws.Cell(1, i);
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#6366f1");
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }
        }

        private static void AlternateRowColor(IXLWorksheet ws, int row, int index, int colCount)
        {
            if (index % 2 == 1)
            {
                for (int c = 1; c <= colCount; c++)
                    ws.Cell(row, c).Style.Fill.BackgroundColor = XLColor.FromHtml("#f8f9fa");
            }
        }

        private static void StyleStatusCell(IXLCell cell, string status)
        {
            var color = status switch
            {
                "Completed" or "Success" or "Active" or "Succeeded" => "#10b981",
                "Failed" or "Suspended" or "Error" => "#ef4444",
                "Running" or "Pending" or "InProgress" => "#3b82f6",
                "CompletedWithErrors" => "#f59e0b",
                _ => "#94a3b8"
            };
            cell.Style.Font.FontColor = XLColor.FromHtml(color);
            cell.Style.Font.Bold = true;
        }

        private static byte[] FinalizeWorkbook(XLWorkbook workbook, IXLWorksheet ws, bool isArabic)
        {
            ws.Columns().AdjustToContents();
            if (isArabic) ws.RightToLeft = true;

            using var ms = new MemoryStream();
            workbook.SaveAs(ms);
            return ms.ToArray();
        }
    }
}
