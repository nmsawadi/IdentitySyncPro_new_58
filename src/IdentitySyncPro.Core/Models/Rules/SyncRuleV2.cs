using IdentitySyncPro.Core.Models.Settings;

namespace IdentitySyncPro.Core.Models.Rules
{
    /// <summary>
    /// Enhanced sync rule supporting all FIM/MIM rule types:
    /// - Join: Match source identity to existing AD object
    /// - Projection: Create new Metaverse entry from source
    /// - ImportFlow: Map attributes from source → Metaverse
    /// - ExportFlow: Map attributes from Metaverse → AD
    /// - Provisioning: When/where to create accounts in AD
    /// - Deprovisioning: When/how to disable/remove accounts
    /// </summary>
    public class SyncRuleV2
    {
        public int Id { get; set; }
        public int TenantId { get; set; }

        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool Enabled { get; set; } = true;

        /// <summary>Execution priority — lower = first (1-999)</summary>
        public int Priority { get; set; } = 100;

        /// <summary>
        /// Rule type:
        /// - Join: Match source → existing target
        /// - Projection: Create new Metaverse entry
        /// - ImportFlow: Source attribute → Metaverse attribute
        /// - ExportFlow: Metaverse attribute → Target attribute
        /// - Provisioning: Create account in target system
        /// - Deprovisioning: Remove/disable account in target
        /// </summary>
        public string RuleType { get; set; } = "ImportFlow";

        /// <summary>Direction: Inbound (Source→MV) or Outbound (MV→Target)</summary>
        public string Direction { get; set; } = "Inbound";

        /// <summary>Source system name (e.g., Oracle, HR_System)</summary>
        public string? SourceSystem { get; set; } = "Oracle";

        /// <summary>Target system name (e.g., ActiveDirectory)</summary>
        public string? TargetSystem { get; set; } = "ActiveDirectory";

        // ══════════════════════════════════════
        // CONDITION — when this rule fires
        // ══════════════════════════════════════

        /// <summary>Scope filter: which identities this rule applies to (e.g., IdentityType == Identity)</summary>
        public string? ScopeFilter { get; set; }

        /// <summary>JSON condition expression for advanced filtering</summary>
        public string? ConditionJson { get; set; }

        // ══════════════════════════════════════
        // CONFIGURATION — what this rule does
        // ══════════════════════════════════════

        /// <summary>
        /// JSON configuration — structure depends on RuleType:
        /// 
        /// ImportFlow/ExportFlow:
        /// { "mappings": [{ "source": "FIRST_NAME", "target": "givenName", "transform": "none" }] }
        /// 
        /// Join:
        /// { "joinAttribute": "sAMAccountName", "sourceAttribute": "IDENTITY_ID" }
        /// 
        /// Provisioning:
        /// { "targetOU": "OU=Identities,DC=...", "enableAccount": true, "setPassword": true }
        /// 
        /// Deprovisioning:
        /// { "action": "disable", "moveToOU": "OU=Disabled,DC=...", "removeGroups": true }
        /// </summary>
        public string ConfigurationJson { get; set; } = "{}";

        // ══════════════════════════════════════
        // METADATA
        // ══════════════════════════════════════

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; } = "System";

        // Navigation
        public TenantSettings? Tenant { get; set; }
        public List<SyncRuleFlowMapping> FlowMappings { get; set; } = new();
    }

    /// <summary>
    /// Individual attribute flow mapping within a rule.
    /// Defines how a single attribute moves between systems.
    /// </summary>
    public class SyncRuleFlowMapping
    {
        public int Id { get; set; }
        public int SyncRuleV2Id { get; set; }

        /// <summary>Source attribute name (e.g., FIRST_NAME, IDENTITY_ID)</summary>
        public string SourceAttribute { get; set; } = string.Empty;

        /// <summary>Target attribute name (e.g., givenName, sAMAccountName)</summary>
        public string TargetAttribute { get; set; } = string.Empty;

        /// <summary>
        /// Transform to apply:
        /// - none: direct copy
        /// - ToUpper / ToLower: case transform
        /// - Format:{template}: string format (e.g., "Format:{0}@example.com")
        /// - GetInitials: extract initials
        /// - Concat:{separator}: join multiple values
        /// - Expression:{code}: custom expression
        /// </summary>
        public string Transform { get; set; } = "none";

        /// <summary>Whether this attribute is required</summary>
        public bool IsRequired { get; set; }

        /// <summary>Default value if source is empty</summary>
        public string? DefaultValue { get; set; }

        /// <summary>Display order in the UI</summary>
        public int DisplayOrder { get; set; }

        // Navigation
        public SyncRuleV2? SyncRuleV2 { get; set; }
    }
}
