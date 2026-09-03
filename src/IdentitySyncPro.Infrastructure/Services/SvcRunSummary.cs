using System.Text;
using IdentitySyncPro.Core.Models.Services;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Builds the one audit entry every run writes, whatever else it did.
    ///
    /// Why it exists: most AD services record an entry only for accounts they ACTED on. A run that
    /// scanned five thousand accounts and changed none — which is what every re-run does once the
    /// eligible accounts are handled — wrote nothing at all, so opening that run's details showed
    /// an empty page indistinguishable from a service that had never run.
    ///
    /// The summary makes a run self-describing: what it examined, what it changed, and why it
    /// passed over the rest.
    /// </summary>
    public static class SvcRunSummary
    {
        public const string ActionName = "RunSummary";

        // The audit columns are bounded (Action 50, KeyValue 200, AttributeName 200,
        // Old/NewValue 2000). Exceeding one throws on SaveChanges at the very end of a run,
        // which would lose the run's final status — so every field is clamped here rather than
        // trusted to be short.
        private const int AttributeNameMax = 200;
        private const int ValueMax = 2000;

        /// <summary>Skip reasons, in the order they should be reported.</summary>
        public sealed class Reasons
        {
            private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
            private readonly List<string> _order = new();

            public void Add(string reason)
            {
                if (string.IsNullOrWhiteSpace(reason)) return;
                if (!_counts.ContainsKey(reason)) _order.Add(reason);
                _counts[reason] = _counts.TryGetValue(reason, out var n) ? n + 1 : 1;
            }

            public int Total => _counts.Values.Sum();
            public bool Any => _counts.Count > 0;

            public IEnumerable<(string Reason, int Count)> Ordered =>
                _order.OrderByDescending(r => _counts[r]).Select(r => (r, _counts[r]));
        }

        /// <summary>
        /// Composes the summary entry.
        /// </summary>
        /// <param name="actedOn">
        /// One line per account the service acted on. Listed in full while they fit; the rest are
        /// replaced by a count, because a truncated list that does not say it was truncated reads
        /// as the complete set.
        /// </param>
        public static SvcAuditEntry Build(
            SvcRunLog runLog,
            int serviceId,
            Reasons? skipReasons = null,
            IEnumerable<string>? actedOn = null,
            string? note = null)
        {
            var counters =
                $"scanned={runLog.TotalRecords} · acted={runLog.UpdatedRecords} · " +
                $"skipped={runLog.SkippedRecords} · failed={runLog.FailedRecords}";

            return new SvcAuditEntry
            {
                SvcRunLogId = runLog.Id,
                SvcServiceId = serviceId,
                Timestamp = DateTime.UtcNow,
                Action = ActionName,
                KeyValue = $"run #{runLog.Id}",
                AttributeName = Clamp(counters, AttributeNameMax),
                OldValue = Clamp(FormatReasons(skipReasons, note), ValueMax),
                NewValue = Clamp(FormatActedOn(actedOn), ValueMax)
            };
        }

        private static string FormatReasons(Reasons? reasons, string? note)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(note))
                sb.Append(note!.Trim()).Append('\n');

            if (reasons is { Any: true })
            {
                sb.Append("Skipped:\n");
                foreach (var (reason, count) in reasons.Ordered)
                    sb.Append("  • ").Append(count).Append(" × ").Append(reason).Append('\n');
            }

            return sb.Length == 0 ? "" : sb.ToString().TrimEnd('\n');
        }

        private static string FormatActedOn(IEnumerable<string>? actedOn)
        {
            var lines = actedOn?.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            if (lines == null || lines.Count == 0) return "";

            var sb = new StringBuilder();
            int written = 0;

            foreach (var line in lines)
            {
                var candidate = line.Trim();
                // Leave room for the "and N more" note so the truncation is always announced.
                if (sb.Length + candidate.Length + 1 > ValueMax - 40) break;
                sb.Append(candidate).Append('\n');
                written++;
            }

            if (written < lines.Count)
                sb.Append($"… and {lines.Count - written} more (see the entries above)");

            return sb.ToString().TrimEnd('\n');
        }

        private static string? Clamp(string? value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= max ? value : value[..max];
        }
    }
}
