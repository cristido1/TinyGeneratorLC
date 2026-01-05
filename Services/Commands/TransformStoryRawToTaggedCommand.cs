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
        private const int MinTokensPerChunk = 1000;
        private const int MaxTokensPerChunk = 2000;
        private const int TargetTokensPerChunk = 1500;
        private const int OverlapTokens = 150;
        private const int MaxAttemptsPerChunk = 3;
        private const int MaxOverlapChars = 8000;

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
            ICommandDispatcher? commandDispatcher = null)
        {
            _storyId = storyId;
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
            _storiesService = storiesService;
            _logger = logger;
            _commandDispatcher = commandDispatcher;
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

                // Requirement: if tagging succeeds, enqueue tts_schema.json generation from inside this command.
                TryEnqueueTtsSchemaGeneration(story, effectiveRunId);

                _logger?.MarkCompleted(effectiveRunId, "ok");
                return new CommandResult(true, "Tagged story generated (TTS schema enqueued)");
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

                // If a schema generation command is already queued/running for this story, don't enqueue another.
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
                    // If snapshots fail, we still try to enqueue.
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
                        ["trigger"] = "tagged_generated",
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

        private async Task<string> FormatChunkWithRetriesAsync(
            LangChainChatBridge bridge,
            string? systemPrompt,
            string chunkText,
            int chunkIndex,
            int chunkCount,
            string runId,
            CancellationToken ct)
        {
            for (int attempt = 1; attempt <= MaxAttemptsPerChunk; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Formatting attempt {attempt}/{MaxAttemptsPerChunk}");

                try
                {
                    var messages = new List<ConversationMessage>();
                    if (!string.IsNullOrWhiteSpace(systemPrompt))
                    {
                        messages.Add(new ConversationMessage { Role = "system", Content = systemPrompt });
                    }
                    messages.Add(new ConversationMessage { Role = "user", Content = chunkText });

                    var responseJson = await bridge.CallModelWithToolsAsync(messages, new List<Dictionary<string, object>>(), ct);
                    var (textContent, _) = LangChainChatBridge.ParseChatResponse(responseJson);
                    var cleaned = textContent?.Trim() ?? string.Empty;

                    if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        return cleaned;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Attempt {attempt} failed: {ex.Message}", "warn");
                }
            }

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

        private static List<Chunk> SplitStoryIntoChunks(string storyText)
        {
            var chunks = new List<Chunk>();
            if (string.IsNullOrWhiteSpace(storyText))
            {
                return chunks;
            }

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
                    if (tokenCount + segTokens > MaxTokensPerChunk && tokenCount >= MinTokensPerChunk)
                    {
                        break;
                    }

                    tokenCount += segTokens;
                    endSeg++;

                    if (tokenCount >= TargetTokensPerChunk && tokenCount >= MinTokensPerChunk)
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
                    if (overlapCount >= OverlapTokens)
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

        private static string MergeTaggedChunks(IReadOnlyList<string> chunks)
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

        private static int FindOverlapLength(string previous, string current)
        {
            if (string.IsNullOrEmpty(previous) || string.IsNullOrEmpty(current))
            {
                return 0;
            }

            int max = Math.Min(previous.Length, current.Length);
            int maxSearch = Math.Min(max, MaxOverlapChars);

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
