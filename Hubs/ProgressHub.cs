using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using TinyGenerator.Services;

namespace TinyGenerator.Hubs
{
    public class ProgressHub : Hub
    {
        private readonly ProgressService? _progressService;

        public ProgressHub(ProgressService? progressService = null)
        {
            _progressService = progressService;
        }

        public override async Task OnConnectedAsync()
        {
            if (_progressService != null)
            {
                var snapshot = _progressService.GetBusyModelsSnapshot();
                if (snapshot.Count > 0)
                {
                    await Clients.Caller.SendAsync("BusyModelsUpdated", snapshot);
                }
            }

            await base.OnConnectedAsync();
        }

        // Join a group corresponding to a generation id
        public Task JoinGroup(string genId)
        {
            if (string.IsNullOrWhiteSpace(genId)) return Task.CompletedTask;
            return Groups.AddToGroupAsync(Context.ConnectionId, genId);
        }

        public Task LeaveGroup(string genId)
        {
            if (string.IsNullOrWhiteSpace(genId)) return Task.CompletedTask;
            return Groups.RemoveFromGroupAsync(Context.ConnectionId, genId);
        }
    }
}
