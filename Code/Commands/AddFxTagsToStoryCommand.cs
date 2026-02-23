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
    /// Reads story_tagged, splits into chunks, adds FX tags via fx_expert agent,
    /// updates story_tagged, then enqueues add_music_tags_to_story.
    /// </summary>
    public sealed class AddFxTagsToStoryCommand : ICommand
    {
        private readonly long _storyId;
        private readonly CommandTuningOptions _tuning;
        private readonly IAgentResolutionService _agentResolutionService;
        private readonly IStoryTaggingPipelineService _storyTaggingPipelineService;
        private readonly INextStatusEnqueuer _nextStatusEnqueuer;
        private readonly ICustomLogger? _logger;
        private readonly ICallCenter _callCenter;
        private readonly Func<string?>? _currentDispatcherRunIdProvider;

        public string CommandName => "add_fx_tags_to_story";
        public int Priority => 2;
        public event EventHandler<CommandProgressEventArgs>? Progress;

        public AddFxTagsToStoryCommand(
            long storyId,
            IAgentResolutionService agentResolutionService,
            IStoryTaggingPipelineService storyTaggingPipelineService,
            INextStatusEnqueuer nextStatusEnqueuer,
            ICallCenter callCenter,
            ICustomLogger? logger = null,
            CommandTuningOptions? tuning = null,
            Func<string?>? currentDispatcherRunIdProvider = null)
        {
            _storyId = storyId;
            _agentResolutionService = agentResolutionService ?? throw new ArgumentNullException(nameof(agentResolutionService));
            _storyTaggingPipelineService = storyTaggingPipelineService ?? throw new ArgumentNullException(nameof(storyTaggingPipelineService));
            _nextStatusEnqueuer = nextStatusEnqueuer ?? throw new ArgumentNullException(nameof(nextStatusEnqueuer));
            _callCenter = callCenter ?? throw new ArgumentNullException(nameof(callCenter));
            _logger = logger;
            _tuning = tuning ?? new CommandTuningOptions();
            _currentDispatcherRunIdProvider = currentDispatcherRunIdProvider;
        }

        // Legacy constructor kept for backward compatibility with existing call sites.
        public AddFxTagsToStoryCommand(
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
                logger,
                tuning,
                () => storiesService?.CurrentDispatcherRunId)
        {
            _ = kernelFactory;
        }

        public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default, string? runId = null)
        {
            var effectiveRunId = string.IsNullOrWhiteSpace(runId)
                ? (_currentDispatcherRunIdProvider?.Invoke() ?? $"add_fx_tags_to_story_{_storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}")
                : runId;

            _logger?.Start(effectiveRunId);
            _logger?.Append(effectiveRunId, $"[story {_storyId}] Starting add_fx_tags_to_story pipeline");

            try
            {
                var resolvedAgent = _agentResolutionService.Resolve(CommandRoleCodes.FxExpert);
                var currentModelName = resolvedAgent.ModelName;

                var systemPrompt = TaggingResponseFormat.AppendToSystemPrompt(
                    resolvedAgent.BaseSystemPrompt,
                    StoryTaggingService.TagTypeFx);

                var fxTargetTokens = Math.Max(1, _tuning.FxExpert.DefaultTargetTokensPerChunk);
                var fxMaxTokens = Math.Max(1, _tuning.FxExpert.DefaultMaxTokensPerChunk);
                var fxMinTokens = Math.Max(0, Math.Min(fxTargetTokens, fxMaxTokens / 2));

                var preparation = _storyTaggingPipelineService.PrepareTagging(
                    _storyId,
                    fxMinTokens,
                    fxMaxTokens,
                    fxTargetTokens);
                _storyTaggingPipelineService.PersistInitialRows(preparation);

                _logger?.Append(effectiveRunId, $"[story {_storyId}] Split into {preparation.Chunks.Count} chunks (rows)");

                var fxTags = new List<StoryTaggingService.StoryTagEntry>();
                var minFxTags = Math.Max(0, _tuning.FxExpert.MinFxTagsPerChunk);

                for (int i = 0; i < preparation.Chunks.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = preparation.Chunks[i];
                    var chunkIndex = i + 1;
                    var chunkCount = preparation.Chunks.Count;

                    ReportProgress(
                        effectiveRunId,
                        chunkIndex,
                        chunkCount,
                        $"Adding FX tags chunk {chunkIndex}/{chunkCount}");

                    var history = new ChatHistory();
                    if (!string.IsNullOrWhiteSpace(systemPrompt))
                    {
                        history.AddSystem(systemPrompt);
                    }
                    history.AddUser(chunk.Text);

                    var callOptions = new CallOptions
                    {
                        Timeout = TimeSpan.FromSeconds(Math.Max(1, _tuning.FxExpert.MaxAttemptsPerChunk * 15)),
                        MaxRetries = Math.Max(0, _tuning.FxExpert.MaxAttemptsPerChunk - 1),
                        UseResponseChecker = true,
                        AskFailExplanation = _tuning.FxExpert.DiagnoseOnFinalFailure,
                        AllowFallback = _tuning.FxExpert.EnableFallback,
                        Operation = CommandScopePaths.AddFxTagsToStory,
                        SystemPromptOverride = systemPrompt
                    };
                    callOptions.DeterministicChecks.Add(new CheckFxMappingValidity
                    {
                        Options = Options.Create<object>(new Dictionary<string, object>
                        {
                            ["MinFxTags"] = minFxTags
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
                        var reason = callResult.FailureReason ?? $"FX expert returned empty text for chunk {chunkIndex}/{chunkCount}";
                        return Fail(effectiveRunId, reason);
                    }

                    if (!string.IsNullOrWhiteSpace(callResult.ModelUsed))
                    {
                        currentModelName = callResult.ModelUsed;
                    }

                    var parsed = _storyTaggingPipelineService.ParseFxMapping(callResult.ResponseText.Trim(), out var invalidLines);
                    if (invalidLines > 0)
                    {
                        return Fail(effectiveRunId, $"Formato FX non valido: {invalidLines} righe non rispettano il formato richiesto.");
                    }

                    _logger?.Append(effectiveRunId, $"[chunk {chunkIndex}/{chunkCount}] Validated mapping: totalFx={parsed.Count}; model={currentModelName}");
                    fxTags.AddRange(parsed);
                }

                if (!_storyTaggingPipelineService.SaveTaggingResult(preparation, fxTags, StoryTaggingService.TagTypeFx, out var saveError))
                {
                    return Fail(effectiveRunId, saveError ?? $"Failed to persist tagged story for {_storyId}");
                }

                _logger?.Append(effectiveRunId, $"[story {_storyId}] FX tags rebuilt from story_tags");

                var enqueued = _nextStatusEnqueuer.TryAdvanceAndEnqueueFx(
                    preparation.Story,
                    effectiveRunId,
                    _storyId,
                    _tuning.FxExpert.AutolaunchNextCommand);

                _logger?.MarkCompleted(effectiveRunId, "ok");
                return new CommandResult(
                    true,
                    enqueued
                        ? "FX tags added (next status enqueued)"
                        : "FX tags added");
            }
            catch (OperationCanceledException)
            {
                return Fail(effectiveRunId, "Operation cancelled");
            }
            catch (Exception ex)
            {
                return Fail(effectiveRunId, ex.Message);
            }
        }

        public Task<CommandResult> Execute(CommandContext context)
            => ExecuteAsync(context.CancellationToken, context.RunId);

        public Task Cancel(CommandContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        private CommandResult Fail(string runId, string message)
        {
            _logger?.Append(runId, message, "error");
            _logger?.MarkCompleted(runId, "failed");
            return new CommandResult(false, message);
        }

        private void ReportProgress(string runId, int current, int max, string description)
        {
            _ = runId;
            try
            {
                Progress?.Invoke(this, new CommandProgressEventArgs(current, max, description));
            }
            catch
            {
                // best-effort
            }
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
            
            throw new InvalidOperationException("ICallCenter non disponibile per AddFxTagsToStoryCommand.");
        }

        private static int BuildThreadId(long storyId, int chunkIndex)
        {
            unchecked
            {
                return ((int)(storyId % int.MaxValue) * 733) ^ chunkIndex;
            }
        }
    }
}
