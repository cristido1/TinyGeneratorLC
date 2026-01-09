using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
        private const int MinTokensPerChunk = 1000;
        private const int MaxTokensPerChunk = 2000;
        private const int TargetTokensPerChunk = 1500;
        private const int OverlapTokens = 150;
        private const int MaxAttemptsPerChunk = 3;

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
            var inputLength = chunkText.Length;
            string? lastError = null;

            for (int attempt = 1; attempt <= MaxAttemptsPerChunk; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Music tagging attempt {attempt}/{MaxAttemptsPerChunk}");

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
                            Content = $"{lastError} Questo è il tentativo {attempt} di {MaxAttemptsPerChunk}." 
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

                    // Validation 1: Check if text is shorter than input
                    if (cleaned.Length < inputLength)
                    {
                        var ratio = (double)cleaned.Length / inputLength;
                        _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Output too short: {cleaned.Length} chars vs {inputLength} input ({ratio:P0})", "warn");
                        lastError = "Il testo ritornato è più corto dell'originale. NON devi tagliare o rimuovere testo, devi solo aggiungere i tag [MUSICA:] mantenendo tutto il contenuto originale.";
                        continue;
                    }

                    // Validation 2: Check for minimum tag count (at least 3 MUSICA tags)
                    var tagCount = System.Text.RegularExpressions.Regex.Matches(cleaned, @"\[MUSICA:", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
                    if (tagCount < 3)
                    {
                        _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Not enough MUSICA tags: {tagCount} found, minimum 3 required", "warn");
                        lastError = $"Hai inserito solo {tagCount} tag [MUSICA:]. Devi inserire ALMENO 3 indicazioni musicali per arricchire l'esperienza sonora della narrazione.";
                        continue;
                    }

                    // All validations passed
                    _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Validated: {cleaned.Length} chars, {tagCount} MUSICA tags");
                    return cleaned;
                }
                catch (Exception ex)
                {
                    _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Error on attempt {attempt}: {ex.Message}", "error");
                    lastError = $"Errore durante l'elaborazione: {ex.Message}";
                    
                    if (attempt == MaxAttemptsPerChunk)
                    {
                        throw;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                }
            }

            throw new InvalidOperationException($"Failed to process chunk {chunkIndex}/{chunkCount} after {MaxAttemptsPerChunk} attempts. Last error: {lastError}");
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

            // Simple chunking by approximate tokens (4 chars ≈ 1 token)
            int pos = 0;
            while (pos < text.Length)
            {
                int chunkSize = Math.Min(MaxTokensPerChunk * 4, text.Length - pos);
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
