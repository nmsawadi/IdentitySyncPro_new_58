using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Audit;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Comprehensive audit trail service for all system operations.
    /// </summary>
    public class AuditService : IAuditService
    {
        private readonly AppDbContext _db;
        private readonly ILogger<AuditService> _logger;
        private readonly ICurrentActor? _actor;

        /// <summary>Recorded when no person is driving the operation — a job, a schedule, startup.</summary>
        public const string SystemActor = "System";

        public AuditService(AppDbContext db, ILogger<AuditService> logger, ICurrentActor? actor = null)
        {
            _db = db;
            _logger = logger;
            _actor = actor;
        }

        public async Task LogAsync(string action, string category, AuditSeverity severity = AuditSeverity.Info,
            string? entityType = null, string? entityId = null,
            string? oldValues = null, string? newValues = null,
            string? details = null, string? performedBy = null,
            string? correlationId = null, string? ipAddress = null)
        {
            try
            {
                var entry = new AuditEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Severity = severity,
                    Category = category,
                    Action = action,
                    EntityType = entityType,
                    EntityId = entityId,
                    OldValues = oldValues,
                    NewValues = newValues,
                    Details = details,
                    // An explicit name still wins — a few callers name a subject other than the
                    // signed-in user. Everything else is attributed automatically, because a trail
                    // that depends on 52 call sites remembering is a trail of "System".
                    PerformedBy = FirstNonBlank(performedBy, _actor?.Username) ?? SystemActor,
                    IpAddress = FirstNonBlank(ipAddress, _actor?.IpAddress),
                    CorrelationId = correlationId
                };

                _db.AuditEntries.Add(entry);
                await _db.SaveChangesAsync();

                if (severity >= AuditSeverity.Warning)
                {
                    _logger.LogWarning("[AUDIT] [{CorrelationId}] {Category}/{Action}: {Details}",
                        correlationId ?? "N/A", category, action, details);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write audit entry: {Action}", action);
            }
        }

        private static string? FirstNonBlank(string? preferred, string? fallback) =>
            !string.IsNullOrWhiteSpace(preferred) ? preferred
            : !string.IsNullOrWhiteSpace(fallback) ? fallback
            : null;

        public async Task<IEnumerable<AuditEntry>> GetEntriesAsync(DateTime? from = null, DateTime? to = null,
            AuditSeverity? severity = null, string? category = null,
            int page = 1, int pageSize = 50, string? performedBy = null)
        {
            return await Filtered(from, to, severity, category, performedBy)
                .OrderByDescending(e => e.Timestamp)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();
        }

        public async Task<int> GetEntryCountAsync(DateTime? from = null, DateTime? to = null,
            AuditSeverity? severity = null, string? category = null, string? performedBy = null)
            => await Filtered(from, to, severity, category, performedBy).CountAsync();

        public async Task<IEnumerable<string>> GetActorsAsync() =>
            await _db.AuditEntries
                .Where(e => e.PerformedBy != null && e.PerformedBy != "")
                .Select(e => e.PerformedBy!)
                .Distinct()
                .OrderBy(p => p)
                .ToListAsync();

        /// <summary>
        /// The one place the filters are expressed. The list and the count used to build their own
        /// copies, which is how a screen ends up reporting a total that does not match its rows.
        /// </summary>
        private IQueryable<AuditEntry> Filtered(DateTime? from, DateTime? to,
            AuditSeverity? severity, string? category, string? performedBy)
        {
            var query = _db.AuditEntries.AsQueryable();

            if (from.HasValue) query = query.Where(e => e.Timestamp >= from.Value);
            if (to.HasValue) query = query.Where(e => e.Timestamp <= to.Value);
            if (severity.HasValue) query = query.Where(e => e.Severity == severity.Value);
            if (!string.IsNullOrEmpty(category)) query = query.Where(e => e.Category == category);
            if (!string.IsNullOrWhiteSpace(performedBy))
                query = query.Where(e => e.PerformedBy != null && e.PerformedBy.Contains(performedBy));

            return query;
        }
    }
}
