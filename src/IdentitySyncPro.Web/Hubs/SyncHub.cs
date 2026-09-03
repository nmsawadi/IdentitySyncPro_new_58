using Microsoft.AspNetCore.SignalR;

namespace IdentitySyncPro.Web.Hubs
{
    /// <summary>
    /// SignalR hub for real-time sync progress monitoring.
    /// Clients can connect to receive live updates during sync operations.
    /// </summary>
    public class SyncHub : Hub
    {
        public async Task JoinSyncMonitor()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, "SyncMonitor");
            await Clients.Caller.SendAsync("Connected", "Connected to sync monitor");
        }

        public async Task LeaveSyncMonitor()
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, "SyncMonitor");
        }

        /// <summary>
        /// Join a service-specific progress monitor group.
        /// </summary>
        public async Task JoinSvcMonitor(int serviceId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"SvcMonitor-{serviceId}");
            await Clients.Caller.SendAsync("Connected", $"Connected to service monitor {serviceId}");
        }

        /// <summary>
        /// Leave a service-specific progress monitor group.
        /// </summary>
        public async Task LeaveSvcMonitor(int serviceId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"SvcMonitor-{serviceId}");
        }

        /// <summary>
        /// Broadcast sync progress to all connected clients.
        /// Called by the sync engine during execution.
        /// </summary>
        public static async Task BroadcastProgress(IHubContext<SyncHub> hubContext, object progress)
        {
            await hubContext.Clients.Group("SyncMonitor").SendAsync("SyncProgress", progress);
        }

        /// <summary>
        /// Broadcast service execution progress to clients monitoring a specific service.
        /// Called by SvcSyncExecutor / SvcOffboardingExecutor during execution.
        /// </summary>
        public static async Task BroadcastSvcProgress(IHubContext<SyncHub> hubContext, int serviceId, object progress)
        {
            await hubContext.Clients.Group($"SvcMonitor-{serviceId}").SendAsync("SvcProgress", progress);
        }

        public static async Task BroadcastNotification(IHubContext<SyncHub> hubContext, string type, string message)
        {
            await hubContext.Clients.All.SendAsync("Notification", new { type, message, timestamp = DateTime.UtcNow });
        }
    }
}
