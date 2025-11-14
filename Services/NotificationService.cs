using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using TinyGenerator.Hubs;

namespace TinyGenerator.Services
{
    public sealed class NotificationService
    {
        private readonly IHubContext<ProgressHub>? _hubContext;

        public NotificationService(IHubContext<ProgressHub>? hubContext = null)
        {
            _hubContext = hubContext;
        }

        // Notify all connected clients
        public async Task NotifyAllAsync(string title, string message, string level = "info")
        {
            try
            {
                if (_hubContext != null)
                {
                    var ts = DateTime.UtcNow.ToString("o");
                    await _hubContext.Clients.All.SendAsync("AppNotification", new { title, message, level, ts });
                }
            }
            catch
            {
                // best-effort, swallow exceptions to avoid cascading failures
            }
        }

        // Notify only clients in a group (e.g. genId)
        public async Task NotifyGroupAsync(string group, string title, string message, string level = "info")
        {
            try
            {
                if (_hubContext != null)
                {
                    var ts = DateTime.UtcNow.ToString("o");
                    await _hubContext.Clients.Group(group).SendAsync("AppNotification", new { title, message, level, ts });
                }
            }
            catch
            {
            }
        }
    }
}
