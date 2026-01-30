using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using TinyGenerator.Hubs;
using TinyGenerator.Models;

namespace TinyGenerator.Services
{
    /// <summary>
    /// Buffered async logger that writes LogEntry objects to the DatabaseService in batches.
    /// </summary>
    public class CustomLogger : ICustomLogger, IDisposable
    {
        private readonly DatabaseService _db;
        private readonly ConcurrentDictionary<string, List<string>> _store = new();
        private readonly ConcurrentDictionary<string, bool> _completed = new();
        private readonly ConcurrentDictionary<string, string?> _result = new();
        private readonly ConcurrentDictionary<string, int> _busyModels = new(StringComparer.OrdinalIgnoreCase);
        private readonly IHubContext<ProgressHub>? _hubContext;
        private readonly ConcurrentDictionary<string, AppEventDefinition> _eventDefinitions = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<LogEntry> _buffer = new();
        private readonly object _lock = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly Timer _timer;
        private readonly int _batchSize;
        private readonly TimeSpan _flushInterval;
        private readonly bool _logRequestResponse;
        private readonly bool _logToolResponses;
        private readonly bool _otherLogs;
        private bool _disposed;

        public CustomLogger(DatabaseService databaseService, CustomLoggerOptions options, IHubContext<ProgressHub>? hubContext = null)
        {
            _db = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
            _hubContext = hubContext;
            if (options == null) options = new CustomLoggerOptions();
            _batchSize = Math.Max(1, options.BatchSize);
            _flushInterval = TimeSpan.FromMilliseconds(Math.Max(100, options.FlushIntervalMs));
            _logRequestResponse = options.LogRequestResponse;
            _logToolResponses = options.LogToolResponses;
            _otherLogs = options.OtherLogs;
            LoadEventDefinitions();

            // Timer triggers periodic flush (best-effort)
            _timer = new Timer(async _ => await OnTimerAsync().ConfigureAwait(false), null, _flushInterval, _flushInterval);
        }

        public void Log(string level, string category, string message, string? exception = null, string? state = null, string? result = null)
        {
            LogWithChatText(level, category, message, null, exception, state, result, null);
        }

        private void LogWithChatText(string level, string category, string message, string? chatText = null, string? exception = null, string? state = null, string? result = null, int? explicitThreadId = null)
        {
            if (_disposed) return;

            // Broadcast to live monitor regardless of database logging setting
            BroadcastLiveLog(level, category, message);

            if (!ShouldPersist(category))
                return;

            var scope = LogScope.Current;

            // Derive a default Result if not provided, to track SUCCESS/FAILED consistently.
            // IMPORTANT: do not derive results for model request/response logs: their payloads may contain
            // words like "error"/"failed" as part of prompts or JSON, causing false FAILED flags.
            string? derivedResult = result;
            if (string.IsNullOrWhiteSpace(derivedResult))
            {
                var cat = (category ?? string.Empty).Trim();
                var isModelTraffic =
                    string.Equals(cat, "ModelPrompt", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cat, "ModelCompletion", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cat, "ModelRequest", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cat, "ModelResponse", StringComparison.OrdinalIgnoreCase);

                if (!isModelTraffic)
                {
                    var lvl = (level ?? string.Empty).Trim().ToLowerInvariant();
                    var msg = message ?? string.Empty;
                    var msgLower = msg.ToLowerInvariant();

                    // Mark failures on error/fatal.
                    if (lvl == "error" || lvl == "fatal")
                    {
                        derivedResult = "FAILED";
                    }
                    else
                    {
                        // Use word-boundary matching to reduce false positives (e.g. embedded substrings).
                        // Keep this lightweight: no heavy parsing.
                        try
                        {
                            if (System.Text.RegularExpressions.Regex.IsMatch(msgLower, @"\b(fail|failed|failure|error|errors|exception)\b"))
                            {
                                derivedResult = "FAILED";
                            }
                            else if (System.Text.RegularExpressions.Regex.IsMatch(msgLower, @"\b(success|successful|completed|passed)\b"))
                            {
                                derivedResult = "SUCCESS";
                            }
                        }
                        catch
                        {
                            // If regex fails, fall back to no derived result.
                        }
                    }
                }
            }

            // Use explicit threadId if provided; otherwise use the allocator-backed id from LogScope.
            // Fallback to managed thread id only when logging outside a CommandDispatcher scope.
            int effectiveThreadId = explicitThreadId
                ?? LogScope.CurrentThreadId
                ?? Environment.CurrentManagedThreadId;

            var entry = new LogEntry
            {
                Ts = DateTime.UtcNow.ToString("o"),
                Level = level ?? "Information",
                Category = category ?? string.Empty,
                Message = message ?? string.Empty,
                Exception = exception,
                State = state,
                ThreadId = effectiveThreadId,
                StoryId = LogScope.CurrentStoryId,
                ThreadScope = scope,
                AgentName = LogScope.CurrentAgentName,
                Context = null,
                ChatText = chatText,
                Result = string.IsNullOrWhiteSpace(derivedResult) ? null : derivedResult,
                ResultFailReason = null,
                Examined = false,
                StepNumber = LogScope.CurrentStepNumber,
                MaxStep = LogScope.CurrentMaxStep
            };

            bool shouldFlush = false;
            lock (_lock)
            {
                _buffer.Add(entry);
                if (_buffer.Count >= _batchSize) shouldFlush = true;
            }

            if (shouldFlush)
            {
                // fire-and-forget flush; if semaphore is busy, FlushAsync will return quickly and postpone
                _ = Task.Run(() => FlushAsync());
            }
        }

