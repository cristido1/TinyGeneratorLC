using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using TinyGenerator.Services;

namespace TinyGenerator.Hubs
{
    public class ProgressHub : Hub
    {
        private readonly ProgressService? _progressService;
        private readonly ICommandDispatcher? _dispatcher;

        public ProgressHub(ProgressService? progressService = null, ICommandDispatcher? dispatcher = null)
        {
            _progressService = progressService;
            _dispatcher = dispatcher;
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

            if (_dispatcher != null)
            {
                var commands = _dispatcher.GetActiveCommands();
                if (commands?.Count > 0)
                {
                    await Clients.Caller.SendAsync("CommandListUpdated", commands);
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
