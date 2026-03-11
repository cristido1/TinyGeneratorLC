using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services
{
    public sealed class CommandDispatcherOptions
    {
        public int MaxParallelCommands { get; set; } = 3;
        public int MaxBatchProcessesGlobal { get; set; } = 8;
        public int MaxBatchProcessesPerOperation { get; set; } = 3;
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
        private readonly ConcurrentDictionary<string, Task> _batchExecutions = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<long, SemaphoreSlim> _batchStoryLocks = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _batchOperationSemaphores = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _batchGlobalSemaphore;
        private readonly int _maxBatchProcessesPerOperation;
        private static readonly HashSet<string> ExternalBatchStoryOperations = new(StringComparer.OrdinalIgnoreCase)
        {
            "generate_ambience_audio",
            "generate_fx_audio",
            "generate_music"
        };

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
            var maxBatchGlobal = Math.Max(1, options?.Value?.MaxBatchProcessesGlobal ?? 8);
            _maxBatchProcessesPerOperation = Math.Max(1, options?.Value?.MaxBatchProcessesPerOperation ?? 3);
            _batchGlobalSemaphore = new SemaphoreSlim(maxBatchGlobal, maxBatchGlobal);
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
                priority ?? command.Priority,
                runAsBatch: command.Batch);
        }

        private CommandHandle EnqueueCore(
            string operationName,
            Func<CommandContext, Task<CommandResult>> handler,
            string? runId = null,
            string? threadScope = null,
            IReadOnlyDictionary<string, string>? metadata = null,
            int priority = 2,
            bool runAsBatch = false)
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
            var agentName = GetMetadataValue(metadata, "agentName", "AgentName", "agent");
            var agentRole = GetMetadataValue(metadata, "agentRole", "AgentRole", "role");
            var modelName = GetMetadataValue(metadata, "modelName", "ModelName", "model");

            var database = _services.GetService(typeof(DatabaseService)) as DatabaseService;
            var resolvedAgentId = TryGetMetadataInt(metadata, "agentId", "writerAgentId", "executorAgentId", "checkerAgentId");
            if (database != null && resolvedAgentId.HasValue && resolvedAgentId.Value > 0)
            {
                var agent = database.GetAgentById(resolvedAgentId.Value);
                if (agent != null)
                {
                    if (string.IsNullOrWhiteSpace(agentName))
                    {
                        agentName = agent.Description;
                    }
                    if (string.IsNullOrWhiteSpace(agentRole))
                    {
                        agentRole = agent.Role;
                    }
                    if (string.IsNullOrWhiteSpace(modelName))
                    {
                        modelName = agent.ModelName;
                    }
                }
            }

            // Fallback for story status commands that often don't pass explicit agent metadata.
            if (database != null && (string.IsNullOrWhiteSpace(agentName) || string.IsNullOrWhiteSpace(modelName)))
            {
                var inferredRole = ResolveAgentRoleFromOperation(op);
                if (!string.IsNullOrWhiteSpace(inferredRole))
                {
                    var inferredAgent = database.ListAgents()
                        .FirstOrDefault(a => a.IsActive && string.Equals(a.Role, inferredRole, StringComparison.OrdinalIgnoreCase));
                    if (inferredAgent != null)
                    {
                        if (string.IsNullOrWhiteSpace(agentName))
                        {
                            agentName = inferredAgent.Name;
                        }
                        if (string.IsNullOrWhiteSpace(agentRole))
                        {
                            agentRole = inferredAgent.Role;
                        }
                        if (string.IsNullOrWhiteSpace(modelName) && inferredAgent.ModelId.HasValue && inferredAgent.ModelId.Value > 0)
                        {
                            modelName = database.GetModelInfoById(inferredAgent.ModelId.Value)?.Name;
                        }
                    }
                }
            }

            if (database != null && string.IsNullOrWhiteSpace(modelName))
            {
                var modelId = TryGetMetadataInt(metadata, "modelId", "ModelId");
                if (modelId.HasValue && modelId.Value > 0)
                {
                    modelName = database.GetModelInfoById(modelId.Value)?.Name;
                }
            }
            var effectiveHandler = runAsBatch
                ? WrapBatchHandler(op, metadata, handler)
                : handler;
            var commandCts = new CancellationTokenSource();
            var workItem = new CommandWorkItem(id, op, safeScope, metadata, effectiveHandler, opNumber, priority, enqueueSeq, commandCts, agentName, agentRole);

            var c = 0;
            var m = 1;
            var hasStepCurrent = metadata != null && metadata.TryGetValue("stepCurrent", out var sc) && int.TryParse(sc, out c);
            var hasStepMax = metadata != null && metadata.TryGetValue("stepMax", out var sm) && int.TryParse(sm, out m);
            var initialCurrentStep = hasStepCurrent ? c : 0;
            var initialMaxStep = hasStepMax ? Math.Max(1, m) : 1;
            if (initialCurrentStep < 0) initialCurrentStep = 0;
            if (initialCurrentStep > initialMaxStep) initialCurrentStep = initialMaxStep;

            var state = new CommandState
            {
                RunId = id,
                OperationName = op,
                ThreadScope = safeScope,
                Metadata = metadata,
                Status = runAsBatch ? "batch_queued" : "queued",
                EnqueuedAt = DateTimeOffset.UtcNow,
                AgentName = agentName,
                ModelName = modelName,
                CurrentStep = initialCurrentStep,
                MaxStep = initialMaxStep,
                MaxRetry = 0
            };
            _active[id] = state;
            _completionTasks[id] = workItem.Completion.Task;
            _commandCancellations[id] = commandCts;

            if (runAsBatch)
            {
                var dispatcherToken = _cts?.Token ?? CancellationToken.None;
                var batchTask = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessWorkItemAsync(workItem, dispatcherToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        _batchExecutions.TryRemove(id, out _);
                    }
                }, CancellationToken.None);
                _batchExecutions[id] = batchTask;
            }
            else
            {
                lock (_queueLock)
                {
                    _queue.Enqueue(workItem, workItem);
                }
                _queueSemaphore.Release();
            }

            _ = BroadcastCommandsAsync();

            return new CommandHandle(id, op, workItem.Completion.Task);
        }

        private Func<CommandContext, Task<CommandResult>> WrapBatchHandler(
            string operationName,
            IReadOnlyDictionary<string, string>? metadata,
            Func<CommandContext, Task<CommandResult>> originalHandler)
        {
            return async ctx =>
            {
                var normalizedOperation = ResolveNormalizedOperation(operationName, metadata);
                var lockHandle = await AcquireBatchStoryLockAsync(metadata, ctx.CancellationToken).ConfigureAwait(false);
                var slotHandle = await AcquireBatchExecutionSlotsAsync(normalizedOperation, ctx.CancellationToken).ConfigureAwait(false);
                try
                {
                    var batchRequest = TryBuildBatchWorkerRequest(ctx.RunId, operationName, metadata);
                    if (batchRequest == null)
                    {
                        return await originalHandler(ctx).ConfigureAwait(false);
                    }

                    var externalResult = await ExecuteInExternalBatchWorkerAsync(batchRequest.Value, ctx.CancellationToken).ConfigureAwait(false);
                    if (externalResult.success)
                    {
                        return new CommandResult(true, externalResult.message);
                    }

                    _logger?.Log("Warning", "Command", $"[{ctx.RunId}] External batch worker failed for {operationName}, fallback in-process. {externalResult.message}");
                    return await originalHandler(ctx).ConfigureAwait(false);
                }
                finally
                {
                    if (slotHandle != null)
                    {
                        await slotHandle.DisposeAsync().ConfigureAwait(false);
                    }
                    if (lockHandle != null)
                    {
                        await lockHandle.DisposeAsync().ConfigureAwait(false);
                    }
                }
            };
        }

        private async ValueTask<IAsyncDisposable?> AcquireBatchExecutionSlotsAsync(string operationName, CancellationToken cancellationToken)
        {
            await _batchGlobalSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            var operationSemaphore = _batchOperationSemaphores.GetOrAdd(
                operationName,
                _ => new SemaphoreSlim(_maxBatchProcessesPerOperation, _maxBatchProcessesPerOperation));
            try
            {
                await operationSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _batchGlobalSemaphore.Release();
                throw;
            }

            return new AsyncReleaseHandle(() =>
            {
                operationSemaphore.Release();
                _batchGlobalSemaphore.Release();
            });
        }

        private async ValueTask<IAsyncDisposable?> AcquireBatchStoryLockAsync(IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
        {
            if (!TryGetStoryId(metadata, out var storyId))
            {
                return null;
            }

            var semaphore = _batchStoryLocks.GetOrAdd(storyId, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new AsyncReleaseHandle(() => semaphore.Release());
        }

        private static bool TryGetStoryId(IReadOnlyDictionary<string, string>? metadata, out long storyId)
        {
            storyId = 0;
            if (metadata == null)
            {
                return false;
            }

            if (!metadata.TryGetValue("storyId", out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            return long.TryParse(raw, out storyId) && storyId > 0;
        }

        private static (string operation, long storyId, string? folder, string runId)? TryBuildBatchWorkerRequest(
            string runId,
            string operationName,
            IReadOnlyDictionary<string, string>? metadata)
        {
            if (!TryGetStoryId(metadata, out var storyId))
            {
                return null;
            }

            var metadataOperation = metadata != null && metadata.TryGetValue("operation", out var op) ? op : null;
            var operation = string.IsNullOrWhiteSpace(metadataOperation) ? operationName : metadataOperation;
            if (operation.StartsWith("story_", StringComparison.OrdinalIgnoreCase))
            {
                operation = operation["story_".Length..];
            }

            if (!ExternalBatchStoryOperations.Contains(operation))
            {
                return null;
            }

            var folder = metadata != null && metadata.TryGetValue("folder", out var folderValue) ? folderValue : null;
            return (operation, storyId, folder, runId);
        }

        private static string ResolveNormalizedOperation(string operationName, IReadOnlyDictionary<string, string>? metadata)
        {
            var metadataOperation = metadata != null && metadata.TryGetValue("operation", out var op) ? op : null;
            var resolved = string.IsNullOrWhiteSpace(metadataOperation) ? operationName : metadataOperation;
            if (resolved.StartsWith("story_", StringComparison.OrdinalIgnoreCase))
            {
                resolved = resolved["story_".Length..];
            }

            return resolved.Trim();
        }

        private async Task<(bool success, string message)> ExecuteInExternalBatchWorkerAsync(
            (string operation, long storyId, string? folder, string runId) request,
            CancellationToken cancellationToken)
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                return (false, "Environment.ProcessPath non disponibile.");
            }

            var commandLineArgs = Environment.GetCommandLineArgs();
            if (commandLineArgs.Length == 0 || string.IsNullOrWhiteSpace(commandLineArgs[0]))
            {
                return (false, "Argomento entry assembly non disponibile.");
            }

            var isDotnetHost = string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
            var serializedOperation = EscapeCliValue(request.operation);
            var serializedRunId = EscapeCliValue(request.runId);
            var serializedFolder = string.IsNullOrWhiteSpace(request.folder) ? null : EscapeCliValue(request.folder!);

            var workerArgs = $"--batch-worker --operation \"{serializedOperation}\" --story-id {request.storyId} --run-id \"{serializedRunId}\"";
            if (!string.IsNullOrWhiteSpace(serializedFolder))
            {
                workerArgs += $" --folder \"{serializedFolder}\"";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = isDotnetHost ? processPath : processPath,
                Arguments = isDotnetHost
                    ? $"\"{EscapeCliValue(commandLineArgs[0])}\" {workerArgs}"
                    : workerArgs,
                WorkingDirectory = Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return (false, "Avvio processo worker non riuscito.");
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var stdoutTask = ConsumeWorkerStreamAsync(process.StandardOutput, request.runId, stdout, cancellationToken);
            var stderrTask = ConsumeWorkerStreamAsync(process.StandardError, request.runId, stderr, cancellationToken);

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Best effort.
                }
                throw;
            }

            var stdoutText = stdout.ToString().Trim();
            var stderrText = stderr.ToString().Trim();
            if (process.ExitCode == 0)
            {
                var message = string.IsNullOrWhiteSpace(stdoutText) ? $"Worker batch {request.operation} completato." : stdoutText;
                return (true, message);
            }

            var error = string.IsNullOrWhiteSpace(stderrText) ? stdoutText : stderrText;
            if (string.IsNullOrWhiteSpace(error))
            {
                error = $"Worker batch terminato con exit code {process.ExitCode}.";
            }
            return (false, error);
        }

        private async Task ConsumeWorkerStreamAsync(
            StreamReader reader,
            string runId,
            StringBuilder sink,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                {
                    break;
                }

                if (TryHandleBatchProgressLine(line, runId))
                {
                    continue;
                }

                if (sink.Length > 0)
                {
                    sink.AppendLine();
                }
                sink.Append(line);
            }
        }

        private bool TryHandleBatchProgressLine(string line, string runId)
        {
            const string Prefix = "__BATCH_PROGRESS__|";
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return false;
            }

            var payload = line[Prefix.Length..];
            var parts = payload.Split('|', 4, StringSplitOptions.None);
            if (parts.Length < 4)
            {
                return false;
            }

            var progressRunId = parts[0].Trim();
            if (!string.Equals(progressRunId, runId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!int.TryParse(parts[1], out var current))
            {
                return false;
            }
            if (!int.TryParse(parts[2], out var max))
            {
                return false;
            }

            var description = parts[3].Trim();
            UpdateStep(runId, current, max, string.IsNullOrWhiteSpace(description) ? null : description);
            return true;
        }

        private static string EscapeCliValue(string value) => value.Replace("\"", "\\\"");

        private sealed class AsyncReleaseHandle : IAsyncDisposable
        {
            private readonly Action _release;
            private bool _released;

            public AsyncReleaseHandle(Action release)
            {
                _release = release ?? throw new ArgumentNullException(nameof(release));
            }

            public ValueTask DisposeAsync()
            {
                if (_released)
                {
                    return ValueTask.CompletedTask;
                }

                _released = true;
                _release();
                return ValueTask.CompletedTask;
            }
        }

        private static string? ResolveAgentRoleFromOperation(string? operationName)
        {
            var op = (operationName ?? string.Empty).Trim().ToLowerInvariant();
            return op switch
            {
                "add_voice_tags_to_story" => "formatter",
                "add_ambient_tags_to_story" => "ambient_expert",
                "add_fx_tags_to_story" => "fx_expert",
                "add_music_tags_to_story" => "music_expert",
                "always_on_story_summaries" => "summarizer",
                "canon_extractor" => "canon_extractor",
                "continuity_validator" => "continuity_validator",
                "state_delta_builder" => "state_delta_builder",
                "recap_builder" => "recap_builder",
                "nre_resource_initializer_init" => "resource_initializer",
                "nre_resource_manager_update" => "resource_manager",
                "run_nre:resource_initializer_init" => "resource_initializer",
                "run_nre:resource_manager_update" => "resource_manager",
                _ => null
            };
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
                var batchTasks = _batchExecutions.Values.ToArray();
                await Task.WhenAll(_workers.Concat(batchTasks));
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
            if (ShouldLogCommandStart(workItem.OperationName))
            {
                _logger?.Log("Information", "Command", startMessage);
            }

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
                    if (!state.CurrentStep.HasValue || !state.MaxStep.HasValue || state.MaxStep.Value <= 0)
                    {
                        state.CurrentStep = 0;
                        state.MaxStep = 1;
                        state.StepDescription ??= "In esecuzione";
                    }
                }
                var maxAttempts = Math.Max(1, policy.MaxAttempts);
                if (_active.TryGetValue(workItem.RunId, out var runningState))
                {
                    runningState.MaxRetry = Math.Max(0, maxAttempts - 1);
                }
                await BroadcastCommandsAsync().ConfigureAwait(false);
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
                if (ShouldLogCommandEnd(workItem.OperationName, result))
                {
                    _logger?.Log(endLevel, "Command", $"[CmdID: {commandId}][{workItem.RunId}] {endIcon} END {workItem.OperationName} => {finalMessage}");
                }
                workItem.Completion.TrySetResult(result);

                UpdateStoryLastErrorStateBestEffort(
                    workItem,
                    success: result.Success,
                    errorMessage: result.Success ? null : finalMessage);

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
                        if (!completedState.MaxStep.HasValue || completedState.MaxStep.Value <= 0)
                        {
                            completedState.MaxStep = 1;
                        }
                        if (!completedState.CurrentStep.HasValue)
                        {
                            completedState.CurrentStep = result.Success ? completedState.MaxStep : 0;
                        }
                        if (result.Success)
                        {
                            completedState.CurrentStep = completedState.MaxStep;
                            if (string.IsNullOrWhiteSpace(completedState.StepDescription))
                            {
                                completedState.StepDescription = "Completato";
                            }
                        }
                        else if (string.IsNullOrWhiteSpace(completedState.StepDescription))
                        {
                            completedState.StepDescription = "Terminato con errore";
                        }
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
                var result = new CommandResult(true, "Operazione annullata");
                workItem.Completion.TrySetResult(result);
                
                if (_active.TryGetValue(workItem.RunId, out var cancelledState))
                {
                    cancelledState.Status = "cancelled";
                    cancelledState.CompletedAt = DateTimeOffset.UtcNow;
                    cancelledState.ErrorMessage = "Operazione annullata";
                    cancelledState.CurrentStep ??= 0;
                    cancelledState.MaxStep ??= 1;
                    cancelledState.StepDescription ??= "Annullato";
                }
                
                RaiseCompleted(workItem.RunId, workItem.OperationName, result, workItem.Metadata);
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "Command", $"[CmdID: {commandId}][{workItem.RunId}] ❌ ERROR {workItem.OperationName}: {ex.Message}", ex.ToString());
                var result = new CommandResult(false, ex.Message);
                workItem.Completion.TrySetResult(result);

                UpdateStoryLastErrorStateBestEffort(
                    workItem,
                    success: false,
                    errorMessage: ex.Message);

                await TryReportFailureAsync(workItem, ex.Message, ex.ToString(), allocatedThreadId).ConfigureAwait(false);
                
                if (_active.TryGetValue(workItem.RunId, out var errorState))
                {
                    errorState.Status = "failed";
                    errorState.CompletedAt = DateTimeOffset.UtcNow;
                    errorState.ErrorMessage = ex.Message;
                    errorState.CurrentStep ??= 0;
                    errorState.MaxStep ??= 1;
                    errorState.StepDescription ??= "Terminato con errore";
                }
                
                RaiseCompleted(workItem.RunId, workItem.OperationName, result, workItem.Metadata);
            }
            finally
            {
                await TryAutoPromoteModelsForRunAsync(workItem, stoppingToken).ConfigureAwait(false);

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

        private void UpdateStoryLastErrorStateBestEffort(
            CommandWorkItem workItem,
            bool success,
            string? errorMessage)
        {
            try
            {
                if (!TryGetStoryId(workItem.Metadata, out var storyId) || storyId <= 0)
                {
                    return;
                }

                var database = _services.GetService(typeof(DatabaseService)) as DatabaseService;
                if (database == null)
                {
                    return;
                }

                if (success)
                {
                    database.ClearStoryLastError(storyId);
                    return;
                }

                var operation = ResolveNormalizedOperation(workItem.OperationName, workItem.Metadata);
                database.SetStoryLastError(storyId, operation, errorMessage);
            }
            catch
            {
                // best-effort
            }
        }

        private async Task TryAutoPromoteModelsForRunAsync(CommandWorkItem workItem, CancellationToken stoppingToken)
        {
            try
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                if (!ShouldRunPromotionOnCompletion(workItem))
                {
                    return;
                }

                if (_logger != null)
                {
                    await _logger.FlushAsync().ConfigureAwait(false);
                }

                using var scope = _services.CreateScope();
                var promoter = scope.ServiceProvider.GetService<ModelPromotionService>();
                if (promoter == null)
                {
                    return;
                }

                var promoted = await promoter
                    .PromoteBestModelsForRunAsync(workItem.RunId, source: $"dispatcher:{workItem.OperationName}", ct: CancellationToken.None)
                    .ConfigureAwait(false);

                var changed = promoted.Count(x => x.Changed);
                if (changed > 0)
                {
                    _logger?.Log(
                        "Information",
                        "PROMOTION",
                        $"PROMOTION summary: run_id={workItem.RunId}; operation={workItem.OperationName}; promoted={changed}/{promoted.Count}",
                        result: "SUCCESS");
                }
            }
            catch (Exception ex)
            {
                _logger?.Log(
                    "Warning",
                    "PROMOTION",
                    $"PROMOTION failed: run_id={workItem.RunId}; operation={workItem.OperationName}; error={ex.Message}",
                    ex.ToString(),
                    result: "FAILED");
            }
        }

        private static bool ShouldRunPromotionOnCompletion(CommandWorkItem workItem)
        {
            if (workItem.Metadata != null &&
                workItem.Metadata.TryGetValue("promotionOnCompletion", out var explicitFlag) &&
                bool.TryParse(explicitFlag, out var enabledByMetadata))
            {
                return enabledByMetadata;
            }

            // Default: run only at end of root story-generation commands,
            // not for every intermediate command.
            return string.Equals(workItem.OperationName, "run_nre", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(workItem.OperationName, "generate_state_driven_single_story", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(workItem.OperationName, "generate_state_driven_episode_to_duration", StringComparison.OrdinalIgnoreCase);
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
                    ModelName: state?.ModelName ?? GetMetadataValue(workItem.Metadata, "modelName", "ModelName", "model"),
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
                    s.ErrorMessage,
                    s.MaxRetry));
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
                    s.ErrorMessage,
                    s.MaxRetry));
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

        private static bool ShouldLogCommandStart(string? operationName)
        {
            // Richiesta esplicita: update_model_stats_from_logs non deve emettere lo START.
            return !string.Equals(operationName, "update_model_stats_from_logs", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldLogCommandEnd(string? operationName, CommandResult result)
        {
            if (!string.Equals(operationName, "update_model_stats_from_logs", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Per update_model_stats_from_logs: loggare solo il messaggio finale utile quando ha elaborato record.
            if (!result.Success)
            {
                return true;
            }

            var message = (result.Message ?? string.Empty).Trim();
            return message.StartsWith("Model stats aggiornate da log:", StringComparison.OrdinalIgnoreCase);
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

        public void UpdateAgentModel(string runId, string? agentName = null, string? modelName = null)
        {
            if (_active.TryGetValue(runId, out var state))
            {
                if (!string.IsNullOrWhiteSpace(agentName))
                {
                    state.AgentName = agentName.Trim();
                }
                if (!string.IsNullOrWhiteSpace(modelName))
                {
                    state.ModelName = modelName.Trim();
                }
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

            if (state.Status == "queued" || state.Status == "running")
            {
                var wasRunning = state.Status == "running";
                state.Status = "cancelled";
                state.CompletedAt = DateTimeOffset.UtcNow;
                state.ErrorMessage = "Operazione annullata";
                var timeoutInfo = state.TimeoutSec > 0
                    ? $"timeout policy per singola richiesta agente configurato={state.TimeoutSec}s"
                    : "nessun timeout policy configurato";
                _logger?.Append(
                    runId,
                    $"[cancelled] Comando annullato manualmente ({(wasRunning ? "in corso" : "in coda")}); {timeoutInfo}",
                    "warning");
            }

            _ = BroadcastCommandsAsync();
            return true;
        }

        public int ClearCompletedCommands()
        {
            var removed = 0;
            foreach (var runId in _completed.Keys)
            {
                if (_completed.TryRemove(runId, out _))
                {
                    removed++;
                }
            }

            if (removed > 0)
            {
                _ = BroadcastCommandsAsync();
            }

            return removed;
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

        private static string? GetMetadataValue(IReadOnlyDictionary<string, string>? metadata, params string[] keys)
        {
            if (metadata == null || keys == null || keys.Length == 0) return null;

            foreach (var key in keys)
            {
                if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            foreach (var kvp in metadata)
            {
                foreach (var key in keys)
                {
                    if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kvp.Value))
                    {
                        return kvp.Value;
                    }
                }
            }

            return null;
        }

        private static int? TryGetMetadataInt(IReadOnlyDictionary<string, string>? metadata, params string[] keys)
        {
            var raw = GetMetadataValue(metadata, keys);
            if (int.TryParse(raw, out var value))
            {
                return value;
            }
            return null;
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
        public int MaxRetry { get; set; }
    }
}

