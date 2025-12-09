using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Models;

namespace TinyGenerator.Services
{
    /// <summary>
    /// Buffered async logger that writes LogEntry objects to the DatabaseService in batches.
    /// </summary>
    public class CustomLogger : ICustomLogger, IDisposable
    {
        private readonly DatabaseService _db;
        private readonly ProgressService? _progress;
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

        public CustomLogger(DatabaseService databaseService, CustomLoggerOptions options, ProgressService? progressService = null)
        {
            _db = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
            _progress = progressService;
            if (options == null) options = new CustomLoggerOptions();
            _batchSize = Math.Max(1, options.BatchSize);
            _flushInterval = TimeSpan.FromMilliseconds(Math.Max(100, options.FlushIntervalMs));
            _logRequestResponse = options.LogRequestResponse;
            _logToolResponses = options.LogToolResponses;
            _otherLogs = options.OtherLogs;

            // Timer triggers periodic flush (best-effort)
            _timer = new Timer(async _ => await OnTimerAsync().ConfigureAwait(false), null, _flushInterval, _flushInterval);
        }

        public void Log(string level, string category, string message, string? exception = null, string? state = null, string? result = null)
        {
            LogWithChatText(level, category, message, null, exception, state, result);
        }

        private void LogWithChatText(string level, string category, string message, string? chatText = null, string? exception = null, string? state = null, string? result = null)
        {
            if (_disposed) return;

            // Broadcast to live monitor regardless of database logging setting
            BroadcastLiveLog(level, category, message);

            if (!ShouldPersist(category))
                return;

            var scope = LogScope.Current;

            // Derive a default Result if not provided, to track SUCCESS/FAILED consistently
            string? derivedResult = result;
            if (string.IsNullOrWhiteSpace(derivedResult))
            {
                var lvl = (level ?? string.Empty).Trim().ToLowerInvariant();
                var msg = message?.ToLowerInvariant() ?? string.Empty;

                // Mark failures on error/fatal or messages that clearly indicate failure
                if (lvl == "error" || lvl == "fatal" || msg.Contains("fail") || msg.Contains("error"))
                {
                    derivedResult = "FAILED";
                }
                // Mark success on messages that clearly indicate completion/passed
                else if (msg.Contains("success") || msg.Contains("completed") || msg.Contains("passed"))
                {
                    derivedResult = "SUCCESS";
                }
            }

            var entry = new LogEntry
            {
                Ts = DateTime.UtcNow.ToString("o"),
                Level = level ?? "Information",
                Category = category ?? string.Empty,
                Message = message ?? string.Empty,
                Exception = exception,
                State = state,
                ThreadId = (int)(LogScope.CurrentOperationId ?? Environment.CurrentManagedThreadId),
                ThreadScope = scope,
                AgentName = null,
                Context = null,
                ChatText = chatText,
                Result = string.IsNullOrWhiteSpace(derivedResult) ? null : derivedResult
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
            LogWithChatText("Information", "ModelCompletion", message, chatText);
        }

        /// <summary>
        /// Logs raw request JSON
        /// </summary>
        public void LogRequestJson(string modelName, string requestJson)
        {
            if (!_logRequestResponse) return;
            var message = $"[{modelName}] REQUEST_JSON: {requestJson}";
            
            // Extract user content from JSON for cleaner chat display
            string chatText;
            try
            {
                var json = System.Text.Json.JsonDocument.Parse(requestJson);
                var messages = json.RootElement.GetProperty("messages");
                var userContent = messages.EnumerateArray()
                    .FirstOrDefault(m => m.GetProperty("role").GetString() == "user")
                    .GetProperty("content").GetString();
                
                chatText = userContent ?? requestJson;
            }
            catch
            {
                // Fallback to full JSON if parsing fails
                chatText = requestJson;
            }
            
            LogWithChatText("Information", "ModelRequest", message, chatText);
        }

        /// <summary>
        /// Logs raw response JSON
        /// </summary>
        public void LogResponseJson(string modelName, string responseJson)
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
                        LogWithChatText("Information", "ModelResponse", message, chatText);
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

            LogWithChatText("Information", "ModelResponse", message, chatText);
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

                if (_progress != null)
                {
                    await _progress.BroadcastLogsAsync(toWrite).ConfigureAwait(false);
                }
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
            if (_progress == null) return;

            // Only forward categories that are useful to follow in real-time
            if (category != "PromptRendering" && category != "FunctionInvocation" && category != "ModelCompletion")
                return;

            var safeLevel = string.IsNullOrWhiteSpace(level) ? "Information" : level;
            var safeCategory = string.IsNullOrWhiteSpace(category) ? "Log" : category;
            var safeMessage = message ?? string.Empty;

            try
            {
                _progress.Append("live-logs", $"[{safeLevel}][{safeCategory}] {safeMessage}");
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
                string.Equals(cat, "ModelResponse", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return _otherLogs;
        }
    }
}
