using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using TinyGenerator.Hubs;

namespace TinyGenerator.Services
{
    // ProgressService persists progress in-memory and broadcasts updates via SignalR so
    // clients can reconnect and receive both historical and live updates for a generation id.
    public sealed class ProgressService
    {
        private readonly ConcurrentDictionary<string, List<string>> _store = new();
        private readonly ConcurrentDictionary<string, bool> _completed = new();
        private readonly ConcurrentDictionary<string, string?> _result = new();
        private readonly IHubContext<ProgressHub>? _hubContext;

        public ProgressService(IHubContext<ProgressHub>? hubContext = null)
        {
            _hubContext = hubContext;
        }

        public void Start(string id)
        {
            _store[id] = new List<string>();
            _completed[id] = false;
            _result[id] = null;
        }

        public async Task AppendAsync(string id, string message)
        {
            if (!_store.ContainsKey(id)) Start(id);
            _store[id].Add(message);
            // Broadcast to connected clients in the group for this id (best-effort)
            try
            {
                if (_hubContext != null)
                {
                    await _hubContext.Clients.Group(id).SendAsync("ProgressAppended", id, message);
                }
            }
            catch { }
        }

        // backward-compatible synchronous wrapper
        public void Append(string id, string message) => AppendAsync(id, message).ConfigureAwait(false).GetAwaiter().GetResult();

        public List<string> Get(string id)
        {
            if (_store.TryGetValue(id, out var list)) return new List<string>(list);
            return new List<string>();
        }

        public async Task MarkCompletedAsync(string id, string? finalResult = null)
        {
            _completed[id] = true;
            _result[id] = finalResult;
            try
            {
                if (_hubContext != null)
                {
                    await _hubContext.Clients.Group(id).SendAsync("ProgressCompleted", id, finalResult);
                }
            }
            catch { }
        }

        // backward-compatible synchronous wrapper
        public void MarkCompleted(string id, string? finalResult = null) => MarkCompletedAsync(id, finalResult).ConfigureAwait(false).GetAwaiter().GetResult();

        public bool IsCompleted(string id) => _completed.TryGetValue(id, out var v) && v;

        public string? GetResult(string id) => _result.TryGetValue(id, out var r) ? r : null;

        public void Clear(string id)
        {
            _store.TryRemove(id, out _);
            _completed.TryRemove(id, out _);
            _result.TryRemove(id, out _);
        }
    }
}
