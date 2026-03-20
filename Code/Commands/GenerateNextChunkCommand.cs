using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands;

public sealed class GenerateNextChunkCommand : ICommand
{
    private readonly CommandTuningOptions _tuning;

    public sealed record GenerateChunkOptions(
        bool RequireCliffhanger = true,
        bool IsFinalChunk = false,
        int? TargetWords = null);

    private readonly long _storyId;
    private readonly int _writerAgentId;
    private readonly DatabaseService _database;
    private readonly ICustomLogger? _logger;
    private readonly TextValidationService _textValidationService;
    private readonly ICallCenter? _callCenter;
    private readonly GenerateChunkOptions _options;
    private readonly IServiceScopeFactory? _scopeFactory;

    public GenerateNextChunkCommand(
        long storyId,
        int writerAgentId,
        DatabaseService database,
        ILangChainKernelFactory kernelFactory,
        TextValidationService textValidationService,
        ICustomLogger? logger = null,
        GenerateChunkOptions? options = null,
        CommandTuningOptions? tuning = null,
        IServiceScopeFactory? scopeFactory = null,
        IAgentCallService? modelExecution = null,
        ICallCenter? callCenter = null)
    {
        _storyId = storyId;
        _writerAgentId = writerAgentId;
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _ = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
        _logger = logger;
        _options = options ?? new GenerateChunkOptions();
        _tuning = tuning ?? new CommandTuningOptions();
        _scopeFactory = scopeFactory;
        _callCenter = callCenter;
        _ = modelExecution;
        _textValidationService = textValidationService ?? throw new ArgumentNullException(nameof(textValidationService));
    }

