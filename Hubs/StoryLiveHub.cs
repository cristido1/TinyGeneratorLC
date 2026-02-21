using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace TinyGenerator.Hubs;

public class StoryLiveHub : Hub
{
    public Task JoinGroup(string group)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return Task.CompletedTask;
        }

        return Groups.AddToGroupAsync(Context.ConnectionId, group);
    }

    public Task LeaveGroup(string group)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return Task.CompletedTask;
        }

        return Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
    }
}
