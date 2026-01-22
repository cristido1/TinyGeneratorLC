using System;
using System.Collections.Generic;
using System.IO;
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
    public sealed class TransformStoryRawToTaggedCommand
    {
        private readonly CommandTuningOptions _tuning;

        private readonly long _storyId;
        private readonly DatabaseService _database;
        private readonly ILangChainKernelFactory _kernelFactory;
        private readonly StoriesService? _storiesService;
        private readonly ICustomLogger? _logger;
        private readonly ICommandDispatcher? _commandDispatcher;

        public TransformStoryRawToTaggedCommand(
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

                if (_storiesService != null)
                {
                    var (ok, msg) = await _storiesService.DeleteStoryTaggedAsync(story.Id);
                    if (!ok)
                    {
                        return Fail(effectiveRunId, msg ?? "Failed to clear story tagged data");
                    }
                }

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

                var normalizedForFormatter = PreNormalizeForFormatter(sourceText);
                if (string.IsNullOrWhiteSpace(normalizedForFormatter))
                {
                    return Fail(effectiveRunId, "Story text became empty after pre-normalization for formatter");
                }

                var chunks = SplitStoryIntoChunks(normalizedForFormatter);
                if (chunks.Count == 0)
                {
                    return Fail(effectiveRunId, "No chunks produced from story text");
                }

                _logger?.Append(effectiveRunId, $"[story {_storyId}] Split into {chunks.Count} chunks");

                var bridge = _kernelFactory.CreateChatBridge(
                    modelInfo.Name,
                    formatterAgent.Temperature,
                    formatterAgent.TopP,
                    formatterAgent.RepeatPenalty,
                    formatterAgent.TopK,
                    formatterAgent.RepeatLastN,
                    formatterAgent.NumPredict);

                var taggedChunks = new List<string>();
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
                    var tagged = await FormatChunkWithRetriesAsync(
                        bridge,
                        systemPrompt,
                        chunk.Text,
                        i + 1,
                        chunks.Count,
                        effectiveRunId,
                        ct);

                    if (string.IsNullOrWhiteSpace(tagged))
                    {
                        return Fail(effectiveRunId, $"Formatter returned empty text for chunk {i + 1}/{chunks.Count}");
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
                    formatterAgent.ModelId,
                    promptHash,
                    nextVersion);

                if (!saved)
                {
                    return Fail(effectiveRunId, $"Failed to persist tagged story for {_storyId}");
                }

                _logger?.Append(effectiveRunId, $"[story {_storyId}] Tagged story saved (version {nextVersion})");

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
                            var cmd = new AmbientExpertCommand(_storyId, _database, _kernelFactory, _storiesService, _logger, _commandDispatcher, _tuning);
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

        private async Task<string> FormatChunkWithRetriesAsync(
            LangChainChatBridge bridge,
            string? systemPrompt,
            string chunkText,
            int chunkIndex,
            int chunkCount,
            string runId,
            CancellationToken ct)
        {
            var inputLength = chunkText.Length;
            string? lastError = null;

            var maxAttempts = Math.Max(1, _tuning.TransformStoryRawToTagged.MaxAttemptsPerChunk);
            var minTagsRequired = Math.Max(0, _tuning.TransformStoryRawToTagged.MinTagsPerChunkRequirement);

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
                        lastError = "Il testo ritornato è vuoto.";
                        continue;
                    }

                    // Validation 1: Check if text is shorter than input
                    if (cleaned.Length < inputLength)
                    {
                        var ratio = (double)cleaned.Length / inputLength;
                        _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Output too short: {cleaned.Length} chars vs {inputLength} input ({ratio:P0})", "warn");
                        lastError = "Il testo ritornato è più corto dell'originale. NON devi tagliare o rimuovere testo, devi solo aggiungere i tag richiesti mantenendo tutto il contenuto originale.";
                        continue;
                    }

                    // Validation 2: Check for minimum tag count
                    var tagCount = System.Text.RegularExpressions.Regex.Matches(cleaned, @"\[(?:PERSONAGGIO|EMOZIONE|VOCE|RITMO|PAUSA|VOLUME|MUSICA|FX|AMBIENTE|NOISE):", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
                    if (tagCount < minTagsRequired)
                    {
                        _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Not enough tags: {tagCount} found, minimum {minTagsRequired} required", "warn");
                        lastError = $"Hai inserito solo {tagCount} tag. Devi inserire ALMENO {minTagsRequired} tag nel testo per arricchirlo adeguatamente.";
                        continue;
                    }

                    // All validations passed
                    _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Validated: {cleaned.Length} chars, {tagCount} tags");
                    return cleaned;
                }
                catch (Exception ex)
                {
                    _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Attempt {attempt} failed: {ex.Message}", "warn");
                    lastError = $"Errore durante l'elaborazione: {ex.Message}";
                }
            }

            _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Failed after {maxAttempts} attempts. Last error: {lastError}", "error");
            return string.Empty;
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
            var overlapTokens = Math.Max(0, _tuning.TransformStoryRawToTagged.OverlapTokens);

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