    public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default, string? runId = null)
    {
        ct.ThrowIfCancellationRequested();

        var effectiveRunId = string.IsNullOrWhiteSpace(runId)
            ? $"generate_next_chunk_{_storyId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}"
            : runId.Trim();
        var snap = _database.GetStateDrivenStorySnapshot(_storyId);
        if (snap == null)
        {
            return new CommandResult(false, $"Story {_storyId}: snapshot not found");
        }

        if (!snap.IsActive)
        {
            return new CommandResult(false, $"Story {_storyId}: runtime not active");
        }

        var writer = _database.GetAgentById(_writerAgentId);
        if (writer == null)
        {
            return new CommandResult(false, $"Writer agent {_writerAgentId} not found");
        }

        _database.UpdateStoryById(_storyId, modelId: writer.ModelId, agentId: writer.Id);

        var phase = DecidePhase(snap);
        var pov = DecidePov(snap);

        var prompt = BuildWriterPrompt(snap, phase, pov, _options);
        var writerResult = await CallWriterWithStandardPatternAsync(writer, prompt, phase, pov, effectiveRunId, ct).ConfigureAwait(false);
        var output = (writerResult.Text ?? string.Empty).Trim();
        var failureDelta = 0;

        // If the only blocker is degenerative punctuation, try an automatic cleanup
        // and continue the pipeline instead of hard-failing the step.
        if (!writerResult.Success && !string.IsNullOrWhiteSpace(output) && IsDegenerativePunctuationError(writerResult.Error))
        {
            var fixedOutput = FixDegenerativePunctuation(output);
            if (!string.Equals(output, fixedOutput, StringComparison.Ordinal))
            {
                _logger?.Append(effectiveRunId, "Applicata autocorrezione punteggiatura degenerativa sul chunk.");
                output = fixedOutput;
            }
        }

        var canPersistWithValidatorFailure = CanPersistWithValidatorFailure(writerResult);

        if (canPersistWithValidatorFailure)
        {
            failureDelta = 1;
            _logger?.MarkLatestModelResponseResult("FAILED", writerResult.Error);
        }
        else if (!writerResult.Success || string.IsNullOrWhiteSpace(output))
        {
            var reason = string.IsNullOrWhiteSpace(writerResult.Error)
                ? "Writer returned empty output; chunk not persisted."
                : writerResult.Error!;
            _logger?.MarkLatestModelResponseResult("FAILED", reason);
            return new CommandResult(false, reason);
        }

        var editedText = await TryApplyNonCreativeStoryEditorAsync(output, snap, phase, pov, effectiveRunId, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(editedText))
        {
            output = editedText.Trim();
        }

        var continuityExtraction = await TryExtractContinuityStateAsync(output, snap, phase, pov, effectiveRunId, ct).ConfigureAwait(false);
        if (!continuityExtraction.Success)
        {
            return new CommandResult(false, continuityExtraction.Error ?? "StateExtractor failed");
        }

        var extractedContinuityStateJson = continuityExtraction.StateJson;
        var tail = GetTail(output, Math.Max(0, _tuning.GenerateNextChunk.ContextTailChars));
        var resourceManagerResult = await TryUpdateResourcesWithAgentAsync(
            snap,
            output,
            effectiveRunId,
            ct).ConfigureAwait(false);
        if (!resourceManagerResult.Success || string.IsNullOrWhiteSpace(resourceManagerResult.CanonStateJson))
        {
            return new CommandResult(false, resourceManagerResult.Error ?? "resource_manager update failed");
        }

        var newResources = ParseResourceValuesFromCanonState(resourceManagerResult.CanonStateJson);

        if (!_database.TryApplyStateDrivenChunk(
                storyId: snap.StoryId,
                expectedChunkIndex: snap.CurrentChunkIndex,
                phase: phase,
                pov: pov,
                chunkText: output,
                failureCountDelta: failureDelta,
                newResourceValues: newResources,
                newLastContextTail: tail,
                canonStateJson: resourceManagerResult.CanonStateJson,
                narrativeContinuityStateJson: extractedContinuityStateJson,
                narrativeQualityScore: null,
                narrativeCoherenceScore: null,
                out var error))
        {
            return new CommandResult(false, $"Persist failed: {error}");
        }

        var message = failureDelta == 0
            ? $"Chunk {snap.CurrentChunkIndex + 1} saved (phase={phase}, pov={pov})"
            : $"Chunk {snap.CurrentChunkIndex + 1} saved with validator failure (phase={phase}, pov={pov})";
        return new CommandResult(true, message);
    }

    private static bool CanPersistWithValidatorFailure(CommandModelExecutionService.Result writerResult)
    {
        if (writerResult.Success || string.IsNullOrWhiteSpace(writerResult.Text) || string.IsNullOrWhiteSpace(writerResult.Error))
        {
            return false;
        }

        var error = writerResult.Error;
        if (error.StartsWith("Cliffhanger validation", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Keep generation flowing when pacing is slower than expected:
        // this remains a tracked validator failure but no longer hard-blocks chunk persistence.
        if (error.Contains("troppi paragrafi senza azione", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsDegenerativePunctuationError(error))
        {
            return true;
        }

        return false;
    }

    private static bool IsDegenerativePunctuationError(string? error)
        => !string.IsNullOrWhiteSpace(error) &&
           error.Contains("punteggiatura degenerativa", StringComparison.OrdinalIgnoreCase);

    private static string FixDegenerativePunctuation(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var fixedText = text;
        fixedText = Regex.Replace(fixedText, @"([!?])\1{1,}", "$1");
        fixedText = Regex.Replace(fixedText, @"\.{4,}", "...");
        fixedText = Regex.Replace(fixedText, @"[-—]{3,}", "—");
        fixedText = Regex.Replace(fixedText, @"\s{2,}", " ");
        fixedText = Regex.Replace(fixedText, @"[ \t]+(\r?\n)", "$1");
        return fixedText.Trim();
    }

    private async Task<CommandModelExecutionService.Result> CallWriterWithStandardPatternAsync(
        Agent writerAgent,
        string prompt,
        string phase,
        string pov,
        string runId,
        CancellationToken ct)
    {
        var callCenter = ResolveCallCenter();
        if (callCenter == null)
        {
            return new CommandModelExecutionService.Result
            {
                Success = false,
                Error = "CallCenter non disponibile"
            };
        }

        var roleCode = string.IsNullOrWhiteSpace(writerAgent.Role) ? "writer" : writerAgent.Role.Trim();
        var commandKey = ResolveCommandKey();
        var storyHistory = BuildStoryHistorySnapshot();
        var agentIdentity = BuildAgentIdentity(writerAgent);
        var maxAttempts = Math.Max(1, _tuning.GenerateNextChunk.MaxAttempts);
        var systemPrompt = writerAgent.SystemPrompt ?? writerAgent.UserPrompt ?? "Sei uno scrittore esperto.";
        var history = new ChatHistory();
        history.AddSystem(systemPrompt);
        history.AddUser(prompt);

        string lastError = "Writer returned empty output";
        string modelUsed = ResolveModelName(writerAgent) ?? string.Empty;
        var hadDeterministicFailure = false;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var options = new CallOptions
            {
                Operation = commandKey,
                Timeout = TimeSpan.FromSeconds(180),
                MaxRetries = 0,
                UseResponseChecker = true,
                AllowFallback = true,
                AskFailExplanation = true
            };
            options.DeterministicChecks.Add(new CheckEmpty
            {
                Options = Options.Create<object>(new Dictionary<string, object>
                {
                    ["ErrorMessage"] = $"Risposta vuota ({agentIdentity})"
                })
            });
            options.DeterministicChecks.Add(new CheckTextValidation
            {
                Options = Options.Create<object>(new Dictionary<string, object>
                {
                    ["TextValidationService"] = _textValidationService,
                    ["StoryHistory"] = storyHistory,
                    ["AgentIdentity"] = agentIdentity,
                    ["RunId"] = runId,
                    ["Phase"] = phase,
                    ["Logger"] = _logger as object ?? new object()
                })
            });
            options.DeterministicChecks.Add(new CheckNarrativeContinuityRules
            {
                Options = Options.Create<object>(BuildNarrativeChecksOptions(storyHistory, phase, pov))
            });
            var callResult = await callCenter.CallAgentAsync(
                storyId: _storyId,
                threadId: BuildThreadId(_storyId, attempt),
                agent: writerAgent,
                history: history,
                options: options,
                cancellationToken: ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(callResult.ModelUsed))
            {
                modelUsed = callResult.ModelUsed;
            }

            if (!callResult.Success || string.IsNullOrWhiteSpace(callResult.ResponseText))
            {
                lastError = callResult.FailureReason ?? "Writer returned empty output";
                _logger?.Append(runId, $"[{roleCode}] Tentativo {attempt}/{maxAttempts} fallito: {lastError}", "warning");
                if (attempt < maxAttempts)
                {
                    history.AddUser(BuildRetryConversationPrompt(lastError));
                }
                continue;
            }

            var cleaned = callResult.ResponseText.Trim();

            return new CommandModelExecutionService.Result
            {
                Success = true,
                Text = cleaned,
                ModelName = modelUsed,
                UsedFallback = !string.Equals(modelUsed, ResolveModelName(writerAgent), StringComparison.OrdinalIgnoreCase),
                DeterministicFailure = false,
                AttemptsUsed = attempt
            };
        }

        if (!string.IsNullOrWhiteSpace(lastError) &&
            (lastError.Contains("GENERIC_ERROR:", StringComparison.OrdinalIgnoreCase) ||
             lastError.Contains("deterministic", StringComparison.OrdinalIgnoreCase) ||
             lastError.Contains("deterministico", StringComparison.OrdinalIgnoreCase)))
        {
            hadDeterministicFailure = true;
        }

        return new CommandModelExecutionService.Result
        {
            Success = false,
            Error = lastError,
            Text = string.Empty,
            ModelName = modelUsed,
            UsedFallback = !string.Equals(modelUsed, ResolveModelName(writerAgent), StringComparison.OrdinalIgnoreCase),
            DeterministicFailure = hadDeterministicFailure,
            AttemptsUsed = maxAttempts
        };
    }

    private async Task<string?> TryApplyNonCreativeStoryEditorAsync(
        string writerOutput,
        DatabaseService.StateDrivenStorySnapshot snap,
        string phase,
        string pov,
        string runId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(writerOutput))
        {
            return writerOutput;
        }

        var editor = _database.GetAgentByRole("story_editor_non_creative");
        if (editor == null || !editor.IsActive)
        {
            return writerOutput;
        }

        var callCenter = ResolveCallCenter();
        if (callCenter == null)
        {
            return writerOutput;
        }

        var prompt = BuildStoryEditorNonCreativePrompt(writerOutput, snap, phase, pov);
        var systemPrompt = editor.SystemPrompt ?? editor.UserPrompt ?? "Sei uno story editor non creativo.";
        var history = new ChatHistory();
        history.AddSystem(systemPrompt);
        history.AddUser(prompt);

        var options = new CallOptions
        {
            Operation = "story_editor_non_creative",
            Timeout = TimeSpan.FromSeconds(120),
            MaxRetries = 1,
            UseResponseChecker = true,
            AllowFallback = true,
            AskFailExplanation = true
        };
        options.DeterministicChecks.Add(new CheckEmpty
        {
            Options = Options.Create<object>(new Dictionary<string, object>
            {
                ["ErrorMessage"] = "StoryEditor non creativo: risposta vuota"
            })
        });
        options.DeterministicChecks.Add(new CheckNarrativeContinuityRules
        {
            Options = Options.Create<object>(BuildNarrativeChecksOptions(BuildStoryHistorySnapshot(), phase, pov))
        });

        var started = DateTime.UtcNow;
        var result = await callCenter.CallAgentAsync(
            storyId: _storyId,
            threadId: BuildThreadId(_storyId, 9001),
            agent: editor,
            history: history,
            options: options,
            cancellationToken: ct).ConfigureAwait(false);

        _database.InsertNarrativeAgentCallLog(
            storyId: _storyId,
            agentName: editor.Name,
            inputTokens: null,
            outputTokens: null,
            deterministicChecksResult: result.Success ? "PASS" : $"FAIL: {TrimForDb(result.FailureReason)}",
            responseCheckerResult: options.UseResponseChecker ? (result.Success ? "PASS" : $"FAIL: {TrimForDb(result.FailureReason)}") : "SKIPPED",
            retryCount: Math.Max(0, result.Attempts - 1),
            latencyMs: Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds));

        if (!result.Success || string.IsNullOrWhiteSpace(result.ResponseText))
        {
            _logger?.Append(runId, $"StoryEditor non creativo fallito/skip: {result.FailureReason}", "warning");
            return writerOutput;
        }

        _logger?.Append(runId, $"StoryEditor non creativo applicato: agente={editor.Name}; modello={result.ModelUsed}");
        return result.ResponseText.Trim();
    }

    private async Task<(bool Success, string? StateJson, string? Error)> TryExtractContinuityStateAsync(
        string blockText,
        DatabaseService.StateDrivenStorySnapshot snap,
        string phase,
        string pov,
        string runId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(blockText))
        {
            return (false, null, "StateExtractor: blocco narrativo vuoto");
        }

        var extractor = _database.GetAgentByRole("state_extractor");
        if (extractor == null || !extractor.IsActive)
        {
            return (false, null, "StateExtractor non configurato o non attivo");
        }

        var callCenter = ResolveCallCenter();
        if (callCenter == null)
        {
            return (false, null, "CallCenter non disponibile per StateExtractor");
        }

        var previousState = _database.GetLatestNarrativeContinuityState(_storyId)?.StateJson ?? "{}";
        var story = _database.GetStoryById(_storyId);
        var seriesId = story?.SerieId ?? 0;
        var episodeId = 0;
        if ((story?.SerieId).HasValue && (story?.SerieEpisode).HasValue)
        {
            episodeId = _database.GetSeriesEpisodeBySerieAndNumber(story.SerieId.Value, story.SerieEpisode.Value)?.Id
                ?? story.SerieEpisode.Value;
        }
        var chapterId = Math.Max(1, snap.CurrentChunkIndex + 1);
        var sceneId = chapterId;

        var prompt = BuildStateExtractorPrompt(
            blockText,
            previousState,
            snap,
            phase,
            pov,
            seriesId,
            episodeId,
            chapterId,
            sceneId);
        var systemPrompt = extractor.SystemPrompt ?? extractor.UserPrompt ?? "Aggiorna stato narrativo strutturato.";
        var history = new ChatHistory();
        history.AddSystem(systemPrompt);
        history.AddUser(prompt);

        var options = new CallOptions
        {
            Operation = "state_extractor",
            Timeout = TimeSpan.FromSeconds(120),
            MaxRetries = 1,
            UseResponseChecker = true,
            AllowFallback = true,
            AskFailExplanation = true
        };
        options.DeterministicChecks.Add(new CheckEmpty
        {
            Options = Options.Create<object>(new Dictionary<string, object>
            {
                ["ErrorMessage"] = "StateExtractor: risposta vuota"
            })
        });

        var started = DateTime.UtcNow;
        var result = await callCenter.CallAgentAsync(
            storyId: _storyId,
            threadId: BuildThreadId(_storyId, 9002),
            agent: extractor,
            history: history,
            options: options,
            cancellationToken: ct).ConfigureAwait(false);

        _database.InsertNarrativeAgentCallLog(
            storyId: _storyId,
            agentName: extractor.Name,
            inputTokens: null,
            outputTokens: null,
            deterministicChecksResult: result.Success ? "PASS" : $"FAIL: {TrimForDb(result.FailureReason)}",
            responseCheckerResult: options.UseResponseChecker ? (result.Success ? "PASS" : $"FAIL: {TrimForDb(result.FailureReason)}") : "SKIPPED",
            retryCount: Math.Max(0, result.Attempts - 1),
            latencyMs: Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds));

        if (!result.Success || string.IsNullOrWhiteSpace(result.ResponseText))
        {
            _logger?.Append(runId, $"StateExtractor fallito: {result.FailureReason}", "error");
            return (false, null, result.FailureReason ?? "StateExtractor failed");
        }

        var raw = result.ResponseText.Trim();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                _logger?.Append(runId, "StateExtractor ha restituito JSON non object.", "error");
                return (false, null, "StateExtractor output JSON non object");
            }
            var normalizedStateJson = NormalizeContinuityStateJson(
                raw,
                snap.StoryId,
                seriesId,
                episodeId,
                chapterId,
                sceneId,
                out var normalizeError);
            if (string.IsNullOrWhiteSpace(normalizedStateJson))
            {
                _logger?.Append(runId, $"StateExtractor normalizzazione JSON fallita: {normalizeError}", "error");
                return (false, null, $"StateExtractor output non normalizzabile: {normalizeError}");
            }

            _logger?.Append(runId, $"StateExtractor applicato: agente={extractor.Name}; modello={result.ModelUsed}");
            return (true, normalizedStateJson, null);
        }
        catch (Exception ex)
        {
            _logger?.Append(runId, $"StateExtractor JSON invalido: {ex.Message}", "error");
            return (false, null, $"StateExtractor JSON invalido: {ex.Message}");
        }
    }

    private static string BuildRetryConversationPrompt(string reason)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ATTENZIONE: il tuo output precedente NON era valido.");

        var modelMessage = BuildModelFriendlyErrorMessage(reason);
        sb.AppendLine("Motivo: " + modelMessage);
        sb.AppendLine("Rigenera la risposta COMPLETA rispettando tutti i vincoli.");
        return sb.ToString();
    }

    private static string BuildModelFriendlyErrorMessage(string reason)
    {
        // Frasi duplicate tra blocchi
        if (reason.Contains("Frasi duplicate tra blocchi", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("Frase duplicata", StringComparison.OrdinalIgnoreCase))
        {
            var duplicate = ExtractDetailValue(reason);
            if (!string.IsNullOrWhiteSpace(duplicate))
                return $"Hai scritto una frase già presente nella storia precedente: '{duplicate}'. Usa frasi completamente diverse — nessuna parola o costrutto già usato in quel modo.";
            return "Hai ripetuto una frase già presente nella storia. Scrivi contenuto completamente nuovo, senza ripetere frasi o costrutti già usati.";
        }

        return reason;
    }

    private static string? ExtractDetailValue(string reason)
    {
        const string marker = "DETAIL:";
        var idx = reason.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var detail = reason[(idx + marker.Length)..].Trim();
        // Estrae il testo tra apici singoli se presente
        var start = detail.IndexOf('\'');
        var end = detail.LastIndexOf('\'');
        if (start >= 0 && end > start)
            return detail[(start + 1)..end];
        return detail.Length > 200 ? detail[..200] : detail;
    }

    private Dictionary<string, object> BuildNarrativeChecksOptions(string storyHistory, string phase, string pov)
    {
        var latestStateJson = _database.GetLatestNarrativeContinuityState(_storyId)?.StateJson ?? "{}";
        return new Dictionary<string, object>
        {
            ["StoryHistory"] = storyHistory,
            ["Phase"] = phase,
            ["CurrentPOV"] = pov,
            ["ContinuityStateJson"] = latestStateJson
        };
    }

    private static string BuildStoryEditorNonCreativePrompt(
        string writerOutput,
        DatabaseService.StateDrivenStorySnapshot snap,
        string phase,
        string pov)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ripulisci il testo mantenendo invariati eventi, outcome e continuity.");
        sb.AppendLine("NON introdurre elementi nuovi. NON cambiare il contenuto fattuale.");
        sb.AppendLine("Mantieni la stessa lingua del testo.");
        sb.AppendLine("Restituisci SOLO il testo finale ripulito, senza commenti.");
        sb.AppendLine();
        sb.AppendLine($"PHASE: {phase}");
        sb.AppendLine($"POV: {pov}");
        sb.AppendLine($"TITLE: {snap.Title}");
        sb.AppendLine();
        sb.AppendLine("TESTO:");
        sb.AppendLine(writerOutput);
        return sb.ToString();
    }

    private static string BuildStateExtractorPrompt(
        string blockText,
        string previousStateJson,
        DatabaseService.StateDrivenStorySnapshot snap,
        string phase,
        string pov,
        int seriesId,
        int episodeId,
        int chapterId,
        int sceneId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aggiorna lo stato narrativo e restituisci SOLO un JSON object valido.");
        sb.AppendLine("Non scrivere testo narrativo.");
        sb.AppendLine("Mantieni i campi esistenti quando non cambiano.");
        sb.AppendLine("NON includere i campi tecnici story_id, series_id, episode_id, chapter_id, scene_id, timeline_index: vengono valorizzati automaticamente dal sistema.");
        sb.AppendLine($"Valori tecnici correnti (gestiti dal sistema): story_id={snap.StoryId}, series_id={seriesId}, episode_id={episodeId}, chapter_id={chapterId}, scene_id={sceneId}, timeline_index={chapterId}.");
        sb.AppendLine("Schema minimo richiesto:");
        sb.AppendLine("{\"location_current\":null,\"time_context\":null,\"active_characters\":[],\"dead_characters\":[],\"missing_characters\":[],\"objects_in_scene\":[],\"environment_state\":{},\"conflict_state\":null,\"goal_current\":null,\"last_events\":[],\"pov_character\":null,\"tone_current\":null,\"custom_flags\":{}}");
        sb.AppendLine();
        sb.AppendLine($"TITLE: {snap.Title}");
        sb.AppendLine($"PHASE: {phase}");
        sb.AppendLine($"POV: {pov}");
        sb.AppendLine();
        sb.AppendLine("PREVIOUS_STATE_JSON:");
        sb.AppendLine(previousStateJson);
        sb.AppendLine();
        sb.AppendLine("NUOVO_BLOCCO:");
        sb.AppendLine(blockText);
        return sb.ToString();
    }

    private static string? NormalizeContinuityStateJson(
        string rawStateJson,
        long storyId,
        int seriesId,
        int episodeId,
        int chapterId,
        int sceneId,
        out string? error)
    {
        error = null;
        try
        {
            var node = JsonNode.Parse(rawStateJson);
            if (node is not JsonObject obj)
            {
                error = "root non object";
                return null;
            }

            obj["story_id"] = storyId;
            obj["series_id"] = seriesId > 0 ? seriesId : null;
            obj["episode_id"] = episodeId > 0 ? episodeId : null;
            obj["chapter_id"] = chapterId > 0 ? chapterId : null;
            obj["scene_id"] = sceneId > 0 ? sceneId : null;
            obj["timeline_index"] = chapterId > 0 ? chapterId : 0;

            return obj.ToJsonString(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static string? TrimForDb(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var t = text.Trim();
        return t.Length <= 500 ? t : t[..500];
    }

    private static string ResolveCommandKey()
    {
        var scope = LogScope.Current;
        if (!string.IsNullOrWhiteSpace(scope))
        {
            var normalized = CommandOperationNameResolver.Normalize(scope);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return "generate_next_chunk";
    }

    private string BuildAgentIdentity(Agent agent)
    {
        var name = string.IsNullOrWhiteSpace(agent.Description) ? $"id={agent.Id}" : agent.Description.Trim();
        var role = string.IsNullOrWhiteSpace(agent.Role) ? "writer" : agent.Role.Trim();
        var model = !string.IsNullOrWhiteSpace(agent.ModelName)
            ? agent.ModelName!.Trim()
            : (agent.ModelId.HasValue ? (_database.GetModelInfoById(agent.ModelId.Value)?.Name ?? $"modelId={agent.ModelId.Value}") : "model=n/a");
        return $"{name}; role={role}; model={model}";
    }

    private static string DecidePhase(DatabaseService.StateDrivenStorySnapshot snap)
    {
        var allowed = ParseSuccessioneStatiOrDefault(snap.EffectiveTipoPlanningSuccessioneStati);

        // Deterministic overrides (no semantic inference): repeated validator failures or depleted resources force EFFETTO.
        if (snap.FailureCount >= 3 || AnyResourceAtMin(snap))
        {
            return allowed.Contains("EFFETTO", StringComparer.OrdinalIgnoreCase) ? "EFFETTO" : allowed.Last();
        }

        var current = NormalizePhaseToken(snap.CurrentPhase);

        // First chunk: force AMBIENTE to establish setting early (audio-first requirement).
        if (string.IsNullOrWhiteSpace(current))
        {
            return "AMBIENTE";
        }

        // Next = next in succession (circular).
        var idx = allowed.FindIndex(s => s.Equals(current, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            // If the stored phase was legacy or invalid, restart from grammar.
            return allowed[0];
        }

        return allowed[(idx + 1) % allowed.Count];
    }

    private static bool AnyResourceAtMin(DatabaseService.StateDrivenStorySnapshot snap)
    {
        foreach (var res in snap.ProfileResources)
        {
            if (snap.CurrentResourceValues.TryGetValue(res.Name, out var current))
            {
                if (current <= res.MinValue) return true;
            }
        }
        return false;
    }

    private static List<string> ParseSuccessioneStatiOrDefault(string? csv)
    {
        static bool IsAllowed(string s) =>
            s.Equals("AMBIENTE", StringComparison.OrdinalIgnoreCase)
            || s.Equals("AZIONE", StringComparison.OrdinalIgnoreCase)
            || s.Equals("STASI", StringComparison.OrdinalIgnoreCase)
            || s.Equals("ERRORE", StringComparison.OrdinalIgnoreCase)
            || s.Equals("EFFETTO", StringComparison.OrdinalIgnoreCase);

        var parts = (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizePhaseToken)
            .Where(s => !string.IsNullOrWhiteSpace(s) && IsAllowed(s))
            .OfType<string>()
            .ToList();

        if (parts.Count == 0)
        {
            return new List<string> { "STASI", "AZIONE", "ERRORE", "EFFETTO" };
        }

        // Keep order and allow repeats, but ensure we have at least one element.
        return parts;
    }

    private static string? NormalizePhaseToken(string? phase)
    {
        if (string.IsNullOrWhiteSpace(phase)) return null;

        var p = phase.Trim();
        // Normalize legacy internal names to the 4-state vocabulary.
        if (p.Equals("Environment", StringComparison.OrdinalIgnoreCase)) return "AMBIENTE";
        if (p.Equals("Ambiente", StringComparison.OrdinalIgnoreCase)) return "AMBIENTE";
        if (p.Equals("Action", StringComparison.OrdinalIgnoreCase)) return "AZIONE";
        if (p.Equals("Stall", StringComparison.OrdinalIgnoreCase)) return "STASI";
        if (p.Equals("Error", StringComparison.OrdinalIgnoreCase)) return "ERRORE";
        if (p.Equals("Consequence", StringComparison.OrdinalIgnoreCase)) return "EFFETTO";

        // Accept already-normalized tokens.
        if (p.Equals("AMBIENTE", StringComparison.OrdinalIgnoreCase)) return "AMBIENTE";
        if (p.Equals("AZIONE", StringComparison.OrdinalIgnoreCase)) return "AZIONE";
        if (p.Equals("STASI", StringComparison.OrdinalIgnoreCase)) return "STASI";
        if (p.Equals("ERRORE", StringComparison.OrdinalIgnoreCase)) return "ERRORE";
        if (p.Equals("EFFETTO", StringComparison.OrdinalIgnoreCase)) return "EFFETTO";

        return p.ToUpperInvariant();
    }

    private static string DecidePov(DatabaseService.StateDrivenStorySnapshot snap)
    {
        var list = ParsePovList(snap.ProfilePovListJson);
        if (list.Count == 0)
        {
            return string.IsNullOrWhiteSpace(snap.CurrentPOV) ? "ThirdPersonLimited" : snap.CurrentPOV!;
        }

        var idx = snap.CurrentChunkIndex % list.Count;
        return list[idx];
    }

    private static List<string> ParsePovList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(json);
            return parsed?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                   ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static Dictionary<string, int> ApplyBaseConsumption(DatabaseService.StateDrivenStorySnapshot snap, string phase)
    {
        // Deterministic base drain (kept conservative).
        var drain = phase switch
        {
            "AMBIENTE" => 0,
            "STASI" => 1,
            "ERRORE" => 1,
            "EFFETTO" => 2,
            _ => 0
        };

        var next = new Dictionary<string, int>(snap.CurrentResourceValues, StringComparer.OrdinalIgnoreCase);

        foreach (var res in snap.ProfileResources)
        {
            if (!next.TryGetValue(res.Name, out var current))
            {
                current = Math.Min(res.MaxValue, Math.Max(res.MinValue, res.InitialValue));
            }

            var updated = current - drain;
            updated = Math.Min(res.MaxValue, Math.Max(res.MinValue, updated));
            next[res.Name] = updated;
        }

        return next;
    }

    private static void ApplyConsequenceImpactsInPlace(DatabaseService.StateDrivenStorySnapshot snap, Dictionary<string, int> resourceValues)
    {
        if (snap.ConsequenceRules.Count == 0) return;

        var idx = (snap.CurrentChunkIndex + snap.FailureCount) % snap.ConsequenceRules.Count;
        var rule = snap.ConsequenceRules[idx];

        foreach (var impact in rule.Impacts)
        {
            if (!resourceValues.TryGetValue(impact.ResourceName, out var current)) continue;
            var resourceDef = snap.ProfileResources.FirstOrDefault(r => r.Name.Equals(impact.ResourceName, StringComparison.OrdinalIgnoreCase));
            if (resourceDef == null) continue;

            var updated = current + impact.DeltaValue;
            updated = Math.Min(resourceDef.MaxValue, Math.Max(resourceDef.MinValue, updated));
            resourceValues[impact.ResourceName] = updated;
        }
    }

    private static string BuildWriterPrompt(DatabaseService.StateDrivenStorySnapshot snap, string phase, string pov, GenerateChunkOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SCRIVI IL PROSSIMO CHUNK (in italiano).");
        sb.AppendLine();
        sb.AppendLine("VINCOLI NON NEGOZIABILI:");
        sb.AppendLine($"- STATO NARRATIVO (deciso dal Planner/codice): {phase}");
        sb.AppendLine($"- POV (deciso dal codice): {pov}");
        if (options.RequireCliffhanger)
        {
            sb.AppendLine("- Il chunk DEVE terminare con tensione aperta (cliffhanger).\n  Vietato chiudere o concludere la storia.");
        }
        else if (options.IsFinalChunk)
        {
            sb.AppendLine("- QUESTO È L'ULTIMO CHUNK: deve CHIUDERE l'episodio in modo soddisfacente.");
            sb.AppendLine("- Vietato cliffhanger finale: niente '...'? niente domanda aperta come ultima frase.");
        }

        if (options.TargetWords.HasValue && options.TargetWords.Value > 0)
        {
            sb.AppendLine($"- Lunghezza target: circa {options.TargetWords.Value} parole (tolleranza ±20%).");
        }
        sb.AppendLine("- NON aggiungere sezioni meta (es. 'capitolo', 'fine', 'riassunto').");
        sb.AppendLine("- Se introduci un CAMBIO DI AMBIENTAZIONE, devi descrivere subito il nuovo ambiente (spazio + elementi sonori percepibili) prima che l'azione prosegua.");
        sb.AppendLine();
        sb.AppendLine("REGOLA SULLO STATO:");
        sb.AppendLine("- AMBIENTE: focalizza scena, spazio sonoro, elementi fisici percepibili (luoghi, distanza, superfici, rumori, atmosfera concreta), senza chiudere eventi.");
        sb.AppendLine("- AZIONE: qualcuno fa qualcosa che cambia la situazione. L’azione deve cambiare la situazione in modo visibile o creare nuove conseguenze. Spostarsi o parlare NON basta se non genera un problema, un rischio o una nuova direzione.");
        sb.AppendLine("- STASI: pausa, dialogo, riflessione, attesa.");
        sb.AppendLine("- ERRORE: deve accadere un EVENTO NEGATIVO NUOVO e CONCRETO che peggiora la situazione.");
        sb.AppendLine("- EFFETTO: si vedono le conseguenze dirette di un evento accaduto prima.");
        sb.AppendLine();
        sb.AppendLine("ESEMPI DI STATO AMBIENTE:");
        sb.AppendLine("- Definisci chiaramente spazio, luce, suoni, odori e disposizione dei personaggi.");
        sb.AppendLine("- Evidenzia subito gli elementi sonori utili al rendering audio.");
        sb.AppendLine("- Inserisci una tensione latente che prepara l'azione successiva.");
        sb.AppendLine("Non valido:");
        sb.AppendLine("- Restare nel puramente estetico senza introdurre un rischio o una direzione narrativa.");
        sb.AppendLine();
        sb.AppendLine("ESEMPI DI STATO AZIONE:");
        sb.AppendLine("- Un personaggio prende una decisione rischiosa e agisce subito.");
        sb.AppendLine("- Qualcuno si sposta, fugge, insegue o cerca qualcosa.");
        sb.AppendLine("- Inizia un conflitto fisico o verbale con conseguenze.");
        sb.AppendLine("- Viene tentato un piano o un’operazione.");
        sb.AppendLine("- Qualcuno entra o esce improvvisamente dalla scena.");
        sb.AppendLine();
        sb.AppendLine("ESEMPI DI STATO STASI:");
        sb.AppendLine("- Personaggi discutono un piano o una scelta difficile.");
        sb.AppendLine("- Osservazione dell’ambiente prima di agire.");
        sb.AppendLine("- Preparazione di strumenti o risorse.");
        sb.AppendLine("- Momento emotivo che precede un’azione.");
        sb.AppendLine("Non valido:");
        sb.AppendLine("- Ripetere atmosfera cupa senza nuove informazioni o preparativi.");
        sb.AppendLine();
        sb.AppendLine("ESEMPI DI STATO EFFETTO:");
        sb.AppendLine("- Ferite, danni o perdite vengono scoperti.");
        sb.AppendLine("- La comunità reagisce a una decisione precedente.");
        sb.AppendLine("- Una nuova difficoltà nasce dalle conseguenze di prima.");
        sb.AppendLine("- Un personaggio cambia atteggiamento dopo l’errore.");
        sb.AppendLine("Non valido:");
        sb.AppendLine("- Introdurre un nuovo evento principale (quello è AZIONE o ERRORE).");
        sb.AppendLine();
        sb.AppendLine("ESEMPI DI STATO ERRORE:");
        sb.AppendLine("- perdita o rottura di una risorsa");
        sb.AppendLine("- minaccia imprevista");
        sb.AppendLine("- piano che fallisce visibilmente");
        sb.AppendLine("- decisione che peggiora la situazione");
        sb.AppendLine("Non valido:");
        sb.AppendLine("- Solo silenzio, tristezza o senso di colpa.");
        sb.AppendLine("- Descrivere fallimento senza conseguenze reali.");
        sb.AppendLine();
        sb.AppendLine("REGOLE DI PROGRESSIONE:");
        sb.AppendLine();
        sb.AppendLine("- Se lo stato è AZIONE o ERRORE:");
        sb.AppendLine("- L’evento deve produrre conseguenze visibili.");
        sb.AppendLine("- Alla fine del chunk la situazione deve essere peggiorata, complicata o resa più incerta.");
        sb.AppendLine("- Muoversi o parlare non basta: deve cambiare la direzione della scena.");
        sb.AppendLine();
        sb.AppendLine("CLIFFHANGER:");
        sb.AppendLine("Il cliffhanger deve derivare direttamente dall’azione appena avvenuta, non da un pensiero o dall’atmosfera.");        
        sb.AppendLine("Deve lasciare in sospeso un evento imminente, una minaccia o una decisione urgente.");
        sb.AppendLine("Non è valido chiudere con silenzio, tristezza o riflessione.");
        sb.AppendLine();
        sb.AppendLine("TEMA/CANONE (input utente):");
        sb.AppendLine(snap.Prompt);
        sb.AppendLine();
        sb.AppendLine("CANON STATE RISORSE (JSON, da rispettare nel chunk):");
        sb.AppendLine(string.IsNullOrWhiteSpace(snap.CanonStateJson) ? "{}" : snap.CanonStateJson);

        var isFirstChunk = snap.CurrentChunkIndex <= 0;
        if (isFirstChunk)
        {
            sb.AppendLine();
            sb.AppendLine("APERTURA EPISODIO (OBBLIGATORIA PER QUESTO CHUNK):");
            sb.AppendLine("- Inizia descrivendo chiaramente la situazione iniziale dell'episodio.");
            sb.AppendLine("- Definisci contesto, luogo e condizione iniziale dei personaggi principali.");
            sb.AppendLine("- Fai emergere subito la tensione o il problema di partenza.");
            sb.AppendLine("- Evita di partire in medias res con eventi confusi senza setup minimo.");
        }

        if (!string.IsNullOrWhiteSpace(snap.LastContext))
        {
            sb.AppendLine();
            sb.AppendLine("CONTESTO RECENTE (coda del chunk precedente):");
            sb.AppendLine(snap.LastContext);
        }

        sb.AppendLine();
        sb.AppendLine("Ora scrivi il prossimo chunk:");
        return sb.ToString();
    }

    private static bool EndsInTension(string text, out string reason)
    {
        reason = string.Empty;
        var t = (text ?? string.Empty).Trim();
        if (t.Length < 40)
        {
            reason = "Il chunk è troppo corto.";
            return false;
        }

        var lower = t.ToLowerInvariant();
        var forbiddenEndings = new[]
        {
            "fine.", "the end", "e vissero felici", "epilogo", "conclusione"
        };
        if (forbiddenEndings.Any(f => lower.EndsWith(f)))
        {
            reason = "Il chunk sembra una conclusione (vietato).";
            return false;
        }

        if (t.EndsWith("...") || t.EndsWith("…") || t.EndsWith("?") || t.EndsWith("!") || t.EndsWith("—") || t.EndsWith(":") || t.EndsWith("…\"") || t.EndsWith("...\""))
        {
            return true;
        }

        // If it ends with a full stop, assume closed beat.
        if (t.EndsWith("."))
        {
            reason = "Il chunk termina con un punto fermo (serve tensione aperta).";
            return false;
        }

        // Fallback: accept if last line ends with open punctuation.
        var lastLine = t.Split('\n').LastOrDefault()?.Trim() ?? t;
        if (lastLine.EndsWith("...") || lastLine.EndsWith("…") || lastLine.EndsWith("?") || lastLine.EndsWith("!") || lastLine.EndsWith("—") || lastLine.EndsWith(":"))
        {
            return true;
        }

        reason = "Il chunk non termina in tensione aperta (usa ? / ... / … / ! / —).";
        return false;
    }

    private static string GetTail(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (maxChars <= 0) return string.Empty;
        var t = text.Trim();
        if (t.Length <= maxChars)
        {
            return t;
        }

        var rawTail = t.Substring(t.Length - maxChars).TrimStart();
        if (string.IsNullOrWhiteSpace(rawTail))
        {
            return rawTail;
        }

        // Evita di partire a meta' frase: se possibile allinea l'inizio
        // al primo delimitatore di fine frase nel frammento.
        var firstSentenceBreak = rawTail.IndexOfAny(new[] { '.', '!', '?' });
        if (firstSentenceBreak >= 0 && firstSentenceBreak + 1 < rawTail.Length)
        {
            var aligned = rawTail.Substring(firstSentenceBreak + 1).TrimStart();
            if (!string.IsNullOrWhiteSpace(aligned))
            {
                return aligned;
            }
        }

        return rawTail;
    }

    private sealed record ResourceManagerUpdateResult(bool Success, string? CanonStateJson, string? Error);

    private async Task<ResourceManagerUpdateResult> TryUpdateResourcesWithAgentAsync(
        DatabaseService.StateDrivenStorySnapshot snap,
        string newChunkText,
        string runId,
        CancellationToken ct)
    {
        var callCenter = ResolveCallCenter();
        if (callCenter == null)
        {
            return new ResourceManagerUpdateResult(false, null, "CallCenter non disponibile per resource_manager");
        }

        var resourceManager = _database.GetAgentByRole("resource_manager");
        if (resourceManager == null || !resourceManager.IsActive)
        {
            return new ResourceManagerUpdateResult(false, null, "Agente resource_manager non configurato o non attivo");
        }

        var systemPrompt = resourceManager.SystemPrompt ?? resourceManager.UserPrompt ?? "Aggiorna il canon state delle risorse e rispondi SOLO in JSON valido.";
        var history = new ChatHistory();
        history.AddSystem(systemPrompt);
        history.AddUser(BuildResourceManagerUpdatePrompt(snap, newChunkText));

        var options = new CallOptions
        {
            Operation = "state_driven_resource_manager_update",
            Timeout = TimeSpan.FromSeconds(120),
            MaxRetries = 1,
            UseResponseChecker = true,
            AllowFallback = true,
            AskFailExplanation = true
        };
        options.DeterministicChecks.Add(new CheckEmpty
        {
            Options = Options.Create<object>(new Dictionary<string, object>
            {
                ["ErrorMessage"] = "resource_manager: risposta vuota"
            })
        });

        var started = DateTime.UtcNow;
        var result = await callCenter.CallAgentAsync(
            storyId: _storyId,
            threadId: BuildThreadId(_storyId, 9100 + snap.CurrentChunkIndex),
            agent: resourceManager,
            history: history,
            options: options,
            cancellationToken: ct).ConfigureAwait(false);

        _database.InsertNarrativeAgentCallLog(
            storyId: _storyId,
            agentName: resourceManager.Name,
            inputTokens: null,
            outputTokens: null,
            deterministicChecksResult: result.Success ? "PASS" : $"FAIL: {TrimForDb(result.FailureReason)}",
            responseCheckerResult: options.UseResponseChecker ? (result.Success ? "PASS" : $"FAIL: {TrimForDb(result.FailureReason)}") : "SKIPPED",
            retryCount: Math.Max(0, result.Attempts - 1),
            latencyMs: Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds));

        if (!result.Success || string.IsNullOrWhiteSpace(result.ResponseText))
        {
            return new ResourceManagerUpdateResult(false, null, result.FailureReason ?? "resource_manager update fallito");
        }

        if (!TryExtractResourceDeltaJson(result.ResponseText, out var deltaJson, out var parseError))
        {
            return new ResourceManagerUpdateResult(false, null, $"resource_manager: delta non valido ({parseError ?? "unknown"})");
        }

        var canonStateJson = ApplyResourceDeltaToCanonState(
            snap.CanonStateJson,
            deltaJson,
            Math.Max(1, snap.CurrentChunkIndex + 1));
        if (string.IsNullOrWhiteSpace(canonStateJson))
        {
            return new ResourceManagerUpdateResult(false, null, "resource_manager: impossibile allineare canon_state con il delta");
        }

        _logger?.Append(runId, $"ResourceManager update ok: agente={resourceManager.Name}; modello={result.ModelUsed}");
        return new ResourceManagerUpdateResult(true, canonStateJson, null);
    }

    private static string BuildResourceManagerUpdatePrompt(
        DatabaseService.StateDrivenStorySnapshot snap,
        string newChunkText)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aggiorna le risorse rispetto all'ULTIMO chunk e restituisci SOLO JSON valido.");
        sb.AppendLine("Modalita: UPDATE.");
        sb.AppendLine("Input disponibili: SOLO previous_canon_state e new_chunk_text.");
        sb.AppendLine("NON riscrivere tutto il canon_state: restituisci SOLO le risorse che cambiano.");
        sb.AppendLine("Il programma applichera' il delta allo stato completo.");
        sb.AppendLine("Per ogni risorsa usa solo: name + (quantity oppure integrity_percent) e opzionalmente status_flag, notes_json.");
        sb.AppendLine();
        sb.AppendLine("{");
        sb.AppendLine("  \"mode\": \"UPDATE\",");
        sb.AppendLine("  \"previous_canon_state\": " + (string.IsNullOrWhiteSpace(snap.CanonStateJson) ? "{}" : snap.CanonStateJson) + ",");
        sb.AppendLine("  \"new_chunk_text\": \"" + EscapeJsonText(newChunkText) + "\"");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Output atteso:");
        sb.AppendLine("{ \"updated_resources\": [ ... ] }");
        return sb.ToString();
    }

    private static IReadOnlyDictionary<string, int> ParseResourceValuesFromCanonState(string canonStateJson)
    {
        var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(canonStateJson))
        {
            return values;
        }

        try
        {
            using var doc = JsonDocument.Parse(canonStateJson);
            if (!doc.RootElement.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
            {
                return values;
            }

            foreach (var item in resources.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var nameEl)) continue;
                var name = nameEl.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;

                var quantity = 0;
                if (item.TryGetProperty("quantity", out var qtyEl) && qtyEl.ValueKind == JsonValueKind.Number)
                {
                    quantity = qtyEl.GetInt32();
                }
                else if (item.TryGetProperty("integrity_percent", out var integEl) && integEl.ValueKind == JsonValueKind.Number)
                {
                    quantity = integEl.GetInt32();
                }

                values[name.Trim()] = Math.Max(0, quantity);
            }
        }
        catch
        {
            // best effort
        }

        return values;
    }

    private static bool TryExtractResourceDeltaJson(string? rawResponse, out string deltaJson, out string? error)
    {
        deltaJson = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            error = "response vuota";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "root JSON non object";
                return false;
            }

            if (root.TryGetProperty("updated_resources", out var updates) &&
                updates.ValueKind == JsonValueKind.Array)
            {
                deltaJson = root.GetRawText();
                return true;
            }

            if (root.TryGetProperty("delta", out var delta) &&
                delta.ValueKind == JsonValueKind.Object &&
                delta.TryGetProperty("updated_resources", out var legacyUpdates) &&
                legacyUpdates.ValueKind == JsonValueKind.Array)
            {
                deltaJson = delta.GetRawText();
                return true;
            }

            error = "campo updated_resources mancante";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string ApplyResourceDeltaToCanonState(string? currentCanonStateJson, string deltaJson, int chunkIndex)
    {
        var canonNode = ParseCanonStateNodeOrDefault(currentCanonStateJson);
        if (canonNode == null)
        {
            return string.Empty;
        }

        if (canonNode["resources"] is not JsonArray resources)
        {
            resources = new JsonArray();
            canonNode["resources"] = resources;
        }

        JsonObject? deltaNode;
        try
        {
            deltaNode = JsonNode.Parse(deltaJson) as JsonObject;
        }
        catch
        {
            return string.Empty;
        }

        if (deltaNode == null)
        {
            return string.Empty;
        }

        var updates = deltaNode["updated_resources"] as JsonArray;
        if (updates == null)
        {
            return canonNode.ToJsonString();
        }

        var existingByName = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in resources)
        {
            if (item is not JsonObject obj) continue;
            var name = obj["name"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            existingByName[name] = obj;
        }

        foreach (var update in updates)
        {
            if (update is not JsonObject updateObj)
            {
                continue;
            }

            var name = updateObj["name"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!existingByName.TryGetValue(name, out var target))
            {
                target = new JsonObject { ["name"] = name };
                resources.Add(target);
                existingByName[name] = target;
            }

            foreach (var kvp in updateObj)
            {
                if (string.Equals(kvp.Key, "story_id", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kvp.Key, "series_id", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kvp.Key, "episode_number", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kvp.Key, "chunk_index", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kvp.Key, "last_update_chunk", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                target[kvp.Key] = kvp.Value?.DeepClone();
            }

            target["name"] = name;
            target["last_update_chunk"] = Math.Max(0, chunkIndex);
            if (target["quantity"] == null && target["integrity_percent"] == null)
            {
                target["quantity"] = 0;
            }
        }

        return canonNode.ToJsonString();
    }

    private static JsonObject? ParseCanonStateNodeOrDefault(string? canonStateJson)
    {
        if (string.IsNullOrWhiteSpace(canonStateJson))
        {
            return new JsonObject
            {
                ["resources"] = new JsonArray()
            };
        }

        try
        {
            var node = JsonNode.Parse(canonStateJson) as JsonObject;
            if (node == null)
            {
                return null;
            }

            if (node["resources"] is not JsonArray)
            {
                node["resources"] = new JsonArray();
            }

            return node;
        }
        catch
        {
            return null;
        }
    }

    private static string EscapeJsonText(string text)
    {
        return (text ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private string BuildStoryHistorySnapshot()
    {
        try
        {
            var builder = new StringBuilder();
            var chapters = _database.ListChaptersForStory(_storyId);
            foreach (var chapter in chapters)
            {
                if (string.IsNullOrWhiteSpace(chapter.Content)) continue;
                builder.AppendLine(chapter.Content.Trim());
                builder.AppendLine();
            }
            return builder.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private ICallCenter? ResolveCallCenter()
    {
        if (_callCenter != null)
        {
            return _callCenter;
        }

        if (_scopeFactory != null)
        {
            using var scope = _scopeFactory.CreateScope();
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
        
        return null;
    }

    private string? ResolveModelName(Agent agent)
    {
        if (agent.ModelId.HasValue && agent.ModelId.Value > 0)
        {
            var byId = _database.ResolveModelCallNameById(agent.ModelId.Value);
            if (!string.IsNullOrWhiteSpace(byId))
            {
                return byId.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(agent.ModelName))
        {
            return _database.ResolveModelCallName(agent.ModelName) ?? agent.ModelName.Trim();
        }

        return null;
    }

    private static int BuildThreadId(long storyId, int attempt)
    {
        unchecked
        {
            return ((int)(storyId % int.MaxValue) * 613) ^ attempt;
        }
    }

}
