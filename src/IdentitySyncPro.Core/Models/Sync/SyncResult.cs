namespace IdentitySyncPro.Core.Models.Sync
{
    /// <summary>
    /// Result of a single identity sync operation.
    /// </summary>
    public class SyncResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string? ChangedFields { get; set; }
        public int DurationMs { get; set; }

        public static SyncResult Succeeded(int durationMs = 0, string? changedFields = null)
        {
            return new SyncResult
            {
                Success = true,
                DurationMs = durationMs,
                ChangedFields = changedFields
            };
        }

        public static SyncResult NoChange(int durationMs = 0)
        {
            return new SyncResult
            {
                Success = true,
                DurationMs = durationMs,
                ChangedFields = "NoChanges"
            };
        }

        public static SyncResult Failure(string error, int durationMs = 0)
        {
            return new SyncResult
            {
                Success = false,
                Error = error,
                DurationMs = durationMs
            };
        }
    }
}
