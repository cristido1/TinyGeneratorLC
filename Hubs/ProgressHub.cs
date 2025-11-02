using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace TinyGenerator.Hubs
{
    public class ProgressHub : Hub
    {
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
