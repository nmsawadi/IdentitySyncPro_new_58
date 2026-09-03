namespace IdentitySyncPro.Core.Interfaces
{
    /// <summary>
    /// Interface for broadcasting service execution progress in real-time.
    /// Implemented by SignalR notifier in the Web layer.
    /// </summary>
    public interface ISvcProgressNotifier
    {
        /// <summary>
        /// Broadcast progress update for a specific service execution.
        /// </summary>
        Task NotifyProgressAsync(int serviceId, object progressData);
    }
}
