using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands
{
    /// <summary>
    /// Reads story_tagged, splits into chunks, adds ambient/noise tags via ambient_expert agent,
    /// updates story_tagged, then enqueues next status.
    /// </summary>
    public sealed class AddAmbientTagsToStoryCommand : ICommand
    {
        private readonly long _storyId;
        private readonly CommandTuningOptions _tuning;
        private readonly IAgentResolutionService _agentResolutionService;
        private readonly ICallCenter _callCenter;
        private readonly IStoryTaggingPipelineService _storyTaggingPipelineService;
        private readonly INextStatusEnqueuer _nextStatusEnqueuer;
        private readonly ICustomLogger? _logger;
        private readonly ICommandDispatcher? _dispatcher;
        private readonly Func<string?>? _currentDispatcherRunIdProvider;

        public string CommandName => "add_ambient_tags_to_story";
        public int Priority => 2;
        public event EventHandler<CommandProgressEventArgs>? Progress;

        public AddAmbientTagsToStoryCommand(
            long storyId,
            IAgentResolutionService agentResolutionService,
            ICallCenter callCenter,
            IStoryTaggingPipelineService storyTaggingPipelineService,
            INextStatusEnqueuer nextStatusEnqueuer,
            ICommandDispatcher? dispatcher = null,
            ICustomLogger? logger = null,
            CommandTuningOptions? tuning = null,
            Func<string?>? currentDispatcherRunIdProvider = null)
        {
            _storyId = storyId;
            _agentResolutionService = agentResolutionService ?? throw new ArgumentNullException(nameof(agentResolutionService));
            _callCenter = callCenter ?? throw new ArgumentNullException(nameof(callCenter));
            _storyTaggingPipelineService = storyTaggingPipelineService ?? throw new ArgumentNullException(nameof(storyTaggingPipelineService));
            _nextStatusEnqueuer = nextStatusEnqueuer ?? throw new ArgumentNullException(nameof(nextStatusEnqueuer));
            _dispatcher = dispatcher;
            _logger = logger;
            _tuning = tuning ?? new CommandTuningOptions();
            _currentDispatcherRunIdProvider = currentDispatcherRunIdProvider;
        }

        // Legacy constructor kept for backward compatibility with existing call sites.
        public AddAmbientTagsToStoryCommand(
            long storyId,
            DatabaseService database,
            ILangChainKernelFactory kernelFactory,
            StoriesService? storiesService = null,
            ICustomLogger? logger = null,
            CommandTuningOptions? tuning = null)
            : this(
                storyId,
                new AgentResolutionService(database),
                ResolveOrCreateCallCenter(database, storiesService, logger),
                new StoryTaggingPipelineService(database),
                new NextStatusEnqueuer(storiesService, logger),
                storiesService?.CommandDispatcher,
                logger,
                tuning,
                () => storiesService?.CurrentDispatcherRunId)
        {
            _ = kernelFactory;
        }

        public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default, string? runId = null)
        {
            var effectiveRunId = string.IsNullOrWhiteSpace(runId)
                ? (_currentDispatcherRunIdProvider?.Invoke() ?? $"add_ambient_tags_to_story_{_storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}")
                : runId;

            var telemetry = new CommandTelemetry(_logger, args =>
            {
                try
                {
                    _dispatcher?.UpdateStep(effectiveRunId, args.Current, args.Max, args.Description);
                }
                catch
                {
                    // best-effort
                }

                Progress?.Invoke(this, args);
            });
            telemetry.Start(effectiveRunId);
            telemetry.Append(effectiveRunId, $"[story {_storyId}] Starting add_ambient_tags_to_story pipeline");

            try
            {
                var resolvedAgent = _agentResolutionService.Resolve(CommandRoleCodes.AmbientExpert);

                var systemPrompt = TaggingResponseFormat.AppendToSystemPrompt(
                    resolvedAgent.BaseSystemPrompt,
                    StoryTaggingService.TagTypeAmbient);

                var preparation = _storyTaggingPipelineService.PrepareAmbientTagging(_storyId, _tuning.AmbientExpert);
                _storyTaggingPipelineService.PersistInitialRows(preparation);

                telemetry.Append(effectiveRunId, $"[story {_storyId}] Split into {preparation.Chunks.Count} chunks (rows)");
                var totalChunks = Math.Max(1, preparation.Chunks.Count);
                telemetry.ReportProgress(0, totalChunks, $"Ambient tags: preparazione completata, chunk 0/{totalChunks} (0%)");

                var ambientTags = new List<StoryTaggingService.StoryTagEntry>();
                var currentModelName = resolvedAgent.ModelName;
                var minAmbientTags = Math.Max(0, _tuning.AmbientExpert.MinAmbientTagsPerChunkRequirement);

                for (int i = 0; i < preparation.Chunks.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = preparation.Chunks[i];
                    var chunkIndex = i + 1;
                    var chunkCount = preparation.Chunks.Count;
                    var percentBefore = chunkCount <= 0 ? 0 : (int)Math.Round(((chunkIndex - 1) * 100.0) / chunkCount);
                    telemetry.ReportProgress(
                        Math.Max(0, chunkIndex - 1),
                        Math.Max(1, chunkCount),
                        $"Ambient tags chunk {chunkIndex}/{chunkCount} ({percentBefore}%) - richiesta agente");

                    var history = new ChatHistory();
                    if (!string.IsNullOrWhiteSpace(systemPrompt))
                    {
                        history.AddSystem(systemPrompt);
                    }
                    history.AddUser(chunk.Text);

                    var callOptions = new CallOptions
                    {
                        Timeout = TimeSpan.FromSeconds(Math.Max(1, _tuning.AmbientExpert.RequestTimeoutSeconds)),
                        MaxRetries = Math.Max(0, _tuning.AmbientExpert.MaxAttemptsPerChunk - 1),
                        UseResponseChecker = true,
                        AskFailExplanation = _tuning.AmbientExpert.DiagnoseOnFinalFailure,
                        AllowFallback = _tuning.AmbientExpert.EnableFallback,
                        Operation = CommandScopePaths.AddAmbientTagsToStory
                    };
                    callOptions.DeterministicChecks.Add(new CheckAmbientTagMinimumCount
                    {
                        Options = Options.Create<object>(new Dictionary<string, object>
                        {
                            ["MinAmbientTags"] = minAmbientTags,
                            ["ErrorMessage"] = $"Hai inserito solo {{count}} tag [RUMORI]. Devi inserirne almeno {minAmbientTags} per chunk {chunkIndex}/{chunkCount}."
                        })
                    });

                    var callResult = await _callCenter.CallAgentAsync(
                        storyId: _storyId,
                        threadId: BuildThreadId(_storyId, chunkIndex),
                        agent: resolvedAgent.Agent,
                        history: history,
                        options: callOptions,
                        cancellationToken: ct).ConfigureAwait(false);

                    if (!callResult.Success)
                    {
                        var reason = callResult.FailureReason ?? $"Ambient expert returned empty text for chunk {chunkIndex}/{chunkCount}";
                        return Fail(telemetry, effectiveRunId, reason);
                    }

                    if (!string.IsNullOrWhiteSpace(callResult.ModelUsed))
                    {
                        currentModelName = callResult.ModelUsed;
                    }

                    var parsed = _storyTaggingPipelineService.ParseAmbientMapping(callResult.ResponseText.Trim());

                    telemetry.Append(
                        effectiveRunId,
                        $"[chunk {chunkIndex}/{chunkCount}] Validated mapping: totalAmbient={parsed.Count}; model={currentModelName}");

                    ambientTags.AddRange(parsed);
                    var percentDone = chunkCount <= 0 ? 100 : (int)Math.Round((chunkIndex * 100.0) / chunkCount);
                    telemetry.ReportProgress(
                        chunkIndex,
                        Math.Max(1, chunkCount),
                        $"Ambient tags chunk {chunkIndex}/{chunkCount} completato ({percentDone}%)");
                }

                if (!_storyTaggingPipelineService.SaveAmbientTaggingResult(preparation, ambientTags, out var saveError))
                {
                    return Fail(telemetry, effectiveRunId, saveError ?? "Failed to save ambient tagging result");
                }

                telemetry.Append(effectiveRunId, $"[story {_storyId}] Ambient tags rebuilt from story_tags");

                var enqueued = _nextStatusEnqueuer.TryAdvanceAndEnqueueAmbient(
                    preparation.Story,
                    effectiveRunId,
                    _storyId,
                    _tuning.AmbientExpert.AutolaunchNextCommand);

                telemetry.ReportProgress(totalChunks, totalChunks, "Ambient tags completati (100%)");
                telemetry.MarkCompleted(effectiveRunId, "ok");
                return new CommandResult(true, enqueued
                    ? "Ambient tags added (next status enqueued)"
                    : "Ambient tags added");
            }
            catch (OperationCanceledException)
            {
                return Fail(telemetry, effectiveRunId, "Operation cancelled");
            }
            catch (Exception ex)
            {
                return Fail(telemetry, effectiveRunId, ex.Message);
            }
        }

        public Task<CommandResult> Execute(CommandContext context)
            => ExecuteAsync(context.CancellationToken, context.RunId);

        public Task Cancel(CommandContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        private static CommandResult Fail(ICommandTelemetry telemetry, string runId, string message)
        {
            telemetry.Append(runId, message, "error");
            telemetry.MarkCompleted(runId, "failed");
            return new CommandResult(false, message);
        }

        private static ICallCenter ResolveOrCreateCallCenter(DatabaseService database, StoriesService? storiesService, ICustomLogger? logger)
        {
            if (storiesService?.ScopeFactory != null)
            {
                using var scope = storiesService.ScopeFactory.CreateScope();
                var scopedCallCenter = scope.ServiceProvider.GetService<ICallCenter>();
                if (scopedCallCenter != null)
                {
                    return scopedCallCenter;
                }
            }

            var rootCallCenter = ServiceLocator.Services?.GetService(typeof(ICallCenter)) as ICallCenter;
            if (rootCallCenter != null)
            {
                return rootCallCenter;
            }
            
            throw new InvalidOperationException("ICallCenter non disponibile per AddAmbientTagsToStoryCommand.");
        }

        private static int BuildThreadId(long storyId, int chunkIndex)
        {
            unchecked
            {
                return ((int)(storyId % int.MaxValue) * 397) ^ chunkIndex;
            }
        }
    }
}
