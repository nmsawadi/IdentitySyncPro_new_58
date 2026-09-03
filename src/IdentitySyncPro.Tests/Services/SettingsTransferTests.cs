using ClosedXML.Excel;
using IdentitySyncPro.Core.Models.Rules;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Services;
using IdentitySyncPro.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Settings import exists because moving rules by hand-written SQL produced two silent
    /// production faults: an operator lost in the round trip (an empty ConditionOperator makes
    /// a rule match EVERY identity) and an OU rule whose placeholders named no real column
    /// (resolving to "OU=DEFAULT", which does not exist). Import must refuse both.
    /// </summary>
    public class SettingsTransferTests
    {
        private const int TenantId = 1;

        private static SettingsTransferService Build(out Infrastructure.Data.AppDbContext db)
        {
            db = TestDbContext.Create();
            db.TenantSettings.Add(new TenantSettings
            {
                Id = TenantId, TenantName = "T", IsActive = true,
                ADUsername = "u", ADPassword = "p", ADBaseDN = "DC=test"
            });
            db.SaveChanges();
            return new SettingsTransferService(db, Mock.Of<ILogger<SettingsTransferService>>());
        }

        /// <summary>Builds a workbook in memory, one sheet at a time.</summary>
        private static MemoryStream Workbook(params (string Sheet, string[] Headers, object?[][] Rows)[] sheets)
        {
            using var wb = new XLWorkbook();
            foreach (var (name, headers, rows) in sheets)
            {
                var ws = wb.Worksheets.Add(name);
                for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
                for (int r = 0; r < rows.Length; r++)
                    for (int c = 0; c < rows[r].Length; c++)
                    {
                        var cell = ws.Cell(r + 2, c + 1);
                        if (rows[r][c] is string s) { cell.SetValue(s); cell.Style.NumberFormat.Format = "@"; }
                        else if (rows[r][c] is bool b) cell.Value = b;
                        else if (rows[r][c] is int i) cell.Value = i;
                    }
            }
            var ms = new MemoryStream();
            wb.SaveAs(ms);
            ms.Position = 0;
            return ms;
        }

        private static readonly string[] MappingHeaders =
            { "SourceColumn", "TargetAttribute", "Transform", "DefaultValue", "IsRequired", "IsIdentifier", "SortOrder", "Condition" };
        private static readonly string[] LifecycleHeaders =
            { "Name", "Description", "Enabled", "Priority", "TriggerType", "ConditionField", "ConditionOperator", "ConditionValue", "ActionType", "ActionValue", "GracePeriodDays" };
        private static readonly string[] OuHeaders =
            { "OUTemplate", "Priority", "ConditionField", "ConditionOperator", "ConditionValue", "ValueMappings", "Description" };

        private static object?[] ValidMappingRow() =>
            new object?[] { "STUDENT_ID", "sAMAccountName", null, null, true, true, 0, null };

        [Fact]
        public async Task ExportThenImport_PreservesTheComparisonOperator()
        {
            // The round trip that used to lose "==": Excel treats a leading '=' as a formula.
            var svc = Build(out var db);
            db.LifecycleRules.Add(new LifecycleRule
            {
                TenantId = TenantId, Name = "Activate", Priority = 10, TriggerType = "OnImport",
                ConditionField = "STATUS_CODE", ConditionOperator = "==", ConditionValue = "1",
                ActionType = "SetState", ActionValue = "Active"
            });
            db.TenantAttributeMappings.Add(new TenantAttributeMapping
            {
                TenantId = TenantId, SourceColumn = "STUDENT_ID",
                TargetAttribute = "sAMAccountName", IsIdentifier = true
            });
            await db.SaveChangesAsync();

            var bytes = await svc.ExportAsync(TenantId);

            using var stream = new MemoryStream(bytes);
            var (preview, parsed) = await svc.PreviewAsync(stream, TenantId);

            Assert.Empty(preview.Errors);
            Assert.Equal("==", parsed!.LifecycleRules!.Single().ConditionOperator);
        }

        [Fact]
        public async Task MissingOperator_IsRejected()
        {
            // An empty operator makes EvaluateCondition return true — the rule matches everyone.
            var svc = Build(out _);
            using var file = Workbook(
                ("AttributeMappings", MappingHeaders, new[] { ValidMappingRow() }),
                ("LifecycleRules", LifecycleHeaders, new[] {
                    new object?[] { "Bad rule", null, true, 10, "OnImport", "STATUS_CODE", null, "1", "SetState", "Active", null }
                }));

            var (preview, _) = await svc.PreviewAsync(file, TenantId);

            Assert.False(preview.CanApply);
            Assert.Contains(preview.Errors, e => e.Contains("ConditionOperator"));
        }

        [Fact]
        public async Task UnknownOperator_IsRejected()
        {
            var svc = Build(out _);
            using var file = Workbook(
                ("AttributeMappings", MappingHeaders, new[] { ValidMappingRow() }),
                ("LifecycleRules", LifecycleHeaders, new[] {
                    new object?[] { "Bad op", null, true, 10, "OnImport", "STATUS_CODE", ">=", "1", "SetState", "Active", null }
                }));

            var (preview, _) = await svc.PreviewAsync(file, TenantId);

            Assert.False(preview.CanApply);
            Assert.Contains(preview.Errors, e => e.Contains(">="));
        }

        [Fact]
        public async Task OuRule_WithUnknownPlaceholder_IsRejected()
        {
            // The exact production fault: {GENDER} against a column named GENDER_CODE.
            var svc = Build(out _);
            using var file = Workbook(
                ("AttributeMappings", MappingHeaders, new[] {
                    ValidMappingRow(),
                    new object?[] { "GENDER_CODE", "info", null, null, false, false, 1, null }
                }),
                ("OURules", OuHeaders, new[] {
                    new object?[] { "OU={GENDER},{BaseDN}", 1, null, null, null, null, null }
                }));

            var (preview, _) = await svc.PreviewAsync(file, TenantId);

            Assert.False(preview.CanApply);
            Assert.Contains(preview.Errors, e => e.Contains("{GENDER}"));
        }

        [Fact]
        public async Task OuRule_WithMalformedValueMappings_IsRejected()
        {
            var svc = Build(out _);
            using var file = Workbook(
                ("AttributeMappings", MappingHeaders, new[] { ValidMappingRow() }),
                ("OURules", OuHeaders, new[] {
                    new object?[] { "OU={STUDENT_ID},{BaseDN}", 1, null, null, null, "{broken json", null }
                }));

            var (preview, _) = await svc.PreviewAsync(file, TenantId);

            Assert.False(preview.CanApply);
            Assert.Contains(preview.Errors, e => e.Contains("JSON"));
        }

        [Fact]
        public async Task MissingIdentifierMapping_IsRejected()
        {
            // Without an identifier there is no account name to sync to.
            var svc = Build(out _);
            using var file = Workbook(("AttributeMappings", MappingHeaders, new[] {
                new object?[] { "STUDENT_ID", "employeeID", null, null, false, false, 0, null }
            }));

            var (preview, _) = await svc.PreviewAsync(file, TenantId);

            Assert.False(preview.CanApply);
            Assert.Contains(preview.Errors, e => e.Contains("IsIdentifier"));
        }

        [Fact]
        public async Task TheStringNULL_BecomesNull_NotFourLetters()
        {
            // SQL exports write empty columns as the text "NULL".
            var svc = Build(out _);
            using var file = Workbook(
                ("AttributeMappings", MappingHeaders, new[] { ValidMappingRow() }),
                ("LifecycleRules", LifecycleHeaders, new[] {
                    new object?[] { "R", "NULL", true, 10, "OnImport", "STATUS_CODE", "==", "1", "SetState", "Active", "NULL" }
                }));

            var (preview, parsed) = await svc.PreviewAsync(file, TenantId);

            Assert.Empty(preview.Errors);
            var rule = parsed!.LifecycleRules!.Single();
            Assert.Null(rule.Description);
            Assert.Null(rule.GracePeriodDays);
        }

        [Fact]
        public async Task AbsentSheet_LeavesThatSectionUntouched()
        {
            // A file with only lifecycle rules must not wipe the attribute mappings.
            var svc = Build(out var db);
            db.TenantAttributeMappings.Add(new TenantAttributeMapping
            {
                TenantId = TenantId, SourceColumn = "STUDENT_ID",
                TargetAttribute = "sAMAccountName", IsIdentifier = true
            });
            await db.SaveChangesAsync();

            using var file = Workbook(("LifecycleRules", LifecycleHeaders, new[] {
                new object?[] { "R", null, true, 10, "OnImport", "STATUS_CODE", "==", "1", "SetState", "Active", null }
            }));

            var (preview, parsed) = await svc.PreviewAsync(file, TenantId);
            Assert.True(preview.CanApply);
            Assert.Null(parsed!.Mappings);          // null = untouched, not "delete all"

            await svc.ApplyAsync(parsed, TenantId);

            Assert.Single(db.TenantAttributeMappings.Where(m => m.TenantId == TenantId));
            Assert.Single(db.LifecycleRules.Where(l => l.TenantId == TenantId));
        }

        [Fact]
        public async Task Apply_ReplacesOnlyTheTargetTenant()
        {
            var svc = Build(out var db);
            db.TenantSettings.Add(new TenantSettings
            {
                Id = 2, TenantName = "Other", IsActive = true,
                ADUsername = "u", ADPassword = "p", ADBaseDN = "DC=other"
            });
            db.LifecycleRules.Add(new LifecycleRule
            {
                TenantId = 2, Name = "Other tenant rule", Priority = 10,
                TriggerType = "OnImport", ActionType = "SetState", ActionValue = "Active"
            });
            await db.SaveChangesAsync();

            using var file = Workbook(("LifecycleRules", LifecycleHeaders, new[] {
                new object?[] { "New", null, true, 10, "OnImport", "STATUS_CODE", "==", "1", "SetState", "Active", null }
            }));
            var (_, parsed) = await svc.PreviewAsync(file, TenantId);
            await svc.ApplyAsync(parsed!, TenantId);

            Assert.Single(db.LifecycleRules.Where(l => l.TenantId == 2));
            Assert.Equal("Other tenant rule", db.LifecycleRules.First(l => l.TenantId == 2).Name);
        }

        [Fact]
        public async Task GraceInName_WithoutGraceDays_WarnsButDoesNotBlock()
        {
            var svc = Build(out _);
            using var file = Workbook(
                ("AttributeMappings", MappingHeaders, new[] { ValidMappingRow() }),
                ("LifecycleRules", LifecycleHeaders, new[] {
                    new object?[] { "تعليق (30 يوم سماح)", null, true, 20, "OnImport", "STATUS_CODE", "==", "4", "SetState", "Suspended", null }
                }));

            var (preview, _) = await svc.PreviewAsync(file, TenantId);

            Assert.True(preview.CanApply);
            Assert.Contains(preview.Warnings, w => w.Contains("سماح"));
        }

        [Fact]
        public async Task NonWorkbookFile_IsReportedNotThrown()
        {
            var svc = Build(out _);
            using var junk = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("this is not a workbook"));

            var (preview, parsed) = await svc.PreviewAsync(junk, TenantId);

            Assert.False(preview.CanApply);
            Assert.Null(parsed);
            Assert.NotEmpty(preview.Errors);
        }

        [Fact]
        public async Task ColumnUsedOnlyAsACondition_CountsAsKnown()
        {
            // Regression guard for a false positive found against real tenant data: CITY_NO drove
            // the group and OU rules but was never mapped to an AD attribute (CITY_DESC was).
            // Validating against mapped columns alone rejected a correct configuration.
            var svc = Build(out _);
            using var file = Workbook(
                ("AttributeMappings", MappingHeaders, new[] {
                    ValidMappingRow(),
                    new object?[] { "CITY_DESC", "l", null, null, false, false, 1, null }
                }),
                ("GroupRules", new[] { "GroupName", "GroupDN", "IsDefault", "ConditionField", "ConditionOperator", "ConditionValue", "Description" },
                 new[] { new object?[] { "Site-Group", null, false, "CITY_NO", "==", "14", null } }),
                ("OURules", OuHeaders, new[] {
                    new object?[] { "OU={CITY_NO},{BaseDN}", 1, null, null, null,
                                    "{\"CITY_NO\":{\"14\":\"NAJRAN\"}}", null }
                }));

            var (preview, _) = await svc.PreviewAsync(file, TenantId);

            Assert.True(preview.CanApply, string.Join(" | ", preview.Errors));
        }

        [Fact]
        public async Task ErrorInOneSheet_DoesNotStopOtherSheetsBeingParsed()
        {
            // The early-return guard used to test the running total of errors, so a problem in an
            // earlier sheet made later ones report zero rows — the preview then understated what
            // the import would do, which is worse than the original error.
            var svc = Build(out _);
            using var file = Workbook(
                ("AttributeMappings", MappingHeaders, new[] { ValidMappingRow() }),
                ("GroupRules", new[] { "GroupName", "GroupDN", "IsDefault", "ConditionField", "ConditionOperator", "ConditionValue", "Description" },
                 new[] { new object?[] { "G", null, false, "CITY_NO", null, "14", null } }),   // broken: no operator
                ("LifecycleRules", LifecycleHeaders, new[] {
                    new object?[] { "R1", null, true, 10, "OnImport", "STATUS_CODE", "==", "1", "SetState", "Active", null },
                    new object?[] { "R2", null, true, 20, "OnImport", "STATUS_CODE", "==", "4", "SetState", "Suspended", null }
                }));

            var (preview, _) = await svc.PreviewAsync(file, TenantId);

            Assert.False(preview.CanApply);   // the group rule is still rejected
            var lifecycle = preview.Sections.Single(s => s.Name == "LifecycleRules");
            Assert.Equal(2, lifecycle.IncomingRows);   // ...but this sheet was still counted
        }

        [Fact]
        public async Task WorkbookWithNoRecognisedSheets_IsReported()
        {
            var svc = Build(out _);
            using var file = Workbook(("SomethingElse", new[] { "A" }, new[] { new object?[] { "x" } }));

            var (preview, _) = await svc.PreviewAsync(file, TenantId);

            Assert.False(preview.CanApply);
            Assert.Contains(preview.Errors, e => e.Contains("AttributeMappings"));
        }
    }
}
