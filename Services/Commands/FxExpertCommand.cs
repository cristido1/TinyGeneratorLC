using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands
{
    /// <summary>
    /// Reads story_tagged, splits into chunks, adds FX tags via fx_expert agent,
    /// updates story_tagged, then enqueues music_expert command.
    /// </summary>
    public sealed class FxExpertCommand
    {
        // NOTE: Models may truncate or fail to reproduce the full input text.
        // We therefore merge returned FX blocks back into the original chunk, using surrounding
        // (previous/next) non-FX tag lines as anchors.
        private readonly CommandTuningOptions _tuning;

        private static readonly Regex FxTagRegex = new(@"\[\s*FX\s*:[^\]]*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex FxTagLineRegex = new(@"(?im)^\s*\[\s*FX\s*:[^\]]*\]", RegexOptions.Compiled);
        private static readonly Regex FxBlockStartRegex = new(@"(?is)^\s*\[\s*FX\s*:[^\]]*\]", RegexOptions.Compiled);
        private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

        private readonly long _storyId;
        private readonly DatabaseService _database;
        private readonly ILangChainKernelFactory _kernelFactory;
        private readonly StoriesService? _storiesService;
        private readonly ICustomLogger? _logger;
        private readonly ICommandDispatcher? _commandDispatcher;

        public FxExpertCommand(
            long storyId,
            DatabaseService database,
            ILangChainKernelFactory kernelFactory,
            StoriesService? storiesService = null,
            ICustomLogger? logger = null,
            ICommandDispatcher? commandDispatcher = null,
            CommandTuningOptions? tuning = null)
        {
            _storyId = storyId;
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
            _storiesService = storiesService;
            _logger = logger;
            _commandDispatcher = commandDispatcher;
            _tuning = tuning ?? new CommandTuningOptions();
        }

        public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default, string? runId = null)
        {
            var effectiveRunId = string.IsNullOrWhiteSpace(runId)
                ? $"fx_expert_{_storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}"
                : runId;

            _logger?.Start(effectiveRunId);
            _logger?.Append(effectiveRunId, $"[story {_storyId}] Starting fx_expert pipeline");

            try
            {
                var story = _database.GetStoryById(_storyId);
                if (story == null)
                {
                    return Fail(effectiveRunId, $"Story {_storyId} not found");
                }

                var sourceText = story.StoryTagged;
                if (string.IsNullOrWhiteSpace(sourceText))
                {
                    return Fail(effectiveRunId, $"Story {_storyId} has no story_tagged text");
                }

                var fxAgent = _database.ListAgents()
                    .FirstOrDefault(a => a.IsActive && string.Equals(a.Role, "fx_expert", StringComparison.OrdinalIgnoreCase));

                if (fxAgent == null)
                {
                    return Fail(effectiveRunId, "No active fx_expert agent found");
                }

                if (!fxAgent.ModelId.HasValue)
                {
                    return Fail(effectiveRunId, $"FX expert agent {fxAgent.Name} has no model configured");
                }

                var modelInfo = _database.GetModelInfoById(fxAgent.ModelId.Value);
                if (string.IsNullOrWhiteSpace(modelInfo?.Name))
                {
                    return Fail(effectiveRunId, $"Model not found for fx expert agent {fxAgent.Name}");
                }

                var systemPrompt = BuildSystemPrompt(fxAgent);
                var chunks = SplitStoryIntoChunks(sourceText, fxAgent.NumPredict);
                if (chunks.Count == 0)
                {
                    return Fail(effectiveRunId, "No chunks produced from story_tagged");
                }

                _logger?.Append(effectiveRunId, $"[story {_storyId}] Split into {chunks.Count} chunks");

                var bridge = _kernelFactory.CreateChatBridge(
                    modelInfo.Name,
                    fxAgent.Temperature,
                    fxAgent.TopP,
                    fxAgent.RepeatPenalty,
                    fxAgent.TopK,
                    fxAgent.RepeatLastN,
                    fxAgent.NumPredict);

                var taggedChunks = new List<string>();
                for (int i = 0; i < chunks.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = chunks[i];

                    try
                    {
                        _commandDispatcher?.UpdateStep(effectiveRunId, i + 1, chunks.Count, $"Adding FX tags chunk {i + 1}/{chunks.Count}");
                    }
                    catch
                    {
                        // best-effort
                    }

                    var tagged = await ProcessChunkWithRetriesAsync(
                        bridge,
                        systemPrompt,
                        chunk.Text,
                        i + 1,
                        chunks.Count,
                        effectiveRunId,
                        ct);

                    if (string.IsNullOrWhiteSpace(tagged))
                    {
                        return Fail(effectiveRunId, $"FX expert returned empty text for chunk {i + 1}/{chunks.Count}");
                    }

                    taggedChunks.Add(tagged);
                }

                var mergedTagged = MergeTaggedChunks(taggedChunks);
                if (string.IsNullOrWhiteSpace(mergedTagged))
                {
                    return Fail(effectiveRunId, "Merged tagged story is empty");
                }

                var nextVersion = story.StoryTaggedVersion.HasValue
                    ? story.StoryTaggedVersion.Value + 1
                    : 1;

                var saved = _database.UpdateStoryTagged(
                    story.Id,
                    mergedTagged,
                    fxAgent.ModelId,
                    null, // promptHash not critical for expert agents
                    nextVersion);

                if (!saved)
                {
                    return Fail(effectiveRunId, $"Failed to persist tagged story for {_storyId}");
                }

                _logger?.Append(effectiveRunId, $"[story {_storyId}] FX tags added (version {nextVersion})");

                // Enqueue next stage: music_expert
                TryEnqueueMusicExpert(story, effectiveRunId);

                _logger?.MarkCompleted(effectiveRunId, "ok");
                return new CommandResult(true, "FX tags added (music_expert enqueued)");
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

        private void TryEnqueueMusicExpert(StoryRecord story, string runId)
        {
            try
            {
                if (_commandDispatcher == null || _storiesService == null || _kernelFactory == null)
                {
                    _logger?.Append(runId, $"[story {_storyId}] music_expert enqueue skipped: missing dependencies", "warn");
                    return;
                }

                var musicRunId = $"music_expert_{_storyId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";

                _commandDispatcher.Enqueue(
                    "music_expert",
                    async ctx =>
                    {
                        try
                        {
                            var cmd = new MusicExpertCommand(_storyId, _database, _kernelFactory, _storiesService, _logger, _commandDispatcher, _tuning);
                            return await cmd.ExecuteAsync(ctx.CancellationToken, musicRunId);
                        }
                        catch (Exception ex)
                        {
                            return new CommandResult(false, ex.Message);
                        }
                    },
                    runId: musicRunId,
                    threadScope: "story/music_expert",
                    metadata: new Dictionary<string, string>
                    {
                        ["storyId"] = _storyId.ToString(),
                        ["operation"] = "music_expert",
                        ["trigger"] = "fx_expert_completed"
                    },
                    priority: 2);

                _logger?.Append(runId, $"[story {_storyId}] Enqueued music_expert (runId={musicRunId})", "info");
            }
            catch (Exception ex)
            {
                _logger?.Append(runId, $"[story {_storyId}] Failed to enqueue music_expert: {ex.Message}", "warn");
            }
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

            var maxAttempts = Math.Max(1, _tuning.FxExpert.MaxAttemptsPerChunk);
            var minFxTags = Math.Max(0, _tuning.FxExpert.MinFxTagsPerChunk);
            var retryDelayBaseSeconds = Math.Max(0, _tuning.FxExpert.RetryDelayBaseSeconds);
            var diagnoseOnFinalFailure = _tuning.FxExpert.DiagnoseOnFinalFailure;
            var finalRetryAfterDiagnosis = _tuning.FxExpert.FinalRetryAfterDiagnosis;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] FX tagging attempt {attempt}/{maxAttempts}");

                try
                {
                    var messages = new List<ConversationMessage>();
                    if (!string.IsNullOrWhiteSpace(systemPrompt))
                    {
                        messages.Add(new ConversationMessage { Role = "system", Content = systemPrompt });
                    }

                    // Add error feedback for retry attempts
                    if (attempt > 1 && !string.IsNullOrWhiteSpace(lastError))
                    {
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
                        lastError = "Il testo ritornato è vuoto.";
                        continue;
                    }

                    // Validation: Check for minimum tag count (at least 1 FX tag)
                    var tagCount = FxTagRegex.Matches(cleaned).Count;
                    if (tagCount < minFxTags)
                    {
                        _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Not enough FX tags: {tagCount} found, minimum {minFxTags} required", "warn");
                        lastError = $"Hai inserito {tagCount} tag [FX:]. Devi inserire ALMENO {minFxTags} effetto sonoro [FX:] (se non trovi eventi evidenti, ricava un effetto dai rumori/contesto).";
                        continue;
                    }

                    // Merge FX blocks back into the ORIGINAL chunk, using surrounding tag anchors.
                    var merged = MergeFxIntoOriginalChunk(chunkText, cleaned, out var insertedFxBlocks);
                    if (insertedFxBlocks < minFxTags)
                    {
                        _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Could not merge FX blocks into original chunk (inserted={insertedFxBlocks}, modelTags={tagCount})", "warn");
                        lastError = "Ho trovato tag [FX:] nella tua risposta ma non riesco a reinserirli nel chunk originale: assicurati che ogni [FX:] sia posizionato tra due tag di sezione (es. [NARRATORE], [PERSONAGGIO: ...]) e che i tag vicini siano riportati in modo chiaro.";
                        continue;
                    }

                    // All validations passed
                    _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Validated+merged: modelLen={cleaned.Length} chars, modelFX={tagCount}, insertedFX={insertedFxBlocks}");
                    return merged;
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
                        _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] fx_expert self-diagnosis: {diagnosis}", "warn");

                        if (finalRetryAfterDiagnosis)
                        {
                            var finalAttempt = await FinalRetryWithDiagnosisAsync(
                                bridge,
                                systemPrompt,
                                chunkText,
                                diagnosis,
                                lastError,
                                chunkIndex,
                                chunkCount,
                                runId,
                                ct).ConfigureAwait(false);

                            if (!string.IsNullOrWhiteSpace(finalAttempt))
                            {
                                _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Final retry after diagnosis succeeded.", "info");
                                return finalAttempt;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Failed to collect fx_expert self-diagnosis: {ex.Message}", "warn");
                }
            }

            throw new InvalidOperationException($"Failed to process chunk {chunkIndex}/{chunkCount} after {maxAttempts} attempts. Last error: {lastError}");
        }

        private async Task<string?> FinalRetryWithDiagnosisAsync(
            LangChainChatBridge bridge,
            string? originalSystemPrompt,
            string chunkText,
            string diagnosis,
            string? lastFailureReason,
            int chunkIndex,
            int chunkCount,
            string runId,
            CancellationToken ct)
        {
            // One extra attempt, seeded with the model's own diagnosis.
            // The goal is not more retries, but one guided correction pass.

            var minFxTags = Math.Max(0, _tuning.FxExpert.MinFxTagsPerChunk);

            var messages = new List<ConversationMessage>();
            if (!string.IsNullOrWhiteSpace(originalSystemPrompt))
            {
                messages.Add(new ConversationMessage { Role = "system", Content = originalSystemPrompt });
            }

            var userPrompt =
                "Hai appena fornito una diagnosi del perché sei fallito. Ora fai UN SOLO ultimo tentativo.\n" +
                "- Non riscrivere il testo: aggiungi SOLO i tag/blocchi [FX:] nel posto giusto (senza spezzare i tag esistenti).\n" +
                $"- Devi inserire ALMENO {minFxTags} tag [FX:].\n" +
                "- Output: SOLO il testo con tag [FX:], niente spiegazioni.\n\n" +
                "=== TUA DIAGNOSI (da mantenere come contesto) ===\n" +
                ClipForPrompt(diagnosis, 2000) +
                "\n\n=== MOTIVO FALLIMENTO PRECEDENTE (validazione) ===\n" +
                (lastFailureReason ?? "(unknown)") +
                "\n\n=== INPUT (chunk) ===\n" +
                chunkText;

            messages.Add(new ConversationMessage { Role = "user", Content = userPrompt });

            var responseJson = await bridge.CallModelWithToolsAsync(messages, new List<Dictionary<string, object>>(), ct).ConfigureAwait(false);
            var (textContent, _) = LangChainChatBridge.ParseChatResponse(responseJson);
            var cleaned = textContent?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(cleaned))
            {
                _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Final retry returned empty output.", "warn");
                return null;
            }

            var tagCount = FxTagRegex.Matches(cleaned).Count;
            if (tagCount < minFxTags)
            {
                _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Final retry still not enough FX tags: {tagCount} found, minimum {minFxTags} required", "warn");
                return null;
            }

            var merged = MergeFxIntoOriginalChunk(chunkText, cleaned, out var insertedFxBlocks);
            if (insertedFxBlocks < minFxTags)
            {
                _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Final retry could not merge FX blocks into original chunk (inserted={insertedFxBlocks}, modelTags={tagCount})", "warn");
                return null;
            }

            return merged;
        }

        private sealed record FxInsertion(string FxBlock, string? PrevTagLine, string? NextTagLine);

        private static string MergeFxIntoOriginalChunk(string originalChunk, string modelOutput, out int insertedFxBlocks)
        {
            insertedFxBlocks = 0;
            if (string.IsNullOrWhiteSpace(originalChunk) || string.IsNullOrWhiteSpace(modelOutput))
            {
                return originalChunk;
            }

            var original = NormalizeNewlines(originalChunk);
            var output = NormalizeNewlines(modelOutput);

            var insertions = ExtractFxInsertions(output);
            if (insertions.Count == 0)
            {
                return original;
            }

            var current = original;
            var searchStart = 0;

            foreach (var ins in insertions)
            {
                var fxBlock = NormalizeNewlines(ins.FxBlock).Trim();
                if (string.IsNullOrWhiteSpace(fxBlock) || !FxBlockStartRegex.IsMatch(fxBlock))
                {
                    continue;
                }

                // Avoid duplicating an identical FX block.
                if (current.Contains(fxBlock, StringComparison.Ordinal))
                {
                    continue;
                }

                int insertAt = -1;
                if (!string.IsNullOrWhiteSpace(ins.NextTagLine))
                {
                    insertAt = FindTagLineStartIndex(current, NormalizeTagLine(ins.NextTagLine), searchStart);
                }

                if (insertAt < 0 && !string.IsNullOrWhiteSpace(ins.PrevTagLine))
                {
                    // Fallback: find the next tag AFTER the prev tag in the original chunk.
                    var prevPos = FindTagLineStartIndex(current, NormalizeTagLine(ins.PrevTagLine), searchStart);
                    if (prevPos >= 0)
                    {
                        var afterPrev = FindNextNonFxTagLineStartIndex(current, prevPos + 1);
                        insertAt = afterPrev >= 0 ? afterPrev : current.Length;
                    }
                }

                if (insertAt < 0)
                {
                    // Last fallback: append.
                    insertAt = current.Length;
                }

                var (safeInsertAt, reopenNarrator) = AdjustInsertionIndexForBlockSafety(current, insertAt);
                insertAt = safeInsertAt;

                var prefix = string.Empty;
                if (insertAt > 0 && current[insertAt - 1] != '\n')
                {
                    prefix = "\n";
                }

                var suffix = string.Empty;
                if (!fxBlock.EndsWith("\n", StringComparison.Ordinal))
                {
                    suffix = "\n";
                }

                // Ensure a blank line separation before the next tag where possible.
                var extraGap = string.Empty;
                if (!reopenNarrator && insertAt < current.Length && (insertAt == 0 || current[insertAt] == '[' || current[insertAt] == '\n'))
                {
                    extraGap = "\n";
                }

                var narratorReopen = reopenNarrator ? "[NARRATORE]\n" : string.Empty;

                current = current.Insert(insertAt, prefix + fxBlock + suffix + narratorReopen + extraGap);
                insertedFxBlocks++;

                // Move searchStart forward to avoid matching earlier repeated tags.
                searchStart = Math.Min(current.Length, insertAt + prefix.Length + fxBlock.Length + suffix.Length + extraGap.Length);
            }

            return current;
        }

        private static (int InsertAt, bool ReopenNarrator) AdjustInsertionIndexForBlockSafety(string current, int insertAt)
        {
            if (string.IsNullOrEmpty(current)) return (0, false);

            var idx = Math.Clamp(insertAt, 0, current.Length);

            // If idx is inside a line, snap to the start of that line.
            if (idx > 0)
            {
                var prevNl = current.LastIndexOf('\n', Math.Min(idx - 1, current.Length - 1));
                var lineStart = prevNl >= 0 ? prevNl + 1 : 0;
                if (idx != lineStart)
                {
                    idx = lineStart;
                }
            }

            // If we are inserting at a tag line boundary, it is safe.
            if (idx < current.Length)
            {
                var lineEnd = current.IndexOf('\n', idx);
                if (lineEnd < 0) lineEnd = current.Length;
                var line = current.Substring(idx, lineEnd - idx);
                if (IsNonFxTagLine(line))
                {
                    return (idx, false);
                }
            }

            // Otherwise, we are inserting before a content line.
            // Do not split an existing block: move insertion BEFORE the block start tag,
            // except for narrator blocks where we allow splitting across newline and reopen narrator.
            var prevTagStart = FindPreviousNonFxTagLineStartIndex(current, Math.Max(0, idx - 1));
            if (prevTagStart < 0)
            {
                return (idx, false);
            }

            var prevLineEnd = current.IndexOf('\n', prevTagStart);
            if (prevLineEnd < 0) prevLineEnd = current.Length;
            var prevTagLine = current.Substring(prevTagStart, prevLineEnd - prevTagStart);

            if (IsNarratorTagLine(prevTagLine))
            {
                // Insert at idx (start of a narrator content line) and reopen narrator after inserted block.
                return (idx, true);
            }

            // Insert before the block tag line to avoid splitting character/dialogue blocks.
            return (prevTagStart, false);
        }

        private static bool IsNarratorTagLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            var t = line.TrimStart();
            return t.StartsWith("[NARRATORE", StringComparison.OrdinalIgnoreCase);
        }

        private static int FindPreviousNonFxTagLineStartIndex(string text, int fromIndex)
        {
            if (string.IsNullOrWhiteSpace(text)) return -1;

            var s = NormalizeNewlines(text);
            var idx = Math.Clamp(fromIndex, 0, Math.Max(0, s.Length - 1));

            // Walk lines backwards.
            var lineStart = s.LastIndexOf('\n', idx);
            if (lineStart < 0) lineStart = 0;
            else lineStart = lineStart + 1;

            while (lineStart >= 0)
            {
                var lineEnd = s.IndexOf('\n', lineStart);
                if (lineEnd < 0) lineEnd = s.Length;
                var line = s.Substring(lineStart, lineEnd - lineStart);

                if (IsNonFxTagLine(line))
                {
                    return lineStart;
                }

                if (lineStart == 0) break;
                var prevNl = s.LastIndexOf('\n', Math.Max(0, lineStart - 2));
                if (prevNl < 0)
                {
                    lineStart = 0;
                }
                else
                {
                    lineStart = prevNl + 1;
                }
            }

            return -1;
        }

        private static List<FxInsertion> ExtractFxInsertions(string modelOutput)
        {
            var result = new List<FxInsertion>();
            if (string.IsNullOrWhiteSpace(modelOutput)) return result;

            var lines = NormalizeNewlines(modelOutput).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i] ?? string.Empty;
                var fxMatch = FxTagRegex.Match(line);
                if (!fxMatch.Success)
                {
                    continue;
                }

                // Start the FX block at the first [FX:...] occurrence (even if the model placed it mid-line).
                var fxFirstLine = line.Substring(fxMatch.Index).TrimStart();

                var start = i;
                var end = i;
                for (var j = i + 1; j < lines.Length; j++)
                {
                    var next = lines[j] ?? string.Empty;
                    if (IsNonFxTagLine(next))
                    {
                        break;
                    }
                    end = j;
                }

                // Build FX block.
                var blockLines = lines.Skip(start).Take(end - start + 1).ToArray();
                blockLines[0] = fxFirstLine;
                var fxBlock = string.Join("\n", blockLines).Trim();

                // Find previous/next non-FX tag lines around the FX block.
                string? prevTag = null;
                for (var p = start - 1; p >= 0; p--)
                {
                    if (IsNonFxTagLine(lines[p] ?? string.Empty))
                    {
                        prevTag = lines[p].TrimEnd();
                        break;
                    }
                }

                string? nextTag = null;
                for (var n = end + 1; n < lines.Length; n++)
                {
                    if (IsNonFxTagLine(lines[n] ?? string.Empty))
                    {
                        nextTag = lines[n].TrimEnd();
                        break;
                    }
                }

                result.Add(new FxInsertion(fxBlock, prevTag, nextTag));
                i = end;
            }

            return result;
        }

        private static bool IsNonFxTagLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            var t = line.TrimStart();
            if (!t.StartsWith("[", StringComparison.Ordinal)) return false;
            if (!t.Contains(']')) return false;
            return !FxTagLineRegex.IsMatch(t);
        }

        private static string NormalizeTagLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return string.Empty;
            return WhitespaceRegex.Replace(line.Trim(), " ");
        }

        private static string NormalizeNewlines(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private static int FindTagLineStartIndex(string text, string normalizedTagLine, int startIndex)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(normalizedTagLine)) return -1;
            var s = NormalizeNewlines(text);

            var idx = Math.Max(0, startIndex);
            // Advance to the next line start.
            if (idx > 0)
            {
                var prevNl = s.LastIndexOf('\n', Math.Min(idx, s.Length - 1));
                if (prevNl >= 0) idx = prevNl + 1;
            }

            while (idx <= s.Length)
            {
                var lineEnd = s.IndexOf('\n', idx);
                if (lineEnd < 0) lineEnd = s.Length;
                var line = s.Substring(idx, lineEnd - idx);

                if (IsNonFxTagLine(line) && string.Equals(NormalizeTagLine(line), normalizedTagLine, StringComparison.Ordinal))
                {
                    return idx;
                }

                if (lineEnd >= s.Length) break;
                idx = lineEnd + 1;
            }

            return -1;
        }

        private static int FindNextNonFxTagLineStartIndex(string text, int startIndex)
        {
            if (string.IsNullOrWhiteSpace(text)) return -1;
            var s = NormalizeNewlines(text);
            var idx = Math.Max(0, startIndex);

            // Move to line start
            if (idx > 0)
            {
                var prevNl = s.LastIndexOf('\n', Math.Min(idx, s.Length - 1));
                if (prevNl >= 0) idx = prevNl + 1;
            }

            while (idx <= s.Length)
            {
                var lineEnd = s.IndexOf('\n', idx);
                if (lineEnd < 0) lineEnd = s.Length;
                var line = s.Substring(idx, lineEnd - idx);

                if (IsNonFxTagLine(line))
                {
                    return idx;
                }

                if (lineEnd >= s.Length) break;
                idx = lineEnd + 1;
            }

            return -1;
        }

        private static string ClipForPrompt(string? text, int maxChars)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (maxChars <= 0) return string.Empty;
            if (text.Length <= maxChars) return text;

            // Keep head+tail for context.
            var half = Math.Max(200, maxChars / 2);
            var head = text.Substring(0, half);
            var tail = text.Substring(text.Length - half, half);
            return head + "\n... (TRUNCATED) ...\n" + tail;
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
            // Ask the SAME model/agent (through the same bridge) to explain what was unclear.
            // The bridge will persist request/response JSON into the DB logs (ModelRequest/ModelResponse).

            var auditSystem =
                "Sei un assistente che deve fare debug del proprio comportamento. " +
                "Ti verranno fornite: le istruzioni originali (system prompt), l'input (chunk), la tua ultima risposta e il motivo del fallimento (validazione). " +
                "Spiega in italiano: (1) perché hai fallito, (2) cosa non era chiaro nelle istruzioni, (3) cosa cambieresti per rispettarle. " +
                "NON riscrivere il testo della storia e NON aggiungere nuovi tag FX: rispondi solo con la spiegazione.";

            var sb = new StringBuilder();
            sb.AppendLine($"DIAGNOSI fx_expert - chunk {chunkIndex}/{chunkCount}");
            sb.AppendLine();
            sb.AppendLine("=== ISTRUZIONI ORIGINALI (system prompt) ===");
            sb.AppendLine(ClipForPrompt(originalSystemPrompt, 2000));
            sb.AppendLine();
            sb.AppendLine("=== INPUT (chunk) ===");
            sb.AppendLine(ClipForPrompt(chunkText, 2500));
            sb.AppendLine();
            sb.AppendLine("=== TUA ULTIMA RISPOSTA (assistant) ===");
            sb.AppendLine(ClipForPrompt(lastAssistantText, 2500));
            sb.AppendLine();
            sb.AppendLine("=== MOTIVO FALLIMENTO (validazione) ===");
            sb.AppendLine(lastFailureReason ?? "(unknown)");

            // Optional: include the last request roles (high-level) for clarity.
            if (lastRequestMessages != null && lastRequestMessages.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("=== ULTIMA CONVERSAZIONE (ruoli) ===");
                foreach (var m in lastRequestMessages)
                {
                    sb.AppendLine($"- {m.Role}: {ClipForPrompt(m.Content, 300)}");
                }
            }

            var diagMessages = new List<ConversationMessage>
            {
                new ConversationMessage { Role = "system", Content = auditSystem },
                new ConversationMessage { Role = "user", Content = sb.ToString() }
            };

            var responseJson = await bridge.CallModelWithToolsAsync(diagMessages, new List<Dictionary<string, object>>(), ct).ConfigureAwait(false);
            var (textContent, _) = LangChainChatBridge.ParseChatResponse(responseJson);
            return string.IsNullOrWhiteSpace(textContent) ? null : textContent.Trim();
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

        private List<(string Text, int StartIndex, int EndIndex)> SplitStoryIntoChunks(string text)
        {
            return SplitStoryIntoChunks(text, null);
        }

        private List<(string Text, int StartIndex, int EndIndex)> SplitStoryIntoChunks(string text, int? numPredict)
        {
            var chunks = new List<(string Text, int StartIndex, int EndIndex)>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return chunks;
            }

            var defaultMaxTokens = Math.Max(1, _tuning.FxExpert.DefaultMaxTokensPerChunk);
            var defaultTargetTokens = Math.Max(1, _tuning.FxExpert.DefaultTargetTokensPerChunk);

            // Chunking by approximate tokens (4 chars ≈ 1 token). We keep chunks small enough so the model can
            // reproduce the full text plus inserted tags within its output cap (NumPredict).
            var approxCharsPerToken = 4;
            var maxTokens = defaultMaxTokens;
            var targetTokens = defaultTargetTokens;
            if (numPredict.HasValue && numPredict.Value > 0)
            {
                // Reserve headroom for tags/system overhead; output must include essentially all input text.
                var safeTarget = (int)Math.Floor(numPredict.Value * 0.75);
                targetTokens = Math.Clamp(safeTarget, 250, defaultMaxTokens);
                maxTokens = Math.Clamp((int)Math.Floor(numPredict.Value * 0.85), 300, defaultMaxTokens);
            }

            int pos = 0;
            while (pos < text.Length)
            {
                int chunkSize = Math.Min(maxTokens * approxCharsPerToken, text.Length - pos);
                var chunkEnd = pos + chunkSize;

                // Try to break on paragraph boundary
                if (chunkEnd < text.Length)
                {
                    var searchWindow = Math.Min(chunkSize / 2, chunkEnd - pos);
                    var lastNewline = text.LastIndexOf("\n\n", chunkEnd, searchWindow);
                    if (lastNewline > pos)
                    {
                        chunkEnd = lastNewline + 2;
                    }
                }

                // If we failed to find a paragraph boundary and the chunk is very large, try a single newline split.
                if (chunkEnd < text.Length && chunkEnd - pos > targetTokens * approxCharsPerToken)
                {
                    var searchWindow = Math.Min(chunkSize / 2, chunkEnd - pos);
                    var lastNl = text.LastIndexOf("\n", chunkEnd, searchWindow);
                    if (lastNl > pos)
                    {
                        chunkEnd = lastNl + 1;
                    }
                }

                var chunkText = text.Substring(pos, chunkEnd - pos);
                chunks.Add((chunkText, pos, chunkEnd));

                pos = chunkEnd;
            }

            return chunks;
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
