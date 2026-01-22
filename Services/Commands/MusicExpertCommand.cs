using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands
{
    /// <summary>
    /// Reads story_tagged, splits into chunks, adds music tags via music_expert agent,
    /// updates story_tagged, then enqueues TTS schema generation (the final step).
    /// </summary>
    public sealed class MusicExpertCommand
    {
        private readonly CommandTuningOptions _tuning;

        private static readonly Regex MusicTagRegex = new(@"\[\s*(?:MUSICA|MUSIC)\s*:[^\]]*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MusicTagLineRegex = new(@"(?im)^\s*\[\s*(?:MUSICA|MUSIC)\s*:[^\]]*\]", RegexOptions.Compiled);
        private static readonly Regex MusicBlockStartRegex = new(@"(?is)^\s*\[\s*(?:MUSICA|MUSIC)\s*:[^\]]*\]", RegexOptions.Compiled);
        private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

        private readonly long _storyId;
        private readonly DatabaseService _database;
        private readonly ILangChainKernelFactory _kernelFactory;
        private readonly StoriesService? _storiesService;
        private readonly ICustomLogger? _logger;
        private readonly ICommandDispatcher? _commandDispatcher;

        public MusicExpertCommand(
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
                ? $"music_expert_{_storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}"
                : runId;

            _logger?.Start(effectiveRunId);
            _logger?.Append(effectiveRunId, $"[story {_storyId}] Starting music_expert pipeline");

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

                var musicAgent = _database.ListAgents()
                    .FirstOrDefault(a => a.IsActive && string.Equals(a.Role, "music_expert", StringComparison.OrdinalIgnoreCase));

                if (musicAgent == null)
                {
                    return Fail(effectiveRunId, "No active music_expert agent found");
                }

                if (!musicAgent.ModelId.HasValue)
                {
                    return Fail(effectiveRunId, $"Music expert agent {musicAgent.Name} has no model configured");
                }

                var modelInfo = _database.GetModelInfoById(musicAgent.ModelId.Value);
                if (string.IsNullOrWhiteSpace(modelInfo?.Name))
                {
                    return Fail(effectiveRunId, $"Model not found for music expert agent {musicAgent.Name}");
                }

                var systemPrompt = BuildSystemPrompt(musicAgent);
                var chunks = SplitStoryIntoChunks(sourceText);
                if (chunks.Count == 0)
                {
                    return Fail(effectiveRunId, "No chunks produced from story_tagged");
                }

                _logger?.Append(effectiveRunId, $"[story {_storyId}] Split into {chunks.Count} chunks");

                var bridge = _kernelFactory.CreateChatBridge(
                    modelInfo.Name,
                    musicAgent.Temperature,
                    musicAgent.TopP,
                    musicAgent.RepeatPenalty,
                    musicAgent.TopK,
                    musicAgent.RepeatLastN,
                    musicAgent.NumPredict);

                var taggedChunks = new List<string>();
                for (int i = 0; i < chunks.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = chunks[i];

                    try
                    {
                        _commandDispatcher?.UpdateStep(effectiveRunId, i + 1, chunks.Count, $"Adding music tags chunk {i + 1}/{chunks.Count}");
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
                        return Fail(effectiveRunId, $"Music expert returned empty text for chunk {i + 1}/{chunks.Count}");
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
                    musicAgent.ModelId,
                    null, // promptHash not critical for expert agents
                    nextVersion);

                if (!saved)
                {
                    return Fail(effectiveRunId, $"Failed to persist tagged story for {_storyId}");
                }

                _logger?.Append(effectiveRunId, $"[story {_storyId}] Music tags added (version {nextVersion})");

                // Final step: enqueue TTS schema generation (was previously triggered by formatter)
                TryEnqueueTtsSchemaGeneration(story, effectiveRunId);

                _logger?.MarkCompleted(effectiveRunId, "ok");
                return new CommandResult(true, "Music tags added (TTS schema enqueued)");
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

        private void TryEnqueueTtsSchemaGeneration(StoryRecord story, string runId)
        {
            try
            {
                if (_commandDispatcher == null)
                {
                    _logger?.Append(runId, $"[story {_storyId}] TTS schema enqueue skipped: dispatcher not available", "warn");
                    return;
                }

                if (_storiesService == null)
                {
                    _logger?.Append(runId, $"[story {_storyId}] TTS schema enqueue skipped: StoriesService not available", "warn");
                    return;
                }

                // Check if already queued/running
                try
                {
                    var alreadyQueued = _commandDispatcher.GetActiveCommands().Any(s =>
                        s.Metadata != null &&
                        s.Metadata.TryGetValue("storyId", out var sid) &&
                        string.Equals(sid, _storyId.ToString(), StringComparison.OrdinalIgnoreCase) &&
                        (
                            string.Equals(s.OperationName, "generate_tts_schema", StringComparison.OrdinalIgnoreCase) ||
                            (s.Metadata.TryGetValue("operation", out var op) && op.Contains("tts_schema", StringComparison.OrdinalIgnoreCase)) ||
                            s.RunId.StartsWith($"tts_schema_{_storyId}_", StringComparison.OrdinalIgnoreCase)
                        ));

                    if (alreadyQueued)
                    {
                        _logger?.Append(runId, $"[story {_storyId}] TTS schema not enqueued: already queued/running", "info");
                        return;
                    }
                }
                catch
                {
                    // If snapshots fail, still try to enqueue
                }

                var ttsRunId = $"tts_schema_{_storyId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";

                _commandDispatcher.Enqueue(
                    "generate_tts_schema",
                    async ctx =>
                    {
                        try
                        {
                            var cmd = new GenerateTtsSchemaCommand(_storiesService, _storyId);
                            return await cmd.ExecuteAsync(ctx.CancellationToken);
                        }
                        catch (Exception ex)
                        {
                            return new CommandResult(false, ex.Message);
                        }
                    },
                    runId: ttsRunId,
                    threadScope: "story/tts_schema",
                    metadata: new Dictionary<string, string>
                    {
                        ["storyId"] = _storyId.ToString(),
                        ["operation"] = "generate_tts_schema",
                        ["trigger"] = "music_expert_completed",
                        ["taggedVersion"] = (story.StoryTaggedVersion ?? 0).ToString()
                    },
                    priority: 2);

                _logger?.Append(runId, $"[story {_storyId}] Enqueued TTS schema generation (runId={ttsRunId})", "info");
            }
            catch (Exception ex)
            {
                _logger?.Append(runId, $"[story {_storyId}] Failed to enqueue TTS schema: {ex.Message}", "warn");
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
            var maxAttempts = Math.Max(1, _tuning.MusicExpert.MaxAttemptsPerChunk);
            var retryDelayBaseSeconds = Math.Max(0, _tuning.MusicExpert.RetryDelayBaseSeconds);
            var requiredTags = ComputeRequiredMusicTagsForChunk(chunkText);

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Music tagging attempt {attempt}/{maxAttempts} (minTags={requiredTags})");

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

                    var responseJson = await bridge.CallModelWithToolsAsync(messages, new List<Dictionary<string, object>>(), ct);
                    var (textContent, _) = LangChainChatBridge.ParseChatResponse(responseJson);
                    var cleaned = textContent?.Trim() ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(cleaned))
                    {
                        _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Empty response on attempt {attempt}", "warn");
                        lastError = "Il testo ritornato è vuoto.";
                        continue;
                    }

                    // Validation: Check for minimum tag count
                    var tagCount = MusicTagRegex.Matches(cleaned).Count;
                    if (tagCount < requiredTags)
                    {
                        _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Not enough music tags: {tagCount} found, minimum {requiredTags} required", "warn");
                        lastError = $"Hai inserito solo {tagCount} tag [MUSICA:/MUSIC:]. Devi inserire ALMENO {requiredTags} indicazioni musicali per arricchire l'esperienza sonora della narrazione.";
                        continue;
                    }

                    // Merge music blocks back into the ORIGINAL chunk, using surrounding tag anchors.
                    var merged = MergeMusicIntoOriginalChunk(chunkText, cleaned, out var insertedMusicBlocks);
                    if (insertedMusicBlocks < requiredTags)
                    {
                        _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Could not merge music blocks into original chunk (inserted={insertedMusicBlocks}, modelTags={tagCount})", "warn");
                        lastError = "Ho trovato tag musicali nella tua risposta ma non riesco a reinserirli nel chunk originale: assicurati che ogni tag [MUSICA:]/[MUSIC:] sia posizionato tra due tag di sezione (es. [NARRATORE], [PERSONAGGIO: ...]) e che i tag vicini siano riportati in modo chiaro.";
                        continue;
                    }

                    // All validations passed
                    _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Validated+merged: modelLen={cleaned.Length} chars, modelMusic={tagCount}, insertedMusic={insertedMusicBlocks}");
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

            throw new InvalidOperationException($"Failed to process chunk {chunkIndex}/{chunkCount} after {maxAttempts} attempts. Last error: {lastError}");
        }

        private int ComputeRequiredMusicTagsForChunk(string chunkText)
        {
            var maxRequired = Math.Max(1, _tuning.MusicExpert.MaxMusicTagsPerChunkRequirement);
            var minRequired = Math.Max(0, _tuning.MusicExpert.MinMusicTagsPerChunkRequirement);

            if (string.IsNullOrWhiteSpace(chunkText)) return maxRequired;

            // Approx tokens (4 chars ≈ 1 token) like other chunkers.
            var approxTokens = Math.Max(1, chunkText.Length / 4);

            // Small chunks shouldn't be forced to produce 3 music cues.
            int required;
            if (approxTokens <= 450) required = 1;
            else if (approxTokens <= 900) required = 2;
            else required = 3;

            required = Math.Clamp(required, minRequired, maxRequired);
            return required;
        }

        private sealed record MusicInsertion(string MusicBlock, string? PrevTagLine, string? NextTagLine);

        private static string MergeMusicIntoOriginalChunk(string originalChunk, string modelOutput, out int insertedMusicBlocks)
        {
            insertedMusicBlocks = 0;
            if (string.IsNullOrWhiteSpace(originalChunk) || string.IsNullOrWhiteSpace(modelOutput))
            {
                return originalChunk;
            }

            var original = NormalizeNewlines(originalChunk);
            var output = NormalizeNewlines(modelOutput);

            var insertions = ExtractMusicInsertions(output);
            if (insertions.Count == 0)
            {
                return original;
            }

            var current = original;
            var searchStart = 0;

            foreach (var ins in insertions)
            {
                var musicBlock = NormalizeNewlines(ins.MusicBlock).Trim();
                if (string.IsNullOrWhiteSpace(musicBlock) || !MusicBlockStartRegex.IsMatch(musicBlock))
                {
                    continue;
                }

                // Avoid duplicating an identical block.
                if (current.Contains(musicBlock, StringComparison.Ordinal))
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
                    var prevPos = FindTagLineStartIndex(current, NormalizeTagLine(ins.PrevTagLine), searchStart);
                    if (prevPos >= 0)
                    {
                        var afterPrev = FindNextNonMusicTagLineStartIndex(current, prevPos + 1);
                        insertAt = afterPrev >= 0 ? afterPrev : current.Length;
                    }
                }

                if (insertAt < 0)
                {
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
                if (!musicBlock.EndsWith("\n", StringComparison.Ordinal))
                {
                    suffix = "\n";
                }

                var extraGap = string.Empty;
                if (!reopenNarrator && insertAt < current.Length && (insertAt == 0 || current[insertAt] == '[' || current[insertAt] == '\n'))
                {
                    extraGap = "\n";
                }

                var narratorReopen = reopenNarrator ? "[NARRATORE]\n" : string.Empty;

                current = current.Insert(insertAt, prefix + musicBlock + suffix + narratorReopen + extraGap);
                insertedMusicBlocks++;
                searchStart = Math.Min(current.Length, insertAt + prefix.Length + musicBlock.Length + suffix.Length + extraGap.Length);
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
                if (IsNonMusicTagLine(line))
                {
                    return (idx, false);
                }
            }

            // Otherwise, we are inserting before a content line.
            // Do not split an existing block: move insertion BEFORE the block start tag,
            // except for narrator blocks where we allow splitting across newline and reopen narrator.
            var prevTagStart = FindPreviousNonMusicTagLineStartIndex(current, Math.Max(0, idx - 1));
            if (prevTagStart < 0)
            {
                return (idx, false);
            }

            var prevLineEnd = current.IndexOf('\n', prevTagStart);
            if (prevLineEnd < 0) prevLineEnd = current.Length;
            var prevTagLine = current.Substring(prevTagStart, prevLineEnd - prevTagStart);

            if (IsNarratorTagLine(prevTagLine))
            {
                return (idx, true);
            }

            return (prevTagStart, false);
        }

        private static bool IsNarratorTagLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            var t = line.TrimStart();
            return t.StartsWith("[NARRATORE", StringComparison.OrdinalIgnoreCase);
        }

        private static int FindPreviousNonMusicTagLineStartIndex(string text, int fromIndex)
        {
            if (string.IsNullOrWhiteSpace(text)) return -1;

            var s = NormalizeNewlines(text);
            var idx = Math.Clamp(fromIndex, 0, Math.Max(0, s.Length - 1));

            var lineStart = s.LastIndexOf('\n', idx);
            if (lineStart < 0) lineStart = 0;
            else lineStart = lineStart + 1;

            while (lineStart >= 0)
            {
                var lineEnd = s.IndexOf('\n', lineStart);
                if (lineEnd < 0) lineEnd = s.Length;
                var line = s.Substring(lineStart, lineEnd - lineStart);

                if (IsNonMusicTagLine(line))
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

        private static List<MusicInsertion> ExtractMusicInsertions(string modelOutput)
        {
            var result = new List<MusicInsertion>();
            if (string.IsNullOrWhiteSpace(modelOutput)) return result;

            var lines = NormalizeNewlines(modelOutput).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i] ?? string.Empty;
                var match = MusicTagRegex.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                // Start the music block at the first music tag occurrence (even if mid-line).
                var musicFirstLine = line.Substring(match.Index).TrimStart();

                var start = i;
                var end = i;
                for (var j = i + 1; j < lines.Length; j++)
                {
                    var next = lines[j] ?? string.Empty;
                    if (IsNonMusicTagLine(next))
                    {
                        break;
                    }
                    end = j;
                }

                var blockLines = lines.Skip(start).Take(end - start + 1).ToArray();
                blockLines[0] = musicFirstLine;
                var musicBlock = string.Join("\n", blockLines).Trim();

                string? prevTag = null;
                for (var p = start - 1; p >= 0; p--)
                {
                    if (IsNonMusicTagLine(lines[p] ?? string.Empty))
                    {
                        prevTag = (lines[p] ?? string.Empty).TrimEnd();
                        break;
                    }
                }

                string? nextTag = null;
                for (var n = end + 1; n < lines.Length; n++)
                {
                    if (IsNonMusicTagLine(lines[n] ?? string.Empty))
                    {
                        nextTag = (lines[n] ?? string.Empty).TrimEnd();
                        break;
                    }
                }

                result.Add(new MusicInsertion(musicBlock, prevTag, nextTag));
                i = end;
            }

            return result;
        }

        private static bool IsNonMusicTagLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            var t = line.TrimStart();
            if (!t.StartsWith("[", StringComparison.Ordinal)) return false;
            if (!t.Contains(']')) return false;
            return !MusicTagLineRegex.IsMatch(t);
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

                if (IsNonMusicTagLine(line) && string.Equals(NormalizeTagLine(line), normalizedTagLine, StringComparison.Ordinal))
                {
                    return idx;
                }

                if (lineEnd >= s.Length) break;
                idx = lineEnd + 1;
            }

            return -1;
        }

        private static int FindNextNonMusicTagLineStartIndex(string text, int startIndex)
        {
            if (string.IsNullOrWhiteSpace(text)) return -1;
            var s = NormalizeNewlines(text);
            var idx = Math.Max(0, startIndex);

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

                if (IsNonMusicTagLine(line))
                {
                    return idx;
                }

                if (lineEnd >= s.Length) break;
                idx = lineEnd + 1;
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

        private List<(string Text, int StartIndex, int EndIndex)> SplitStoryIntoChunks(string text)
        {
            var chunks = new List<(string Text, int StartIndex, int EndIndex)>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return chunks;
            }

            var maxTokensPerChunk = Math.Max(1, _tuning.MusicExpert.MaxTokensPerChunk);

            // Simple chunking by approximate tokens (4 chars ≈ 1 token)
            int pos = 0;
            while (pos < text.Length)
            {
                int chunkSize = Math.Min(maxTokensPerChunk * 4, text.Length - pos);
                var chunkEnd = pos + chunkSize;

                // Try to break on paragraph boundary
                if (chunkEnd < text.Length)
                {
                    var lastNewline = text.LastIndexOf("\n\n", chunkEnd, Math.Min(chunkSize / 2, chunkEnd - pos));
                    if (lastNewline > pos)
                    {
                        chunkEnd = lastNewline + 2;
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
