using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands
{
    public sealed class AddVoiceTagsToStoryCommand : ICommand
    {
        private readonly long _storyId;
        private readonly CommandTuningOptions _tuning;
        private readonly IAgentResolutionService _agentResolutionService;
        private readonly IStoryTaggingPipelineService _storyTaggingPipelineService;
        private readonly INextStatusEnqueuer _nextStatusEnqueuer;
        private readonly ICustomLogger? _logger;
        private readonly ModelExecutionOrchestrator _modelExecutionOrchestrator;
        private readonly Func<string?>? _currentDispatcherRunIdProvider;

        public string CommandName => "add_voice_tags_to_story";
        public int Priority => 2;
        public event EventHandler<CommandProgressEventArgs>? Progress;

        public AddVoiceTagsToStoryCommand(
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
        public AddVoiceTagsToStoryCommand(
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
                ? (_currentDispatcherRunIdProvider?.Invoke() ?? $"add_voice_tags_to_story_{_storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}")
                : runId;

            _logger?.Start(effectiveRunId);
            _logger?.Append(effectiveRunId, $"[story {_storyId}] Starting add_voice_tags_to_story pipeline");

            try
            {
                var resolvedAgent = _agentResolutionService.Resolve(CommandRoleCodes.Formatter);
                var formatterAgent = resolvedAgent.Agent;
                var currentModelId = resolvedAgent.ModelId;
                var currentModelName = resolvedAgent.ModelName;
                var triedModelNames = resolvedAgent.TriedModelNames;

                var systemPrompt = TaggingResponseFormat.AppendToSystemPrompt(
                    BuildSystemPrompt(formatterAgent),
                    StoryTaggingService.TagTypeFormatter);
                var promptHash = string.IsNullOrWhiteSpace(systemPrompt)
                    ? null
                    : ComputeSha256(systemPrompt);

                var preparation = _storyTaggingPipelineService.PrepareTagging(
                    _storyId,
                    _tuning.TransformStoryRawToTagged.MinTokensPerChunk,
                    _tuning.TransformStoryRawToTagged.MaxTokensPerChunk,
                    _tuning.TransformStoryRawToTagged.TargetTokensPerChunk);
                _storyTaggingPipelineService.PersistInitialRows(preparation);

                _logger?.Append(effectiveRunId, $"[story {_storyId}] Split into {preparation.Chunks.Count} chunks (rows)");

                var executionOptions = BuildExecutionOptions(_tuning.TransformStoryRawToTagged);
                var formatterTags = new List<StoryTaggingService.StoryTagEntry>();

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
                        $"Formatting chunk {chunkIndex}/{chunkCount}");

                    var executionResult = await _modelExecutionOrchestrator.ExecuteAsync(
                        new ModelExecutionRequest
                        {
                            RoleCode = CommandRoleCodes.Formatter,
                            Agent = formatterAgent,
                            InitialModelId = currentModelId,
                            InitialModelName = currentModelName,
                            TriedModelNames = triedModelNames,
                            SystemPrompt = systemPrompt,
                            WorkInput = chunk.Text,
                            RunId = effectiveRunId,
                            ChunkIndex = chunkIndex,
                            ChunkCount = chunkCount,
                            WorkLabel = "Formatting",
                            Options = executionOptions,
                            WorkAsync = (bridge, token) => ProcessVoiceChunkAsync(
                                bridge,
                                systemPrompt,
                                chunk.Text,
                                chunk.Rows,
                                chunkIndex,
                                chunkCount,
                                effectiveRunId,
                                token)
                        },
                        ct).ConfigureAwait(false);

                    currentModelId = executionResult.ModelId;
                    currentModelName = executionResult.ModelName;

                    var taggedText = executionResult.OutputText ?? string.Empty;
                    var parsedTags = _storyTaggingPipelineService.ParseFormatterMapping(chunk.Rows, taggedText);
                    formatterTags.AddRange(parsedTags);
                }

                if (!_storyTaggingPipelineService.SaveFormatterTaggingResult(
                    preparation,
                    formatterTags,
                    formatterAgent.ModelId,
                    promptHash,
                    out var saveError))
                {
                    return Fail(effectiveRunId, saveError ?? $"Failed to persist tagged story for {_storyId}");
                }

                _logger?.Append(effectiveRunId, $"[story {_storyId}] Tagged story rebuilt from story_tags");

                var enqueued = _nextStatusEnqueuer.TryAdvanceAndEnqueueVoice(
                    preparation.Story,
                    effectiveRunId,
                    _storyId,
                    _tuning.TransformStoryRawToTagged.AutolaunchNextCommand);

                _logger?.MarkCompleted(effectiveRunId, "ok");
                return new CommandResult(
                    true,
                    enqueued
                        ? "Tagged story generated (next status enqueued)"
                        : "Tagged story generated");
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

        private async Task<ModelWorkResult> ProcessVoiceChunkAsync(
            LangChainChatBridge bridge,
            string? systemPrompt,
            string chunkText,
            IReadOnlyList<StoryTaggingService.StoryRow> chunkRows,
            int chunkIndex,
            int chunkCount,
            string runId,
            CancellationToken ct)
        {
            if (chunkRows == null || chunkRows.Count == 0)
            {
                return ModelWorkResult.Fail("Chunk without rows");
            }

            try
            {
                var quoteLineIds = new HashSet<int>();
                foreach (var row in chunkRows)
                {
                    if (StartsWithOpeningQuote(row.Text))
                    {
                        quoteLineIds.Add(row.LineId);
                    }
                }

                var dialogueLines = new List<string>();
                if (quoteLineIds.Count > 0)
                {
                    foreach (var row in chunkRows)
                    {
                        if (quoteLineIds.Contains(row.LineId))
                        {
                            dialogueLines.Add($"{row.LineId:000} {row.Text}".TrimEnd());
                        }
                    }
                }

                var userContent =
                    (dialogueLines.Count > 0
                        ? "RIGHE DI DIALOGO (tra virgolette) DA TAGGARE CON PERSONAGGIO+EMOZIONE:\n" + string.Join("\n", dialogueLines) + "\n\n"
                        : "NESSUNA RIGA DI DIALOGO TRA VIRGOLETTE IN QUESTO TESTO.\n\n")
                    + "TESTO COMPLETO (righe numerate):\n" + chunkText;

                var messages = new List<ConversationMessage>();
                if (!string.IsNullOrWhiteSpace(systemPrompt))
                {
                    messages.Add(new ConversationMessage { Role = "system", Content = systemPrompt });
                }

                messages.Add(new ConversationMessage { Role = "user", Content = userContent });

                string responseJson;
                using (LogScope.Push(
                           LogScope.Current ?? CommandScopePaths.AddVoiceTagsToStory,
                           operationId: null,
                           stepNumber: null,
                           maxStep: null,
                           agentName: null,
                           agentRole: CommandRoleCodes.Formatter))
                {
                    responseJson = await bridge.CallModelWithToolsAsync(
                        messages,
                        new List<Dictionary<string, object>>(),
                        ct,
                        skipResponseChecker: false).ConfigureAwait(false);
                }

                var (textContent, _) = LangChainChatBridge.ParseChatResponse(responseJson);
                var mappingText = textContent?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(mappingText))
                {
                    if (quoteLineIds.Count == 0)
                    {
                        return ModelWorkResult.Ok(string.Empty);
                    }

                    if (quoteLineIds.Count == 1)
                    {
                        var onlyId = quoteLineIds.First();
                        var fallback = $"{onlyId:000} [PERSONAGGIO: Sconosciuto] [EMOZIONE: neutra]";
                        return ModelWorkResult.Ok(fallback);
                    }

                    return ModelWorkResult.Fail("La risposta e' vuota ma ci sono righe di dialogo: devi restituire il mapping per le righe dove parla qualcuno.");
                }

                var idToTags = FormatterV2.ParseIdToTagsMapping(mappingText);

                if (quoteLineIds.Count > 0)
                {
                    var bad = new List<int>();
                    foreach (var id in quoteLineIds.OrderBy(x => x))
                    {
                        if (!idToTags.TryGetValue(id, out var tags) ||
                            tags.IndexOf("[PERSONAGGIO:", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            bad.Add(id);
                            if (bad.Count >= 8) break;
                        }
                    }

                    if (bad.Count == 1)
                    {
                        var missingId = bad[0];
                        idToTags[missingId] = "[PERSONAGGIO: Sconosciuto] [EMOZIONE: neutra]";
                    }
                    else if (bad.Count > 1)
                    {
                        return ModelWorkResult.Fail(
                            $"Controllo fallito: sulle righe con doppi apici deve esserci un tag PERSONAGGIO. ID: {string.Join(", ", bad.Select(x => x.ToString("000")))}",
                            mappingText);
                    }
                }

                var mappingNormalized = string.Join(
                    "\n",
                    idToTags
                        .OrderBy(k => k.Key)
                        .Select(k => $"{k.Key:000} {k.Value}".TrimEnd()));

                _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Validated mapping: {idToTags.Count} lines");
                return ModelWorkResult.Ok(mappingNormalized);
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

        private static bool StartsWithOpeningQuote(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var trimmed = text.TrimStart();
            if (trimmed.Length == 0) return false;
            return trimmed[0] == '"' || trimmed[0] == '“' || trimmed[0] == '«';
        }

        private static ModelExecutionOptions BuildExecutionOptions(CommandTuningOptions.TransformStoryRawToTaggedTuning tuning)
        {
            var correctionRetries = Math.Max(0, tuning.FormatterV2CorrectionRetries);
            var maxAttempts = correctionRetries > 0
                ? 1 + correctionRetries
                : Math.Max(1, tuning.MaxAttemptsPerChunk);

            return new ModelExecutionOptions
            {
                MaxAttemptsPerModel = maxAttempts,
                RetryDelayBaseSeconds = Math.Max(0, tuning.RetryDelayBaseSeconds),
                EnableFallback = tuning.EnableFallback,
                EnableDiagnosis = tuning.DiagnoseOnFinalFailure
            };
        }

        private static string? BuildSystemPrompt(Agent agent)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(agent.Prompt))
            {
                parts.Add(agent.Prompt);
            }

            if (!string.IsNullOrWhiteSpace(agent.ExecutionPlan))
            {
                var plan = LoadExecutionPlan(agent.ExecutionPlan);
                if (!string.IsNullOrWhiteSpace(plan))
                {
                    parts.Add(plan);
                }
            }

            if (!string.IsNullOrWhiteSpace(agent.Instructions))
            {
                parts.Add(agent.Instructions);
            }

            return parts.Count == 0 ? null : string.Join("\n\n", parts);
        }

        private static string? LoadExecutionPlan(string planName)
        {
            try
            {
                var planPath = Path.Combine(Directory.GetCurrentDirectory(), "execution_plans", planName);
                if (File.Exists(planPath))
                {
                    return File.ReadAllText(planPath);
                }
            }
            catch
            {
                // ignore loading errors
            }

            return null;
        }

        private static string ComputeSha256(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var hash = SHA256.HashData(bytes);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
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
