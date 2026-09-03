using System.Text.Json;

namespace IdentitySyncPro.Core.Models.Rules
{
    /// <summary>
    /// Stores a historical snapshot of a SyncRuleV2 for versioning and rollback.
    /// Each time a rule is modified, a version is saved here.
    /// </summary>
    public class SyncRuleVersion
    {
        public long Id { get; set; }
        public int SyncRuleV2Id { get; set; }
        public int VersionNumber { get; set; }

        /// <summary>Full JSON snapshot of the rule at this version</summary>
        public string SnapshotJson { get; set; } = "{}";

        /// <summary>Change notes describing what was modified</summary>
        public string? ChangeNotes { get; set; }

        public string ChangedBy { get; set; } = "System";
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

        /// <summary>Whether this version is the currently active one</summary>
        public bool IsCurrent { get; set; }

        // Navigation
        public SyncRuleV2? SyncRuleV2 { get; set; }
    }
}