        /// <summary>
        /// Logs a model prompt (question to the AI model)
        /// </summary>
        public void LogPrompt(string modelName, string prompt)
        {
            if (!_logRequestResponse) return;
            // Don't truncate - save full prompt to DB for complete audit trail
            var displayPrompt = string.IsNullOrWhiteSpace(prompt) 
                ? "(empty prompt)" 
                : prompt;
            
            var message = $"[{modelName}] PROMPT: {displayPrompt}";
            var chatText = displayPrompt;
            LogWithChatText("Information", "ModelPrompt", message, chatText);
        }

        /// <summary>
        /// Logs a model response (answer from the AI model)
        /// </summary>
        public void LogResponse(string modelName, string response)
        {
            if (!_logRequestResponse) return;
            // Don't truncate - save full response to DB for complete audit trail
            var displayResponse = string.IsNullOrWhiteSpace(response) 
                ? "(empty response)" 
                : response;
            
            var message = $"[{modelName}] RESPONSE: {displayResponse}";
            var chatText = displayResponse;
            LogWithChatText("Information", "ModelCompletion", message, chatText, result: "SUCCESS");
        }

        /// <summary>
        /// Logs raw request JSON
        /// </summary>
        public void LogRequestJson(string modelName, string requestJson, int? threadId = null)
        {
            if (!_logRequestResponse) return;
            var message = $"[{modelName}] REQUEST_JSON: {requestJson}";
            
            // Extract the LAST message from the conversation for cleaner chat display
            // This allows following the conversation flow, showing feedback messages during retries
            string chatText;
            try
            {
                var json = System.Text.Json.JsonDocument.Parse(requestJson);
                var messages = json.RootElement.GetProperty("messages");
                var lastMessage = messages.EnumerateArray().LastOrDefault();
                
                if (lastMessage.ValueKind != System.Text.Json.JsonValueKind.Undefined)
                {
                    var content = lastMessage.TryGetProperty("content", out var contentProp) 
                        ? contentProp.GetString() 
                        : null;
                    chatText = content ?? requestJson;
                }
                else
                {
                    chatText = requestJson;
                }
            }
            catch
            {
                // Fallback to full JSON if parsing fails
                chatText = requestJson;
            }
            
            LogWithChatText("Information", "ModelRequest", message, chatText, null, null, null, threadId);
        }

