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
    /// Reads story_tagged, splits into chunks, adds music tags via music_expert agent,
    /// updates story_tagged, then enqueues TTS schema generation (the final step).
    /// </summary>
    public sealed class AddMusicTagsToStoryCommand : ICommand
    {
        private readonly long _storyId;
        private readonly CommandTuningOptions _tuning;
        private readonly IAgentResolutionService _agentResolutionService;
        private readonly IStoryTaggingPipelineService _storyTaggingPipelineService;
        private readonly INextStatusEnqueuer _nextStatusEnqueuer;
        private readonly ICustomLogger? _logger;
        private readonly ICallCenter _callCenter;
        private readonly ICommandDispatcher? _dispatcher;
        private readonly Func<string?>? _currentDispatcherRunIdProvider;

        public string CommandName => "add_music_tags_to_story";
        public int Priority => 2;
        public event EventHandler<CommandProgressEventArgs>? Progress;

        public AddMusicTagsToStoryCommand(
            long storyId,
            IAgentResolutionService agentResolutionService,
            IStoryTaggingPipelineService storyTaggingPipelineService,
            INextStatusEnqueuer nextStatusEnqueuer,
            ICallCenter callCenter,
            ICommandDispatcher? dispatcher = null,
            ICustomLogger? logger = null,
            CommandTuningOptions? tuning = null,
            Func<string?>? currentDispatcherRunIdProvider = null)
        {
            _storyId = storyId;
            _agentResolutionService = agentResolutionService ?? throw new ArgumentNullException(nameof(agentResolutionService));
            _storyTaggingPipelineService = storyTaggingPipelineService ?? throw new ArgumentNullException(nameof(storyTaggingPipelineService));
            _nextStatusEnqueuer = nextStatusEnqueuer ?? throw new ArgumentNullException(nameof(nextStatusEnqueuer));
            _callCenter = callCenter ?? throw new ArgumentNullException(nameof(callCenter));
            _dispatcher = dispatcher;
            _logger = logger;
            _tuning = tuning ?? new CommandTuningOptions();
            _currentDispatcherRunIdProvider = currentDispatcherRunIdProvider;
        }

        // Legacy constructor kept for backward compatibility with existing call sites.
        public AddMusicTagsToStoryCommand(
            long storyId,
            DatabaseService database,
            ILangChainKernelFactory kernelFactory,
            StoriesService? storiesService = null,
            ICustomLogger? logger = null,
            CommandTuningOptions? tuning = null)
            : this(
                storyId,
                new AgentResolutionService(database),
                new StoryTaggingPipelineService(database),
                new NextStatusEnqueuer(storiesService, logger),
                ResolveOrCreateCallCenter(database, storiesService, logger),
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
                ? (_currentDispatcherRunIdProvider?.Invoke() ?? $"add_music_tags_to_story_{_storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}")
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
            telemetry.Append(effectiveRunId, $"[story {_storyId}] Starting add_music_tags_to_story pipeline");

            try
            {
                var resolvedAgent = _agentResolutionService.Resolve(CommandRoleCodes.MusicExpert);
                var currentModelName = resolvedAgent.ModelName;

                var systemPrompt = TaggingResponseFormat.AppendToSystemPrompt(
                    resolvedAgent.BaseSystemPrompt,
                    StoryTaggingService.TagTypeMusic);

                var preparation = _storyTaggingPipelineService.PrepareTagging(
                    _storyId,
                    _tuning.MusicExpert.MinTokensPerChunk,
                    _tuning.MusicExpert.MaxTokensPerChunk,
                    _tuning.MusicExpert.TargetTokensPerChunk);
                _storyTaggingPipelineService.PersistInitialRows(preparation);

                telemetry.Append(effectiveRunId, $"[story {_storyId}] Split into {preparation.Chunks.Count} chunks (rows)");
                telemetry.ReportProgress(
                    0,
                    Math.Max(1, preparation.Chunks.Count),
                    preparation.Chunks.Count > 0
                        ? $"Music tags: preparazione completata, chunk 0/{preparation.Chunks.Count} (0%)"
                        : "Music tags: nessun chunk da processare");

                var musicTags = new List<StoryTaggingService.StoryTagEntry>();

                for (int i = 0; i < preparation.Chunks.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = preparation.Chunks[i];
                    var chunkIndex = i + 1;
                    var chunkCount = preparation.Chunks.Count;
                    var percent = chunkCount <= 0 ? 0 : (int)Math.Round(((chunkIndex - 1) * 100.0) / chunkCount);

                    telemetry.ReportProgress(
                        Math.Max(0, chunkIndex - 1),
                        chunkCount,
                        $"Music tags chunk {chunkIndex}/{chunkCount} ({percent}%) - richiesta agente");

                    var history = new ChatHistory();
                    if (!string.IsNullOrWhiteSpace(systemPrompt))
                    {
                        history.AddSystem(systemPrompt);
                    }
                    history.AddUser(chunk.Text);

                    var requiredTags = ComputeRequiredMusicTagsForChunk(chunk.Text);
                    var callOptions = new CallOptions
                    {
                        Timeout = TimeSpan.FromSeconds(Math.Max(1, _tuning.MusicExpert.MaxAttemptsPerChunk * 15)),
                        MaxRetries = Math.Max(0, _tuning.MusicExpert.MaxAttemptsPerChunk - 1),
                        UseResponseChecker = true,
                        AskFailExplanation = _tuning.MusicExpert.DiagnoseOnFinalFailure,
                        AllowFallback = _tuning.MusicExpert.EnableFallback,
                        Operation = CommandScopePaths.AddMusicTagsToStory,
                        SystemPromptOverride = systemPrompt
                    };
                    callOptions.DeterministicChecks.Add(new CheckMusicTagMinimumCount
                    {
                        Options = Options.Create<object>(new Dictionary<string, object>
                        {
                            ["RequiredTags"] = requiredTags,
                            ["MinLineDistance"] = 20
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
                        var reason = callResult.FailureReason ?? $"Music expert returned empty text for chunk {chunkIndex}/{chunkCount}";
                        return Fail(telemetry, effectiveRunId, reason);
                    }

                    if (!string.IsNullOrWhiteSpace(callResult.ModelUsed))
                    {
                        currentModelName = callResult.ModelUsed;
                    }

                    var parsed = _storyTaggingPipelineService.ParseMusicMapping(callResult.ResponseText.Trim());
                    parsed = StoryTaggingService.FilterMusicTagsByProximity(parsed, minLineDistance: 20);

                    telemetry.Append(effectiveRunId, $"[chunk {chunkIndex}/{chunkCount}] Validated mapping: totalMusic={parsed.Count}; model={currentModelName}");
                    musicTags.AddRange(parsed);
                    var percentDone = chunkCount <= 0 ? 100 : (int)Math.Round((chunkIndex * 100.0) / chunkCount);
                    telemetry.ReportProgress(
                        chunkIndex,
                        chunkCount,
                        $"Music tags chunk {chunkIndex}/{chunkCount} completato ({percentDone}%)");
                }

                if (!_storyTaggingPipelineService.SaveTaggingResult(preparation, musicTags, StoryTaggingService.TagTypeMusic, out var saveError))
                {
                    return Fail(telemetry, effectiveRunId, saveError ?? $"Failed to persist tagged story for {_storyId}");
                }

                telemetry.Append(effectiveRunId, $"[story {_storyId}] Music tags rebuilt from story_tags");

                var enqueued = _nextStatusEnqueuer.TryAdvanceAndEnqueueMusic(
                    preparation.Story,
                    effectiveRunId,
                    _storyId,
                    _tuning.MusicExpert.AutolaunchNextCommand);

                telemetry.MarkCompleted(effectiveRunId, "ok");
                telemetry.ReportProgress(
                    Math.Max(1, preparation.Chunks.Count),
                    Math.Max(1, preparation.Chunks.Count),
                    "Music tags completati (100%)");
                return new CommandResult(
                    true,
                    enqueued
                        ? "Music tags added (next status enqueued)"
                        : "Music tags added");
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

        private int ComputeRequiredMusicTagsForChunk(string chunkText)
        {
            var maxRequired = Math.Max(1, _tuning.MusicExpert.MaxMusicTagsPerChunkRequirement);
            var minRequired = Math.Max(0, _tuning.MusicExpert.MinMusicTagsPerChunkRequirement);

            if (string.IsNullOrWhiteSpace(chunkText)) return maxRequired;

            var approxTokens = Math.Max(1, chunkText.Length / 4);
            int required;
            if (approxTokens <= 450) required = 1;
            else if (approxTokens <= 900) required = 2;
            else required = 3;

            required = Math.Clamp(required, minRequired, maxRequired);
            return required;
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
            
            throw new InvalidOperationException("ICallCenter non disponibile per AddMusicTagsToStoryCommand.");
        }

        private static int BuildThreadId(long storyId, int chunkIndex)
        {
            unchecked
            {
                return ((int)(storyId % int.MaxValue) * 991) ^ chunkIndex;
            }
        }
    }
}
