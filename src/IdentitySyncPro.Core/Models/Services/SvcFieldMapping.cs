namespace IdentitySyncPro.Core.Models.Services
{
    /// <summary>
    /// Maps a source database column to an AD attribute.
    /// Example: EMPLOYEE_ID → extensionAttribute2, DEPARTMENT → department
    /// </summary>
    public class SvcFieldMapping
    {
        public int Id { get; set; }
        public int SvcServiceId { get; set; }

        /// <summary>Column name in the source view/table</summary>
        public string SourceColumn { get; set; } = string.Empty;

        /// <summary>AD attribute name to update</summary>
        public string TargetAttribute { get; set; } = string.Empty;

        /// <summary>If true, this mapping is used only for searching (key), not for updating</summary>
        public bool IsKeyMapping { get; set; } = false;

        /// <summary>Display order in the UI</summary>
        public int SortOrder { get; set; }

        // Navigation
        public SvcService Service { get; set; } = null!;
    }
}
