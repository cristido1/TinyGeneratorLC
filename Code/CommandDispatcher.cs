using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services
{
    public sealed class CommandDispatcherOptions
    {
        public int MaxParallelCommands { get; set; } = 3;
    }

    internal sealed class CommandWorkItem : IComparable<CommandWorkItem>
    {
        public string RunId { get; }
        public string OperationName { get; }
        public string ThreadScope { get; }
        public IReadOnlyDictionary<string, string>? Metadata { get; }
        public Func<CommandContext, Task<CommandResult>> Handler { get; }
        public TaskCompletionSource<CommandResult> Completion { get; }
        public long OperationNumber { get; }
        public string? AgentName { get; }
        public string? AgentRole { get; }
        public int Priority { get; }
        public long EnqueueSequence { get; }
        public CancellationTokenSource Cancellation { get; }

        public CommandWorkItem(
            string runId,
            string operationName,
            string threadScope,
            IReadOnlyDictionary<string, string>? metadata,
            Func<CommandContext, Task<CommandResult>> handler,
            long operationNumber,
            int priority,
            long enqueueSequence,
            CancellationTokenSource cancellation,
            string? agentName = null,
            string? agentRole = null)
        {
            RunId = runId;
            OperationName = operationName;
            ThreadScope = threadScope;
            Metadata = metadata;
            Handler = handler;
            OperationNumber = operationNumber;
            Priority = priority;
            EnqueueSequence = enqueueSequence;
            AgentName = agentName;
            AgentRole = agentRole;
            Cancellation = cancellation;
            Completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public int CompareTo(CommandWorkItem? other)
        {
            if (other == null) return -1;
            // Prima per priorità (1 = massima, numeri più bassi = priorità più alta)
            var priorityComparison = Priority.CompareTo(other.Priority);
            if (priorityComparison != 0) return priorityComparison;
            // A parità di priorità, FIFO (sequence più bassa = più vecchio)
            return EnqueueSequence.CompareTo(other.EnqueueSequence);
        }
    }

    public sealed class CommandDispatcher : IHostedService, ICommandDispatcher, IDisposable
    {
        private readonly PriorityQueue<CommandWorkItem, CommandWorkItem> _queue = new();
        private readonly object _queueLock = new();
        private readonly SemaphoreSlim _queueSemaphore = new(0);
        private readonly int _parallelism;
        private readonly ICustomLogger? _logger;
        private readonly Microsoft.AspNetCore.SignalR.IHubContext<TinyGenerator.Hubs.ProgressHub>? _hubContext;
        private readonly NumeratorService? _numerator;
        private readonly IServiceProvider _services;
        private readonly CommandPoliciesOptions _policies;
        private readonly List<Task> _workers = new();
        private CancellationTokenSource? _cts;
        private long _counter;
        private long _enqueueCounter;
        private bool _disposed;
        private readonly ConcurrentDictionary<string, CommandState> _active = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, CommandState> _completed = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Task<CommandResult>> _completionTasks = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _commandCancellations = new(StringComparer.OrdinalIgnoreCase);

        public event Action<CommandCompletedEventArgs>? CommandCompleted;

        public CommandDispatcher(
            IOptions<CommandDispatcherOptions>? options,
            IOptions<CommandPoliciesOptions>? policies,
            ICustomLogger? logger = null,
            Microsoft.AspNetCore.SignalR.IHubContext<TinyGenerator.Hubs.ProgressHub>? hubContext = null,
            NumeratorService? numerator = null,
            IServiceProvider? services = null)
        {
            _parallelism = Math.Max(1, options?.Value?.MaxParallelCommands ?? 2);
            _logger = logger;
            _hubContext = hubContext;
            _numerator = numerator;
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _policies = policies?.Value ?? new CommandPoliciesOptions();
        }

        public CommandHandle Enqueue(
            ICommand command,
            string? runId = null,
            string? threadScope = null,
            IReadOnlyDictionary<string, string>? metadata = null,
            int? priority = null)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));

            var operationName = string.IsNullOrWhiteSpace(command.CommandName) ? "command" : command.CommandName;
            return EnqueueCore(
                operationName,
                ctx => ExecuteCommandLifecycleAsync(command, ctx),
                runId,
                threadScope,
                metadata,
                priority ?? command.Priority);
        }

        private CommandHandle EnqueueCore(
            string operationName,
            Func<CommandContext, Task<CommandResult>> handler,
            string? runId = null,
            string? threadScope = null,
            IReadOnlyDictionary<string, string>? metadata = null,
            int priority = 2)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (_disposed) throw new ObjectDisposedException(nameof(CommandDispatcher));

            var op = string.IsNullOrWhiteSpace(operationName) ? "command" : operationName.Trim();
            var safeScope = string.IsNullOrWhiteSpace(threadScope) ? op : threadScope.Trim();
            var id = string.IsNullOrWhiteSpace(runId)
                ? $"{op}_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Interlocked.Increment(ref _counter)}"
                : runId.Trim();

            var opNumber = LogScope.GenerateOperationId();
            var enqueueSeq = Interlocked.Increment(ref _enqueueCounter);
            var agentName = metadata != null && metadata.TryGetValue("agentName", out var an) ? an : null;
            var agentRole = metadata != null && metadata.TryGetValue("agentRole", out var ar) ? ar : null;
            var commandCts = new CancellationTokenSource();
            var workItem = new CommandWorkItem(id, op, safeScope, metadata, handler, opNumber, priority, enqueueSeq, commandCts, agentName, agentRole);

            lock (_queueLock)
            {
                _queue.Enqueue(workItem, workItem);
            }
            _queueSemaphore.Release();

            var state = new CommandState
            {
                RunId = id,
                OperationName = op,
                ThreadScope = safeScope,
                Metadata = metadata,
                Status = "queued",
                EnqueuedAt = DateTimeOffset.UtcNow,
                AgentName = agentName,
                ModelName = metadata != null && metadata.TryGetValue("modelName", out var mn) ? mn : null,
                CurrentStep = metadata != null && metadata.TryGetValue("stepCurrent", out var sc) && int.TryParse(sc, out var c) ? c : null,
                MaxStep = metadata != null && metadata.TryGetValue("stepMax", out var sm) && int.TryParse(sm, out var m) ? m : null
            };
            _active[id] = state;
            _completionTasks[id] = workItem.Completion.Task;
            _commandCancellations[id] = commandCts;

            _ = BroadcastCommandsAsync();

            return new CommandHandle(id, op, workItem.Completion.Task);
        }

        private async Task<CommandResult> ExecuteCommandLifecycleAsync(ICommand command, CommandContext context)
        {
            void OnProgress(object? _, CommandProgressEventArgs args)
            {
                UpdateStep(context.RunId, args.Current, args.Max, args.Description);
            }

            command.Progress += OnProgress;
            try
            {
                return await command.Execute(context).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                await command.Cancel(context).ConfigureAwait(false);
                throw;
            }
            finally
            {
                command.Progress -= OnProgress;
            }
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
                while (!stoppingToken.IsCancellationRequested)
                {
                    await _queueSemaphore.WaitAsync(stoppingToken).ConfigureAwait(false);
                    
                    CommandWorkItem? workItem = null;
                    lock (_queueLock)
                    {
                        if (_queue.TryDequeue(out workItem, out _))
                        {
                            // Item dequeued successfully
                        }
                    }

                    if (workItem != null)
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
            var allocatedThreadId = _numerator?.NextThreadId() ?? Environment.CurrentManagedThreadId;
            long? allocatedStoryId = null;
            if (_numerator != null && string.Equals(workItem.OperationName, "create_story", StringComparison.OrdinalIgnoreCase))
            {
                allocatedStoryId = _numerator.NextStoryId();
            }
            else if (_numerator != null && workItem.Metadata != null)
            {
                // If command targets a specific story, propagate story_id to all logs.
                // Many callers provide metadata["storyId"]. Some use "entityId".
                if (workItem.Metadata.TryGetValue("storyId", out var sidRaw) || workItem.Metadata.TryGetValue("entityId", out sidRaw))
                {
                    if (long.TryParse(sidRaw, out var storyDbId) && storyDbId > 0)
                    {
                        try
                        {
                            allocatedStoryId = _numerator.EnsureStoryIdForStoryDbId(storyDbId);
                        }
                        catch
                        {
                            // best-effort; keep null if story cannot be resolved
                            allocatedStoryId = null;
                        }
                    }
                }
            }

            var policy = _policies.Resolve(workItem.OperationName, workItem.Metadata);
            var timeoutSec = policy.TimeoutSec;

            using var scope = LogScope.Push(workItem.ThreadScope, workItem.OperationNumber, null, null, workItem.AgentName, agentRole: workItem.AgentRole, threadId: allocatedThreadId, storyId: allocatedStoryId);
            using var commandRuntimeScope = CommandExecutionRuntime.Push(workItem.OperationName, timeoutSec);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, workItem.Cancellation.Token);
            var ctx = new CommandContext(workItem.RunId, workItem.OperationName, workItem.Metadata, workItem.OperationNumber, linkedCts.Token);
            
            // Generate command ID for tracking
            var commandId = RequestIdGenerator.Generate();
            var storyIdInfo = allocatedStoryId.HasValue ? $" (Story: {allocatedStoryId})" : "";
            var startMessage = $"[CmdID: {commandId}][{workItem.RunId}]{storyIdInfo} ▶️ START {workItem.OperationName}";
            _logger?.Log("Information", "Command", startMessage);

            try
            {
                if (_active.TryGetValue(workItem.RunId, out var state))
                {
                    if (state.Status == "cancelled")
                    {
                        throw new OperationCanceledException("Operazione annullata");
                    }
                    state.Status = "running";
                    state.StartedAt = DateTimeOffset.UtcNow;
                    state.TimeoutSec = timeoutSec;
                }
                await BroadcastCommandsAsync().ConfigureAwait(false);

                var maxAttempts = Math.Max(1, policy.MaxAttempts);
                CommandResult result;

                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    linkedCts.Token.ThrowIfCancellationRequested();

                    try
                    {
                        result = await workItem.Handler(ctx).ConfigureAwait(false);

                        if (result.Success)
                        {
                            goto Completed;
                        }

                        if (!policy.RetryOnFailureResult || attempt == maxAttempts)
                        {
                            goto Completed;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        result = new CommandResult(false, ex.Message);

                        if (!policy.RetryOnException || attempt == maxAttempts)
                        {
                            goto Completed;
                        }
                    }

                    // Retry path
                    try
                    {
                        if (_active.TryGetValue(workItem.RunId, out var retryState))
                        {
                            retryState.RetryCount = attempt;
                        }
                        UpdateRetry(workItem.RunId, attempt);

                        var baseDelay = Math.Max(0, policy.RetryDelayBaseSeconds);
                        if (baseDelay > 0)
                        {
                            var delaySeconds = policy.ExponentialBackoff
                                ? Math.Min(policy.RetryDelayMaxSeconds, baseDelay * (int)Math.Pow(2, Math.Max(0, attempt - 1)))
                                : Math.Min(policy.RetryDelayMaxSeconds, baseDelay);
                            if (delaySeconds > 0)
                            {
                                _logger?.Log("Warning", "Command", $"[{workItem.RunId}] RETRY {workItem.OperationName} attempt {attempt + 1}/{maxAttempts} in {delaySeconds}s");
                                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), linkedCts.Token).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                }

                // Unreachable, but required by compiler
                result = new CommandResult(false, "Failed");

Completed:
                var finalMessage = result.Message ?? (result.Success ? "OK" : "FAILED");
                var endLevel = result.Success ? "Information" : "Error";
                var endIcon = result.Success ? "✅" : "❌";
                _logger?.Log(endLevel, "Command", $"[CmdID: {commandId}][{workItem.RunId}] {endIcon} END {workItem.OperationName} => {finalMessage}");
                workItem.Completion.TrySetResult(result);

                if (!result.Success)
                {
                    await TryReportFailureAsync(workItem, result.Message, null, allocatedThreadId).ConfigureAwait(false);
                }
                
                // Aggiorna stato completato
                if (_active.TryGetValue(workItem.RunId, out var completedState))
                {
                    if (completedState.Status != "cancelled")
                    {
                        completedState.Status = result.Success ? "completed" : "failed";
                        completedState.CompletedAt = DateTimeOffset.UtcNow;
                        completedState.ErrorMessage = result.Success ? null : result.Message;
                    }
                }
                
                RaiseCompleted(workItem.RunId, workItem.OperationName, result, workItem.Metadata);
            }
            catch (OperationCanceledException oce)
            {
                var cancelSource = stoppingToken.IsCancellationRequested
                    ? "dispatcher_stopping"
                    : workItem.Cancellation.IsCancellationRequested
                        ? "manual_cancel_request"
                        : "linked_token_or_unknown";
                var timeoutInfo = timeoutSec > 0
                    ? $"timeout policy per singola richiesta agente (TimeoutSec={timeoutSec}s)"
                    : "nessun timeout policy configurato";
                _logger?.Append(
                    workItem.RunId,
                    $"[cancelled] Dettaglio cancellazione: source={cancelSource}; {timeoutInfo}; stopping={stoppingToken.IsCancellationRequested}; manualCancel={workItem.Cancellation.IsCancellationRequested}",
                    "warning");


                _logger?.Log("Warning", "Command", $"[CmdID: {commandId}][{workItem.RunId}] 🚫 CANCEL {workItem.OperationName}: {oce.Message}", oce.ToString());
                var result = new CommandResult(false, "Operazione annullata");
                workItem.Completion.TrySetResult(result);
                
                if (_active.TryGetValue(workItem.RunId, out var cancelledState))
                {
                    cancelledState.Status = "cancelled";
                    cancelledState.CompletedAt = DateTimeOffset.UtcNow;
                    cancelledState.ErrorMessage = "Operazione annullata";
                }
                
                RaiseCompleted(workItem.RunId, workItem.OperationName, result, workItem.Metadata);
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "Command", $"[CmdID: {commandId}][{workItem.RunId}] ❌ ERROR {workItem.OperationName}: {ex.Message}", ex.ToString());
                var result = new CommandResult(false, ex.Message);
                workItem.Completion.TrySetResult(result);

                await TryReportFailureAsync(workItem, ex.Message, ex.ToString(), allocatedThreadId).ConfigureAwait(false);
                
                if (_active.TryGetValue(workItem.RunId, out var errorState))
                {
                    errorState.Status = "failed";
                    errorState.CompletedAt = DateTimeOffset.UtcNow;
                    errorState.ErrorMessage = ex.Message;
                }
                
                RaiseCompleted(workItem.RunId, workItem.OperationName, result, workItem.Metadata);
            }
            finally
            {
                // Sposta da _active a _completed per mostrare per 5 minuti
                if (_active.TryRemove(workItem.RunId, out var finalState))
                {
                    _completed[workItem.RunId] = finalState;
                    // Schedula rimozione dopo 5 minuti
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                        _completed.TryRemove(workItem.RunId, out _);
                        await BroadcastCommandsAsync().ConfigureAwait(false);
                    });
                }
                _completionTasks.TryRemove(workItem.RunId, out _);
                if (_commandCancellations.TryRemove(workItem.RunId, out var cancelCts))
                {
                    cancelCts.Dispose();
                }
                await BroadcastCommandsAsync().ConfigureAwait(false);
            }
        }

        private async Task TryReportFailureAsync(CommandWorkItem workItem, string? message, string? exception, int threadId)
        {
            var systemReports = _services.GetService<SystemReportService>();
            if (systemReports == null) return;
            try
            {
                CommandState? state = null;
                int? retryCount = null;
                if (_active.TryGetValue(workItem.RunId, out var activeState))
                {
                    state = activeState;
                    retryCount = activeState.RetryCount;
                }

                int? durationMs = null;
                if (state != null && state.StartedAt.HasValue)
                {
                    durationMs = (int)Math.Max(0, (DateTimeOffset.UtcNow - state.StartedAt.Value).TotalMilliseconds);
                }

                long? storyCorrelationId = LogScope.CurrentStoryId;
                var opType = workItem.Metadata != null && workItem.Metadata.TryGetValue("operation", out var op)
                    ? op
                    : workItem.OperationName;

                var rawLogRef = $"thread:{threadId};run:{workItem.RunId}";
                var ctx = new SystemReportService.FailureContext(
                    OperationName: workItem.OperationName,
                    OperationType: opType,
                    Message: message,
                    Exception: exception,
                    ThreadId: threadId,
                    StoryCorrelationId: storyCorrelationId,
                    AgentName: workItem.AgentName,
                    ModelName: workItem.Metadata != null && workItem.Metadata.TryGetValue("modelName", out var mn) ? mn : null,
                    RetryCount: retryCount,
                    ExecutionTimeMs: durationMs,
                    RawLogRef: rawLogRef);

                await systemReports.ReportFailureAsync(ctx).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "SystemReport", $"Failed to enqueue system report: {ex.Message}", ex.ToString());
            }
        }

        public IReadOnlyList<CommandSnapshot> GetActiveCommands()
        {
            var list = new List<CommandSnapshot>();
            
            // Aggiungi comandi attivi (queued o running)
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
                    s.CompletedAt,
                    s.Metadata,
                    s.AgentName,
                    s.ModelName,
                    s.CurrentStep,
                    s.MaxStep,
                    s.StepDescription,
                    s.RetryCount,
                    s.ErrorMessage));
            }
            
            // Aggiungi comandi completati (da mostrare per 5 minuti)
            foreach (var kvp in _completed)
            {
                var s = kvp.Value;
                list.Add(new CommandSnapshot(
                    s.RunId,
                    s.OperationName,
                    s.ThreadScope,
                    s.Status,
                    s.EnqueuedAt,
                    s.StartedAt,
                    s.CompletedAt,
                    s.Metadata,
                    s.AgentName,
                    s.ModelName,
                    s.CurrentStep,
                    s.MaxStep,
                    s.StepDescription,
                    s.RetryCount,
                    s.ErrorMessage));
            }
            
            list.Sort((a, b) => a.EnqueuedAt.CompareTo(b.EnqueuedAt));
            return list;
        }

        public void UpdateStep(string runId, int current, int max, string? stepDescription = null)
        {
            if (_active.TryGetValue(runId, out var state))
            {
                state.CurrentStep = current;
                state.MaxStep = max;
                if (stepDescription != null)
                {
                    state.StepDescription = stepDescription;
                }
                _ = BroadcastCommandsAsync();
            }
        }

        public void UpdateRetry(string runId, int retryCount)
        {
            if (_active.TryGetValue(runId, out var state))
            {
                state.RetryCount = retryCount;
                _ = BroadcastCommandsAsync();
            }
        }

        public void UpdateOperationName(string runId, string newOperationName)
        {
            if (_active.TryGetValue(runId, out var state) && !string.IsNullOrWhiteSpace(newOperationName))
            {
                state.OperationName = newOperationName.Trim();
                _ = BroadcastCommandsAsync();
            }
        }

        public bool CancelCommand(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId))
            {
                return false;
            }

            if (!_active.TryGetValue(runId, out var state))
            {
                return false;
            }

            if (_commandCancellations.TryGetValue(runId, out var cts))
            {
                cts.Cancel();
            }

            if (state.Status == "queued")
            {
                state.Status = "cancelled";
                state.CompletedAt = DateTimeOffset.UtcNow;
                state.ErrorMessage = "Operazione annullata";
                var timeoutInfo = state.TimeoutSec > 0
                    ? $"timeout policy per singola richiesta agente configurato={state.TimeoutSec}s"
                    : "nessun timeout policy configurato";
                _logger?.Append(
                    runId,
                    $"[cancelled] Comando annullato manualmente in coda; {timeoutInfo}",
                    "warning");
            }

            _ = BroadcastCommandsAsync();
            return true;
        }

        private Task BroadcastCommandsAsync()
        {
            if (_hubContext == null) return Task.CompletedTask;
            var snapshot = GetActiveCommands();
            return _hubContext.Clients.All.SendAsync("CommandListUpdated", snapshot);
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
            _queueSemaphore?.Dispose();
        }

        public async Task<CommandResult> WaitForCompletionAsync(string runId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(runId))
                throw new ArgumentException("runId is required", nameof(runId));

            if (!_completionTasks.TryGetValue(runId, out var task))
            {
                return new CommandResult(false, $"Comando {runId} non trovato o già completato.");
            }

            var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
            if (completed != task)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            _completionTasks.TryRemove(runId, out _);
            return await task.ConfigureAwait(false);
        }

        private void RaiseCompleted(string runId, string operationName, CommandResult result, IReadOnlyDictionary<string, string>? metadata)
        {
            var handlers = CommandCompleted;
            if (handlers == null) return;

            try
            {
                handlers(new CommandCompletedEventArgs(runId, operationName, result.Success, result.Message, metadata));
            }
            catch
            {
                // Intentionally swallow exceptions from event subscribers
            }
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
        public DateTimeOffset? CompletedAt { get; set; }
        public IReadOnlyDictionary<string, string>? Metadata { get; set; }
        public string? AgentName { get; set; }
        public string? ModelName { get; set; }
        public int? CurrentStep { get; set; }
        public int? MaxStep { get; set; }
        public string? StepDescription { get; set; }
        public int RetryCount { get; set; }
        public string? ErrorMessage { get; set; }
        public int TimeoutSec { get; set; }
    }
}

