using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
        private readonly ModelExecutionOrchestrator _modelExecutionOrchestrator;
        private readonly Func<string?>? _currentDispatcherRunIdProvider;

        public string CommandName => "add_fx_tags_to_story";
        public int Priority => 2;
        public event EventHandler<CommandProgressEventArgs>? Progress;

        public AddFxTagsToStoryCommand(
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
        public AddFxTagsToStoryCommand(
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
                ? (_currentDispatcherRunIdProvider?.Invoke() ?? $"add_fx_tags_to_story_{_storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}")
                : runId;

            _logger?.Start(effectiveRunId);
            _logger?.Append(effectiveRunId, $"[story {_storyId}] Starting add_fx_tags_to_story pipeline");

            try
            {
                var resolvedAgent = _agentResolutionService.Resolve(CommandRoleCodes.FxExpert);
                var currentModelId = resolvedAgent.ModelId;
                var currentModelName = resolvedAgent.ModelName;
                var triedModelNames = resolvedAgent.TriedModelNames;

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

                var executionOptions = BuildExecutionOptions(_tuning.FxExpert);
                var fxTags = new List<StoryTaggingService.StoryTagEntry>();

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

                    var executionResult = await _modelExecutionOrchestrator.ExecuteAsync(
                        new ModelExecutionRequest
                        {
                            RoleCode = CommandRoleCodes.FxExpert,
                            Agent = resolvedAgent.Agent,
                            InitialModelId = currentModelId,
                            InitialModelName = currentModelName,
                            TriedModelNames = triedModelNames,
                            SystemPrompt = systemPrompt,
                            WorkInput = chunk.Text,
                            RunId = effectiveRunId,
                            ChunkIndex = chunkIndex,
                            ChunkCount = chunkCount,
                            WorkLabel = "FX tagging",
                            Options = executionOptions,
                            WorkAsync = (bridge, token) => ProcessFxChunkAsync(
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
                        return Fail(effectiveRunId, $"FX expert returned empty text for chunk {chunkIndex}/{chunkCount}");
                    }

                    var parsed = _storyTaggingPipelineService.ParseFxMapping(mappingText, out var invalidLines);
                    if (invalidLines > 0)
                    {
                        return Fail(effectiveRunId, $"Formato FX non valido: {invalidLines} righe non rispettano il formato richiesto.");
                    }

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

        private async Task<ModelWorkResult> ProcessFxChunkAsync(
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

                var tags = StoryTaggingService.ParseFxMapping(cleaned, out var invalidLines);
                if (invalidLines > 0)
                {
                    return ModelWorkResult.Fail(
                        $"Formato FX non valido: {invalidLines} righe non rispettano il formato richiesto.",
                        cleaned);
                }

                var minFxTags = Math.Max(0, _tuning.FxExpert.MinFxTagsPerChunk);
                var tagCount = tags.Count;
                if (tagCount < minFxTags)
                {
                    return ModelWorkResult.Fail(
                        $"Hai inserito {tagCount} righe valide. Devi inserire ALMENO {minFxTags} effetti sonori (formato: ID descrizione [secondi]).",
                        cleaned);
                }

                _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Validated mapping: totalFx={tagCount}");
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

        private static ModelExecutionOptions BuildExecutionOptions(CommandTuningOptions.FxExpertTuning tuning)
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
