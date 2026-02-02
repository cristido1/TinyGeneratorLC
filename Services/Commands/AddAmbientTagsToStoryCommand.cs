using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Models;
using TinyGenerator.Services;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace TinyGenerator.Services.Commands
{
    /// <summary>
    /// Reads story_tagged, splits into chunks, adds ambient/noise tags via ambient_expert agent,
    /// updates story_tagged, then enqueues add_fx_tags_to_story.
    /// </summary>
    public sealed class AddAmbientTagsToStoryCommand
    {
        private readonly CommandTuningOptions _tuning;

        private readonly long _storyId;
        private readonly DatabaseService _database;
        private readonly ILangChainKernelFactory _kernelFactory;
        private readonly StoriesService? _storiesService;
        private readonly ICustomLogger? _logger;
        private readonly ICommandDispatcher? _commandDispatcher;
        private readonly IServiceScopeFactory? _scopeFactory;

        public AddAmbientTagsToStoryCommand(
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
                ? (_storiesService?.CurrentDispatcherRunId ?? $"add_ambient_tags_to_story_{_storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}")
                : runId;

            _logger?.Start(effectiveRunId);
            _logger?.Append(effectiveRunId, $"[story {_storyId}] Starting add_ambient_tags_to_story pipeline");

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
                    return Fail(effectiveRunId, $"Story {_storyId} has no revised text");
                }

                var ambientAgent = _database.ListAgents()
                    .FirstOrDefault(a => a.IsActive && string.Equals(a.Role, "ambient_expert", StringComparison.OrdinalIgnoreCase));

                if (ambientAgent == null)
                {
                    return Fail(effectiveRunId, "No active ambient_expert agent found");
                }

                if (!ambientAgent.ModelId.HasValue)
                {
                    return Fail(effectiveRunId, $"Ambient expert agent {ambientAgent.Name} has no model configured");
                }

                var currentModelId = ambientAgent.ModelId.Value;

                var modelInfo = _database.GetModelInfoById(ambientAgent.ModelId.Value);
                if (string.IsNullOrWhiteSpace(modelInfo?.Name))
                {
                    return Fail(effectiveRunId, $"Model not found for ambient expert agent {ambientAgent.Name}");
                }

                var triedModelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    modelInfo.Name
                };

                var currentModelName = modelInfo.Name;

                var systemPrompt = BuildSystemPrompt(ambientAgent);
                var rowsBuild = StoryTaggingService.BuildStoryRows(sourceText);
                var storyRows = string.IsNullOrWhiteSpace(story.StoryRows) ? rowsBuild.StoryRows : story.StoryRows;
                if (string.IsNullOrWhiteSpace(storyRows))
                {
                    storyRows = rowsBuild.StoryRows;
                }

                var rows = StoryTaggingService.ParseStoryRows(storyRows);
                if (rows.Count == 0)
                {
                    return Fail(effectiveRunId, "No rows produced from story text");
                }

                _database.UpdateStoryRowsAndTags(story.Id, storyRows, story.StoryTags);

                var chunks = StoryTaggingService.SplitRowsIntoChunks(
                    rows,
                    _tuning.AmbientExpert.MinTokensPerChunk,
                    _tuning.AmbientExpert.MaxTokensPerChunk,
                    _tuning.AmbientExpert.TargetTokensPerChunk);
                if (chunks.Count == 0)
                {
                    return Fail(effectiveRunId, "No chunks produced from story rows");
                }

                _logger?.Append(effectiveRunId, $"[story {_storyId}] Split into {chunks.Count} chunks (rows)");

                var bridge = _kernelFactory.CreateChatBridge(
                    currentModelName,
                    ambientAgent.Temperature,
                    ambientAgent.TopP,
                    ambientAgent.RepeatPenalty,
                    ambientAgent.TopK,
                    ambientAgent.RepeatLastN,
                    ambientAgent.NumPredict);

                var ambientTags = new List<StoryTaggingService.StoryTagEntry>();
                for (int i = 0; i < chunks.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = chunks[i];

                    try
                    {
                        _commandDispatcher?.UpdateStep(effectiveRunId, i + 1, chunks.Count, $"Adding ambient tags chunk {i + 1}/{chunks.Count}");
                    }
                    catch
                    {
                        // best-effort
                    }

                    string mappingText;
                    try
                    {
                        mappingText = await ProcessChunkWithRetriesAsync(
                            bridge,
                            systemPrompt,
                            chunk.Text,
                            i + 1,
                            chunks.Count,
                            effectiveRunId,
                            ct);
                    }
                    catch (Exception ex) when (_scopeFactory != null)
                    {
                        _logger?.Append(effectiveRunId,
                            $"[chunk {i + 1}/{chunks.Count}] Primary ambient_expert model '{currentModelName}' failed: {ex.Message}. Attempting fallback models...",
                            "warn");

                        var fallback = await TryChunkWithFallbackAsync(
                            roleCode: "ambient_expert",
                            failingModelId: currentModelId,
                            systemPrompt: systemPrompt,
                            chunkText: chunk.Text,
                            chunkIndex: i + 1,
                            chunkCount: chunks.Count,
                            runId: effectiveRunId,
                            agent: ambientAgent,
                            triedModelNames: triedModelNames,
                            ct: ct);

                        if (fallback == null)
                        {
                            throw;
                        }

                        mappingText = fallback.Value.Tagged;

                        // Switch "in-place" for subsequent chunks.
                        currentModelId = fallback.Value.ModelId;
                        currentModelName = fallback.Value.ModelName;
                        bridge = _kernelFactory.CreateChatBridge(
                            currentModelName,
                            ambientAgent.Temperature,
                            ambientAgent.TopP,
                            ambientAgent.RepeatPenalty,
                            ambientAgent.TopK,
                            ambientAgent.RepeatLastN,
                            ambientAgent.NumPredict);
                    }

                    if (string.IsNullOrWhiteSpace(mappingText))
                    {
                        return Fail(effectiveRunId, $"Ambient expert returned empty text for chunk {i + 1}/{chunks.Count}");
                    }

                    var parsed = StoryTaggingService.ParseAmbientMapping(mappingText);
                    ambientTags.AddRange(parsed);
                }

                var existingTags = StoryTaggingService.LoadStoryTags(story.StoryTags);
                existingTags.RemoveAll(t => t.Type == StoryTaggingService.TagTypeAmbient);
                existingTags.AddRange(ambientTags);
                var storyTagsJson = StoryTaggingService.SerializeStoryTags(existingTags);
                _database.UpdateStoryRowsAndTags(story.Id, storyRows, storyTagsJson);

                var rebuiltTagged = StoryTaggingService.BuildStoryTagged(sourceText, existingTags);
                if (string.IsNullOrWhiteSpace(rebuiltTagged))
                {
                    return Fail(effectiveRunId, "Rebuilt tagged story is empty");
                }

                var saved = _database.UpdateStoryTaggedContent(story.Id, rebuiltTagged);

                if (!saved)
                {
                    return Fail(effectiveRunId, $"Failed to persist tagged story for {_storyId}");
                }

                _logger?.Append(effectiveRunId, $"[story {_storyId}] Ambient tags rebuilt from story_tags");

                var allowNext = _storiesService?.ApplyStatusTransitionWithCleanup(story, "tagged_ambient", effectiveRunId) ?? true;
                var enqueued = TryEnqueueNextStatus(story, allowNext, effectiveRunId);

                _logger?.MarkCompleted(effectiveRunId, "ok");
                return new CommandResult(true, enqueued
                    ? "Ambient tags added (next status enqueued)"
                    : "Ambient tags added");
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

        private bool TryEnqueueNextStatus(StoryRecord story, bool allowNext, string runId)
        {
            try
            {
                if (!allowNext)
                {
                    _logger?.Append(runId, $"[story {_storyId}] next status enqueue skipped: delete_next_items attivo", "info");
                    return false;
                }

                if (_storiesService != null && !_storiesService.IsTaggedAmbientAutoLaunchEnabled())
                {
                    _logger?.Append(runId, $"[story {_storyId}] next status enqueue skipped: StoryTaggingPipeline ambient autolaunch disabled", "info");
                    return false;
                }

                if (_storiesService != null && !_storiesService.TryValidateTaggedAmbient(story, out var ambientReason))
                {
                    _logger?.Append(runId, $"[story {_storyId}] next status enqueue skipped: ambient validation failed ({ambientReason})", "warn");
                    return false;
                }

                if (!_tuning.AmbientExpert.AutolaunchNextCommand)
                {
                    _logger?.Append(runId, $"[story {_storyId}] next status enqueue skipped: AutolaunchNextCommand disabled", "info");
                    return false;
                }

                if (_storiesService == null)
                {
                    _logger?.Append(runId, $"[story {_storyId}] next status enqueue skipped: stories service missing", "warn");
                    return false;
                }

                var refreshedStory = _storiesService.GetStoryById(story.Id) ?? story;
                var nextRunId = _storiesService.EnqueueNextStatusCommand(refreshedStory, "ambient_tags_completed", priority: 2);
                if (!string.IsNullOrWhiteSpace(nextRunId))
                {
                    _logger?.Append(runId, $"[story {_storyId}] Enqueued next status (runId={nextRunId})", "info");
                    return true;
                }

                _logger?.Append(runId, $"[story {_storyId}] Next status enqueue skipped: no next status available", "info");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.Append(runId, $"[story {_storyId}] Failed to enqueue next status: {ex.Message}", "warn");
                return false;
            }
        }

        private readonly record struct FallbackChunkResult(string Tagged, int ModelId, string ModelName);

        private async Task<FallbackChunkResult?> TryChunkWithFallbackAsync(
            string roleCode,
            int failingModelId,
            string? systemPrompt,
            string chunkText,
            int chunkIndex,
            int chunkCount,
            string runId,
            Agent agent,
            HashSet<string> triedModelNames,
            CancellationToken ct)
        {
            if (_scopeFactory == null)
            {
                return null;
            }

            using var scope = _scopeFactory.CreateScope();
            var fallbackService = scope.ServiceProvider.GetService<ModelFallbackService>();
            if (fallbackService == null)
            {
                _logger?.Append(runId, "ModelFallbackService not available in DI scope; cannot fallback.", "warn");
                return null;
            }

            var (result, successfulModelRole) = await fallbackService.ExecuteWithFallbackAsync(
                roleCode,
                failingModelId,
                async modelRole =>
                {
                    var modelName = modelRole.Model?.Name;
                    if (string.IsNullOrWhiteSpace(modelName))
                    {
                        throw new InvalidOperationException("Fallback ModelRole has no Model.Name");
                    }

                    var candidateBridge = _kernelFactory.CreateChatBridge(
                        modelName,
                        agent.Temperature,
                        agent.TopP,
                        agent.RepeatPenalty,
                        agent.TopK,
                        agent.RepeatLastN,
                        agent.NumPredict);

                    return await ProcessChunkWithRetriesAsync(
                        candidateBridge,
                        systemPrompt,
                        chunkText,
                        chunkIndex,
                        chunkCount,
                        runId,
                        ct);
                },
                validateResult: s => !string.IsNullOrWhiteSpace(s),
                shouldTryModelRole: mr =>
                {
                    var name = mr.Model?.Name;
                    return !string.IsNullOrWhiteSpace(name) && triedModelNames.Add(name);
                });

            if (string.IsNullOrWhiteSpace(result) || successfulModelRole?.Model == null || string.IsNullOrWhiteSpace(successfulModelRole.Model.Name))
            {
                _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Fallback models exhausted for role '{roleCode}'.", "error");
                return null;
            }

            return new FallbackChunkResult(result, successfulModelRole.ModelId, successfulModelRole.Model.Name);
        }

        private CommandResult Fail(string runId, string message)
        {
            _logger?.Append(runId, message, "error");
            _logger?.MarkCompleted(runId, "failed");
            return new CommandResult(false, message);
        }

        private async Task<string> ProcessChunkWithRetriesAsync(
            LangChainChatBridge bridge,
            string? systemPrompt,
            string chunkText,
            int chunkIndex,
            int chunkCount,
            string runId,
            CancellationToken ct)
        {
            string? lastError = null;
            string? lastAssistantText = null;
            List<ConversationMessage>? lastRequestMessages = null;

            var maxAttempts = Math.Max(1, _tuning.AmbientExpert.MaxAttemptsPerChunk);
            var minTagsRequired = Math.Max(0, _tuning.AmbientExpert.MinAmbientTagsPerChunkRequirement);
            var retryDelayBaseSeconds = Math.Max(0, _tuning.AmbientExpert.RetryDelayBaseSeconds);
            var diagnoseOnFinalFailure = _tuning.AmbientExpert.DiagnoseOnFinalFailure;
            var hadCorrections = false;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Ambient tagging attempt {attempt}/{maxAttempts}");

                try
                {
                    var messages = new List<ConversationMessage>();
                    if (!string.IsNullOrWhiteSpace(systemPrompt))
                    {
                        messages.Add(new ConversationMessage { Role = "system", Content = systemPrompt });
                    }

                    messages.Add(new ConversationMessage
                    {
                        Role = "system",
                        Content =
                            "FORMATO RISPOSTA OBBLIGATORIO:\n" +
                            "- Restituisci SOLO righe nel formato: ID descrizione dei rumori\n" +
                            "- Non usare parentesi o tag [RUMORI]\n" +
                            "- Non riscrivere il testo, non aggiungere spiegazioni\n" +
                            "- Gli ID sono quelli del testo numerato fornito\n" +
                            "- Se non c'è un rumore per una riga, non restituire quella riga\n"
                    });

                    // Add error feedback for retry attempts
                    if (attempt > 1 && !string.IsNullOrWhiteSpace(lastError))
                    {
                        hadCorrections = true;
                        messages.Add(new ConversationMessage 
                        { 
                            Role = "system", 
                            Content = $"{lastError} Questo è il tentativo {attempt} di {maxAttempts}." 
                        });
                    }

                    messages.Add(new ConversationMessage { Role = "user", Content = chunkText });

                    // Keep the last request/response around for diagnostics.
                    lastRequestMessages = messages;

                    var responseJson = await bridge.CallModelWithToolsAsync(messages, new List<Dictionary<string, object>>(), ct);
                    var (textContent, _) = LangChainChatBridge.ParseChatResponse(responseJson);
                    var cleaned = textContent?.Trim() ?? string.Empty;
                    lastAssistantText = cleaned;

                    if (string.IsNullOrWhiteSpace(cleaned))
                    {
                        _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Empty response on attempt {attempt}", "warn");
                        _logger?.MarkLatestModelResponseResult("FAILED", "Risposta vuota");
                        lastError = "Il testo ritornato è vuoto.";
                        hadCorrections = true;
                        continue;
                    }

                    var tags = StoryTaggingService.ParseAmbientMapping(cleaned);
                    var tagCount = tags.Count;
                    if (tagCount == 0)
                    {
                        _logger?.MarkLatestModelResponseResult("FAILED", "Nessuna riga valida nel formato richiesto");
                        lastError = "Non ho trovato righe valide nel formato \"ID descrizione\".";
                        hadCorrections = true;
                        continue;
                    }

                    if (tagCount < minTagsRequired)
                    {
                        _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Not enough ambient tags: {tagCount} found, minimum {minTagsRequired} required", "warn");
                        _logger?.MarkLatestModelResponseResult("FAILED", $"Hai inserito solo {tagCount} tag. Devi inserirne almeno {minTagsRequired}.");
                        lastError = $"Hai inserito solo {tagCount} tag [RUMORI]. Devi inserire ALMENO {minTagsRequired} tag di questo tipo per arricchire l'atmosfera della scena, non ripetere gli stessi tag.";
                        hadCorrections = true;
                        continue;
                    }

                    _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Validated mapping: totalAmbient={tagCount}");
                    _logger?.MarkLatestModelResponseResult(
                        hadCorrections ? "FAILED" : "SUCCESS",
                        hadCorrections ? "Risposta corretta dopo retry" : null);
                    return cleaned;
                }
                catch (Exception ex)
                {
                    _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Error on attempt {attempt}: {ex.Message}", "error");
                    lastError = $"Errore durante l'elaborazione: {ex.Message}";
                    
                    if (attempt == maxAttempts)
                    {
                        throw;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(retryDelayBaseSeconds * attempt), ct);
                }
            }

            // If we reached here, all attempts failed for THIS chunk.
            if (diagnoseOnFinalFailure)
            {
                try
                {
                    var diagnosis = await DiagnoseFailureAsync(
                        bridge,
                        systemPrompt,
                        lastRequestMessages,
                        chunkText,
                        lastAssistantText,
                        lastError,
                        chunkIndex,
                        chunkCount,
                        ct).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(diagnosis))
                    {
                        _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] ambient_expert self-diagnosis: {diagnosis}", "warn");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Failed to collect ambient_expert self-diagnosis: {ex.Message}", "warn");
                }
            }

            throw new InvalidOperationException($"Failed to process chunk {chunkIndex}/{chunkCount} after {maxAttempts} attempts. Last error: {lastError}");
        }

        private async Task<string?> DiagnoseFailureAsync(
            LangChainChatBridge bridge,
            string? originalSystemPrompt,
            List<ConversationMessage>? lastRequestMessages,
            string chunkText,
            string? lastAssistantText,
            string? lastFailureReason,
            int chunkIndex,
            int chunkCount,
            CancellationToken ct)
        {
            var auditSystem =
                "Sei un auditor tecnico per l'agente ambient_expert. " +
                "Devi spiegare in modo conciso perché l'output non ha superato la validazione o perché è fallito. " +
                "Non inventare contenuti; basati sui dati forniti.";

            var sb = new StringBuilder();
            sb.AppendLine($"DIAGNOSI ambient_expert - chunk {chunkIndex}/{chunkCount}");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(lastFailureReason))
            {
                sb.AppendLine("=== MOTIVO FALLIMENTO (validazione/errore) ===");
                sb.AppendLine(ClipForPrompt(lastFailureReason, 2000));
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(lastAssistantText))
            {
                sb.AppendLine("=== ULTIMO OUTPUT MODELLO (estratto) ===");
                sb.AppendLine(ClipForPrompt(lastAssistantText, 2000));
                sb.AppendLine();
            }

            sb.AppendLine("=== INPUT (chunk) ===");
            sb.AppendLine(ClipForPrompt(chunkText, 2500));
            sb.AppendLine();

            if (lastRequestMessages != null && lastRequestMessages.Count > 0)
            {
                sb.AppendLine("=== ULTIMA CONVERSAZIONE (ruoli) ===");
                foreach (var m in lastRequestMessages)
                {
                    sb.AppendLine($"- {m.Role}: {ClipForPrompt(m.Content, 250)}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("Output richiesto: 5-10 righe, punti elenco se utile, senza proporre un 'nuovo tentativo'.");

            var diagMessages = new List<ConversationMessage>();
            if (!string.IsNullOrWhiteSpace(originalSystemPrompt))
            {
                diagMessages.Add(new ConversationMessage { Role = "system", Content = originalSystemPrompt });
            }

            diagMessages.Add(new ConversationMessage { Role = "system", Content = auditSystem });
            diagMessages.Add(new ConversationMessage { Role = "user", Content = sb.ToString() });

            var responseJson = await bridge.CallModelWithToolsAsync(diagMessages, new List<Dictionary<string, object>>(), ct).ConfigureAwait(false);
            var (textContent, _) = LangChainChatBridge.ParseChatResponse(responseJson);
            return string.IsNullOrWhiteSpace(textContent) ? null : textContent.Trim();
        }

        private static string MergeAmbientTagsIntoOriginalChunk(string originalChunk, string modelOutput, out int insertedCount)
        {
            insertedCount = 0;
            if (string.IsNullOrWhiteSpace(originalChunk) || string.IsNullOrWhiteSpace(modelOutput))
            {
                return originalChunk ?? string.Empty;
            }

            var newline = DetectNewline(originalChunk);
            var originalNormalized = NormalizeNewlines(originalChunk);
            var outputNormalized = NormalizeNewlines(modelOutput);

            var originalLines = originalNormalized.Split('\n').ToList();
            var outputLines = outputNormalized.Split('\n').ToList();

            var existingTagLines = new HashSet<string>(
                originalLines.Where(IsAmbientTagLine).Select(NormalizeTagLine),
                StringComparer.OrdinalIgnoreCase);

            var searchStart = 0;

            // Group ambient insertions by anchor position (PrevAnchorTagLine, NextAnchorTagLine, NearestContentAnchor)
            var pending = ExtractAmbientInsertions(outputLines);
            var grouped = pending
                .GroupBy(ins => $"{ins.PrevAnchorTagLine}|{ins.NextAnchorTagLine}|{ins.NearestContentAnchor}")
                .ToList();

            foreach (var group in grouped)
            {
                // Concatenate all tag texts for this anchor
                var allTexts = group.Select(g => Regex.Replace(g.TagLine, @"^\s*\[(?:AMBIENTE|RUMORI|RUMORE)\s*:?(.*?)\]", "$1", RegexOptions.IgnoreCase).Trim()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                if (allTexts.Count == 0) continue;
                var tagText = string.Join("; ", allTexts);
                var tagLine = $"[RUMORI: {tagText}]";
                var tagKey = NormalizeTagLine(tagLine);
                if (string.IsNullOrWhiteSpace(tagLine) || existingTagLines.Contains(tagKey)) continue;

                var anchor = group.First();
                var insertAt = -1;
                if (!string.IsNullOrWhiteSpace(anchor.NextAnchorTagLine))
                    insertAt = FindLineIndex(originalLines, anchor.NextAnchorTagLine!, searchStart);
                if (insertAt < 0 && !string.IsNullOrWhiteSpace(anchor.PrevAnchorTagLine))
                {
                    var prevIdx = FindLineIndex(originalLines, anchor.PrevAnchorTagLine!, searchStart);
                    if (prevIdx >= 0)
                    {
                        insertAt = FindNextTagLineIndex(originalLines, prevIdx + 1);
                        if (insertAt < 0) insertAt = originalLines.Count;
                    }
                }
                if (insertAt < 0 && !string.IsNullOrWhiteSpace(anchor.NearestContentAnchor))
                    insertAt = FindLineIndex(originalLines, anchor.NearestContentAnchor!, searchStart);
                if (insertAt < 0) insertAt = originalLines.Count;

                // Safety: do not split an existing multi-line block.
                if (insertAt < originalLines.Count && insertAt > 0)
                {
                    var blockStart = FindNearestBlockStartTagIndex(originalLines, insertAt - 1);
                    if (blockStart >= 0)
                    {
                        var blockTagLine = (originalLines[blockStart] ?? string.Empty).TrimStart();
                        var isNarrator = IsNarratorTagLine(blockTagLine);
                        var nextLine = (originalLines[insertAt] ?? string.Empty).TrimStart();
                        if (!nextLine.StartsWith("[", StringComparison.Ordinal) && blockStart < insertAt && !isNarrator)
                            insertAt = blockStart;
                    }
                }

                insertAt = Math.Clamp(insertAt, 0, originalLines.Count);
                originalLines.Insert(insertAt, tagLine);
                insertedCount++;
                existingTagLines.Add(tagKey);

                // Narrator block reopen rule
                if (insertAt + 1 < originalLines.Count)
                {
                    var next = (originalLines[insertAt + 1] ?? string.Empty).TrimStart();
                    if (!next.StartsWith("[", StringComparison.Ordinal))
                    {
                        var blockStart = FindNearestBlockStartTagIndex(originalLines, insertAt - 1);
                        if (blockStart >= 0 && IsNarratorTagLine((originalLines[blockStart] ?? string.Empty).TrimStart()))
                            originalLines.Insert(insertAt + 1, "[NARRATORE]");
                    }
                }
                searchStart = insertAt + 1;
            }

            var merged = string.Join("\n", originalLines);
            merged = merged.Replace("\n", newline);
            return merged;
        }

        private sealed record AmbientInsertion(string TagLine, string? PrevAnchorTagLine, string? NextAnchorTagLine, string? NearestContentAnchor);

        private static List<AmbientInsertion> ExtractAmbientInsertions(IReadOnlyList<string> outputLines)
        {
            var result = new List<AmbientInsertion>();
            if (outputLines.Count == 0) return result;

            for (var i = 0; i < outputLines.Count; i++)
            {
                var line = outputLines[i] ?? string.Empty;
                var match = Regex.Match(line, @"\[(?:AMBIENTE|RUMORI|RUMORE)\b[^\]]*\]", RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    continue;
                }

                // If the tag occurs mid-line, start the insertion from there.
                var tagLine = line.Substring(match.Index).TrimStart();
                tagLine = Regex.Replace(tagLine, @"\[(\s*)RUMORE\b", "[$1RUMORI", RegexOptions.IgnoreCase).TrimEnd();

                if (string.IsNullOrWhiteSpace(tagLine))
                {
                    continue;
                }

                // Optionally include a short description line immediately after the tag.
                // We keep it on the SAME line (do not split blocks), unless the tag line already has tail text.
                if (!tagLine.Contains(']') || tagLine.TrimEnd().EndsWith("]", StringComparison.Ordinal))
                {
                    if (i + 1 < outputLines.Count)
                    {
                        var next = (outputLines[i + 1] ?? string.Empty).Trim();
                        if (!string.IsNullOrWhiteSpace(next) &&
                            !next.StartsWith("[", StringComparison.Ordinal) &&
                            next.Length <= 200)
                        {
                            // append as tail to the tag line (safer than adding a new line that might split blocks)
                            tagLine = tagLine + " " + next;
                        }
                    }
                }

                string? prevTag = null;
                for (var p = i - 1; p >= 0; p--)
                {
                    var t = (outputLines[p] ?? string.Empty).TrimStart();
                    if (IsNonAmbientTagLine(t))
                    {
                        prevTag = (outputLines[p] ?? string.Empty).TrimEnd();
                        break;
                    }
                }

                string? nextTag = null;
                for (var n = i + 1; n < outputLines.Count; n++)
                {
                    var t = (outputLines[n] ?? string.Empty).TrimStart();
                    if (IsNonAmbientTagLine(t))
                    {
                        nextTag = (outputLines[n] ?? string.Empty).TrimEnd();
                        break;
                    }
                }

                var contentAnchor = FindNearestContentAnchor(outputLines, i);
                result.Add(new AmbientInsertion(tagLine, prevTag, nextTag, contentAnchor));
            }

            return result;
        }

        private static string NormalizeNewlines(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private static string DetectNewline(string text)
        {
            if (string.IsNullOrEmpty(text)) return "\n";
            return text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        }

        private static bool IsAmbientTagLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            // Tag lines are expected to start the line.
            return Regex.IsMatch(line, @"^\s*\[(?:AMBIENTE|RUMORI|RUMORE)\b[^\]]*\]", RegexOptions.IgnoreCase);
        }

        private static bool IsNarratorTagLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            return Regex.IsMatch(line, @"^\s*\[NARRATORE\b[^\]]*\]", RegexOptions.IgnoreCase);
        }

        private static bool IsNonAmbientTagLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            var t = line.TrimStart();
            if (!t.StartsWith("[", StringComparison.Ordinal)) return false;
            if (!t.Contains(']')) return false;
            return !IsAmbientTagLine(t);
        }

        private static bool IsContentLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            // Treat any line starting with '[' as a tag line (not content).
            return !Regex.IsMatch(line, @"^\s*\[", RegexOptions.Compiled);
        }

        private static string NormalizeTagLine(string line)
        {
            return (line ?? string.Empty).Trim();
        }

        private static string? FindNearestContentAnchor(IReadOnlyList<string> lines, int tagLineIndex)
        {
            // Prefer the next content line as an anchor (insert tag *before* it).
            for (var j = tagLineIndex + 1; j < lines.Count; j++)
            {
                var candidate = lines[j] ?? string.Empty;
                if (IsContentLine(candidate))
                {
                    return candidate.Trim();
                }
            }

            // Fallback: use previous content line as anchor (insert tag *after* it).
            for (var j = tagLineIndex - 1; j >= 0; j--)
            {
                var candidate = lines[j] ?? string.Empty;
                if (IsContentLine(candidate))
                {
                    return candidate.Trim();
                }
            }

            return null;
        }

        private static int FindLineIndex(IReadOnlyList<string> lines, string anchorLine, int startIndex)
        {
            if (lines.Count == 0 || string.IsNullOrWhiteSpace(anchorLine)) return -1;

            var anchorTrim = anchorLine.Trim();
            var start = Math.Clamp(startIndex, 0, Math.Max(0, lines.Count - 1));

            // 1) Exact match (trimmed)
            for (var i = start; i < lines.Count; i++)
            {
                if (string.Equals((lines[i] ?? string.Empty).Trim(), anchorTrim, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            // 2) Contains match (trimmed)
            for (var i = start; i < lines.Count; i++)
            {
                var candidate = (lines[i] ?? string.Empty).Trim();
                if (candidate.Length == 0) continue;

                if (candidate.Contains(anchorTrim, StringComparison.Ordinal) ||
                    anchorTrim.Contains(candidate, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        private static int FindNextTagLineIndex(IReadOnlyList<string> lines, int startIndex)
        {
            var start = Math.Clamp(startIndex, 0, lines.Count);
            for (var i = start; i < lines.Count; i++)
            {
                var t = (lines[i] ?? string.Empty).TrimStart();
                if (t.StartsWith("[", StringComparison.Ordinal) && t.Contains(']'))
                {
                    return i;
                }
            }
            return -1;
        }

        private static int FindNearestBlockStartTagIndex(IReadOnlyList<string> lines, int fromIndex)
        {
            var i = Math.Min(fromIndex, lines.Count - 1);
            for (; i >= 0; i--)
            {
                var t = (lines[i] ?? string.Empty).TrimStart();
                if (t.StartsWith("[", StringComparison.Ordinal) && t.Contains(']'))
                {
                    return i;
                }
            }
            return -1;
        }

        private string? BuildSystemPrompt(TinyGenerator.Models.Agent agent)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(agent.Prompt))
            {
                sb.AppendLine(agent.Prompt);
            }

            if (!string.IsNullOrWhiteSpace(agent.Instructions))
            {
                sb.AppendLine(agent.Instructions);
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        private sealed record Segment(int Start, int End, int TokenCount);

        private List<(string Text, int StartIndex, int EndIndex)> SplitStoryIntoChunks(string text)
        {
            var chunks = new List<(string Text, int StartIndex, int EndIndex)>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return chunks;
            }

            var minTokensPerChunk = Math.Max(0, _tuning.AmbientExpert.MinTokensPerChunk);
            var maxTokensPerChunk = Math.Max(1, _tuning.AmbientExpert.MaxTokensPerChunk);
            var targetTokensPerChunk = Math.Max(1, _tuning.AmbientExpert.TargetTokensPerChunk);
            // NOTE: These expert commands merge and then concatenate chunks. Overlap would duplicate content.
            var overlapTokens = 0;

            var segments = SplitIntoSegments(text);
            if (segments.Count == 0)
            {
                chunks.Add((text, 0, text.Length));
                return chunks;
            }

            int segmentIndex = 0;
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
                chunks.Add((text.Substring(startChar, endChar - startChar), startChar, endChar));

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

        private static string ClipForPrompt(string? text, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var t = text.Trim();
            if (t.Length <= maxChars) return t;
            return t.Substring(0, maxChars) + "...";
        }

        private string MergeTaggedChunks(List<string> chunks)
        {
            if (chunks == null || chunks.Count == 0)
            {
                return string.Empty;
            }

            return string.Join("\n\n", chunks);
        }

        private static string ComputeSha256(string text)
        {
            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(text);
            var hash = sha256.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }
}

