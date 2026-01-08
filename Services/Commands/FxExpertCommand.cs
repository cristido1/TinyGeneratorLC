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
    /// Reads story_tagged, splits into chunks, adds FX tags via fx_expert agent,
    /// updates story_tagged, then enqueues music_expert command.
    /// </summary>
    public sealed class FxExpertCommand
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

        public FxExpertCommand(
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
                var chunks = SplitStoryIntoChunks(sourceText);
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
                            var cmd = new MusicExpertCommand(_storyId, _database, _kernelFactory, _storiesService, _logger, _commandDispatcher);
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
            for (int attempt = 1; attempt <= MaxAttemptsPerChunk; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] FX tagging attempt {attempt}/{MaxAttemptsPerChunk}");

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
                        _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Success on attempt {attempt}");
                        return cleaned;
                    }

                    _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Empty response on attempt {attempt}", "warn");
                }
                catch (Exception ex)
                {
                    _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Error on attempt {attempt}: {ex.Message}", "error");
                    if (attempt == MaxAttemptsPerChunk)
                    {
                        throw;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                }
            }

            throw new InvalidOperationException($"Failed to process chunk {chunkIndex}/{chunkCount} after {MaxAttemptsPerChunk} attempts");
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
