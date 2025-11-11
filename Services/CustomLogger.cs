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
        private readonly List<LogEntry> _buffer = new();
        private readonly object _lock = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly Timer _timer;
        private readonly int _batchSize;
        private readonly TimeSpan _flushInterval;
        private bool _disposed;

        public CustomLogger(DatabaseService databaseService, CustomLoggerOptions options)
        {
            _db = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
            if (options == null) options = new CustomLoggerOptions();
            _batchSize = Math.Max(1, options.BatchSize);
            _flushInterval = TimeSpan.FromMilliseconds(Math.Max(100, options.FlushIntervalMs));

            // Timer triggers periodic flush (best-effort)
            _timer = new Timer(async _ => await OnTimerAsync().ConfigureAwait(false), null, _flushInterval, _flushInterval);
        }

        public void Log(string level, string category, string message, string? exception = null, string? state = null)
        {
            if (_disposed) return;

            var entry = new LogEntry
            {
                Ts = DateTime.UtcNow.ToString("o"),
                Level = level ?? "Information",
                Category = category ?? string.Empty,
                Message = message ?? string.Empty,
                Exception = exception,
                State = state,
                ThreadId = Environment.CurrentManagedThreadId,
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
    }
}