        /// <summary>
        /// Logs raw response JSON
        /// </summary>
        public void LogResponseJson(string modelName, string responseJson, int? threadId = null)
        {
            if (!_logRequestResponse) return;
            var message = $"[{modelName}] RESPONSE_JSON: {responseJson}";
            
            // Extract assistant content or function calls for cleaner chat display
            string chatText;
            try
            {
                var json = System.Text.Json.JsonDocument.Parse(responseJson);
                if (json.RootElement.TryGetProperty("message", out var messageObj))
                {
                    var role = messageObj.TryGetProperty("role", out var roleProp) ? roleProp.GetString() : null;
                    // If it's a tool response and tool logging is disabled, skip persisting it
                    if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase) && !_logToolResponses)
                    {
                        return;
                    }

                    // If it's a tool response and logging is enabled, prefer the tool output
                    if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase) && messageObj.TryGetProperty("content", out var toolContent))
                    {
                        chatText = toolContent.GetString() ?? responseJson;
                        LogWithChatText("Information", "ModelResponse", message, chatText, null, null, "SUCCESS", threadId);
                        return;
                    }

                    // Prefer tool_calls summary if present
                    if (messageObj.TryGetProperty("tool_calls", out var tcProp) && tcProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        var calls = new List<string>();
                        foreach (var tc in tcProp.EnumerateArray())
                        {
                            var fn = tc.GetProperty("function").GetProperty("name").GetString() ?? "unknown_fn";
                            var args = tc.GetProperty("function").GetProperty("arguments").GetString() ?? "{}";
                            calls.Add($"{fn} {args}");
                        }
                        chatText = string.Join(" | ", calls);
                    }
                    else if (messageObj.TryGetProperty("content", out var contentProp))
                    {
                        chatText = contentProp.GetString() ?? responseJson;
                    }
                    else
                    {
                        chatText = responseJson;
                    }
                }
                else
                {
                    chatText = responseJson;
                }
            }
            catch
            {
                // Fallback to full JSON if parsing fails
                chatText = responseJson;
            }

            LogWithChatText("Information", "ModelResponse", message, chatText, null, null, "SUCCESS", threadId);
        }

        public void MarkLatestModelResponseResult(string result, string? failReason = null, bool? examined = null)
        {
            if (_disposed) return;

            var effectiveThreadId = LogScope.CurrentThreadId ?? Environment.CurrentManagedThreadId;
            if (effectiveThreadId <= 0) return;

            try
            {
                _db.UpdateLatestModelResponseResult(
                    effectiveThreadId,
                    result,
                    failReason,
                    examined ?? true);
            }
            catch
            {
                // best-effort
            }
        }

        private async Task OnTimerAsync()
        {
            try
            {
                await FlushAsync().ConfigureAwait(false);
            }
            catch
            {
                // swallow timer exceptions to avoid crashing
            }
        }

        public async Task FlushAsync()
        {
            if (_disposed) return;

            // Try acquire semaphore immediately; if not available, postpone
            if (!await _writeLock.WaitAsync(0).ConfigureAwait(false))
            {
                return;
            }

            List<LogEntry>? toWrite = null;
            try
            {
                lock (_lock)
                {
                    if (_buffer.Count == 0) return;
                    toWrite = _buffer.ToList();
                    _buffer.Clear();
                }

                // Write via DatabaseService in a single batch
                await _db.InsertLogsAsync(toWrite).ConfigureAwait(false);

                await BroadcastLogsAsync(toWrite).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // On failure, re-queue entries at the front to avoid data loss
                try
                {
                    if (toWrite != null && toWrite.Count > 0)
                    {
                        lock (_lock)
                        {
                            // prepend failed batch preserving order
                            _buffer.InsertRange(0, toWrite);
                        }
                    }
                }
                catch { }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _timer?.Change(Timeout.Infinite, Timeout.Infinite);
                _timer?.Dispose();
            }
            catch { }

            try
            {
                // Ensure final flush (wait synchronously)
                FlushAsync().GetAwaiter().GetResult();
            }
            catch { }

            _writeLock?.Dispose();
        }

        private void BroadcastLiveLog(string? level, string? category, string? message)
        {
            // Only forward categories that are useful to follow in real-time
            if (category != "PromptRendering" && category != "FunctionInvocation" && category != "ModelCompletion")
                return;

            var safeLevel = string.IsNullOrWhiteSpace(level) ? "Information" : level;
            var safeCategory = string.IsNullOrWhiteSpace(category) ? "Log" : category;
            var safeMessage = message ?? string.Empty;

            try
            {
                Append("live-logs", $"[{safeLevel}][{safeCategory}] {safeMessage}");
            }
            catch
            {
                // best-effort broadcast
            }
        }

        private bool ShouldPersist(string? category)
        {
            var cat = category ?? string.Empty;

            if (string.Equals(cat, "Command", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(cat, "ModelPrompt", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cat, "ModelCompletion", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cat, "ModelRequest", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cat, "ModelResponse", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cat, "llama.cpp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return _otherLogs;
        }

        private void LoadEventDefinitions()
        {
            try
            {
                var defs = _db.GetAppEventDefinitions();
                _eventDefinitions.Clear();
                foreach (var kvp in defs)
                {
                    _eventDefinitions[kvp.Key] = kvp.Value;
                }
            }
            catch
            {
                _eventDefinitions.Clear();
            }
        }

        private AppEventDefinition GetEventDefinition(string eventType)
        {
            if (string.IsNullOrWhiteSpace(eventType))
            {
                return new AppEventDefinition { Enabled = true, Logged = true, Notified = true, EventType = eventType ?? string.Empty };
            }

            if (_eventDefinitions.TryGetValue(eventType, out var definition))
            {
                return definition;
            }

            return new AppEventDefinition
            {
                EventType = eventType,
                Enabled = true,
                Logged = true,
                Notified = true,
                CreatedAt = DateTime.UtcNow.ToString("o"),
                UpdatedAt = DateTime.UtcNow.ToString("o")
            };
        }

        public Task PublishEventAsync(string eventType, string title, string message, string level = "information", string? group = null)
        {
            if (string.IsNullOrWhiteSpace(eventType)) return Task.CompletedTask;
            var definition = GetEventDefinition(eventType);
            if (!definition.Enabled) return Task.CompletedTask;

            if (definition.Logged)
            {
                Log(level, eventType, message);
            }

            if (definition.Notified)
            {
                if (string.IsNullOrWhiteSpace(group))
                {
                    return NotifyAllAsync(title, message, level);
                }
                return NotifyGroupAsync(group, title, message, level);
            }

            return Task.CompletedTask;
        }

        public Task NotifyAllAsync(string title, string message, string level = "info")
        {
            if (_hubContext == null) return Task.CompletedTask;
            try
            {
                var ts = DateTime.UtcNow.ToString("o");
                return _hubContext.Clients.All.SendAsync("AppNotification", new { title, message, level, ts });
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        public Task NotifyGroupAsync(string group, string title, string message, string level = "info")
        {
            if (_hubContext == null || string.IsNullOrWhiteSpace(group)) return Task.CompletedTask;
            try
            {
                var ts = DateTime.UtcNow.ToString("o");
                return _hubContext.Clients.Group(group).SendAsync("AppNotification", new { title, message, level, ts });
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        public void Start(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId)) return;
            _store[runId] = new List<string>();
            _completed[runId] = false;
            _result[runId] = null;
        }

        public async Task AppendAsync(string runId, string message, string? extraClass = null)
        {
            if (string.IsNullOrWhiteSpace(runId)) return;
            if (!_store.ContainsKey(runId)) Start(runId);
            _store.TryGetValue(runId, out var list);
            list?.Add(message);
            try
            {
                Console.WriteLine($"[Progress] {runId}: {message}");
            }
            catch { }

            if (_hubContext != null)
            {
                try
                {
                    await _hubContext.Clients.All.SendAsync("ProgressAppended", runId, message, extraClass).ConfigureAwait(false);
                }
                catch { }
            }
        }

        public void Append(string runId, string message, string? extraClass = null)
        {
            AppendAsync(runId, message, extraClass).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        public List<string> Get(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId)) return new List<string>();
            if (_store.TryGetValue(runId, out var list))
            {
                return new List<string>(list);
            }
            return new List<string>();
        }

        public async Task MarkCompletedAsync(string runId, string? finalResult = null)
        {
            if (string.IsNullOrWhiteSpace(runId)) return;
            _completed[runId] = true;
            _result[runId] = finalResult;

            try
            {
                Console.WriteLine($"[Progress] Completed {runId}: {finalResult}");
            }
            catch { }

            if (_hubContext != null)
            {
                try
                {
                    await _hubContext.Clients.All.SendAsync("ProgressCompleted", runId, finalResult).ConfigureAwait(false);
                }
                catch { }
            }
        }

        public void MarkCompleted(string runId, string? finalResult = null)
            => MarkCompletedAsync(runId, finalResult).ConfigureAwait(false).GetAwaiter().GetResult();

        public bool IsCompleted(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId)) return false;
            return _completed.TryGetValue(runId, out var completed) && completed;
        }

        public string? GetResult(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId)) return null;
            return _result.TryGetValue(runId, out var value) ? value : null;
        }

        public void Clear(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId)) return;
            _store.TryRemove(runId, out _);
            _completed.TryRemove(runId, out _);
            _result.TryRemove(runId, out _);
        }

        public async Task ShowAgentActivityAsync(string agentName, string status, string? agentId = null, string testType = "question")
        {
            if (_hubContext == null) return;
            try
            {
                var id = agentId ?? $"agent_{agentName}_{DateTime.UtcNow.Ticks}";
                await _hubContext.Clients.All.SendAsync("AgentActivityStarted", id, agentName, status, testType).ConfigureAwait(false);
            }
            catch { }
        }

        public void ShowAgentActivity(string agentName, string status, string? agentId = null, string testType = "question")
            => ShowAgentActivityAsync(agentName, status, agentId, testType).ConfigureAwait(false).GetAwaiter().GetResult();

        public async Task HideAgentActivityAsync(string agentId)
        {
            if (_hubContext == null || string.IsNullOrWhiteSpace(agentId)) return;
            try
            {
                await _hubContext.Clients.All.SendAsync("AgentActivityEnded", agentId).ConfigureAwait(false);
            }
            catch { }
        }

        public void HideAgentActivity(string agentId)
            => HideAgentActivityAsync(agentId).ConfigureAwait(false).GetAwaiter().GetResult();

        public async Task BroadcastLogsAsync(IEnumerable<LogEntry> entries)
        {
            if (_hubContext == null) return;
            try
            {
                await _hubContext.Clients.All.SendAsync("LogEntriesAppended", entries).ConfigureAwait(false);
            }
            catch { }
        }

        public async Task BroadcastStepProgress(Guid generationId, int current, int max, string stepDescription)
        {
            if (_hubContext == null) return;
            try
            {
                await _hubContext.Clients.All.SendAsync("StepProgress", generationId.ToString(), current, max, stepDescription).ConfigureAwait(false);
            }
            catch { }
        }

        public async Task BroadcastStepRetry(Guid generationId, int retryCount, string reason)
        {
            if (_hubContext == null) return;
            try
            {
                await _hubContext.Clients.All.SendAsync("StepRetry", generationId.ToString(), retryCount, reason).ConfigureAwait(false);
            }
            catch { }
        }

        public async Task BroadcastStepComplete(Guid generationId, int stepNumber)
        {
            if (_hubContext == null) return;
            try
            {
                await _hubContext.Clients.All.SendAsync("StepComplete", generationId.ToString(), stepNumber).ConfigureAwait(false);
            }
            catch { }
        }

        public async Task BroadcastTaskComplete(Guid generationId, string status)
        {
            if (_hubContext == null) return;
            try
            {
                await _hubContext.Clients.All.SendAsync("TaskComplete", generationId.ToString(), status).ConfigureAwait(false);
            }
            catch { }
        }

        public async Task ModelRequestStartedAsync(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return;
            _busyModels.AddOrUpdate(modelName, 1, (_, current) => current + 1);
            await BroadcastBusyModelsAsync().ConfigureAwait(false);
        }

        public void ModelRequestStarted(string modelName)
            => ModelRequestStartedAsync(modelName).ConfigureAwait(false).GetAwaiter().GetResult();

        public async Task ModelRequestFinishedAsync(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return;

            _busyModels.AddOrUpdate(modelName, 0, (_, current) =>
            {
                var next = current - 1;
                return next < 0 ? 0 : next;
            });

            if (_busyModels.TryGetValue(modelName, out var remaining) && remaining <= 0)
            {
                _busyModels.TryRemove(modelName, out _);
            }

            await BroadcastBusyModelsAsync().ConfigureAwait(false);
        }

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
            try
            {
                return _hubContext.Clients.All.SendAsync("BusyModelsUpdated", snapshot);
            }
            catch
            {
                return Task.CompletedTask;
            }
        }
    }
}
