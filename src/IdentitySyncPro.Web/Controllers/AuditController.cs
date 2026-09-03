using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace IdentitySyncPro.Web.Controllers
{
    public class AuditController : Controller
    {
        private readonly IAuditService _auditService;

        public AuditController(IAuditService auditService)
        {
            _auditService = auditService;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? category, string? severity, DateTime? dateFrom = null,
            DateTime? dateTo = null, int page = 1, string? performedBy = null)
        {
            AuditSeverity? severityEnum = null;
            if (!string.IsNullOrEmpty(severity) && Enum.TryParse<AuditSeverity>(severity, out var s))
                severityEnum = s;

            var entries = await _auditService.GetEntriesAsync(
                from: dateFrom,
                to: dateTo?.AddDays(1), // Include the full day
                category: category,
                severity: severityEnum,
                page: page,
                pageSize: 30,
                performedBy: performedBy);

            ViewBag.TotalCount = await _auditService.GetEntryCountAsync(
                from: dateFrom, to: dateTo?.AddDays(1), category: category,
                severity: severityEnum, performedBy: performedBy);
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = (int)Math.Ceiling((double)ViewBag.TotalCount / 30);
            ViewBag.Category = category;
            ViewBag.Severity = severity;
            ViewBag.PerformedBy = performedBy;
            ViewBag.Actors = await _auditService.GetActorsAsync();
            ViewBag.DateFrom = dateFrom?.ToString("yyyy-MM-dd");
            ViewBag.DateTo = dateTo?.ToString("yyyy-MM-dd");

            return View(entries);
        }
    }
}
