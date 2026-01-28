using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands
{
    public sealed class TransformStoryRawToTaggedCommand
    {
        private const string FormatterV2SystemPrompt =
            "Sei un agente di classificazione.\n\n" +
            "Ti verranno fornite righe numerate. Ogni riga è una porzione di testo originale.\n\n" +
            "COMPITO:\nIndica SOLO le righe dove parla qualcuno (dialogo).\nPer quelle righe, restituisci il tag del personaggio e la sua emozione.\n\n" +
            "TAG POSSIBILI:\n[PERSONAGGIO: Nome] [EMOZIONE: emozione]\n\n" +
            "REGOLE OBBLIGATORIE:\n" +
            "- NON riscrivere il testo\n" +
            "- NON restituire il testo\n" +
            "- NON spiegare\n" +
            "- NON commentare\n" +
            "- NON aggiungere contenuti\n" +
            "- Restituisci un mapping ID → TAG nel formato: ID TAG\n" +
            "- Restituisci SOLO le righe dove parla qualcuno (non tutte le righe)\n" +
            "- Non includere alcun altro tag oltre a PERSONAGGIO ed EMOZIONE\n\n" +
            "Esempio:\n004 [PERSONAGGIO: Luca] [EMOZIONE: paura]";

        private readonly CommandTuningOptions _tuning;

        private readonly long _storyId;
        private readonly DatabaseService _database;
        private readonly ILangChainKernelFactory _kernelFactory;
        private readonly StoriesService? _storiesService;
        private readonly ICustomLogger? _logger;
        private readonly ICommandDispatcher? _commandDispatcher;
        private readonly IServiceScopeFactory? _scopeFactory;

        private sealed record FormatChunkResult(string TaggedText, string? LastError);

        public TransformStoryRawToTaggedCommand(
            long storyId,
            DatabaseService database,
            ILangChainKernelFactory kernelFactory,
            StoriesService? storiesService = null,
            ICustomLogger? logger = null,
            ICommandDispatcher? commandDispatcher = null,
            CommandTuningOptions? tuning = null,
            IServiceScopeFactory? scopeFactory = null)
        {
            _storyId = storyId;
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
            _storiesService = storiesService;
            _logger = logger;
            _commandDispatcher = commandDispatcher;
            _tuning = tuning ?? new CommandTuningOptions();
            _scopeFactory = scopeFactory;
        }

        public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default, string? runId = null)
        {
            var effectiveRunId = string.IsNullOrWhiteSpace(runId)
                ? $"format_story_{_storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}"
                : runId;

            _logger?.Start(effectiveRunId);
            _logger?.Append(effectiveRunId, $"[story {_storyId}] Starting formatter pipeline");

            try
            {
                var story = _database.GetStoryById(_storyId);
                if (story == null)
                {
                    return Fail(effectiveRunId, $"Story {_storyId} not found");
                }

                var sourceText = !string.IsNullOrWhiteSpace(story.StoryRevised)
                    ? story.StoryRevised
                    : story.StoryRaw;

                if (string.IsNullOrWhiteSpace(sourceText))
                {
                    return Fail(effectiveRunId, $"Story {_storyId} has no text");
                }

                // Formatter runs do not clear other tag types; tags are managed per-type in story_tags.

                var formatterAgent = _database.ListAgents()
                    .FirstOrDefault(a => a.IsActive && string.Equals(a.Role, "formatter", StringComparison.OrdinalIgnoreCase));

                if (formatterAgent == null)
                {
                    return Fail(effectiveRunId, "No active formatter agent found");
                }

                if (!formatterAgent.ModelId.HasValue)
                {
                    return Fail(effectiveRunId, $"Formatter agent {formatterAgent.Name} has no model configured");
                }

                var modelInfo = _database.GetModelInfoById(formatterAgent.ModelId.Value);
                if (string.IsNullOrWhiteSpace(modelInfo?.Name))
                {
                    return Fail(effectiveRunId, $"Model not found for formatter agent {formatterAgent.Name}");
                }

                var systemPrompt = BuildSystemPrompt(formatterAgent);
                var promptHash = string.IsNullOrWhiteSpace(systemPrompt)
                    ? null
                    : ComputeSha256(systemPrompt);

                // Formatter V2 requirement: NEVER modify the original text. We keep sourceText as-is.
                var rowsBuild = StoryTaggingService.BuildStoryRows(sourceText);
                var storyRows = rowsBuild.StoryRows;
                var rows = StoryTaggingService.ParseStoryRows(storyRows);
                if (rows.Count == 0)
                {
                    return Fail(effectiveRunId, "No rows produced from story text");
                }

                _database.UpdateStoryRowsAndTags(story.Id, storyRows, story.StoryTags);

                var chunks = StoryTaggingService.SplitRowsIntoChunks(
                    rows,
                    _tuning.TransformStoryRawToTagged.MinTokensPerChunk,
                    _tuning.TransformStoryRawToTagged.MaxTokensPerChunk,
                    _tuning.TransformStoryRawToTagged.TargetTokensPerChunk);
                if (chunks.Count == 0)
                {
                    return Fail(effectiveRunId, "No chunks produced from story rows");
                }

                _logger?.Append(effectiveRunId, $"[story {_storyId}] Split into {chunks.Count} chunks (rows)");

                var bridge = _kernelFactory.CreateChatBridge(
                    modelInfo.Name,
                    formatterAgent.Temperature,
                    formatterAgent.TopP,
                    formatterAgent.RepeatPenalty,
                    formatterAgent.TopK,
                    formatterAgent.RepeatLastN,
                    formatterAgent.NumPredict);

                var formatterTags = new List<StoryTaggingService.StoryTagEntry>();
                for (int i = 0; i < chunks.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = chunks[i];
                    // Update dispatcher step so UI shows progress (current chunk / total)
                    try
                    {
                        _commandDispatcher?.UpdateStep(effectiveRunId, i + 1, chunks.Count, $"Formatting chunk {i + 1}/{chunks.Count}");
                    }
                    catch
                    {
                        // best-effort: do not fail the command if update fails
                    }
                    var result = await FormatChunkWithRetriesAsync(
                        bridge,
                        systemPrompt,
                        chunk.Text,
                        chunk.Rows,
                        i + 1,
                        chunks.Count,
                        effectiveRunId,
                        formatterAgent.Name,
                        modelInfo.Name,
                        ct);

                    if (string.IsNullOrWhiteSpace(result.TaggedText))
                    {
                        // Primary formatter failed - try fallback models if available
                        _logger?.Append(effectiveRunId, $"[chunk {i+1}/{chunks.Count}] Primary formatter failed, attempting fallback models...", "warn");
                        
                        if (_scopeFactory != null)
                        {
                            using var scope = _scopeFactory.CreateScope();
                            var fallbackService = scope.ServiceProvider.GetService<ModelFallbackService>();

                            if (fallbackService == null)
                            {
                                _logger?.Append(effectiveRunId, "ModelFallbackService not available in DI scope; skipping fallback.", "warn");
                            }
                            else
                            {
                            var fallbackAttempted = false;
                            var (fallbackResult, successfulModelRole) = await fallbackService.ExecuteWithFallbackAsync(
                                roleCode: "formatter",
                                primaryModelId: formatterAgent.ModelId,
                                operationAsync: async (modelRole) =>
                                {
                                    fallbackAttempted = true;
                                    var fallbackModel = modelRole.Model;
                                    if (fallbackModel == null || string.IsNullOrWhiteSpace(fallbackModel.Name))
                                    {
                                        throw new InvalidOperationException($"ModelRole {modelRole.Id} has no valid model.");
                                    }

                                    _logger?.Append(effectiveRunId, $"[chunk {i+1}/{chunks.Count}] Trying fallback model: {fallbackModel.Name}", "info");

                                    // Override systemPrompt with modelRole instructions if provided
                                    var fallbackSystemPrompt = !string.IsNullOrWhiteSpace(modelRole.Instructions)
                                        ? modelRole.Instructions
                                        : systemPrompt;

                                    var fallbackBridge = _kernelFactory.CreateChatBridge(
                                        fallbackModel.Name,
                                        formatterAgent.Temperature,
                                        modelRole.TopP ?? formatterAgent.TopP,
                                        formatterAgent.RepeatPenalty,
                                        modelRole.TopK ?? formatterAgent.TopK,
                                        formatterAgent.RepeatLastN,
                                        formatterAgent.NumPredict);

                                    var fallbackChunkResult = await FormatChunkWithRetriesAsync(
                                        fallbackBridge,
                                        fallbackSystemPrompt,
                                        chunk.Text,
                                        chunk.Rows,
                                        i + 1,
                                        chunks.Count,
                                        effectiveRunId,
                                        formatterAgent.Name,
                                        fallbackModel.Name,
                                        ct);

                                    if (string.IsNullOrWhiteSpace(fallbackChunkResult.TaggedText))
                                    {
                                        throw new InvalidOperationException($"Fallback model {fallbackModel.Name} also failed: {fallbackChunkResult.LastError}");
                                    }

                                    return fallbackChunkResult;
                                },
                                validateResult: (chunkResult) => !string.IsNullOrWhiteSpace(chunkResult.TaggedText)
                            );

                            if (fallbackResult != null && !string.IsNullOrWhiteSpace(fallbackResult.TaggedText))
                            {
                                _logger?.Append(effectiveRunId, $"[chunk {i+1}/{chunks.Count}] Fallback model {successfulModelRole?.Model?.Name} succeeded!", "info");
                                result = fallbackResult;
                            }
                            else if (fallbackAttempted)
                            {
                                var msg = $"Formatter failed for chunk {i + 1}/{chunks.Count} after trying all fallback models. Last error: {result.LastError ?? "(none)"}";
                                return Fail(effectiveRunId, msg);
                            }
                            }
                        }

                        // Still failed after fallback attempts
                        if (string.IsNullOrWhiteSpace(result.TaggedText))
                        {
                            var msg = $"Formatter failed for chunk {i + 1}/{chunks.Count}. Last error: {result.LastError ?? "(none)"}";
                            return Fail(effectiveRunId, msg);
                        }
                    }

                    var parsedTags = StoryTaggingService.ParseFormatterMapping(chunk.Rows, result.TaggedText);
                    formatterTags.AddRange(parsedTags);
                }

                var existingTags = StoryTaggingService.LoadStoryTags(story.StoryTags);
                existingTags.RemoveAll(t => t.Type == StoryTaggingService.TagTypeFormatter);
                existingTags.AddRange(formatterTags);

                var storyTagsJson = StoryTaggingService.SerializeStoryTags(existingTags);
                _database.UpdateStoryRowsAndTags(story.Id, storyRows, storyTagsJson);

                var rebuiltTagged = StoryTaggingService.BuildStoryTagged(sourceText, existingTags);
                if (string.IsNullOrWhiteSpace(rebuiltTagged))
                {
                    return Fail(effectiveRunId, "Rebuilt tagged story is empty");
                }

                var saved = _database.UpdateStoryTagged(
                    story.Id,
                    rebuiltTagged,
                    formatterAgent.ModelId,
                    promptHash,
                    null);

                if (!saved)
                {
                    return Fail(effectiveRunId, $"Failed to persist tagged story for {_storyId}");
                }

                _logger?.Append(effectiveRunId, $"[story {_storyId}] Tagged story rebuilt from story_tags");

                // Requirement: if tagging succeeds, enqueue ambient_expert to add ambient tags
                TryEnqueueAmbientExpert(story, effectiveRunId);

                _logger?.MarkCompleted(effectiveRunId, "ok");
                return new CommandResult(true, "Tagged story generated (ambient_expert enqueued)");
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

        private void TryEnqueueAmbientExpert(StoryRecord story, string runId)
        {
            try
            {
                if (_commandDispatcher == null)
                {
                    _logger?.Append(runId, $"[story {_storyId}] ambient_expert enqueue skipped: dispatcher not available", "warn");
                    return;
                }

                if (_storiesService == null || _kernelFactory == null)
                {
                    _logger?.Append(runId, $"[story {_storyId}] ambient_expert enqueue skipped: missing dependencies", "warn");
                    return;
                }

                var ambientRunId = $"ambient_expert_{_storyId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";

                _commandDispatcher.Enqueue(
                    "ambient_expert",
                    async ctx =>
                    {
                        try
                        {
                            var cmd = new AmbientExpertCommand(_storyId, _database, _kernelFactory, _storiesService, _logger, _commandDispatcher, _tuning, _scopeFactory);
                            return await cmd.ExecuteAsync(ctx.CancellationToken, ambientRunId);
                        }
                        catch (Exception ex)
                        {
                            return new CommandResult(false, ex.Message);
                        }
                    },
                    runId: ambientRunId,
                    threadScope: "story/ambient_expert",
                    metadata: new Dictionary<string, string>
                    {
                        ["storyId"] = _storyId.ToString(),
                        ["operation"] = "ambient_expert",
                        ["trigger"] = "formatter_completed",
                        ["taggedVersion"] = (story.StoryTaggedVersion ?? 0).ToString()
                    },
                    priority: 2);

                _logger?.Append(runId, $"[story {_storyId}] Enqueued ambient_expert (runId={ambientRunId})", "info");
            }
            catch (Exception ex)
            {
                _logger?.Append(runId, $"[story {_storyId}] Failed to enqueue ambient_expert: {ex.Message}", "warn");
            }
        }

        private CommandResult Fail(string runId, string message)
        {
            _logger?.Append(runId, message, "error");
            _logger?.MarkCompleted(runId, "failed");
            return new CommandResult(false, message);
        }

        private async Task<FormatChunkResult> FormatChunkWithRetriesAsync(
            LangChainChatBridge bridge,
            string? systemPrompt,
            string chunkText,
            IReadOnlyList<StoryTaggingService.StoryRow> chunkRows,
            int chunkIndex,
            int chunkCount,
            string runId,
            string formatterAgentName,
            string modelName,
            CancellationToken ct)
        {
            string? lastError = null;
            string? lastRequestText = null;
            string? lastMappingText = null;

            var correctionRetries = Math.Max(0, _tuning.TransformStoryRawToTagged.FormatterV2CorrectionRetries);
            var maxAttempts = correctionRetries > 0
                ? 1 + correctionRetries
                : Math.Max(1, _tuning.TransformStoryRawToTagged.MaxAttemptsPerChunk);

            if (chunkRows == null || chunkRows.Count == 0)
            {
                return new FormatChunkResult(string.Empty, "Chunk without rows");
            }

            var quoteLineIds = new HashSet<int>();
            foreach (var row in chunkRows)
            {
                if (StartsWithOpeningQuote(row.Text))
                {
                    quoteLineIds.Add(row.LineId);
                }
            }

            // Provide the agent an explicit list of dialogue lines to tag.
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

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Formatting attempt {attempt}/{maxAttempts}");

                try
                {
                    var messages = new List<ConversationMessage>();
                    if (!string.IsNullOrWhiteSpace(systemPrompt))
                    {
                        messages.Add(new ConversationMessage { Role = "system", Content = systemPrompt });
                    }

                    // Always append strict formatter-v2 rules to prevent the model from rewriting text.
                    messages.Add(new ConversationMessage { Role = "system", Content = FormatterV2SystemPrompt });

                    // IMPORTANT: on retries we resend the original request, without echoing the model's previous answer.
                    var userContent =
                        (dialogueLines.Count > 0
                            ? "RIGHE DI DIALOGO (tra virgolette) DA TAGGARE CON PERSONAGGIO+EMOZIONE:\n" + string.Join("\n", dialogueLines) + "\n\n"
                            : "NESSUNA RIGA DI DIALOGO TRA VIRGOLETTE IN QUESTO TESTO.\n\n")
                        + "TESTO COMPLETO (righe numerate):\n" + chunkText;

                    lastRequestText = userContent;

                    messages.Add(new ConversationMessage { Role = "user", Content = userContent });

                    string responseJson;
                    using (LogScope.Push("formatter", operationId: null, stepNumber: null, maxStep: null, agentName: formatterAgentName))
                    {
                        responseJson = await bridge.CallModelWithToolsAsync(messages, new List<Dictionary<string, object>>(), ct);
                    }
                    var (textContent, _) = LangChainChatBridge.ParseChatResponse(responseJson);

                    var mappingText = textContent?.Trim() ?? string.Empty;
                    lastMappingText = mappingText;
                    if (string.IsNullOrWhiteSpace(mappingText))
                    {
                        if (quoteLineIds.Count == 0)
                        {
                            return new FormatChunkResult(string.Empty, null);
                        }

                        if (quoteLineIds.Count == 1)
                        {
                            var onlyId = quoteLineIds.First();
                            var fallback = $"{onlyId:000} [PERSONAGGIO: Sconosciuto] [EMOZIONE: neutra]";
                            return new FormatChunkResult(fallback, null);
                        }

                        lastError = "La risposta è vuota ma ci sono righe di dialogo: devi restituire il mapping per le righe dove parla qualcuno.";
                        continue;
                    }

                    var idToTags = FormatterV2.ParseIdToTagsMapping(mappingText);

                    // Requested checks on agent return:
                    // - It is allowed to return an empty mapping (meaning: no dialogue in this chunk).
                    // - All lines that start with an opening quote must be tagged as PERSONAGGIO.

                    // 2) Must have PERSONAGGIO tag on dialogue lines (those starting with an opening quote)
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

                        // Allow one unassigned dialogue line: default it to an unknown speaker with neutral emotion.
                        if (bad.Count == 1)
                        {
                            var missingId = bad[0];
                            idToTags[missingId] = "[PERSONAGGIO: Sconosciuto] [EMOZIONE: neutra]";
                        }
                        else if (bad.Count > 1)
                        {
                            lastError = $"Controllo fallito: sulle righe con doppi apici deve esserci un tag PERSONAGGIO. ID: {string.Join(", ", bad.Select(x => x.ToString("000")))}";
                            continue;
                        }
                    }

                    var mappingNormalized = string.Join("\n",
                        idToTags
                            .OrderBy(k => k.Key)
                            .Select(k => $"{k.Key:000} {k.Value}".TrimEnd()));

                    _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Validated mapping: {idToTags.Count} lines");
                    return new FormatChunkResult(mappingNormalized, null);
                }
                catch (Exception ex)
                {
                    _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Attempt {attempt} failed: {ex.Message}", "warn");
                    lastError = $"Errore durante l'elaborazione: {ex.Message}";
                }
            }

            // Hard failure: best-effort ask for a free-text explanation, and log it with source=explanation.
            try
            {
                if (!string.IsNullOrWhiteSpace(lastRequestText))
                {
                    var explanationRequest =
                        "MODALITÀ DIAGNOSTICA (TESTO LIBERO).\n" +
                        "Non rispondere con mapping.\n" +
                        "Spiega in italiano perché non hai assegnato PERSONAGGIO+EMOZIONE alle righe di dialogo richieste.\n\n" +
                        "RIGHE DI DIALOGO DA TAGGARE:\n" + (dialogueLines.Count > 0 ? string.Join("\n", dialogueLines) : "(nessuna)") + "\n\n" +
                        "ULTIMA RICHIESTA CHE TI È STATA INVIATA:\n" + lastRequestText + "\n\n" +
                        "TUA ULTIMA RISPOSTA:\n" + (string.IsNullOrWhiteSpace(lastMappingText) ? "(vuota)" : lastMappingText) + "\n\n" +
                        "ULTIMO ERRORE RILEVATO DAL PROGRAMMA:\n" + (lastError ?? "(non specificato)") + "\n\n" +
                        "Spiega cosa ti ha confuso (formato, ambiguità, limiti del modello, ecc.)." +
                        "Proponi una modifica o aggiunta alle istruzioni system per evitare questo problema in futuro.";

                    _logger?.Log("Information", "explanation", $"[formatter={formatterAgentName}] [model={modelName}] REQUEST\n{explanationRequest}");

                    var diagMessages = new List<ConversationMessage>
                    {
                        new ConversationMessage { Role = "system", Content = "Sei in modalità diagnostica. Rispondi SOLO in testo libero." },
                        new ConversationMessage { Role = "user", Content = explanationRequest }
                    };

                    string diagJson;
                    using (LogScope.Push("formatter_explanation", operationId: null, stepNumber: null, maxStep: null, agentName: formatterAgentName))
                    {
                        diagJson = await bridge.CallModelWithToolsAsync(diagMessages, new List<Dictionary<string, object>>(), ct);
                    }

                    var (diagText, _) = LangChainChatBridge.ParseChatResponse(diagJson);
                    var diagTextTrim = (diagText ?? string.Empty).Trim();
                    _logger?.Log(
                        string.IsNullOrWhiteSpace(diagTextTrim) ? "Warning" : "Information",
                        "explanation",
                        $"[formatter={formatterAgentName}] [model={modelName}] RESPONSE\n" + (string.IsNullOrWhiteSpace(diagTextTrim) ? "(empty)" : diagTextTrim));
                }
            }
            catch (Exception ex)
            {
                _logger?.Log("Warning", "explanation", $"Failed to capture formatter explanation: {ex.Message}");
            }

            _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Failed after {maxAttempts} attempts. Last error: {lastError}", "error");
            return new FormatChunkResult(string.Empty, lastError);
        }

        private static bool StartsWithOpeningQuote(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var trimmed = text.TrimStart();
            if (trimmed.Length == 0) return false;
            return trimmed[0] == '"' || trimmed[0] == '“' || trimmed[0] == '«';
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

        /// <summary>
        /// IMPORTANT: the formatter must never see editorial structure (titles/chapters/markdown).
        /// It should receive only continuous narration text.
        /// </summary>
        private static string PreNormalizeForFormatter(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            var text = input.Replace("\r\n", "\n");
            var lines = text.Split('\n');

            // Build paragraphs: join non-empty lines with spaces, keep blank lines as paragraph breaks.
            var paragraphs = new List<string>();
            var current = new StringBuilder();

            void FlushParagraph()
            {
                var p = current.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(p))
                    paragraphs.Add(p);
                current.Clear();
            }

            foreach (var rawLine in lines)
            {
                var line = (rawLine ?? string.Empty).Trim();

                if (line.Length == 0)
                {
                    FlushParagraph();
                    continue;
                }

                // Remove markdown headings entirely (editorial structure)
                if (Regex.IsMatch(line, @"^#{1,6}\s+"))
                    continue;

                // Remove common title/chapter lines (editorial structure)
                if (Regex.IsMatch(line, @"^(titolo|title)\b\s*[:\-–—]?\s*.*$", RegexOptions.IgnoreCase))
                    continue;
                if (Regex.IsMatch(line, @"^(capitolo|chapter)\b\s*(\d+|[ivxlcdm]+)?\b\s*[:\-–—]?.*$", RegexOptions.IgnoreCase))
                    continue;

                // Remove horizontal rules / separators
                if (Regex.IsMatch(line, @"^[-*_]{3,}\s*$"))
                    continue;

                // Remove fenced code blocks markers (safety: formatter shouldn't see them)
                if (Regex.IsMatch(line, @"^`{3,}"))
                    continue;

                // Strip markdown emphasis/bold markers but keep the text
                var cleaned = line;
                cleaned = cleaned.Replace("**", string.Empty).Replace("__", string.Empty);
                cleaned = Regex.Replace(cleaned, @"\*(?<t>[^*]+)\*", "${t}");
                cleaned = Regex.Replace(cleaned, @"_(?<t>[^_]+)_", "${t}");

                // Dialogue asterisks (common pattern: *dialogo*)
                cleaned = Regex.Replace(cleaned, @"^\*+\s*(?<t>.+?)\s*\*+$", "${t}");

                // If any stray asterisks remain, remove them to avoid confusing the formatter
                cleaned = cleaned.Replace("*", string.Empty);

                cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
                if (cleaned.Length == 0)
                    continue;

                if (current.Length > 0)
                    current.Append(' ');
                current.Append(cleaned);
            }

            FlushParagraph();

            // Re-join as continuous narration with paragraph breaks
            return string.Join("\n\n", paragraphs).Trim();
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

        private sealed record Chunk(int Index, string Text);
        private sealed record Segment(int Start, int End, int TokenCount);

        private List<Chunk> SplitStoryIntoChunks(string storyText)
        {
            var chunks = new List<Chunk>();
            if (string.IsNullOrWhiteSpace(storyText))
            {
                return chunks;
            }

            var minTokensPerChunk = Math.Max(0, _tuning.TransformStoryRawToTagged.MinTokensPerChunk);
            var maxTokensPerChunk = Math.Max(1, _tuning.TransformStoryRawToTagged.MaxTokensPerChunk);
            var targetTokensPerChunk = Math.Max(1, _tuning.TransformStoryRawToTagged.TargetTokensPerChunk);
            // Formatter V2 builds tags deterministically on the original text. Using overlap would
            // duplicate content and complicate merging, so we force overlap to 0.
            var overlapTokens = 0;

            var segments = SplitIntoSegments(storyText);
            if (segments.Count == 0)
            {
                chunks.Add(new Chunk(0, storyText));
                return chunks;
            }

            int segmentIndex = 0;
            int chunkIndex = 0;
            while (segmentIndex < segments.Count)
            {
                int startSeg = segmentIndex;
                int endSeg = startSeg;
                int tokenCount = 0;

                while (endSeg < segments.Count)
                {
                    var segTokens = segments[endSeg].TokenCount;
                    if (tokenCount + segTokens > maxTokensPerChunk && tokenCount >= minTokensPerChunk)
                    {
                        break;
                    }

                    tokenCount += segTokens;
                    endSeg++;

                    if (tokenCount >= targetTokensPerChunk && tokenCount >= minTokensPerChunk)
                    {
                        break;
                    }
                }

                if (endSeg == startSeg)
                {
                    endSeg = Math.Min(startSeg + 1, segments.Count);
                }

                var startChar = segments[startSeg].Start;
                var endChar = segments[endSeg - 1].End;
                var chunkText = storyText.Substring(startChar, endChar - startChar);
                chunks.Add(new Chunk(chunkIndex, chunkText));

                if (endSeg >= segments.Count)
                {
                    break;
                }

                int overlapCount = 0;
                int nextStartSeg = endSeg;
                for (int i = endSeg - 1; i >= startSeg; i--)
                {
                    overlapCount += segments[i].TokenCount;
                    if (overlapCount >= overlapTokens)
                    {
                        nextStartSeg = i;
                        break;
                    }
                }

                if (nextStartSeg <= startSeg)
                {
                    nextStartSeg = endSeg;
                }

                segmentIndex = nextStartSeg;
                chunkIndex++;
            }

            return chunks;
        }

        private static List<Segment> SplitIntoSegments(string text)
        {
            var segments = new List<Segment>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                var c = text[i];
                bool boundary = false;
                int end = i + 1;

                if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        end = i + 2;
                        i++;
                    }
                    boundary = true;
                }
                else if (c == '\n' || c == '.' || c == '!' || c == '?')
                {
                    boundary = true;
                }

                if (boundary)
                {
                    var segmentText = text.Substring(start, end - start);
                    segments.Add(new Segment(start, end, CountTokens(segmentText)));
                    start = end;
                }
            }

            if (start < text.Length)
            {
                var tail = text.Substring(start);
                segments.Add(new Segment(start, text.Length, CountTokens(tail)));
            }

            return segments;
        }

        private static int CountTokens(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            int count = 0;
            bool inToken = false;
            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (inToken)
                    {
                        inToken = false;
                    }
                }
                else
                {
                    if (!inToken)
                    {
                        count++;
                        inToken = true;
                    }
                }
            }

            return count;
        }

        private string MergeTaggedChunks(IReadOnlyList<string> chunks)
        {
            if (chunks.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(chunks[0]);
            var previous = chunks[0];

            for (int i = 1; i < chunks.Count; i++)
            {
                var current = chunks[i];
                var overlap = FindOverlapLength(previous, current);
                builder.Append(overlap > 0 ? current.Substring(overlap) : current);
                previous = current;
            }

            return builder.ToString();
        }

        private int FindOverlapLength(string previous, string current)
        {
            if (string.IsNullOrEmpty(previous) || string.IsNullOrEmpty(current))
            {
                return 0;
            }

            var maxOverlapChars = Math.Max(0, _tuning.TransformStoryRawToTagged.MaxOverlapChars);

            int max = Math.Min(previous.Length, current.Length);
            int maxSearch = Math.Min(max, maxOverlapChars);

            for (int len = maxSearch; len > 0; len--)
            {
                if (previous.AsSpan(previous.Length - len).SequenceEqual(current.AsSpan(0, len)))
                {
                    return len;
                }
            }

            return 0;
        }
    }
}
