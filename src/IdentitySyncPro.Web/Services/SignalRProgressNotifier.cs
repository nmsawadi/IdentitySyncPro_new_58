using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Sync;
using IdentitySyncPro.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace IdentitySyncPro.Web.Services
{
    /// <summary>
    /// SignalR-based implementation of ISyncProgressNotifier.
    /// Broadcasts sync progress to all connected SignalR clients in the SyncMonitor group.
    /// </summary>
    public class SignalRProgressNotifier : ISyncProgressNotifier
    {
        private readonly IHubContext<SyncHub> _hubContext;

        public SignalRProgressNotifier(IHubContext<SyncHub> hubContext)
        {
            _hubContext = hubContext;
        }

        public async Task NotifyProgressAsync(SyncProgressInfo progress)
        {
            await SyncHub.BroadcastProgress(_hubContext, progress);
        }
    }
}
