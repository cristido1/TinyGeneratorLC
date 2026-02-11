using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
        private readonly IChunkProcessingService _chunkProcessingService;
        private readonly IStoryTaggingPipelineService _storyTaggingPipelineService;
        private readonly INextStatusEnqueuer _nextStatusEnqueuer;
        private readonly ICustomLogger? _logger;
        private readonly Func<string?>? _currentDispatcherRunIdProvider;

        public string CommandName => "add_ambient_tags_to_story";
        public int Priority => 2;
        public event EventHandler<CommandProgressEventArgs>? Progress;

        public AddAmbientTagsToStoryCommand(
            long storyId,
            IAgentResolutionService agentResolutionService,
            IChunkProcessingService chunkProcessingService,
            IStoryTaggingPipelineService storyTaggingPipelineService,
            INextStatusEnqueuer nextStatusEnqueuer,
            ICustomLogger? logger = null,
            CommandTuningOptions? tuning = null,
            Func<string?>? currentDispatcherRunIdProvider = null)
        {
            _storyId = storyId;
            _agentResolutionService = agentResolutionService ?? throw new ArgumentNullException(nameof(agentResolutionService));
            _chunkProcessingService = chunkProcessingService ?? throw new ArgumentNullException(nameof(chunkProcessingService));
            _storyTaggingPipelineService = storyTaggingPipelineService ?? throw new ArgumentNullException(nameof(storyTaggingPipelineService));
            _nextStatusEnqueuer = nextStatusEnqueuer ?? throw new ArgumentNullException(nameof(nextStatusEnqueuer));
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
                new ChunkProcessingService(kernelFactory, storiesService?.ScopeFactory, logger),
                new StoryTaggingPipelineService(database),
                new NextStatusEnqueuer(storiesService, logger),
                logger,
                tuning,
                () => storiesService?.CurrentDispatcherRunId)
        {
        }

        public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default, string? runId = null)
        {
            var effectiveRunId = string.IsNullOrWhiteSpace(runId)
                ? (_currentDispatcherRunIdProvider?.Invoke() ?? $"add_ambient_tags_to_story_{_storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}")
                : runId;

            var telemetry = new CommandTelemetry(_logger, args => Progress?.Invoke(this, args));
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

                var currentModelId = resolvedAgent.ModelId;
                var currentModelName = resolvedAgent.ModelName;
                var triedModelNames = resolvedAgent.TriedModelNames;

                var ambientTags = new List<StoryTaggingService.StoryTagEntry>();

                for (int i = 0; i < preparation.Chunks.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = preparation.Chunks[i];
                    telemetry.ReportProgress(i + 1, preparation.Chunks.Count, $"Adding ambient tags chunk {i + 1}/{preparation.Chunks.Count}");

                    var chunkResult = await _chunkProcessingService.ProcessAsync(
                        new ChunkProcessRequest(
                            resolvedAgent.Agent,
                            CommandRoleCodes.AmbientExpert,
                            systemPrompt,
                            chunk.Text,
                            i + 1,
                            preparation.Chunks.Count,
                            effectiveRunId,
                            currentModelId,
                            currentModelName,
                            triedModelNames,
                            _tuning.AmbientExpert,
                            telemetry,
                            CommandScopePaths.AddAmbientTagsToStory),
                        ct).ConfigureAwait(false);

                    currentModelId = chunkResult.ModelId;
                    currentModelName = chunkResult.ModelName;

                    if (string.IsNullOrWhiteSpace(chunkResult.MappingText))
                    {
                        return Fail(telemetry, effectiveRunId, $"Ambient expert returned empty text for chunk {i + 1}/{preparation.Chunks.Count}");
                    }

                    var parsed = _storyTaggingPipelineService.ParseAmbientMapping(chunkResult.MappingText);
                    ambientTags.AddRange(parsed);
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
    }
}

