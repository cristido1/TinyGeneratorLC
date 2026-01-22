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

namespace TinyGenerator.Services.Commands
{
    /// <summary>
    /// Reads story_tagged, splits into chunks, adds ambient/noise tags via ambient_expert agent,
    /// updates story_tagged, then enqueues fx_expert command.
    /// </summary>
    public sealed class AmbientExpertCommand
    {
        private readonly CommandTuningOptions _tuning;

        private readonly long _storyId;
        private readonly DatabaseService _database;
        private readonly ILangChainKernelFactory _kernelFactory;
        private readonly StoriesService? _storiesService;
        private readonly ICustomLogger? _logger;
        private readonly ICommandDispatcher? _commandDispatcher;

        public AmbientExpertCommand(
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
                ? $"ambient_expert_{_storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}"
                : runId;

            _logger?.Start(effectiveRunId);
            _logger?.Append(effectiveRunId, $"[story {_storyId}] Starting ambient_expert pipeline");

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

                var modelInfo = _database.GetModelInfoById(ambientAgent.ModelId.Value);
                if (string.IsNullOrWhiteSpace(modelInfo?.Name))
                {
                    return Fail(effectiveRunId, $"Model not found for ambient expert agent {ambientAgent.Name}");
                }

                var systemPrompt = BuildSystemPrompt(ambientAgent);
                var chunks = SplitStoryIntoChunks(sourceText);
                if (chunks.Count == 0)
                {
                    return Fail(effectiveRunId, "No chunks produced from story_tagged");
                }

                _logger?.Append(effectiveRunId, $"[story {_storyId}] Split into {chunks.Count} chunks");

                var bridge = _kernelFactory.CreateChatBridge(
                    modelInfo.Name,
                    ambientAgent.Temperature,
                    ambientAgent.TopP,
                    ambientAgent.RepeatPenalty,
                    ambientAgent.TopK,
                    ambientAgent.RepeatLastN,
                    ambientAgent.NumPredict);

                var taggedChunks = new List<string>();
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
                        return Fail(effectiveRunId, $"Ambient expert returned empty text for chunk {i + 1}/{chunks.Count}");
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
                    ambientAgent.ModelId,
                    null, // promptHash not critical for expert agents
                    nextVersion);

                if (!saved)
                {
                    return Fail(effectiveRunId, $"Failed to persist tagged story for {_storyId}");
                }

                _logger?.Append(effectiveRunId, $"[story {_storyId}] Ambient tags added (version {nextVersion})");

                // Enqueue next stage: fx_expert
                TryEnqueueFxExpert(story, effectiveRunId);

                _logger?.MarkCompleted(effectiveRunId, "ok");
                return new CommandResult(true, "Ambient tags added (fx_expert enqueued)");
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

        private void TryEnqueueFxExpert(StoryRecord story, string runId)
        {
            try
            {
                if (_commandDispatcher == null || _storiesService == null || _kernelFactory == null)
                {
                    _logger?.Append(runId, $"[story {_storyId}] fx_expert enqueue skipped: missing dependencies", "warn");
                    return;
                }

                var fxRunId = $"fx_expert_{_storyId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";

                _commandDispatcher.Enqueue(
                    "fx_expert",
                    async ctx =>
                    {
                        try
                        {
                            var cmd = new FxExpertCommand(_storyId, _database, _kernelFactory, _storiesService, _logger, _commandDispatcher, _tuning);
                            return await cmd.ExecuteAsync(ctx.CancellationToken, fxRunId);
                        }
                        catch (Exception ex)
                        {
                            return new CommandResult(false, ex.Message);
                        }
                    },
                    runId: fxRunId,
                    threadScope: "story/fx_expert",
                    metadata: new Dictionary<string, string>
                    {
                        ["storyId"] = _storyId.ToString(),
                        ["operation"] = "fx_expert",
                        ["trigger"] = "ambient_expert_completed"
                    },
                    priority: 2);

                _logger?.Append(runId, $"[story {_storyId}] Enqueued fx_expert (runId={fxRunId})", "info");
            }
            catch (Exception ex)
            {
                _logger?.Append(runId, $"[story {_storyId}] Failed to enqueue fx_expert: {ex.Message}", "warn");
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

            var maxAttempts = Math.Max(1, _tuning.AmbientExpert.MaxAttemptsPerChunk);
            var minTagsRequired = Math.Max(0, _tuning.AmbientExpert.MinAmbientTagsPerChunkRequirement);
            var retryDelayBaseSeconds = Math.Max(0, _tuning.AmbientExpert.RetryDelayBaseSeconds);

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

                    // Canonicalize tag name: we only use [RUMORI] in the pipeline.
                    // Convert any model-produced [RUMORE] / [RUMORE: ...] to [RUMORI] / [RUMORI: ...].
                    cleaned = Regex.Replace(
                        cleaned,
                        @"\[(\s*)RUMORE\b",
                        "[$1RUMORI",
                        RegexOptions.IgnoreCase);

                    if (string.IsNullOrWhiteSpace(cleaned))
                    {
                        _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Empty response on attempt {attempt}", "warn");
                        lastError = "Il testo ritornato è vuoto.";
                        continue;
                    }

                    // Normal behavior: keep the ORIGINAL chunk verbatim and inject ONLY the new ambient tags.
                    // We do not trust (or require) the model to reprint the whole chunk.
                    var candidate = MergeAmbientTagsIntoOriginalChunk(chunkText, cleaned, out var insertedCount);
                    if (insertedCount == 0)
                    {
                        lastError = "Ho trovato una risposta ma non ho estratto nessun nuovo tag [RUMORI]/[AMBIENTE] valido da inserire. Rispondi inserendo chiaramente righe [RUMORI...] (anche [RUMORI: ...]) nel punto giusto.";
                        continue;
                    }

                    // Validation 2: Check for minimum tag count (at least minTagsRequired ambient/rumore tags)
                    var tagCount = Regex.Matches(candidate, @"\[(?:AMBIENTE|RUMORI)\b[^\]]*\]", RegexOptions.IgnoreCase).Count;
                    if (tagCount < minTagsRequired)
                    {
                        _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Not enough ambient tags: {tagCount} found, minimum {minTagsRequired} required", "warn");
                        lastError = $"Hai inserito solo {tagCount} tag [RUMORI]. Devi inserire ALMENO {minTagsRequired} tag di questo tipo per arricchire l'atmosfera della scena, non ripetere gli stessi tag.";
                        continue;
                    }

                    // All validations passed
                    _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Validated+merged: insertedAmbient={insertedCount}, totalAmbient={tagCount}");
                    return candidate;
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

        private List<(string Text, int StartIndex, int EndIndex)> SplitStoryIntoChunks(string text)
        {
            var chunks = new List<(string Text, int StartIndex, int EndIndex)>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return chunks;
            }

            var maxTokensPerChunk = Math.Max(1, _tuning.AmbientExpert.MaxTokensPerChunk);

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
