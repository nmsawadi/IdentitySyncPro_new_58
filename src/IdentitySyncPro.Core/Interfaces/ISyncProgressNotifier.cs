using IdentitySyncPro.Core.Models.Sync;

namespace IdentitySyncPro.Core.Interfaces
{
    /// <summary>
    /// Abstraction for broadcasting sync progress to external consumers (e.g. SignalR).
    /// Allows SyncEngine (in Infrastructure) to notify UI without referencing Web project.
    /// </summary>
    public interface ISyncProgressNotifier
    {
        Task NotifyProgressAsync(SyncProgressInfo progress);
    }
}
