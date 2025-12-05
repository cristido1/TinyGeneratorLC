using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace TinyGenerator.Services
{
    public sealed class CommandDispatcherOptions
    {
        public int MaxParallelCommands { get; set; } = 2;
    }

    internal sealed class CommandWorkItem
    {
        public string RunId { get; }
        public string OperationName { get; }
        public string ThreadScope { get; }
        public IReadOnlyDictionary<string, string>? Metadata { get; }
        public Func<CommandContext, Task<CommandResult>> Handler { get; }
        public TaskCompletionSource<CommandResult> Completion { get; }
        public long OperationNumber { get; }

        public CommandWorkItem(
            string runId,
            string operationName,
            string threadScope,
            IReadOnlyDictionary<string, string>? metadata,
            Func<CommandContext, Task<CommandResult>> handler,
            long operationNumber)
        {
            RunId = runId;
            OperationName = operationName;
            ThreadScope = threadScope;
            Metadata = metadata;
            Handler = handler;
            OperationNumber = operationNumber;
            Completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public sealed class CommandDispatcher : IHostedService, ICommandDispatcher, IDisposable
    {
        private readonly Channel<CommandWorkItem> _queue;
        private readonly int _parallelism;
        private readonly ICustomLogger? _logger;
        private readonly List<Task> _workers = new();
        private CancellationTokenSource? _cts;
        private long _counter;
        private bool _disposed;
        private readonly ConcurrentDictionary<string, CommandState> _active = new(StringComparer.OrdinalIgnoreCase);

        public CommandDispatcher(IOptions<CommandDispatcherOptions>? options, ICustomLogger? logger = null)
        {
            _parallelism = Math.Max(1, options?.Value?.MaxParallelCommands ?? 2);
            _logger = logger;
            _queue = Channel.CreateUnbounded<CommandWorkItem>(new UnboundedChannelOptions
            {
                SingleWriter = false,
                SingleReader = false
            });
        }

        public CommandHandle Enqueue(
            string operationName,
            Func<CommandContext, Task<CommandResult>> handler,
            string? runId = null,
            string? threadScope = null,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (_disposed) throw new ObjectDisposedException(nameof(CommandDispatcher));

            var op = string.IsNullOrWhiteSpace(operationName) ? "command" : operationName.Trim();
            var safeScope = string.IsNullOrWhiteSpace(threadScope) ? op : threadScope.Trim();
            var id = string.IsNullOrWhiteSpace(runId)
                ? $"{op}_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Interlocked.Increment(ref _counter)}"
                : runId.Trim();

            var opNumber = LogScope.GenerateOperationId();
            var workItem = new CommandWorkItem(id, op, safeScope, metadata, handler, opNumber);

            if (!_queue.Writer.TryWrite(workItem))
            {
                throw new InvalidOperationException("La coda comandi non accetta nuovi elementi.");
            }

            var state = new CommandState
            {
                RunId = id,
                OperationName = op,
                ThreadScope = safeScope,
                Metadata = metadata,
                Status = "queued",
                EnqueuedAt = DateTimeOffset.UtcNow
            };
            _active[id] = state;

            return new CommandHandle(id, op, workItem.Completion.Task);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            for (int i = 0; i < _parallelism; i++)
            {
                _workers.Add(Task.Run(() => WorkerLoopAsync(_cts.Token), CancellationToken.None));
            }
            _logger?.Log("Information", "Command", $"CommandDispatcher avviato con parallelismo={_parallelism}");
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                _queue.Writer.TryComplete();
                _cts?.Cancel();
            }
            catch { }

            try
            {
                await Task.WhenAll(_workers);
            }
            catch { }
        }

        private async Task WorkerLoopAsync(CancellationToken stoppingToken)
        {
            try
            {
                while (await _queue.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                {
                    while (_queue.Reader.TryRead(out var workItem))
                    {
                        await ProcessWorkItemAsync(workItem, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "Command", $"WorkerLoop interrotto: {ex.Message}", ex.ToString());
            }
        }

        private async Task ProcessWorkItemAsync(CommandWorkItem workItem, CancellationToken stoppingToken)
        {
            using var scope = LogScope.Push(workItem.ThreadScope, workItem.OperationNumber);
            var ctx = new CommandContext(workItem.RunId, workItem.OperationName, workItem.Metadata, workItem.OperationNumber, stoppingToken);
            var startMessage = $"[{workItem.RunId}] START {workItem.OperationName}";
            _logger?.Log("Information", "Command", startMessage);

            if (_active.TryGetValue(workItem.RunId, out var state))
            {
                state.Status = "running";
                state.StartedAt = DateTimeOffset.UtcNow;
            }

            try
            {
                var result = await workItem.Handler(ctx).ConfigureAwait(false);
                var finalMessage = result.Message ?? (result.Success ? "OK" : "FAILED");
                var endLevel = result.Success ? "Information" : "Error";
                _logger?.Log(endLevel, "Command", $"[{workItem.RunId}] END {workItem.OperationName} => {finalMessage}");
                workItem.Completion.TrySetResult(result);
            }
            catch (OperationCanceledException oce)
            {
                _logger?.Log("Warning", "Command", $"[{workItem.RunId}] CANCEL {workItem.OperationName}: {oce.Message}", oce.ToString());
                workItem.Completion.TrySetResult(new CommandResult(false, "Operazione annullata"));
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "Command", $"[{workItem.RunId}] ERROR {workItem.OperationName}: {ex.Message}", ex.ToString());
                workItem.Completion.TrySetResult(new CommandResult(false, ex.Message));
            }
            finally
            {
                _active.TryRemove(workItem.RunId, out _);
            }
        }

        public IReadOnlyList<CommandSnapshot> GetActiveCommands()
        {
            var list = new List<CommandSnapshot>();
            foreach (var kvp in _active)
            {
                var s = kvp.Value;
                list.Add(new CommandSnapshot(
                    s.RunId,
                    s.OperationName,
                    s.ThreadScope,
                    s.Status,
                    s.EnqueuedAt,
                    s.StartedAt,
                    s.Metadata));
            }
            list.Sort((a, b) => a.EnqueuedAt.CompareTo(b.EnqueuedAt));
            return list;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _cts?.Cancel();
            }
            catch { }
            _cts?.Dispose();
        }
    }

    internal sealed class CommandState
    {
        public string RunId { get; set; } = string.Empty;
        public string OperationName { get; set; } = string.Empty;
        public string ThreadScope { get; set; } = string.Empty;
        public string Status { get; set; } = "queued";
        public DateTimeOffset EnqueuedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? StartedAt { get; set; }
        public IReadOnlyDictionary<string, string>? Metadata { get; set; }
    }
}
