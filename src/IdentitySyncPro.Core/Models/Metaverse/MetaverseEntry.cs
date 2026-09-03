using System.Text.Json;

namespace IdentitySyncPro.Core.Models.Metaverse
{
    /// <summary>
    /// Central identity store — consolidates all identity data from all sources.
    /// This is the "single source of truth" for each identity in the system.
    /// 
    /// Lifecycle states: Pending → Active → Suspended → Deprovisioned
    /// </summary>
    public class MetaverseEntry
    {
        public int Id { get; set; }

        /// <summary>The tenant (source) this identity was imported from.</summary>
        public int TenantId { get; set; }

        /// <summary>External identifier from source system (IDENTITY_ID)</summary>
        public string ExternalId { get; set; } = string.Empty;

        /// <summary>Identity type: User, Identity, Employee, etc.</summary>
        public string IdentityType { get; set; } = "User";

        // ══════════════════════════════════════
        // LIFECYCLE STATE
        // ══════════════════════════════════════

        /// <summary>Current lifecycle state: Pending, Active, Suspended, Deprovisioned</summary>
        public string LifecycleState { get; set; } = "Pending";

        /// <summary>Previous lifecycle state before the last transition</summary>
        public string? PreviousState { get; set; }

        /// <summary>When the lifecycle state last changed</summary>
        public DateTime StateChangedDate { get; set; } = DateTime.UtcNow;

        /// <summary>Original status code from source system (STATUSE_CODE)</summary>
        public int SourceStatusCode { get; set; }

        /// <summary>Status description from source system</summary>
        public string? SourceStatusDesc { get; set; }

        // ══════════════════════════════════════
        // CONSOLIDATED ATTRIBUTES
        // ══════════════════════════════════════

        /// <summary>JSON: all merged attributes from all sources</summary>
        public string AttributesJson { get; set; } = "{}";

        /// <summary>JSON: which source systems contributed data</summary>
        public string? SourceSystemsJson { get; set; }

        /// <summary>JSON: which target systems have this identity provisioned</summary>
        public string? ProvisionedTargetsJson { get; set; }

        // ══════════════════════════════════════
        // AD-SPECIFIC TRACKING
        // ══════════════════════════════════════

        /// <summary>Full Distinguished Name in AD</summary>
        public string? ADDistinguishedName { get; set; }

        /// <summary>AD Object GUID for permanent reference</summary>
        public string? ADObjectGuid { get; set; }

        /// <summary>Whether the AD account is currently enabled</summary>
        public bool ADAccountEnabled { get; set; }

        /// <summary>Current OU path in AD</summary>
        public string? ADCurrentOU { get; set; }

        // ══════════════════════════════════════
        // CHANGE DETECTION
        // ══════════════════════════════════════

        /// <summary>SHA256 hash of current attributes for change detection</summary>
        public string CurrentHash { get; set; } = string.Empty;

        /// <summary>Previous hash before the last import</summary>
        public string? PreviousHash { get; set; }

        /// <summary>Flag: set to true when import detects a change that requires rule re-evaluation</summary>
        public bool NeedsRuleEval { get; set; }

        // ══════════════════════════════════════
        // TIMESTAMPS
        // ══════════════════════════════════════

        /// <summary>When this identity was first seen in any source</summary>
        public DateTime FirstSeenDate { get; set; } = DateTime.UtcNow;

        /// <summary>Last time data was imported from source</summary>
        public DateTime LastImportDate { get; set; } = DateTime.UtcNow;

        /// <summary>Last time data was exported to a target</summary>
        public DateTime? LastExportDate { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;

        // ══════════════════════════════════════
        // NAVIGATION
        // ══════════════════════════════════════

        public List<MetaverseHistory> History { get; set; } = new();

        // ══════════════════════════════════════
        // HELPER METHODS
        // ══════════════════════════════════════

        /// <summary>
        /// Gets consolidated attributes as a typed dictionary.
        /// </summary>
        public Dictionary<string, object?> GetAttributes()
        {
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, object?>>(AttributesJson)
                       ?? new Dictionary<string, object?>();
            }
            catch
            {
                return new Dictionary<string, object?>();
            }
        }

        /// <summary>
        /// Sets consolidated attributes from a dictionary.
        /// </summary>
        public void SetAttributes(Dictionary<string, object?> attributes)
        {
            AttributesJson = JsonSerializer.Serialize(attributes, new JsonSerializerOptions
            {
                WriteIndented = false
            });
        }

        /// <summary>
        /// Gets a specific attribute value.
        /// </summary>
        public string? GetAttribute(string key)
        {
            var attrs = GetAttributes();
            return attrs.TryGetValue(key, out var val) ? val?.ToString() : null;
        }
    }
}
