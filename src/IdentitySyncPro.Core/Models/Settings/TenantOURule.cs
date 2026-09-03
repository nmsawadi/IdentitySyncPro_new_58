namespace IdentitySyncPro.Core.Models.Settings
{
    /// <summary>
    /// Defines OU placement rules for a tenant.
    /// Determines which OU a identity is placed in based on conditions (city, gender, etc.)
    /// </summary>
    public class TenantOURule
    {
        public int Id { get; set; }
        public int TenantId { get; set; }

        /// <summary>
        /// OU template with placeholders from source columns.
        /// Example: OU={GENDER},OU={CITY},{BaseDN}
        /// Placeholders are replaced at runtime from identity data.
        /// Special placeholders: {BaseDN} → tenant's ADBaseDN
        /// </summary>
        public string OUTemplate { get; set; } = string.Empty;

        /// <summary>Priority — lower number = higher priority (first match wins)</summary>
        public int Priority { get; set; }

        /// <summary>Source column to evaluate for this rule</summary>
        public string? ConditionField { get; set; }

        /// <summary>Operator: ==, !=, in, not_in</summary>
        public string? ConditionOperator { get; set; }

        /// <summary>Value to compare against</summary>
        public string? ConditionValue { get; set; }

        /// <summary>
        /// Value mappings for placeholders: JSON object.
        /// Example: {"DEPARTMENT":{"1":"SITE-A","2":"SITE-B"}, "TYPE":{"1":"GroupA","2":"GroupB"}}
        /// </summary>
        public string? ValueMappings { get; set; }

        /// <summary>Description for display</summary>
        public string? Description { get; set; }

        // Navigation
        public TenantSettings? Tenant { get; set; }
    }
}
