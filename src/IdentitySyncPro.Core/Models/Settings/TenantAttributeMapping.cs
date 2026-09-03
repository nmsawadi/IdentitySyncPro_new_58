namespace IdentitySyncPro.Core.Models.Settings
{
    /// <summary>
    /// Maps a source database column to an Active Directory attribute for a specific tenant.
    /// Supports transforms (Format, ToUpper, GetInitials) and conditional application.
    /// </summary>
    public class TenantAttributeMapping
    {
        public int Id { get; set; }
        public int TenantId { get; set; }

        /// <summary>Source DB column name (e.g., IDENTITY_ID, FIRST_NAME)</summary>
        public string SourceColumn { get; set; } = string.Empty;

        /// <summary>Target AD attribute (e.g., sAMAccountName, givenName, sn, mail)</summary>
        public string TargetAttribute { get; set; } = string.Empty;

        /// <summary>
        /// Optional transform expression:
        /// - Format:{0}@example.com → formats the value
        /// - ToUpper / ToLower     → case transform
        /// - GetInitials           → first letter only
        /// - Concat:{FirstName} {LastName} → combine multiple columns
        /// - Map:1=ValueA,2=ValueB → value mapping
        /// </summary>
        public string? Transform { get; set; }

        /// <summary>Default value if source is null/empty</summary>
        public string? DefaultValue { get; set; }

        /// <summary>Whether this field is required for sync to proceed</summary>
        public bool IsRequired { get; set; }

        /// <summary>Whether this is the unique identifier field (e.g., IDENTITY_ID → sAMAccountName)</summary>
        public bool IsIdentifier { get; set; }

        /// <summary>Display order in the mapping UI</summary>
        public int SortOrder { get; set; }

        /// <summary>
        /// Optional JSON condition for when this mapping applies.
        /// Example: {"field":"CATEGORY_CODE","op":"==","value":"1"}
        /// If null, mapping always applies.
        /// </summary>
        public string? Condition { get; set; }

        // Navigation
        public TenantSettings? Tenant { get; set; }
    }
}
