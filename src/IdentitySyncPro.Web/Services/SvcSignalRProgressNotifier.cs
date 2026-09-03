using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace IdentitySyncPro.Web.Services
{
    /// <summary>
    /// SignalR-based implementation of ISvcProgressNotifier.
    /// Broadcasts service execution progress to clients monitoring a specific service.
    /// </summary>
    public class SvcSignalRProgressNotifier : ISvcProgressNotifier
    {
        private readonly IHubContext<SyncHub> _hubContext;

        public SvcSignalRProgressNotifier(IHubContext<SyncHub> hubContext)
        {
            _hubContext = hubContext;
        }

        public async Task NotifyProgressAsync(int serviceId, object progressData)
        {
            await SyncHub.BroadcastSvcProgress(_hubContext, serviceId, progressData);
        }
    }
}
