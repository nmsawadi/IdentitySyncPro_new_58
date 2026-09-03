using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Models.Audit;

namespace IdentitySyncPro.Core.Interfaces
{
    /// <summary>
    /// Service for comprehensive audit trail logging.
    /// </summary>
    public interface IAuditService
    {
        /// <summary>
        /// Writes an audit entry. Leave <paramref name="performedBy"/> null and the signed-in user
        /// is filled in automatically — callers cannot forget to record who acted.
        /// </summary>
        Task LogAsync(string action, string category, AuditSeverity severity = AuditSeverity.Info,
            string? entityType = null, string? entityId = null,
            string? oldValues = null, string? newValues = null,
            string? details = null, string? performedBy = null,
            string? correlationId = null, string? ipAddress = null);

        /// <param name="performedBy">
        /// Partial, case-insensitive match on the acting user — the filter that answers
        /// "what did this person do".
        /// </param>
        Task<IEnumerable<AuditEntry>> GetEntriesAsync(DateTime? from = null, DateTime? to = null,
            AuditSeverity? severity = null, string? category = null,
            int page = 1, int pageSize = 50, string? performedBy = null);

        Task<int> GetEntryCountAsync(DateTime? from = null, DateTime? to = null,
            AuditSeverity? severity = null, string? category = null, string? performedBy = null);

        /// <summary>Distinct actors seen in the log, for the filter list.</summary>
        Task<IEnumerable<string>> GetActorsAsync();
    }
}
