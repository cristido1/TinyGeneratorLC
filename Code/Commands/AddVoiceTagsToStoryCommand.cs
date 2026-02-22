using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
        private readonly ICallCenter _callCenter;
        private readonly Func<string?>? _currentDispatcherRunIdProvider;

        public string CommandName => "add_voice_tags_to_story";
        public int Priority => 2;
        public event EventHandler<CommandProgressEventArgs>? Progress;

        public AddVoiceTagsToStoryCommand(
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
        public AddVoiceTagsToStoryCommand(
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
                ? (_currentDispatcherRunIdProvider?.Invoke() ?? $"add_voice_tags_to_story_{_storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}")
                : runId;

            _logger?.Start(effectiveRunId);
            _logger?.Append(effectiveRunId, $"[story {_storyId}] Starting add_voice_tags_to_story pipeline");

            try
            {
                var resolvedAgent = _agentResolutionService.Resolve(CommandRoleCodes.Formatter);
                var formatterAgent = resolvedAgent.Agent;
                var currentModelName = resolvedAgent.ModelName;

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

                var formatterTags = new List<StoryTaggingService.StoryTagEntry>();

                for (int i = 0; i < preparation.Chunks.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = preparation.Chunks[i];
                    var chunkIndex = i + 1;
                    var chunkCount = preparation.Chunks.Count;
                    var maxDialogueLinesPerRequest = Math.Max(1, _tuning.TransformStoryRawToTagged.MaxDialogueLinesPerFormatterRequest);
                    var chunkSlices = SplitChunkRowsByDialogueLimit(chunk.Rows, maxDialogueLinesPerRequest);

                    ReportProgress(
                        effectiveRunId,
                        chunkIndex,
                        chunkCount,
                        $"Formatting chunk {chunkIndex}/{chunkCount}");

                    if (chunkSlices.Count > 1)
                    {
                        _logger?.Append(
                            effectiveRunId,
                            $"[chunk {chunkIndex}/{chunkCount}] Suddiviso in {chunkSlices.Count} sotto-chunk (max {maxDialogueLinesPerRequest} righe dialogo)");
                    }

                    for (int sliceIndex = 0; sliceIndex < chunkSlices.Count; sliceIndex++)
                    {
                        var sliceRows = chunkSlices[sliceIndex];
                        var sliceText = BuildChunkText(sliceRows);
                        var userContent = BuildVoiceUserContent(sliceText, sliceRows, out var quoteLineIds);
                        if (quoteLineIds.Count == 0)
                        {
                            _logger?.Append(
                                effectiveRunId,
                                $"[chunk {chunkIndex}/{chunkCount} part {sliceIndex + 1}/{chunkSlices.Count}] Nessun dialogo tra doppi apici: skip formatter");
                            continue;
                        }

                        var history = new ChatHistory();
                        if (!string.IsNullOrWhiteSpace(systemPrompt))
                        {
                            history.AddSystem(systemPrompt);
                        }
                        history.AddUser(userContent);

                        var correctionRetries = Math.Max(0, _tuning.TransformStoryRawToTagged.FormatterV2CorrectionRetries);
                        var maxAttempts = correctionRetries > 0
                            ? 1 + correctionRetries
                            : Math.Max(1, _tuning.TransformStoryRawToTagged.MaxAttemptsPerChunk);

                        var callOptions = new CallOptions
                        {
                            Operation = CommandScopePaths.AddVoiceTagsToStory,
                            Timeout = TimeSpan.FromSeconds(90),
                            MaxRetries = Math.Max(0, maxAttempts - 1),
                            UseResponseChecker = true,
                            AllowFallback = _tuning.TransformStoryRawToTagged.EnableFallback,
                            AskFailExplanation = _tuning.TransformStoryRawToTagged.DiagnoseOnFinalFailure,
                            SystemPromptOverride = systemPrompt
                        };
                        foreach (var lineId in quoteLineIds.OrderBy(x => x))
                        {
                            callOptions.DeterministicChecks.Add(new CheckDialogueLineSpeakerEmotion
                            {
                                Options = Options.Create<object>(new Dictionary<string, object>
                                {
                                    ["LineId"] = lineId
                                })
                            });
                        }

                        var callResult = await _callCenter.CallAgentAsync(
                            storyId: _storyId,
                            threadId: BuildThreadId(_storyId, (chunkIndex * 1000) + sliceIndex + 1),
                            agent: formatterAgent,
                            history: history,
                            options: callOptions,
                            cancellationToken: ct).ConfigureAwait(false);

                        if (!callResult.Success)
                        {
                            return Fail(effectiveRunId, callResult.FailureReason ?? "Formatter call failed");
                        }

                        if (!string.IsNullOrWhiteSpace(callResult.ModelUsed))
                        {
                            currentModelName = callResult.ModelUsed;
                        }

                        var mappingText = (callResult.ResponseText ?? string.Empty).Trim();
                        var normalizedMapping = NormalizeVoiceMapping(mappingText, quoteLineIds, out var errorMessage);
                        if (!string.IsNullOrWhiteSpace(errorMessage))
                        {
                            return Fail(effectiveRunId, errorMessage);
                        }

                        var parsedTags = _storyTaggingPipelineService.ParseFormatterMapping(sliceRows, normalizedMapping);
                        formatterTags.AddRange(parsedTags);
                        _logger?.Append(
                            effectiveRunId,
                            $"[chunk {chunkIndex}/{chunkCount} part {sliceIndex + 1}/{chunkSlices.Count}] Validated mapping: {parsedTags.Count} lines; model={currentModelName}");
                    }
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

        private static string BuildVoiceUserContent(
            string chunkText,
            IReadOnlyList<StoryTaggingService.StoryRow> chunkRows,
            out HashSet<int> quoteLineIds)
        {
            quoteLineIds = new HashSet<int>();
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

            return (dialogueLines.Count > 0
                    ? "RIGHE DI DIALOGO (tra virgolette) DA TAGGARE CON PERSONAGGIO+EMOZIONE:\n" + string.Join("\n", dialogueLines) + "\n\n"
                    : "NESSUNA RIGA DI DIALOGO TRA VIRGOLETTE IN QUESTO TESTO.\n\n")
                   + "TESTO COMPLETO (righe numerate):\n" + chunkText;
        }

        private static string BuildChunkText(IReadOnlyList<StoryTaggingService.StoryRow> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(
                "\n",
                rows.Select(r => $"{r.LineId:000} {r.Text}".TrimEnd()));
        }

        private static List<List<StoryTaggingService.StoryRow>> SplitChunkRowsByDialogueLimit(
            IReadOnlyList<StoryTaggingService.StoryRow> rows,
            int maxDialogueLinesPerSlice)
        {
            var result = new List<List<StoryTaggingService.StoryRow>>();
            if (rows == null || rows.Count == 0)
            {
                return result;
            }

            var limit = Math.Max(1, maxDialogueLinesPerSlice);
            var current = new List<StoryTaggingService.StoryRow>();
            var currentDialogueCount = 0;

            foreach (var row in rows)
            {
                var isDialogue = StartsWithOpeningQuote(row.Text);
                if (isDialogue && currentDialogueCount >= limit && current.Count > 0)
                {
                    result.Add(current);
                    current = new List<StoryTaggingService.StoryRow>();
                    currentDialogueCount = 0;
                }

                current.Add(row);
                if (isDialogue)
                {
                    currentDialogueCount++;
                }
            }

            if (current.Count > 0)
            {
                result.Add(current);
            }

            return result;
        }

        private static string NormalizeVoiceMapping(string mappingText, HashSet<int> quoteLineIds, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(mappingText))
            {
                if (quoteLineIds.Count == 0)
                {
                    return string.Empty;
                }

                if (quoteLineIds.Count == 1)
                {
                    var onlyId = quoteLineIds.First();
                    return $"{onlyId:000} [PERSONAGGIO: Sconosciuto] [EMOZIONE: neutra]";
                }

                error = "La risposta e' vuota ma ci sono righe di dialogo: devi restituire il mapping per le righe dove parla qualcuno.";
                return string.Empty;
            }

            var idToTags = FormatterV2.ParseIdToTagsMapping(mappingText);
            if (quoteLineIds.Count > 0)
            {
                var bad = new List<int>();
                foreach (var id in quoteLineIds.OrderBy(x => x))
                {
                    var check = new CheckDialogueLineSpeakerEmotion
                    {
                        Options = Options.Create<object>(new Dictionary<string, object>
                        {
                            ["LineId"] = id
                        })
                    };
                    var checkResult = check.Execute(mappingText);
                    if (!checkResult.Successed)
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
                    error = $"Controllo fallito: sulle righe con doppi apici deve esserci PERSONAGGIO+EMOZIONE. ID: {string.Join(", ", bad.Select(x => x.ToString("000")))}";
                    return string.Empty;
                }
            }

            return string.Join(
                "\n",
                idToTags
                    .OrderBy(k => k.Key)
                    .Select(k => $"{k.Key:000} {k.Value}".TrimEnd()));
        }

        private static bool StartsWithOpeningQuote(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var trimmed = text.TrimStart();
            if (trimmed.Length == 0) return false;
            return trimmed[0] == '"' || trimmed[0] == '\u201C' || trimmed[0] == '\u00AB';
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
            var rootCallCenter = ServiceLocator.Services?.GetService(typeof(ICallCenter)) as ICallCenter;
            if (rootCallCenter != null)
            {
                return rootCallCenter;
            }

            IAgentCallService? agentCallService = null;
            if (storiesService?.ScopeFactory != null)
            {
                using var scope = storiesService.ScopeFactory.CreateScope();
                agentCallService = scope.ServiceProvider.GetService<IAgentCallService>();
            }

            agentCallService ??= ServiceLocator.Services?.GetService(typeof(IAgentCallService)) as IAgentCallService;
            if (agentCallService == null)
            {
                throw new InvalidOperationException("ICallCenter/IAgentCallService non disponibile per AddVoiceTagsToStoryCommand.");
            }

            return new CallCenter(agentCallService, database, logger);
        }

        private static int BuildThreadId(long storyId, int chunkIndex)
        {
            unchecked
            {
                return ((int)(storyId % int.MaxValue) * 881) ^ chunkIndex;
            }
        }
    }
}
