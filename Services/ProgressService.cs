using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using TinyGenerator.Hubs;
using TinyGenerator.Models;

namespace TinyGenerator.Services
{
    // ProgressService persists progress in-memory and broadcasts updates via SignalR so
    // clients can reconnect and receive both historical and live updates for a generation id.
    public sealed class ProgressService
    {
        private readonly ConcurrentDictionary<string, List<string>> _store = new();
        private readonly ConcurrentDictionary<string, bool> _completed = new();
        private readonly ConcurrentDictionary<string, string?> _result = new();
        private readonly ConcurrentDictionary<string, int> _busyModels = new(StringComparer.OrdinalIgnoreCase);
        private readonly IHubContext<ProgressHub>? _hubContext;
        private readonly Microsoft.Extensions.Logging.ILogger<ProgressService>? _logger;

        public ProgressService(IHubContext<ProgressHub>? hubContext = null, Microsoft.Extensions.Logging.ILogger<ProgressService>? logger = null)
        {
            _hubContext = hubContext;
            _logger = logger;
        }

        public void Start(string id)
        {
            _store[id] = new List<string>();
            _completed[id] = false;
            _result[id] = null;
        }

        public async Task AppendAsync(string id, string message, string? extraClass = null)
        {
            if (!_store.ContainsKey(id)) Start(id);
            _store[id].Add(message);
            try
            {
                if (_logger != null) _logger.LogInformation("ProgressAppend [{RunId}] {Message}", id, message);
                else Console.WriteLine($"[ProgressService] Append {id}: {message}");
            }
            catch { }
            // Broadcast to connected clients in the group for this id (best-effort)
            try
            {
                if (_hubContext != null)
                {
                    // Broadcast to all clients so progress messages appear on any page
                    await _hubContext.Clients.All.SendAsync("ProgressAppended", id, message, extraClass);
                }
            }
            catch { }
        }

        // backward-compatible synchronous wrapper
        public void Append(string id, string message, string? extraClass = null) => AppendAsync(id, message, extraClass).ConfigureAwait(false).GetAwaiter().GetResult();

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
                if (_logger != null) _logger.LogInformation("ProgressCompleted [{RunId}] {Result}", id, finalResult);
                else Console.WriteLine($"[ProgressService] Completed {id}: {finalResult}");

                if (_hubContext != null)
                {
                    // Broadcast to all clients so completion messages appear on any page
                    await _hubContext.Clients.All.SendAsync("ProgressCompleted", id, finalResult);
                }
            }
            catch (Exception ex)
            {
                try { _logger?.LogWarning(ex, "Failed broadcasting ProgressCompleted for {RunId}", id); } catch { }
            }
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

        // Agent activity tracking
        public async Task ShowAgentActivityAsync(string agentName, string status, string? agentId = null, string testType = "question")
        {
            try
            {
                if (_hubContext != null)
                {
                    var id = agentId ?? $"agent_{agentName}_{DateTime.UtcNow.Ticks}";
                    await _hubContext.Clients.All.SendAsync("AgentActivityStarted", id, agentName, status, testType);
                }
            }
            catch (Exception ex)
            {
                try { _logger?.LogWarning(ex, "Failed broadcasting AgentActivityStarted for {AgentName}", agentName); } catch { }
            }
        }

        public void ShowAgentActivity(string agentName, string status, string? agentId = null, string testType = "question") 
            => ShowAgentActivityAsync(agentName, status, agentId, testType).ConfigureAwait(false).GetAwaiter().GetResult();

        public async Task HideAgentActivityAsync(string agentId)
        {
            try
            {
                if (_hubContext != null)
                {
                    await _hubContext.Clients.All.SendAsync("AgentActivityEnded", agentId);
                }
            }
            catch (Exception ex)
            {
                try { _logger?.LogWarning(ex, "Failed broadcasting AgentActivityEnded for {AgentId}", agentId); } catch { }
            }
        }

        public void HideAgentActivity(string agentId) 
            => HideAgentActivityAsync(agentId).ConfigureAwait(false).GetAwaiter().GetResult();

        public async Task BroadcastLogsAsync(IEnumerable<LogEntry> entries)
        {
            if (_hubContext == null) return;
            try
            {
                await _hubContext.Clients.All.SendAsync("LogEntriesAppended", entries);
            }
            catch (Exception ex)
            {
                try { _logger?.LogWarning(ex, "Failed broadcasting LogEntriesAppended"); } catch { }
            }
        }

        public void BroadcastLogs(IEnumerable<LogEntry> entries)
            => BroadcastLogsAsync(entries).ConfigureAwait(false).GetAwaiter().GetResult();

        public Task ModelRequestStartedAsync(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return Task.CompletedTask;
            _busyModels.AddOrUpdate(modelName, 1, (_, current) => current + 1);
            return BroadcastBusyModelsAsync();
        }

        public Task ModelRequestFinishedAsync(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return Task.CompletedTask;

            _busyModels.AddOrUpdate(modelName, 0, (_, current) =>
            {
                var next = current - 1;
                return next < 0 ? 0 : next;
            });

            if (_busyModels.TryGetValue(modelName, out var remaining) && remaining <= 0)
            {
                _busyModels.TryRemove(modelName, out _);
            }

            return BroadcastBusyModelsAsync();
        }

        public void ModelRequestStarted(string modelName)
            => ModelRequestStartedAsync(modelName).ConfigureAwait(false).GetAwaiter().GetResult();

        public void ModelRequestFinished(string modelName)
            => ModelRequestFinishedAsync(modelName).ConfigureAwait(false).GetAwaiter().GetResult();

        public IReadOnlyList<string> GetBusyModelsSnapshot()
        {
            return _busyModels
                .Where(kvp => kvp.Value > 0)
                .Select(kvp => kvp.Key)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private Task BroadcastBusyModelsAsync()
        {
            if (_hubContext == null) return Task.CompletedTask;
            var snapshot = GetBusyModelsSnapshot();
            return _hubContext.Clients.All.SendAsync("BusyModelsUpdated", snapshot);
        }
    }
}
