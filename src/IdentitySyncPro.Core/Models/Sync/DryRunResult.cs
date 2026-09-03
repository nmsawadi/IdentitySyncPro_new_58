namespace IdentitySyncPro.Core.Models.Sync
{
    /// <summary>
    /// Result of a Dry Run sync — preview of what would happen without executing.
    /// </summary>
    public class DryRunResult
    {
        public int TotalRecords { get; set; }
        public int WouldCreate { get; set; }
        public int WouldUpdate { get; set; }
        public int WouldMove { get; set; }
        public int WouldSkip { get; set; }
        public int WouldFail { get; set; }
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public string RunType { get; set; } = "Full";
        public TimeSpan Duration { get; set; }

        public List<DryRunEntry> Entries { get; set; } = new();
    }

    /// <summary>
    /// Single identity preview in a Dry Run.
    /// </summary>
    public class DryRunEntry
    {
        public int IdentityId { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty; // Create, Update, Move, Skip
        public string? CurrentOU { get; set; }
        public string? TargetOU { get; set; }
        public List<string> ChangedAttributes { get; set; } = new();
        public List<string> GroupsToAdd { get; set; } = new();
        public string? Reason { get; set; }
    }
}
