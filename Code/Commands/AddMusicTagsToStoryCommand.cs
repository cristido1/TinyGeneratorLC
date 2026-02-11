using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
        private readonly ModelExecutionOrchestrator _modelExecutionOrchestrator;
        private readonly Func<string?>? _currentDispatcherRunIdProvider;

        public string CommandName => "add_music_tags_to_story";
        public int Priority => 2;
        public event EventHandler<CommandProgressEventArgs>? Progress;

        public AddMusicTagsToStoryCommand(
            long storyId,
            ILangChainKernelFactory kernelFactory,
            IAgentResolutionService agentResolutionService,
            IStoryTaggingPipelineService storyTaggingPipelineService,
            INextStatusEnqueuer nextStatusEnqueuer,
            ICustomLogger? logger = null,
            CommandTuningOptions? tuning = null,
            Func<string?>? currentDispatcherRunIdProvider = null,
            ModelExecutionOrchestrator? modelExecutionOrchestrator = null)
        {
            _storyId = storyId;
            _agentResolutionService = agentResolutionService ?? throw new ArgumentNullException(nameof(agentResolutionService));
            _storyTaggingPipelineService = storyTaggingPipelineService ?? throw new ArgumentNullException(nameof(storyTaggingPipelineService));
            _nextStatusEnqueuer = nextStatusEnqueuer ?? throw new ArgumentNullException(nameof(nextStatusEnqueuer));
            _logger = logger;
            _tuning = tuning ?? new CommandTuningOptions();
            _currentDispatcherRunIdProvider = currentDispatcherRunIdProvider;
            _modelExecutionOrchestrator = modelExecutionOrchestrator
                ?? new ModelExecutionOrchestrator(kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory)), null, logger);
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
                kernelFactory,
                new AgentResolutionService(database),
                new StoryTaggingPipelineService(database),
                new NextStatusEnqueuer(storiesService, logger),
                logger,
                tuning,
                () => storiesService?.CurrentDispatcherRunId,
                new ModelExecutionOrchestrator(kernelFactory, storiesService?.ScopeFactory, logger))
        {
        }

        public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default, string? runId = null)
        {
            var effectiveRunId = string.IsNullOrWhiteSpace(runId)
                ? (_currentDispatcherRunIdProvider?.Invoke() ?? $"add_music_tags_to_story_{_storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}")
                : runId;

            _logger?.Start(effectiveRunId);
            _logger?.Append(effectiveRunId, $"[story {_storyId}] Starting add_music_tags_to_story pipeline");

            try
            {
                var resolvedAgent = _agentResolutionService.Resolve(CommandRoleCodes.MusicExpert);
                var currentModelId = resolvedAgent.ModelId;
                var currentModelName = resolvedAgent.ModelName;
                var triedModelNames = resolvedAgent.TriedModelNames;

                var systemPrompt = TaggingResponseFormat.AppendToSystemPrompt(
                    resolvedAgent.BaseSystemPrompt,
                    StoryTaggingService.TagTypeMusic);

                var preparation = _storyTaggingPipelineService.PrepareTagging(
                    _storyId,
                    _tuning.MusicExpert.MinTokensPerChunk,
                    _tuning.MusicExpert.MaxTokensPerChunk,
                    _tuning.MusicExpert.TargetTokensPerChunk);
                _storyTaggingPipelineService.PersistInitialRows(preparation);

                _logger?.Append(effectiveRunId, $"[story {_storyId}] Split into {preparation.Chunks.Count} chunks (rows)");

                var executionOptions = BuildExecutionOptions(_tuning.MusicExpert);
                var musicTags = new List<StoryTaggingService.StoryTagEntry>();

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
                        $"Adding music tags chunk {chunkIndex}/{chunkCount}");

                    var executionResult = await _modelExecutionOrchestrator.ExecuteAsync(
                        new ModelExecutionRequest
                        {
                            RoleCode = CommandRoleCodes.MusicExpert,
                            Agent = resolvedAgent.Agent,
                            InitialModelId = currentModelId,
                            InitialModelName = currentModelName,
                            TriedModelNames = triedModelNames,
                            SystemPrompt = systemPrompt,
                            WorkInput = chunk.Text,
                            RunId = effectiveRunId,
                            ChunkIndex = chunkIndex,
                            ChunkCount = chunkCount,
                            WorkLabel = "Music tagging",
                            Options = executionOptions,
                            WorkAsync = (bridge, token) => ProcessMusicChunkAsync(
                                bridge,
                                systemPrompt,
                                chunk.Text,
                                chunkIndex,
                                chunkCount,
                                effectiveRunId,
                                token)
                        },
                        ct).ConfigureAwait(false);

                    currentModelId = executionResult.ModelId;
                    currentModelName = executionResult.ModelName;

                    var mappingText = executionResult.OutputText;
                    if (string.IsNullOrWhiteSpace(mappingText))
                    {
                        return Fail(effectiveRunId, $"Music expert returned empty text for chunk {chunkIndex}/{chunkCount}");
                    }

                    var parsed = _storyTaggingPipelineService.ParseMusicMapping(mappingText);
                    parsed = StoryTaggingService.FilterMusicTagsByProximity(parsed, minLineDistance: 20);
                    musicTags.AddRange(parsed);
                }

                if (!_storyTaggingPipelineService.SaveTaggingResult(preparation, musicTags, StoryTaggingService.TagTypeMusic, out var saveError))
                {
                    return Fail(effectiveRunId, saveError ?? $"Failed to persist tagged story for {_storyId}");
                }

                _logger?.Append(effectiveRunId, $"[story {_storyId}] Music tags rebuilt from story_tags");

                var enqueued = _nextStatusEnqueuer.TryAdvanceAndEnqueueMusic(
                    preparation.Story,
                    effectiveRunId,
                    _storyId,
                    _tuning.MusicExpert.AutolaunchNextCommand);

                _logger?.MarkCompleted(effectiveRunId, "ok");
                return new CommandResult(
                    true,
                    enqueued
                        ? "Music tags added (next status enqueued)"
                        : "Music tags added");
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

        private async Task<ModelWorkResult> ProcessMusicChunkAsync(
            LangChainChatBridge bridge,
            string? systemPrompt,
            string chunkText,
            int chunkIndex,
            int chunkCount,
            string runId,
            CancellationToken ct)
        {
            try
            {
                var messages = new List<ConversationMessage>();
                if (!string.IsNullOrWhiteSpace(systemPrompt))
                {
                    messages.Add(new ConversationMessage { Role = "system", Content = systemPrompt });
                }

                messages.Add(new ConversationMessage { Role = "user", Content = chunkText });

                var responseJson = await bridge.CallModelWithToolsAsync(
                    messages,
                    new List<Dictionary<string, object>>(),
                    ct,
                    skipResponseChecker: false).ConfigureAwait(false);

                var (textContent, _) = LangChainChatBridge.ParseChatResponse(responseJson);
                var cleaned = textContent?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(cleaned))
                {
                    return ModelWorkResult.Fail("Il testo ritornato e' vuoto.");
                }

                var tags = StoryTaggingService.ParseMusicMapping(cleaned);
                tags = StoryTaggingService.FilterMusicTagsByProximity(tags, minLineDistance: 20);

                var requiredTags = ComputeRequiredMusicTagsForChunk(chunkText);
                var tagCount = tags.Count;
                if (tagCount < requiredTags)
                {
                    return ModelWorkResult.Fail(
                        $"Hai inserito solo {tagCount} righe valide. Devi inserire ALMENO {requiredTags} indicazioni musicali (formato: ID emozione [secondi]).",
                        cleaned);
                }

                _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Validated mapping: totalMusic={tagCount}");
                return ModelWorkResult.Ok(cleaned);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ModelWorkResult.Fail($"Errore durante l'elaborazione: {ex.Message}");
            }
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

        private static ModelExecutionOptions BuildExecutionOptions(CommandTuningOptions.MusicExpertTuning tuning)
        {
            return new ModelExecutionOptions
            {
                MaxAttemptsPerModel = Math.Max(1, tuning.MaxAttemptsPerChunk),
                RetryDelayBaseSeconds = Math.Max(0, tuning.RetryDelayBaseSeconds),
                EnableFallback = tuning.EnableFallback,
                EnableDiagnosis = tuning.DiagnoseOnFinalFailure
            };
        }

        private CommandResult Fail(string runId, string message)
        {
            _logger?.Append(runId, message, "error");
            _logger?.MarkCompleted(runId, "failed");
            return new CommandResult(false, message);
        }

        private void ReportProgress(string runId, int current, int max, string description)
        {
            try
            {
                Progress?.Invoke(this, new CommandProgressEventArgs(current, max, description));
            }
            catch
            {
                // best-effort
            }
        }
    }
}
