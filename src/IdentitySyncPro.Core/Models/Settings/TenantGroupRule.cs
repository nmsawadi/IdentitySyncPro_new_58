namespace IdentitySyncPro.Core.Models.Settings
{
    /// <summary>
    /// Defines an AD group membership rule for a tenant.
    /// Identities matching the condition are added to the specified group.
    /// </summary>
    public class TenantGroupRule
    {
        public int Id { get; set; }
        public int TenantId { get; set; }

        /// <summary>AD group name (e.g., All-Users-Group)</summary>
        public string GroupName { get; set; } = string.Empty;

        /// <summary>Full DN of the group in AD (optional, resolved at runtime if empty)</summary>
        public string? GroupDN { get; set; }

        /// <summary>If true, ALL identities join this group regardless of conditions</summary>
        public bool IsDefault { get; set; }

        /// <summary>Source column to evaluate (e.g., LOCATION_CODE, DEPARTMENT_CODE, STATUS_CODE)</summary>
        public string? ConditionField { get; set; }

        /// <summary>Operator: ==, !=, in, not_in, gt, lt</summary>
        public string? ConditionOperator { get; set; }

        /// <summary>Value to compare against. For 'in': comma-separated (e.g., "1,2")</summary>
        public string? ConditionValue { get; set; }

        /// <summary>Description for display</summary>
        public string? Description { get; set; }

        // Navigation
        public TenantSettings? Tenant { get; set; }
    }
}
