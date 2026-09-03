namespace IdentitySyncPro.Core.Models.Metaverse
{
    /// <summary>
    /// Records every change that happens to a Metaverse identity.
    /// Provides a complete audit trail for lifecycle transitions and attribute changes.
    /// </summary>
    public class MetaverseHistory
    {
        public long Id { get; set; }
        public int MetaverseEntryId { get; set; }

        /// <summary>Type of change: Import, Export, StateChange, AttributeChange, Move, Provision, Deprovision</summary>
        public string ChangeType { get; set; } = string.Empty;

        /// <summary>Previous lifecycle state (for StateChange events)</summary>
        public string? OldState { get; set; }

        /// <summary>New lifecycle state (for StateChange events)</summary>
        public string? NewState { get; set; }

        /// <summary>JSON: attributes that changed</summary>
        public string? ChangedAttributesJson { get; set; }

        /// <summary>What triggered this change: rule name, manual action, or system</summary>
        public string? TriggeredBy { get; set; }

        /// <summary>Additional details about the change</summary>
        public string? Details { get; set; }

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        // Navigation
        public MetaverseEntry? MetaverseEntry { get; set; }
    }
}
