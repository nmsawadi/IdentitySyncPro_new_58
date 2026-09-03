namespace IdentitySyncPro.Core.Models.Rules
{
    /// <summary>
    /// Defines an attribute mapping rule between source and target systems.
    /// </summary>
    public class SyncRule
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool Enabled { get; set; } = true;
        public int Priority { get; set; } = 100;
        public string RuleType { get; set; } = "AttributeMapping"; // AttributeMapping, OUPlacement, GroupAssignment
        public string? Condition { get; set; } // JSON condition expression
        public string Configuration { get; set; } = "{}"; // JSON rule configuration
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Defines mapping between a source attribute and a target attribute.
    /// </summary>
    public class AttributeMapping
    {
        public int Id { get; set; }
        public int SyncRuleId { get; set; }
        public string SourceAttribute { get; set; } = string.Empty;
        public string TargetAttribute { get; set; } = string.Empty;
        public string? TransformExpression { get; set; } // e.g., "GetInitials", "ToUpper", "Format:{0}@example.com"
        public bool IsRequired { get; set; } = false;
        public string? DefaultValue { get; set; }

        // Navigation
        public SyncRule? SyncRule { get; set; }
    }
}
