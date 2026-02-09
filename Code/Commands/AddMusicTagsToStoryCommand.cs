using System;
using System.Collections.Generic;
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
    /// <summary>
    /// Reads story_tagged, splits into chunks, adds music tags via music_expert agent,
    /// updates story_tagged, then enqueues TTS schema generation (the final step).
    /// </summary>
    public sealed class AddMusicTagsToStoryCommand : ICommand
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
        private readonly IServiceScopeFactory? _scopeFactory;

        public string CommandName => "add_music_tags_to_story";
        public int Priority => 2;
        public event EventHandler<CommandProgressEventArgs>? Progress;

        public AddMusicTagsToStoryCommand(
            long storyId,
            DatabaseService database,
            ILangChainKernelFactory kernelFactory,
            StoriesService? storiesService = null,
            ICustomLogger? logger = null,
            CommandTuningOptions? tuning = null,
            IServiceScopeFactory? scopeFactory = null)
        {
            _storyId = storyId;
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
            _storiesService = storiesService;
            _logger = logger;
            _tuning = tuning ?? new CommandTuningOptions();
            _scopeFactory = scopeFactory;
        }

        public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default, string? runId = null)
        {
            var effectiveRunId = string.IsNullOrWhiteSpace(runId)
                ? (_storiesService?.CurrentDispatcherRunId ?? $"add_music_tags_to_story_{_storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}")
                : runId;

            _logger?.Start(effectiveRunId);
            _logger?.Append(effectiveRunId, $"[story {_storyId}] Starting add_music_tags_to_story pipeline");

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

                var currentModelId = musicAgent.ModelId.Value;

                var modelInfo = _database.GetModelInfoById(musicAgent.ModelId.Value);
                if (string.IsNullOrWhiteSpace(modelInfo?.Name))
                {
                    return Fail(effectiveRunId, $"Model not found for music expert agent {musicAgent.Name}");
                }

                var triedModelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    modelInfo.Name
                };

                var currentModelName = modelInfo.Name;

                var systemPrompt = TaggingResponseFormat.AppendToSystemPrompt(
                    BuildSystemPrompt(musicAgent),
                    StoryTaggingService.TagTypeMusic);
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
                    _tuning.MusicExpert.MinTokensPerChunk,
                    _tuning.MusicExpert.MaxTokensPerChunk,
                    _tuning.MusicExpert.TargetTokensPerChunk);
                if (chunks.Count == 0)
                {
                    return Fail(effectiveRunId, "No chunks produced from story rows");
                }

                _logger?.Append(effectiveRunId, $"[story {_storyId}] Split into {chunks.Count} chunks (rows)");

                var bridge = _kernelFactory.CreateChatBridge(
                    currentModelName,
                    musicAgent.Temperature,
                    musicAgent.TopP,
                    musicAgent.RepeatPenalty,
                    musicAgent.TopK,
                    musicAgent.RepeatLastN,
                    musicAgent.NumPredict);

                var musicTags = new List<StoryTaggingService.StoryTagEntry>();
                for (int i = 0; i < chunks.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = chunks[i];
                    ReportProgress(effectiveRunId, i + 1, chunks.Count, $"Adding music tags chunk {i + 1}/{chunks.Count}");

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
                            $"[chunk {i + 1}/{chunks.Count}] Primary music_expert model '{currentModelName}' failed: {ex.Message}. Attempting fallback models...",
                            "warn");

                        var fallback = await TryChunkWithFallbackAsync(
                            roleCode: "music_expert",
                            failingModelId: currentModelId,
                            systemPrompt: systemPrompt,
                            chunkText: chunk.Text,
                            chunkIndex: i + 1,
                            chunkCount: chunks.Count,
                            runId: effectiveRunId,
                            agent: musicAgent,
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
                            musicAgent.Temperature,
                            musicAgent.TopP,
                            musicAgent.RepeatPenalty,
                            musicAgent.TopK,
                            musicAgent.RepeatLastN,
                            musicAgent.NumPredict);
                    }

                    if (string.IsNullOrWhiteSpace(mappingText))
                    {
                        return Fail(effectiveRunId, $"Music expert returned empty text for chunk {i + 1}/{chunks.Count}");
                    }

                    var parsed = StoryTaggingService.ParseMusicMapping(mappingText);
                    parsed = StoryTaggingService.FilterMusicTagsByProximity(parsed, minLineDistance: 20);
                    musicTags.AddRange(parsed);
                }

                var existingTags = StoryTaggingService.LoadStoryTags(story.StoryTags);
                existingTags.RemoveAll(t => t.Type == StoryTaggingService.TagTypeMusic);
                existingTags.AddRange(musicTags);
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

                _logger?.Append(effectiveRunId, $"[story {_storyId}] Music tags rebuilt from story_tags");

                var allowNext = _storiesService?.ApplyStatusTransitionWithCleanup(story, "tagged", effectiveRunId) ?? true;
                var enqueued = TryEnqueueNextStatus(story, allowNext, effectiveRunId);

                _logger?.MarkCompleted(effectiveRunId, "ok");
                return new CommandResult(true, enqueued
                    ? "Music tags added (next status enqueued)"
                    : "Music tags added");
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

        public Task<CommandResult> Start(CommandContext context)
            => ExecuteAsync(context.CancellationToken, context.RunId);

        public Task End(CommandContext context, CommandResult result)
            => Task.CompletedTask;

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

        private bool TryEnqueueNextStatus(StoryRecord story, bool allowNext, string runId)
        {
            try
            {
                if (!allowNext)
                {
                    _logger?.Append(runId, $"[story {_storyId}] next status enqueue skipped: delete_next_items attivo", "info");
                    return false;
                }

                if (_storiesService != null && !_storiesService.IsTaggedFinalAutoLaunchEnabled())
                {
                    _logger?.Append(runId, $"[story {_storyId}] next status enqueue skipped: StoryTaggingPipeline final autolaunch disabled", "info");
                    return false;
                }

                if (_storiesService != null && !_storiesService.TryValidateTaggedMusic(story, out var musicReason))
                {
                    _logger?.Append(runId, $"[story {_storyId}] next status enqueue skipped: music validation failed ({musicReason})", "warn");
                    return false;
                }

                if (!_tuning.MusicExpert.AutolaunchNextCommand)
                {
                    _logger?.Append(runId, $"[story {_storyId}] next status enqueue skipped: AutolaunchNextCommand disabled", "info");
                    return false;
                }

                if (_storiesService == null)
                {
                    _logger?.Append(runId, $"[story {_storyId}] next status enqueue skipped: StoriesService not available", "warn");
                    return false;
                }

                var refreshedStory = _storiesService.GetStoryById(story.Id) ?? story;
                var nextRunId = _storiesService.EnqueueNextStatusCommand(refreshedStory, "music_tags_completed", priority: 2);
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
            var maxAttempts = Math.Max(1, _tuning.MusicExpert.MaxAttemptsPerChunk);
            var retryDelayBaseSeconds = Math.Max(0, _tuning.MusicExpert.RetryDelayBaseSeconds);
            var requiredTags = ComputeRequiredMusicTagsForChunk(chunkText);
            var diagnoseOnFinalFailure = _tuning.MusicExpert.DiagnoseOnFinalFailure;

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

                    // IMPORTANT: the response format requirements are appended to the agent system prompt
                    // (single system message). Do not add extra system messages here.

                    messages.Add(new ConversationMessage { Role = "user", Content = chunkText });

                    // Keep the last request/response around for diagnostics.
                    lastRequestMessages = messages;

                    var responseJson = await bridge.CallModelWithToolsAsync(messages, new List<Dictionary<string, object>>(), ct, skipResponseChecker: false);
                    var (textContent, _) = LangChainChatBridge.ParseChatResponse(responseJson);
                    var cleaned = textContent?.Trim() ?? string.Empty;
                    lastAssistantText = cleaned;

                    if (string.IsNullOrWhiteSpace(cleaned))
                    {
                        _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Empty response on attempt {attempt}", "warn");
                        _logger?.MarkLatestModelResponseResult("FAILED", "Risposta vuota");
                        lastError = "Il testo ritornato è vuoto.";
                        continue;
                    }

                    var tags = StoryTaggingService.ParseMusicMapping(cleaned);
                    tags = StoryTaggingService.FilterMusicTagsByProximity(tags, minLineDistance: 20);
                    var tagCount = tags.Count;
                    if (tagCount < requiredTags)
                    {
                        _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Not enough music tags: {tagCount} found, minimum {requiredTags} required", "warn");
                        _logger?.MarkLatestModelResponseResult("FAILED", $"Hai inserito solo {tagCount} righe valide. Devi inserirne almeno {requiredTags}.");
                        lastError = $"Hai inserito solo {tagCount} righe valide. Devi inserire ALMENO {requiredTags} indicazioni musicali (formato: ID emozione [secondi]).";
                        continue;
                    }

                    _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Validated mapping: totalMusic={tagCount}");
                    _logger?.MarkLatestModelResponseResult(
                        "SUCCESS",
                        null);
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
                        _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] music_expert self-diagnosis: {diagnosis}", "warn");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Append(runId, $"[chunk {chunkIndex}/{chunkCount}] Failed to collect music_expert self-diagnosis: {ex.Message}", "warn");
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
                "Sei un auditor tecnico per l'agente music_expert. " +
                "Devi spiegare in modo conciso perché l'output non ha superato la validazione o perché è fallito. " +
                "Non inventare contenuti; basati sui dati forniti.";

            var sb = new StringBuilder();
            sb.AppendLine($"DIAGNOSI music_expert - chunk {chunkIndex}/{chunkCount}");
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
                diagMessages.Add(new ConversationMessage
                {
                    Role = "user",
                    Content = "ISTRUZIONI DIAGNOSTICHE:\n" + auditSystem + "\n\n" + sb
                });
            }
            else
            {
                diagMessages.Add(new ConversationMessage { Role = "system", Content = auditSystem });
                diagMessages.Add(new ConversationMessage { Role = "user", Content = sb.ToString() });
            }

            var responseJson = await bridge.CallModelWithToolsAsync(diagMessages, new List<Dictionary<string, object>>(), ct, skipResponseChecker: true).ConfigureAwait(false);
            var (textContent, _) = LangChainChatBridge.ParseChatResponse(responseJson);
            return string.IsNullOrWhiteSpace(textContent) ? null : textContent.Trim();
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

        private sealed record Segment(int Start, int End, int TokenCount);

        private List<(string Text, int StartIndex, int EndIndex)> SplitStoryIntoChunks(string text)
        {
            var chunks = new List<(string Text, int StartIndex, int EndIndex)>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return chunks;
            }

            var minTokensPerChunk = Math.Max(0, _tuning.MusicExpert.MinTokensPerChunk);
            var maxTokensPerChunk = Math.Max(1, _tuning.MusicExpert.MaxTokensPerChunk);
            var targetTokensPerChunk = Math.Max(1, _tuning.MusicExpert.TargetTokensPerChunk);
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

        private void ReportProgress(string runId, int current, int max, string description)
        {
            try
            {
                Progress?.Invoke(this, new CommandProgressEventArgs(current, max, description));
            }
            catch
            {
                // best-effort
            }
        }
    }
}
