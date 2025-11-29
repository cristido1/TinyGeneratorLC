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
            _otherLogs = options.OtherLogs;

            // Timer triggers periodic flush (best-effort)
            _timer = new Timer(async _ => await OnTimerAsync().ConfigureAwait(false), null, _flushInterval, _flushInterval);
        }

        public void Log(string level, string category, string message, string? exception = null, string? state = null)
        {
            if (_disposed) return;

            // Broadcast to live monitor regardless of database logging setting
            BroadcastLiveLog(level, category, message);

            if (!ShouldPersist(category))
                return;

            var scope = LogScope.Current;

            var entry = new LogEntry
            {
                Ts = DateTime.UtcNow.ToString("o"),
                Level = level ?? "Information",
                Category = category ?? string.Empty,
                Message = message ?? string.Empty,
                Exception = exception,
                State = state,
                ThreadId = Environment.CurrentManagedThreadId,
                ThreadScope = scope,
                AgentName = null,
                Context = null
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
            Log("Information", "ModelPrompt", message);
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
            Log("Information", "ModelCompletion", message);
        }

        /// <summary>
        /// Logs raw request JSON
        /// </summary>
        public void LogRequestJson(string modelName, string requestJson)
        {
            if (!_logRequestResponse) return;
            var message = $"[{modelName}] REQUEST_JSON: {requestJson}";
            Log("Information", "ModelRequest", message);
        }

        /// <summary>
        /// Logs raw response JSON
        /// </summary>
        public void LogResponseJson(string modelName, string responseJson)
        {
            if (!_logRequestResponse) return;
            var message = $"[{modelName}] RESPONSE_JSON: {responseJson}";
            Log("Information", "ModelResponse", message);
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
