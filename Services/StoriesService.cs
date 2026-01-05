using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Encodings.Web;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TinyGenerator.Models;
using TinyGenerator.Skills;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed class StoriesService
{
    public void UpdateStoryTitle(long storyId, string title)
    {
        _database.UpdateStoryTitle(storyId, title);
    }
    private const int TtsSchemaReadyStatusId = 3;
    private const int DefaultPhraseGapMs = 2000;
    private const string NarratorFallbackVoiceName = "Dionisio Schuyler";
    private readonly DatabaseService _database;
    private readonly ILogger<StoriesService>? _logger;
    private readonly TtsService _ttsService;
    private readonly ILangChainKernelFactory? _kernelFactory;
    private readonly ICustomLogger? _customLogger;
    private readonly ICommandDispatcher? _commandDispatcher;
    private readonly MultiStepOrchestrationService? _multiStepOrchestrator;
    private readonly SentimentMappingService? _sentimentMappingService;
    private readonly ResponseCheckerService? _responseChecker;
    private readonly ConcurrentDictionary<long, StatusChainState> _statusChains = new();
    private static readonly JsonSerializerOptions SchemaJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        // IMPORTANT: write real UTF-8 characters (no \uXXXX escaping) so TTS reads accents correctly.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private const double AutoFormatMinAverageScore = 65.0;
    private const int AutoFormatMinEvaluations = 2;

    public StoriesService(
        DatabaseService database,
        TtsService ttsService,
        ILangChainKernelFactory? kernelFactory = null,
        ICustomLogger? customLogger = null,
        ILogger<StoriesService>? logger = null,
        ICommandDispatcher? commandDispatcher = null,
        MultiStepOrchestrationService? multiStepOrchestrator = null,
        SentimentMappingService? sentimentMappingService = null,
        ResponseCheckerService? responseChecker = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _ttsService = ttsService ?? throw new ArgumentNullException(nameof(ttsService));
        _kernelFactory = kernelFactory;
        _customLogger = customLogger;
        _logger = logger;
        _commandDispatcher = commandDispatcher;
        _multiStepOrchestrator = multiStepOrchestrator;
        _sentimentMappingService = sentimentMappingService;
        _responseChecker = responseChecker;
    }

    public long SaveGeneration(string prompt, StoryGenerationResult r, string? memoryKey = null)
    {
        return _database.SaveGeneration(prompt, r, memoryKey);
    }

    public List<StoryRecord> GetAllStories()
    {
        var stories = _database.GetAllStories();
        // Populate test info for each story
        foreach (var story in stories)
        {
            var testInfo = _database.GetTestInfoForStory(story.Id);
            story.TestRunId = testInfo.runId;
            story.TestStepId = testInfo.stepId;
        }
        return stories;
    }

    public void Delete(long id)
    {
        _database.DeleteStoryById(id);
    }

    public long InsertSingleStory(string prompt, string story, int? modelId = null, int? agentId = null, double score = 0.0, string? eval = null, int approved = 0, int? statusId = null, string? memoryKey = null, string? title = null, long? storyId = null)
    {
        storyId ??= LogScope.CurrentStoryId;
        return _database.InsertSingleStory(prompt, story, storyId, modelId, agentId, score, eval, approved, statusId, memoryKey, title);
    }

    public bool UpdateStoryById(long id, string? story = null, int? modelId = null, int? agentId = null, int? statusId = null, bool updateStatus = false, bool allowCreatorMetadataUpdate = false)
    {
        return _database.UpdateStoryById(id, story, modelId, agentId, statusId, updateStatus, allowCreatorMetadataUpdate);
    }

    public bool UpdateStoryCharacters(long storyId, string charactersJson)
    {
        return _database.UpdateStoryCharacters(storyId, charactersJson);
    }

    public List<StoryStatus> GetAllStoryStatuses()
    {
        return _database.ListAllStoryStatuses();
    }

    public StoryStatus? GetNextStatusForStory(StoryRecord story, IReadOnlyList<StoryStatus>? statuses = null)
    {
        statuses ??= _database.ListAllStoryStatuses();
        if (statuses == null || statuses.Count == 0 || story == null)
            return null;

        var ordered = statuses
            .OrderBy(s => s.Step)
            .ThenBy(s => s.Id)
            .ToList();

        StoryStatus? current = null;
        if (story.StatusId.HasValue)
        {
            current = ordered.FirstOrDefault(s => s.Id == story.StatusId.Value);
        }

        if (current == null)
        {
            return ordered.FirstOrDefault();
        }

        return ordered.FirstOrDefault(s => s.Step > current.Step);
    }

    private sealed class StatusChainState
    {
        public string ChainId { get; }
        public int? LastEnqueuedStatusId { get; set; }

        public StatusChainState(string chainId)
        {
            ChainId = chainId;
        }
    }

    public bool IsStatusChainActive(long storyId, out string chainId)
    {
        chainId = string.Empty;
        if (storyId <= 0) return false;

        if (_statusChains.TryGetValue(storyId, out var state) && state != null && !string.IsNullOrWhiteSpace(state.ChainId))
        {
            chainId = state.ChainId;
            return true;
        }

        return false;
    }

    public void StopStatusChain(long storyId)
    {
        if (storyId <= 0) return;
        _statusChains.TryRemove(storyId, out _);
    }

    public string? EnqueueStatusChain(long storyId)
    {
        if (storyId <= 0) return null;
        if (_commandDispatcher == null) return null;

        if (IsStatusChainActive(storyId, out var existing))
        {
            return existing;
        }

        var story = GetStoryById(storyId);
        if (story == null) return null;

        var chainId = $"chain_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        var state = new StatusChainState(chainId);
        if (!_statusChains.TryAdd(storyId, state))
        {
            return IsStatusChainActive(storyId, out var current) ? current : null;
        }

        // Enqueue the first step immediately.
        var started = TryAdvanceStatusChain(storyId, chainId);
        if (!started)
        {
            StopStatusChain(storyId);
            return null;
        }

        return chainId;
    }

    public bool TryAdvanceStatusChain(long storyId, string chainId)
    {
        if (storyId <= 0) return false;
        if (string.IsNullOrWhiteSpace(chainId)) return false;
        if (_commandDispatcher == null) return false;

        if (!_statusChains.TryGetValue(storyId, out var state) || state == null)
            return false;
        if (!string.Equals(state.ChainId, chainId, StringComparison.OrdinalIgnoreCase))
            return false;

        var story = GetStoryById(storyId);
        if (story == null)
        {
            StopStatusChain(storyId);
            return false;
        }

        var statuses = _database.ListAllStoryStatuses();
        var next = GetNextStatusForStory(story, statuses);
        if (next == null)
        {
            StopStatusChain(storyId);
            return false;
        }

        if (state.LastEnqueuedStatusId.HasValue && state.LastEnqueuedStatusId.Value == next.Id)
        {
            // Best-effort de-dup if we get multiple completion signals.
            return false;
        }

        var cmd = CreateCommandForStatus(next);
        if (cmd == null)
        {
            StopStatusChain(storyId);
            return false;
        }

        var runId = $"status_{next.Code ?? next.Id.ToString()}_{story.Id}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        var metadata = new Dictionary<string, string>
        {
            ["storyId"] = story.Id.ToString(),
            ["operation"] = next.FunctionName ?? next.Code ?? "status",
            ["chainId"] = state.ChainId,
            ["chainMode"] = "1",
            ["statusId"] = next.Id.ToString(),
            ["statusCode"] = next.Code ?? string.Empty
        };

        _commandDispatcher.Enqueue(
            next.FunctionName ?? next.Code ?? "status",
            async ctx =>
            {
                var latestStory = GetStoryById(story.Id);
                if (latestStory == null)
                    return new CommandResult(false, "Storia non trovata");

                var (success, message) = await ExecuteStoryCommandAsync(latestStory, cmd, next);
                return new CommandResult(success, message);
            },
            runId: runId,
            threadScope: $"story/status_chain/{story.Id}",
            metadata: metadata,
            priority: 2);

        state.LastEnqueuedStatusId = next.Id;
        return true;
    }

    public StoryStatus? GetStoryStatusById(int id)
    {
        return _database.GetStoryStatusById(id);
    }

    public StoryStatus? GetStoryStatusByCode(string? code)
    {
        return _database.GetStoryStatusByCode(code);
    }

    public int? ResolveStatusId(string? statusCode)
    {
        if (string.IsNullOrWhiteSpace(statusCode)) return null;
        try { return _database.GetStoryStatusByCode(statusCode)?.Id; }
        catch { return null; }
    }

    public StoryRecord? GetStoryById(long id)
    {
        var story = _database.GetStoryById(id);
        if (story == null) return null;
        try
        {
            story.Evaluations = _database.GetStoryEvaluations(id);
            var testInfo = _database.GetTestInfoForStory(id);
            story.TestRunId = testInfo.runId;
            story.TestStepId = testInfo.stepId;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load evaluations for story {Id}", id);
        }
        return story;
    }

    /// <summary>
    /// Scan the stories_folder for existing final_mix.wav files and set the story status
    /// to `audio_master_generated` when appropriate. Returns the number of stories updated.
    /// </summary>
    public async Task<int> ScanAndMarkAudioMastersAsync()
    {
        var baseFolder = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder");
        if (!Directory.Exists(baseFolder))
            return 0;

        var dirs = Directory.GetDirectories(baseFolder);
        var stories = _database.GetAllStories() ?? new List<StoryRecord>();
        var byFolder = stories.Where(s => !string.IsNullOrWhiteSpace(s.Folder))
                             .ToDictionary(s => s.Folder!, s => s, StringComparer.OrdinalIgnoreCase);

        var audioStatus = GetStoryStatusByCode("audio_master_generated");
        int updatedCount = 0;

        foreach (var dir in dirs)
        {
            try
            {
                var folderName = Path.GetFileName(dir);
                var finalMix = Path.Combine(dir, "final_mix.wav");
                if (!File.Exists(finalMix))
                    continue;

                if (!byFolder.TryGetValue(folderName, out var story))
                {
                    _customLogger?.Append("live-logs", $"[Scan] Folder '{folderName}' contains final_mix.wav but no DB story found.");
                    continue;
                }

                if (audioStatus == null)
                {
                    _customLogger?.Append("live-logs", "[Scan] Status code 'audio_master_generated' not found in DB.");
                    break;
                }

                if (story.StatusId.HasValue && story.StatusId.Value == audioStatus.Id)
                {
                    // already set
                    continue;
                }

                var ok = _database.UpdateStoryById(story.Id, statusId: audioStatus.Id, updateStatus: true);
                if (ok)
                {
                    updatedCount++;
                    _customLogger?.Append("live-logs", $"[Scan] Story {story.Id} ('{folderName}') status updated to {audioStatus.Code}.");
                }
                else
                {
                    _customLogger?.Append("live-logs", $"[Scan] Failed to update story {story.Id} ('{folderName}').");
                }
            }
            catch (Exception ex)
            {
                _customLogger?.Append("live-logs", $"[Scan] Exception scanning '{dir}': {ex.Message}");
            }
        }

        await Task.CompletedTask;
        return updatedCount;
    }

    private Task<(bool success, string? message)> ExecuteStoryCommandAsync(long storyId, IStoryCommand command)
    {
        var story = GetStoryById(storyId);
        if (story == null)
            return Task.FromResult<(bool, string?)>((false, "Storia non trovata"));

        return ExecuteStoryCommandAsync(story, command, null);
    }

    private async Task<(bool success, string? message)> ExecuteStoryCommandAsync(StoryRecord story, IStoryCommand command, StoryStatus? targetStatus = null)
    {
        if (command.RequireStoryText && string.IsNullOrWhiteSpace(story.StoryRaw))
            return (false, "La storia non contiene testo");

        string folderPath = string.Empty;
        if (command.EnsureFolder)
        {
            folderPath = EnsureStoryFolder(story);
        }
        else if (!string.IsNullOrWhiteSpace(story.Folder))
        {
            folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", story.Folder);
            Directory.CreateDirectory(folderPath);
        }

        var context = new StoryCommandContext(story, folderPath, targetStatus);
        var result = await command.ExecuteAsync(context);

        if (result.success && targetStatus?.Id > 0 && !command.HandlesStatusTransition)
        {
            try
            {
                _database.UpdateStoryById(story.Id, statusId: targetStatus.Id, updateStatus: true);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to update story {Id} to status {StatusId}", story.Id, targetStatus.Id);
            }
        }

        return result;
    }

    public List<StoryEvaluation> GetEvaluationsForStory(long storyId)
    {
        return _database.GetStoryEvaluations(storyId);
    }

    public async Task<(bool success, string? message)> ExecuteNextStatusOperationAsync(long storyId)
    {
        var story = GetStoryById(storyId);
        if (story == null)
            return (false, "Storia non trovata");

        var statuses = _database.ListAllStoryStatuses();
        var nextStatus = GetNextStatusForStory(story, statuses);
        if (nextStatus == null)
            return (false, "Nessuno stato successivo disponibile");

        var command = CreateCommandForStatus(nextStatus);
        if (command == null)
            return (false, $"Operazione non supportata per lo stato {nextStatus.Code ?? nextStatus.Id.ToString()}");

        return await ExecuteStoryCommandAsync(story, command, nextStatus);
    }

    public void SaveChapter(string memoryKey, int chapterNumber, string content)
    {
        _database.SaveChapter(memoryKey, chapterNumber, content);
    }

    public async Task<(bool success, double score, string? error)> EvaluateStoryWithAgentAsync(long storyId, int agentId)
    {
        if (_kernelFactory == null)
            return (false, 0, "Kernel factory non disponibile");

        var story = GetStoryById(storyId);
        if (story == null)
            return (false, 0, "Storia non trovata");

        var storyText = !string.IsNullOrWhiteSpace(story.StoryRevised)
            ? story.StoryRevised
            : story.StoryRaw;

        if (string.IsNullOrWhiteSpace(storyText))
            return (false, 0, "Storia non trovata o priva di contenuto");

        var agent = _database.GetAgentById(agentId);
        if (agent == null || !agent.IsActive)
            return (false, 0, "Agente valutatore non trovato");

        if (!agent.ModelId.HasValue)
            return (false, 0, "Modello non configurato per l'agente valutatore");

        var modelInfo = _database.GetModelInfoById(agent.ModelId.Value);
        var modelName = modelInfo?.Name;
        if (string.IsNullOrWhiteSpace(modelName))
            return (false, 0, "Modello associato all'agente non disponibile");

        var allowedPlugins = ParseAgentSkills(agent)?.ToList() ?? new List<string>();

        var runId = $"storyeval_{story.Id}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        _customLogger?.Start(runId);
        var evaluationOpId = LogScope.GenerateOperationId();
        using var scope = LogScope.Push($"story_evaluation_{story.Id}", evaluationOpId, null, null, agent.Name);

        try
        {
            var orchestrator = _kernelFactory.CreateOrchestrator(modelName, allowedPlugins, agent.Id);
            var evaluatorTool = orchestrator.GetTool<EvaluatorTool>("evaluate_full_story");
            if (evaluatorTool != null)
            {
                evaluatorTool.CurrentStoryId = story.Id;
            }
            
            // Set CurrentStoryId for coherence evaluation tools
            var chunkFactsTool = orchestrator.GetTool<ChunkFactsExtractorTool>("extract_chunk_facts");
            if (chunkFactsTool != null)
            {
                chunkFactsTool.CurrentStoryId = story.Id;
            }
            
            var coherenceTool = orchestrator.GetTool<CoherenceCalculatorTool>("calculate_coherence");
            if (coherenceTool != null)
            {
                coherenceTool.CurrentStoryId = story.Id;
            }
            
            var chatBridge = _kernelFactory.CreateChatBridge(
                modelName,
                agent.Temperature,
                agent.TopP,
                agent.RepeatPenalty,
                agent.RepeatLastN,
                agent.NumPredict);

            var isStoryEvaluatorTextProtocol = agent.Role?.Equals("story_evaluator", StringComparison.OrdinalIgnoreCase) ?? false;
            var systemMessage = (isStoryEvaluatorTextProtocol
                ? StoriesServiceDefaults.EvaluatorTextProtocolInstructions
                : (!string.IsNullOrWhiteSpace(agent.Instructions) ? agent.Instructions : ComposeSystemMessage(agent)))
                ?? string.Empty;

            // Use different prompt based on agent role
            bool isCoherenceEvaluation = agent.Role?.Equals("coherence_evaluator", StringComparison.OrdinalIgnoreCase) ?? false;
            var prompt = BuildStoryEvaluationPrompt(story, isCoherenceEvaluation);

            var beforeEvaluations = _database.GetStoryEvaluations(storyId)
                .Select(e => e.Id)
                .ToHashSet();

            if (isStoryEvaluatorTextProtocol)
            {
                var protocolResult = await EvaluateStoryWithTextProtocolAsync(
                    story,
                    chatBridge,
                    systemMessage,
                    modelId: agent.ModelId,
                    agentId: agent.Id).ConfigureAwait(false);

                if (!protocolResult.success)
                {
                    var error = protocolResult.error ?? "Valutazione fallita";
                    _customLogger?.Append(runId, $"[{storyId}] Valutazione fallita: {error}", "Error");
                    return (false, 0, error);
                }
            }
            else
            {
                if (!allowedPlugins.Any())
                    return (false, 0, "L'agente valutatore non ha strumenti abilitati");

                var reactLoop = new ReActLoopOrchestrator(
                    orchestrator,
                    _customLogger,
                    runId: runId,
                    modelBridge: chatBridge,
                    systemMessage: systemMessage,
                    responseChecker: _responseChecker,
                    agentRole: agent.Role);

                var reactResult = await reactLoop.ExecuteAsync(prompt);
                if (!reactResult.Success)
                {
                    var error = reactResult.Error ?? "Valutazione fallita";
                    _customLogger?.Append(runId, $"[{storyId}] Valutazione fallita: {error}", "Error");
                    return (false, 0, error);
                }
            }

            // Check results based on agent type
            if (isCoherenceEvaluation)
            {
                // For coherence evaluation, check if global coherence was saved
                var globalCoherence = _database.GetGlobalCoherence((int)storyId);
                if (globalCoherence == null)
                {
                    var msg = "L'agente non ha salvato la coerenza globale.";
                    _customLogger?.Append(runId, $"[{storyId}] {msg}", "Warn");
                    TryLogEvaluationResult(runId, storyId, agent?.Name, success: false, msg);
                    return (false, 0, msg);
                }

                var score = globalCoherence.GlobalCoherenceValue * 10; // Convert 0-1 to 0-10 scale
                _customLogger?.Append(runId, $"[{storyId}] Valutazione di coerenza completata. Score: {score:F2}");
                TryLogEvaluationResult(runId, storyId, agent?.Name, success: true, $"Valutazione di coerenza completata. Score: {score:F2}");
                return (true, score, null);
            }
            else
            {
                // For standard evaluation, check stories_evaluations table
                var afterEvaluations = _database.GetStoryEvaluations(storyId)
                    .Where(e => !beforeEvaluations.Contains(e.Id))
                    .OrderBy(e => e.Id)
                    .ToList();

                if (afterEvaluations.Count == 0)
                {
                    var msg = "L'agente non ha salvato alcuna valutazione.";
                    _customLogger?.Append(runId, $"[{storyId}] {msg}", "Warn");
                    TryLogEvaluationResult(runId, storyId, agent?.Name, success: false, msg);
                    return (false, 0, msg);
                }

                var avgScore = afterEvaluations.Average(e => e.TotalScore);
                _customLogger?.Append(runId, $"[{storyId}] Valutazione completata. Score medio: {avgScore:F2}");
                TryLogEvaluationResult(runId, storyId, agent?.Name, success: true, $"Valutazione completata. Score medio: {avgScore:F2}");
                
                // Se score > 60% (su scala 0-40), avvia riassunto automatico con priorit� bassa
                if (avgScore > 24 && _commandDispatcher != null)
                {
                    try
                    {
                        var summarizerAgent = _database.ListAgents()
                            .FirstOrDefault(a => a.Role == "summarizer" && a.IsActive);
                        
                        if (summarizerAgent != null)
                        {
                            var summarizeRunId = Guid.NewGuid().ToString();
                            var cmd = new Commands.SummarizeStoryCommand(
                                storyId,
                                _database,
                                _kernelFactory!,
                                _customLogger!);
                            
                            _commandDispatcher.Enqueue(
                                "SummarizeStory",
                                async ctx => {
                                    bool success = await cmd.ExecuteAsync(ctx.CancellationToken);
                                    return new CommandResult(success, success ? "Summary generated" : "Failed to generate summary");
                                },
                                runId: summarizeRunId,
                                metadata: new Dictionary<string, string>
                                {
                                    ["storyId"] = storyId.ToString(),
                                    ["storyTitle"] = story.Title ?? "Untitled",
                                    ["triggeredBy"] = "auto_evaluation",
                                    ["evaluationScore"] = avgScore.ToString("F2")
                                },
                                priority: 3);
                            
                            _customLogger?.Append(runId, $"[{storyId}] Auto-summarization enqueued (score: {avgScore:F2})");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to enqueue auto-summarization for story {StoryId}", storyId);
                    }
                }
                
                return (true, avgScore, null);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Errore durante EvaluateStoryWithAgent per storia {StoryId} agente {AgentId}", storyId, agentId);
            TryLogEvaluationResult(runId, storyId, agent?.Name, success: false, ex.Message);
            return (false, 0, ex.Message);
        }
        finally
        {
            _customLogger?.MarkCompleted(runId);
        }

    }

    private async Task<(bool success, double score, string? error)> EvaluateStoryWithTextProtocolAsync(
        StoryRecord story,
        LangChainChatBridge chatBridge,
        string systemMessage,
        int? modelId,
        int agentId)
    {
        if (story == null) return (false, 0, "Storia non trovata");
        var storyText = !string.IsNullOrWhiteSpace(story.StoryRevised)
            ? story.StoryRevised
            : story.StoryRaw;

        if (string.IsNullOrWhiteSpace(storyText)) return (false, 0, "Storia non trovata o priva di contenuto");

        var messages = new List<ConversationMessage>
        {
            new ConversationMessage { Role = "system", Content = systemMessage }
        };
        const int maxAttemptsFinalEvaluation = 3;

        // Single-shot evaluation: send the full story once and expect immediate formatted evaluation.
        // Allow a few attempts to recover if the model uses the wrong format.
        messages.Add(new ConversationMessage
        {
            Role = "user",
            Content = $"TESTO:\n\n{storyText}\n\nVALUTA"
        });

        var parsed = new ParsedEvaluation(0, string.Empty, 0, string.Empty, 0, string.Empty, 0, string.Empty);
        string? parseError = null;
        string evalText = string.Empty;

        var evalOk = false;
        for (var attempt = 1; attempt <= maxAttemptsFinalEvaluation; attempt++)
        {
            var evalRawJson = await chatBridge.CallModelWithToolsAsync(messages, tools: new List<Dictionary<string, object>>()).ConfigureAwait(false);
            var (evalResponseText, _) = LangChainChatBridge.ParseChatResponse(evalRawJson);
            evalText = NormalizeEvaluatorOutput((evalResponseText ?? string.Empty));
            messages.Add(new ConversationMessage { Role = "assistant", Content = evalText });
if (TryParseEvaluationText(evalText, out parsed, out parseError))
            {
                evalOk = true;
                break;
            }

                if (attempt < maxAttemptsFinalEvaluation)
                {
                    messages.Add(new ConversationMessage
                    {
                        Role = "user",
                        Content =
                            "Formato non valido. Devi rispondere SOLO nel formato obbligatorio, senza markup né elenchi. " +
                            "Ripeti la valutazione rispettando esattamente le 4 intestazioni e i punteggi 1-5." +
                            " Le intestazioni richieste sono (in questo ordine):\nCoerenza narrativa\nOriginalità\nImpatto emotivo\nAzione"
                    });
                }
        }

        if (!evalOk)
        {
            return (false, 0, parseError ?? "Formato valutazione non valido.");
        }

        var totalScore = (double)(parsed.NarrativeScore10 + parsed.OriginalityScore10 + parsed.EmotionalScore10 + parsed.ActionScore10);
        try
        {
            _database.AddStoryEvaluation(
                story.Id,
                parsed.NarrativeScore10, parsed.NarrativeExplanation,
                parsed.OriginalityScore10, parsed.OriginalityExplanation,
                parsed.EmotionalScore10, parsed.EmotionalExplanation,
                parsed.ActionScore10, parsed.ActionExplanation,
                totalScore,
                rawJson: evalText,
                modelId: modelId,
                agentId: agentId);

            // Requirement: whenever an evaluation is performed (i.e., saved), optionally enqueue
            // the formatter command if conditions are met. Non-blocking: we enqueue and return.
            TryEnqueueAutoFormatAfterEvaluation(story.Id);
        }
        catch (Exception ex)
        {
            return (false, 0, $"Errore nel salvataggio della valutazione: {ex.Message}");
        }

        return (true, totalScore, null);
    }

    private void TryEnqueueAutoFormatAfterEvaluation(long storyId)
    {
        try
        {
            if (storyId <= 0) return;
            if (_commandDispatcher == null) return;

            var story = _database.GetStoryById(storyId);
            if (story == null) return;
            if (!string.IsNullOrWhiteSpace(story.StoryTagged)) return;
            if (string.IsNullOrWhiteSpace(story.StoryRaw)) return;

            // Avoid duplicate enqueues if a format command is already queued/running.
            try
            {
                var alreadyQueued = _commandDispatcher.GetActiveCommands().Any(s =>
                    s.Metadata != null &&
                    s.Metadata.TryGetValue("storyId", out var sid) &&
                    string.Equals(sid, storyId.ToString(), StringComparison.OrdinalIgnoreCase) &&
                    (
                        string.Equals(s.OperationName, "TransformStoryRawToTagged", StringComparison.OrdinalIgnoreCase) ||
                        (s.Metadata.TryGetValue("operation", out var op) && op.Contains("format_story", StringComparison.OrdinalIgnoreCase)) ||
                        s.RunId.StartsWith($"format_story_{storyId}_", StringComparison.OrdinalIgnoreCase)
                    ));

                if (alreadyQueued) return;
            }
            catch
            {
                // If dispatcher snapshots fail for any reason, we still allow enqueue.
            }

            var (count, average) = _database.GetStoryEvaluationStats(storyId);
            if (count < AutoFormatMinEvaluations) return;
            if (average <= AutoFormatMinAverageScore) return;

            if (_kernelFactory == null) return;

            var formatRunId = $"format_story_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            _commandDispatcher.Enqueue(
                "TransformStoryRawToTagged",
                async ctx =>
                {
                    try
                    {
                        var cmd = new TransformStoryRawToTaggedCommand(
                            storyId,
                            _database,
                            _kernelFactory,
                            storiesService: this,
                            logger: _customLogger,
                            commandDispatcher: _commandDispatcher);

                        return await cmd.ExecuteAsync(ctx.CancellationToken, ctx.RunId);
                    }
                    catch (Exception ex)
                    {
                        return new CommandResult(false, ex.Message);
                    }
                },
                runId: formatRunId,
                threadScope: "story/format",
                metadata: new Dictionary<string, string>
                {
                    ["storyId"] = storyId.ToString(),
                    ["operation"] = "format_story_auto",
                    ["trigger"] = "evaluation_saved",
                    ["evaluationCount"] = count.ToString(),
                    ["evaluationAvg"] = average.ToString("F2")
                },
                priority: 2);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Auto-format enqueue failed for story {StoryId}", storyId);
        }
    }
    private static string NormalizeEvaluatorOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var t = text;

        // Remove common "thinking" blocks.
        t = Regex.Replace(t, "(?is)<think>.*?</think>", " ");
        t = Regex.Replace(t, "(?is)\\[think\\].*?\\[/think\\]", " ");
        t = Regex.Replace(t, "(?im)^\\s*(thinking|reasoning)\\s*:\\s*.*$", " ");

        // Preserve content inside fenced code blocks (models often wrap the whole evaluation in ```...```).
        // We only strip the fence markers.
        t = Regex.Replace(t, "(?is)```(?:[a-z0-9_+-]+)?\\s*(.*?)\\s*```", "$1");

        // Collapse whitespace (line breaks are not meaningful for parsing).
        t = Regex.Replace(t, "\\s+", " ").Trim();
        return t;
    }

    private sealed record ParsedEvaluation(
        int NarrativeScore10,
        string NarrativeExplanation,
        int OriginalityScore10,
        string OriginalityExplanation,
        int EmotionalScore10,
        string EmotionalExplanation,
        int ActionScore10,
        string ActionExplanation);

    private static bool TryParseEvaluationText(string text, out ParsedEvaluation parsed, out string? error)
    {
        parsed = new ParsedEvaluation(0, string.Empty, 0, string.Empty, 0, string.Empty, 0, string.Empty);
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Risposta di valutazione vuota.";
            return false;
        }

        var normalized = NormalizeEvaluatorOutput(text);

        // Flexible headings (accept missing accents / minor variations).
        // IMPORTANT: parse them sequentially in the required order to avoid false positives
        // (e.g., the word "azione" mentioned inside another section's comment).
        var hNarr = new Regex(@"\bcoerenza\s+narrativ[aà]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var hOrig = new Regex(@"\boriginalit[aà]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var hEmot = new Regex(@"\bimpatto\s+emotiv[oòaà]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var hAzione = new Regex(@"\bazione\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        (int idx, int len)? FindAfter(Regex rx, int startAt)
        {
            if (startAt < 0) startAt = 0;
            if (startAt >= normalized.Length) return null;
            var m = rx.Match(normalized, startAt);
            return m.Success ? (m.Index, m.Length) : null;
        }

        var narr = FindAfter(hNarr, 0);
        if (narr == null)
        {
            error = "Formato valutazione non valido: manca la sezione 'Coerenza narrativa'.";
            return false;
        }

        var orig = FindAfter(hOrig, narr.Value.idx + narr.Value.len);
        if (orig == null)
        {
            error = "Formato valutazione non valido: manca la sezione 'Originalità'.";
            return false;
        }

        var emot = FindAfter(hEmot, orig.Value.idx + orig.Value.len);
        if (emot == null)
        {
            error = "Formato valutazione non valido: manca la sezione 'Impatto emotivo'.";
            return false;
        }

        var azione = FindAfter(hAzione, emot.Value.idx + emot.Value.len);
        if (azione == null)
        {
            error = "Formato valutazione non valido: manca la sezione 'Azione'.";
            return false;
        }

        (int score5, string explanation) ExtractSection(int start, int end)
        {
            var seg = normalized.Substring(start, Math.Max(0, end - start)).Trim();
            if (string.IsNullOrWhiteSpace(seg)) return (0, string.Empty);

            var mScore = Regex.Match(seg, @"\b([1-5])\b");
            if (!mScore.Success) return (0, string.Empty);
            var score = int.Parse(mScore.Groups[1].Value);

            // Explanation is optional.
            var rest = seg.Substring(mScore.Index + mScore.Length).Trim();
            rest = rest.TrimStart('-', ':', '.', ')', ']', ' ');
            return (score, rest);
        }

        var (nScore5, nExp) = ExtractSection(narr.Value.idx + narr.Value.len, orig.Value.idx);
        var (oScore5, oExp) = ExtractSection(orig.Value.idx + orig.Value.len, emot.Value.idx);
        var (eScore5, eExp) = ExtractSection(emot.Value.idx + emot.Value.len, azione.Value.idx);
        var (aScore5, aExp) = ExtractSection(azione.Value.idx + azione.Value.len, normalized.Length);

        if (nScore5 < 1 || oScore5 < 1 || eScore5 < 1 || aScore5 < 1)
        {
            error = "Formato valutazione non valido: punteggi non trovati o fuori range (atteso 1-5 per sezione).";
            return false;
        }

        parsed = new ParsedEvaluation(
            nScore5 * 2, nExp,
            oScore5 * 2, oExp,
            eScore5 * 2, eExp,
            aScore5 * 2, aExp);

        return true;
    }

    private void TryLogEvaluationResult(string runId, long storyId, string? agentName, bool success, string message)
    {
        try
        {
            var entry = new TinyGenerator.Models.LogEntry
            {
                Ts = DateTime.UtcNow.ToString("o"),
                Level = success ? "Information" : "Warning",
                Category = "StoryEvaluation",
                Message = $"[{storyId}] {message}",
                ThreadScope = runId,
                ThreadId = 0,
                AgentName = agentName,
                Result = success ? "SUCCESS" : "FAILED"
            };
            _database.InsertLogsAsync(new[] { entry }).ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch { }
    }

    public async Task<(bool success, double score, string? error)> EvaluateActionPacingWithAgentAsync(long storyId, int agentId)
    {
        if (_kernelFactory == null)
            return (false, 0, "Kernel factory non disponibile");

        var story = GetStoryById(storyId);
        if (story == null || string.IsNullOrWhiteSpace(story.StoryRaw))
            return (false, 0, "Storia non trovata o priva di contenuto");

        var agent = _database.GetAgentById(agentId);
        if (agent == null || !agent.IsActive)
            return (false, 0, "Agente valutatore non trovato");

        if (!agent.ModelId.HasValue)
            return (false, 0, "Modello non configurato per l'agente valutatore");

        var modelInfo = _database.GetModelInfoById(agent.ModelId.Value);
        var modelName = modelInfo?.Name;
        if (string.IsNullOrWhiteSpace(modelName))
            return (false, 0, "Modello associato all'agente non disponibile");

        var allowedPlugins = ParseAgentSkills(agent)?.ToList() ?? new List<string>();
        if (!allowedPlugins.Any())
            return (false, 0, "L'agente valutatore non ha strumenti abilitati");

        var runId = $"actioneval_{story.Id}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        _customLogger?.Start(runId);
        var evaluationOpId = LogScope.GenerateOperationId();
        using var scope = LogScope.Push($"action_evaluation_{story.Id}", evaluationOpId, null, null, agent.Name);

        try
        {
            var orchestrator = _kernelFactory.CreateOrchestrator(modelName, allowedPlugins, agent.Id);

            var chatBridge = _kernelFactory.CreateChatBridge(
                modelName,
                agent.Temperature,
                agent.TopP,
                agent.RepeatPenalty,
                agent.TopK,
                agent.RepeatLastN,
                agent.NumPredict);

            var systemMessage = !string.IsNullOrWhiteSpace(agent.Instructions)
                ? agent.Instructions
                : ComposeSystemMessage(agent);

            var prompt = BuildActionPacingPrompt(story);

            var reactLoop = new ReActLoopOrchestrator(
                orchestrator,
                _customLogger,
                runId: runId,
                modelBridge: chatBridge,
                systemMessage: systemMessage,
                responseChecker: _responseChecker,
                agentRole: agent.Role);

            var reactResult = await reactLoop.ExecuteAsync(prompt);
            if (!reactResult.Success)
            {
                var error = reactResult.Error ?? "Valutazione azione/ritmo fallita";
                _customLogger?.Append(runId, $"[{storyId}] Valutazione fallita: {error}", "Error");
                return (false, 0, error);
            }

            _customLogger?.Append(runId, $"[{storyId}] Valutazione azione/ritmo completata");
            return (true, 0, null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Errore durante EvaluateActionPacingWithAgent per storia {StoryId} agente {AgentId}", storyId, agentId);
            return (false, 0, ex.Message);
        }
        finally
        {
            _customLogger?.MarkCompleted(runId);
        }
    }

    /// <summary>
    /// Generates TTS audio for a story and saves it to the specified folder.
    /// If dispatcherRunId is provided, progress will be reported to the CommandDispatcher.
    /// </summary>
    public async Task<(bool success, string? error)> GenerateTtsForStoryAsync(long storyId, string folderName, string? dispatcherRunId = null)
    {
        // Back-compat overload. Prefer using the storyId-only overload.
        if (string.IsNullOrWhiteSpace(folderName))
            return (false, "Folder name is required");

        return await GenerateTtsForStoryInternalAsync(storyId, folderName, dispatcherRunId, targetStatusId: null);
    }

    /// <summary>
    /// Generates TTS audio for a story, resolving the folder from the story record.
    /// If dispatcherRunId is provided, progress will be reported to the CommandDispatcher.
    /// </summary>
    public async Task<(bool success, string? error)> GenerateTtsForStoryByIdAsync(long storyId, string? dispatcherRunId = null)
    {
        var story = GetStoryById(storyId);
        if (story == null)
            return (false, "Story not found");

        var folderName = !string.IsNullOrWhiteSpace(story.Folder)
            ? story.Folder
            : new DirectoryInfo(EnsureStoryFolder(story)).Name;

        return await GenerateTtsForStoryInternalAsync(storyId, folderName, dispatcherRunId, targetStatusId: null);
    }

    private async Task<(bool success, string? error)> GenerateTtsForStoryInternalAsync(long storyId, string folderName, string? dispatcherRunId, int? targetStatusId)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return (false, "Folder name is required");

        var story = GetStoryById(storyId);
        if (story == null)
            return (false, "Story not found");

        // Usa il nuovo flusso basato su tts_schema.json e timeline
        var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", folderName);
        Directory.CreateDirectory(folderPath);

        var context = new StoryCommandContext(story, folderPath, targetStatusId.HasValue ? new StoryStatus { Id = targetStatusId.Value } : null);

        var deleteCmd = new DeleteTtsCommand(this);
        var (cleanupOk, cleanupMessage) = await deleteCmd.ExecuteAsync(context);
        if (!cleanupOk)
        {
            return (false, cleanupMessage ?? "Impossibile cancellare i file TTS esistenti");
        }

        // Run synchronously when dispatcherRunId is provided so we can report progress
        if (!string.IsNullOrWhiteSpace(dispatcherRunId))
        {
            var result = await GenerateTtsAudioWithProgressAsync(context, dispatcherRunId);
            if (result.success && targetStatusId.HasValue)
            {
                try
                {
                    _database.UpdateStoryById(storyId, statusId: targetStatusId.Value, updateStatus: true);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to update story {Id} to status {StatusId}", storyId, targetStatusId.Value);
                }
            }
            return result;
        }

        var (success, message) = await StartTtsAudioGenerationAsync(context);
        return (success, message);
    }

    public sealed record EnqueueResult(bool Enqueued, string Message, string? RunId = null);

    public string? EnqueueGenerateTtsAudioCommand(long storyId, string trigger, int priority = 3)
    {
        var result = TryEnqueueGenerateTtsAudioCommandInternal(storyId, trigger, priority, targetStatusId: null);
        return result.Enqueued ? result.RunId : null;
    }

    public EnqueueResult TryEnqueueGenerateTtsAudioCommand(long storyId, string trigger, int priority = 3)
        => TryEnqueueGenerateTtsAudioCommandInternal(storyId, trigger, priority, targetStatusId: null);

    private EnqueueResult TryEnqueueGenerateTtsAudioCommandInternal(long storyId, string trigger, int priority, int? targetStatusId)
    {
        try
        {
            if (storyId <= 0)
                return new EnqueueResult(false, "StoryId non valido.");
            if (_commandDispatcher == null)
                return new EnqueueResult(false, "CommandDispatcher non disponibile (app non inizializzata o servizio non registrato).");

            var story = GetStoryById(storyId);
            if (story == null)
                return new EnqueueResult(false, $"Storia {storyId} non trovata.");

            var folderName = !string.IsNullOrWhiteSpace(story.Folder)
                ? story.Folder
                : new DirectoryInfo(EnsureStoryFolder(story)).Name;

            // De-dup: don't enqueue if already queued/running.
            try
            {
                var existing = _commandDispatcher.GetActiveCommands().FirstOrDefault(s =>
                    s.Metadata != null &&
                    s.Metadata.TryGetValue("storyId", out var sid) &&
                    string.Equals(sid, storyId.ToString(), StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(s.OperationName, "generate_tts_audio", StringComparison.OrdinalIgnoreCase) ||
                     s.RunId.StartsWith($"generate_tts_audio_{storyId}_", StringComparison.OrdinalIgnoreCase)));

                if (existing != null && !string.IsNullOrWhiteSpace(existing.RunId))
                {
                    var status = string.IsNullOrWhiteSpace(existing.Status) ? "" : $" (stato: {existing.Status})";
                    return new EnqueueResult(false, $"Generazione audio TTS già accodata/in esecuzione{status} (run {existing.RunId}).", existing.RunId);
                }
            }
            catch
            {
                // Best-effort: if snapshots fail, still allow enqueue.
            }

            var runId = $"generate_tts_audio_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            _commandDispatcher.Enqueue(
                "generate_tts_audio",
                async ctx =>
                {
                    var (ok, err) = await GenerateTtsForStoryInternalAsync(storyId, folderName, ctx.RunId, targetStatusId);
                    if (ok)
                    {
                        // Requirement: if TTS audio generation succeeds, enqueue music/ambience/fx individually with lower priority.
                        EnqueuePostTtsAudioFollowups(storyId, trigger: "tts_audio_generated", priority: Math.Max(priority + 1, 4));
                    }

                    return new CommandResult(ok, ok ? "Generazione audio TTS completata." : err);
                },
                runId: runId,
                threadScope: "story/tts_audio",
                metadata: new Dictionary<string, string>
                {
                    ["storyId"] = storyId.ToString(),
                    ["operation"] = "generate_tts_audio",
                    ["trigger"] = trigger,
                    ["folder"] = folderName,
                    ["targetStatusId"] = targetStatusId?.ToString() ?? string.Empty
                },
                priority: priority);

            return new EnqueueResult(true, $"Generazione audio TTS accodata (run {runId}).", runId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enqueue generate_tts_audio for story {StoryId}", storyId);
        }

        return new EnqueueResult(false, "Errore inatteso durante l'accodamento della generazione audio TTS (vedi log applicazione).");
    }

    public void EnqueuePostTtsAudioFollowups(long storyId, string trigger, int priority = 4)
    {
        try
        {
            if (storyId <= 0) return;
            if (_commandDispatcher == null) return;

            var story = GetStoryById(storyId);
            if (story == null) return;

            var folderName = !string.IsNullOrWhiteSpace(story.Folder)
                ? story.Folder
                : new DirectoryInfo(EnsureStoryFolder(story)).Name;

            void EnqueueIfNotQueued(string operationName, string runPrefix, Func<CommandContext, Task<CommandResult>> handler)
            {
                try
                {
                    var alreadyQueued = _commandDispatcher.GetActiveCommands().Any(s =>
                        s.Metadata != null &&
                        s.Metadata.TryGetValue("storyId", out var sid) &&
                        string.Equals(sid, storyId.ToString(), StringComparison.OrdinalIgnoreCase) &&
                        (string.Equals(s.OperationName, operationName, StringComparison.OrdinalIgnoreCase) ||
                         s.RunId.StartsWith(runPrefix, StringComparison.OrdinalIgnoreCase)));

                    if (alreadyQueued) return;
                }
                catch
                {
                    // If snapshots fail, still try to enqueue.
                }

                var runId = $"{runPrefix}{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                _commandDispatcher.Enqueue(
                    operationName,
                    handler,
                    runId: runId,
                    threadScope: $"story/{operationName}",
                    metadata: new Dictionary<string, string>
                    {
                        ["storyId"] = storyId.ToString(),
                        ["operation"] = operationName,
                        ["trigger"] = trigger,
                        ["folder"] = folderName
                    },
                    priority: priority);
            }

            EnqueueIfNotQueued(
                "generate_music",
                $"generate_music_{storyId}_",
                async ctx =>
                {
                    var (ok, err) = await GenerateMusicForStoryAsync(storyId, folderName, ctx.RunId);
                    return new CommandResult(ok, ok ? "Generazione musica completata." : err);
                });

            EnqueueIfNotQueued(
                "generate_ambience_audio",
                $"generate_ambience_audio_{storyId}_",
                async ctx =>
                {
                    var (ok, err) = await GenerateAmbienceForStoryAsync(storyId, folderName, ctx.RunId);
                    return new CommandResult(ok, ok ? "Generazione audio ambientale completata." : err);
                });

            EnqueueIfNotQueued(
                "generate_fx_audio",
                $"generate_fx_audio_{storyId}_",
                async ctx =>
                {
                    var (ok, err) = await GenerateFxForStoryAsync(storyId, folderName, ctx.RunId);
                    return new CommandResult(ok, ok ? "Generazione effetti sonori completata." : err);
                });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enqueue post-TTS followups for story {StoryId}", storyId);
        }
    }

    public string? EnqueueReviseStoryCommand(long storyId, string trigger, int priority = 2, bool force = false)
    {
        try
        {
            if (storyId <= 0) return null;
            if (_commandDispatcher == null) return null;

            // De-dup (optional): skip if already queued/running unless force=true.
            if (!force)
            {
                try
                {
                    var alreadyQueued = _commandDispatcher.GetActiveCommands().Any(s =>
                        s.Metadata != null &&
                        s.Metadata.TryGetValue("storyId", out var sid) &&
                        string.Equals(sid, storyId.ToString(), StringComparison.OrdinalIgnoreCase) &&
                        (string.Equals(s.OperationName, "revise_story", StringComparison.OrdinalIgnoreCase) ||
                         s.RunId.StartsWith($"revise_story_{storyId}_", StringComparison.OrdinalIgnoreCase)));
                    if (alreadyQueued) return null;
                }
                catch
                {
                    // best-effort
                }
            }

            var runId = $"revise_story_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            _commandDispatcher.Enqueue(
                "revise_story",
                async ctx =>
                {
                    try
                    {
                        var story = GetStoryById(storyId);
                        if (story == null)
                        {
                            return new CommandResult(false, "Story not found");
                        }

                        var raw = story.StoryRaw ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(raw))
                        {
                            return new CommandResult(false, "Story raw is empty");
                        }

                        var revisor = _database.ListAgents()
                            .Where(a => a.IsActive && !string.IsNullOrWhiteSpace(a.Role) &&
                                        a.Role.Equals("revisor", StringComparison.OrdinalIgnoreCase))
                            .OrderBy(a => a.Id)
                            .FirstOrDefault();

                        // If no revisor available, fall back to raw -> revised so the pipeline can continue.
                        if (revisor == null)
                        {
                            _database.UpdateStoryRevised(storyId, raw);
                            EnqueueAutomaticStoryEvaluations(storyId, trigger: "revision_skipped", priority: Math.Max(priority + 1, 3));
                            return new CommandResult(true, "No revisor configured: copied raw to story_revised and enqueued evaluations.");
                        }

                        if (!revisor.ModelId.HasValue)
                        {
                            _database.UpdateStoryRevised(storyId, raw);
                            EnqueueAutomaticStoryEvaluations(storyId, trigger: "revision_skipped_no_model", priority: Math.Max(priority + 1, 3));
                            return new CommandResult(true, "Revisor has no model: copied raw to story_revised and enqueued evaluations.");
                        }

                        var modelInfo = _database.GetModelInfoById(revisor.ModelId.Value);
                        if (string.IsNullOrWhiteSpace(modelInfo?.Name))
                        {
                            _database.UpdateStoryRevised(storyId, raw);
                            EnqueueAutomaticStoryEvaluations(storyId, trigger: "revision_skipped_no_model_name", priority: Math.Max(priority + 1, 3));
                            return new CommandResult(true, "Revisor model not found: copied raw to story_revised and enqueued evaluations.");
                        }

                        if (_kernelFactory == null)
                        {
                            return new CommandResult(false, "Kernel factory non disponibile");
                        }

                        var systemPrompt = (revisor.Prompt ?? string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(systemPrompt))
                        {
                            _database.UpdateStoryRevised(storyId, raw);
                            EnqueueAutomaticStoryEvaluations(storyId, trigger: "revision_skipped_empty_prompt", priority: Math.Max(priority + 1, 3));
                            return new CommandResult(true, "Revisor prompt empty: copied raw to story_revised and enqueued evaluations.");
                        }

                        var bridge = _kernelFactory.CreateChatBridge(
                            modelInfo.Name,
                            revisor.Temperature,
                            revisor.TopP,
                            revisor.RepeatPenalty,
                            revisor.TopK,
                            revisor.RepeatLastN,
                            revisor.NumPredict);

                        // Use smaller chunks for the revisor to reduce repetition risk and improve responsiveness.
                        // Prepare all chunks up-front and validate we are not losing/skipping text.
                        var chunks = SplitIntoRevisionChunks(raw, approxChunkChars: 1000);
                        if (chunks.Count == 0)
                        {
                            return new CommandResult(false, "Revision chunking produced zero chunks");
                        }

                        // Sanity check: total chunk sizes (core + trailing separators) should match the normalized input length.
                        // Note: SplitIntoRevisionChunks normalizes CRLF -> LF internally, so compare to the same normalization.
                        var normalizedRaw = raw.Replace("\r\n", "\n");
                        var totalCore = chunks.Sum(c => c.Text.Length);
                        var totalSep = chunks.Sum(c => c.TrailingSeparator?.Length ?? 0);
                        var totalChunkChars = totalCore + totalSep;
                        var diff = Math.Abs(totalChunkChars - normalizedRaw.Length);

                        // Allow a small tolerance (defensive). If it's large, something is wrong and we should stop.
                        if (diff > 64)
                        {
                            _customLogger?.Append(runId,
                                $"[story {storyId}] Revision chunking size mismatch: normalizedRawLen={normalizedRaw.Length}, totalChunkChars={totalChunkChars}, diff={diff}. Aborting revision to avoid loops.",
                                "error");
                            return new CommandResult(false, "Revision chunking mismatch; aborting");
                        }
                        else if (diff > 0)
                        {
                            _customLogger?.Append(runId,
                                $"[story {storyId}] Revision chunking minor size mismatch: normalizedRawLen={normalizedRaw.Length}, totalChunkChars={totalChunkChars}, diff={diff}.",
                                "warn");
                        }
                        var revisedBuilder = new StringBuilder(raw.Length + 256);

                        var previousStart = -1;

                        for (var i = 0; i < chunks.Count; i++)
                        {
                            ctx.CancellationToken.ThrowIfCancellationRequested();
                            var chunk = chunks[i];

                            // Safety: never send the same chunk twice in one run.
                            if (chunk.Start <= previousStart)
                            {
                                _customLogger?.Append(runId, $"[story {storyId}] Revision chunk ordering issue: idx={i} start={chunk.Start} prevStart={previousStart}. Stopping to avoid duplicate chunks.", "warn");
                                break;
                            }
                            previousStart = chunk.Start;

                            try
                            {
                                _customLogger?.Append(runId, $"[story {storyId}] Revising chunk {i + 1}/{chunks.Count} start={chunk.Start} len={chunk.Text.Length}");
                            }
                            catch { }

                            // Publish progress to the global "Comandi in esecuzione" panel.
                            try
                            {
                                _commandDispatcher?.UpdateStep(ctx.RunId, i + 1, chunks.Count, $"chunk {i + 1}/{chunks.Count}");
                            }
                            catch { }

                            // IMPORTANT: do not send any conversation history. Only (systemPrompt + current chunk).
                            var systemPromptWithChunk = systemPrompt + $"\n\n(chunk: {i + 1}/{chunks.Count})";
                            var messages = new List<ConversationMessage>
                            {
                                new ConversationMessage { Role = "system", Content = systemPromptWithChunk },
                                new ConversationMessage { Role = "user", Content = chunk.Text }
                            };

                            var responseJson = await bridge.CallModelWithToolsAsync(messages, new List<Dictionary<string, object>>(), ctx.CancellationToken);
                            var (textContent, _) = LangChainChatBridge.ParseChatResponse(responseJson);
                            var revisedChunk = (textContent ?? string.Empty).Trim();
                            if (string.IsNullOrWhiteSpace(revisedChunk))
                            {
                                // If the model returned nothing, fall back to original chunk.
                                revisedChunk = chunk.Text;
                            }

                            revisedBuilder.Append(revisedChunk);

                            // Preserve original chunk boundary whitespace (newline/space/etc.) so words don't get glued.
                            if (!string.IsNullOrEmpty(chunk.TrailingSeparator) &&
                                !revisedChunk.EndsWith(chunk.TrailingSeparator, StringComparison.Ordinal))
                            {
                                revisedBuilder.Append(chunk.TrailingSeparator);
                            }
                        }

                        var revised = revisedBuilder.ToString();
                        _database.UpdateStoryRevised(storyId, revised);

                        // After revision, enqueue evaluations.
                        EnqueueAutomaticStoryEvaluations(storyId, trigger: "revised_saved", priority: Math.Max(priority + 1, 3));

                        return new CommandResult(true, "Story revised saved and evaluations enqueued.");
                    }
                    catch (OperationCanceledException)
                    {
                        return new CommandResult(false, "Revision cancelled");
                    }
                    catch (Exception ex)
                    {
                        return new CommandResult(false, ex.Message);
                    }
                },
                runId: runId,
                threadScope: "story/revision",
                metadata: new Dictionary<string, string>
                {
                    ["storyId"] = storyId.ToString(),
                    ["operation"] = "revise_story",
                    ["trigger"] = trigger
                },
                priority: priority);

            return runId;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enqueue revise_story for story {StoryId}", storyId);
            return null;
        }
    }

    private sealed record RevisionChunk(int Start, string Text, string TrailingSeparator);

    private static List<RevisionChunk> SplitIntoRevisionChunks(string text, int approxChunkChars)
    {
        var result = new List<RevisionChunk>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var normalized = text.Replace("\r\n", "\n");
        var i = 0;
        while (i < normalized.Length)
        {
            var remaining = normalized.Length - i;
            var take = Math.Min(approxChunkChars, remaining);
            var end = i + take;

            if (end >= normalized.Length)
            {
                // Last chunk: keep as-is.
                var last = normalized.Substring(i, normalized.Length - i);
                result.Add(new RevisionChunk(i, last, TrailingSeparator: string.Empty));
                break;
            }

            // Prefer cutting at a newline within the window.
            var cutPos = normalized.LastIndexOf('\n', end - 1, take);

            // If no newline, prefer cutting on whitespace to avoid splitting words.
            if (cutPos <= i)
            {
                cutPos = FindCutOnWhitespace(normalized, i, end);
            }

            if (cutPos <= i)
            {
                // As a last resort, cut at end (may split a long token, but avoids infinite loops).
                var fallback = normalized.Substring(i, take);
                result.Add(new RevisionChunk(i, fallback, TrailingSeparator: string.Empty));
                i += take;
                continue;
            }

            // Capture trailing whitespace (newline/space/etc.) as separator and strip it from the chunk text.
            var separatorStart = cutPos;
            var separatorLen = 0;
            while (separatorStart + separatorLen < normalized.Length &&
                   char.IsWhiteSpace(normalized[separatorStart + separatorLen]))
            {
                separatorLen++;

                // Prevent pathological huge separators.
                if (separatorLen >= 16) break;
            }

            var chunkCore = normalized.Substring(i, cutPos - i);
            var trailing = separatorLen > 0
                ? normalized.Substring(separatorStart, separatorLen)
                : string.Empty;

            result.Add(new RevisionChunk(i, chunkCore, TrailingSeparator: trailing));
            i = separatorStart + separatorLen;
        }

        return result;
    }

    private static int FindCutOnWhitespace(string text, int start, int preferredEnd)
    {
        // Look backward for whitespace to cut cleanly.
        for (var j = preferredEnd - 1; j > start; j--)
        {
            if (char.IsWhiteSpace(text[j])) return j;
        }

        // If none, look forward a bit for whitespace (avoid cutting a word if we're in the middle).
        var forwardLimit = Math.Min(text.Length, preferredEnd + 256);
        for (var j = preferredEnd; j < forwardLimit; j++)
        {
            if (char.IsWhiteSpace(text[j])) return j;
        }

        return -1;
    }

    private void EnqueueAutomaticStoryEvaluations(long storyId, string trigger, int priority)
    {
        try
        {
            if (storyId <= 0) return;
            if (_commandDispatcher == null) return;

            var evaluators = _database.ListAgents()
                .Where(a => a.IsActive && !string.IsNullOrWhiteSpace(a.Role) &&
                    (a.Role.Equals("story_evaluator", StringComparison.OrdinalIgnoreCase) ||
                     a.Role.Equals("evaluator", StringComparison.OrdinalIgnoreCase) ||
                     a.Role.Equals("writer_evaluator", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(a => a.Id)
                .ToList();

            if (evaluators.Count == 0) return;

            foreach (var evaluator in evaluators)
            {
                try
                {
                    var runId = $"evaluate_story_{storyId}_agent_{evaluator.Id}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                    var metadata = new Dictionary<string, string>
                    {
                        ["storyId"] = storyId.ToString(),
                        ["agentId"] = evaluator.Id.ToString(),
                        ["agentName"] = evaluator.Name ?? string.Empty,
                        ["operation"] = "evaluate_story",
                        ["trigger"] = trigger
                    };

                    _commandDispatcher.Enqueue(
                        "evaluate_story",
                        async ctx =>
                        {
                            var (success, score, error) = await EvaluateStoryWithAgentAsync(storyId, evaluator.Id);
                            var msg = success ? $"Valutazione completata. Score: {score:F2}" : $"Valutazione fallita: {error}";
                            return new CommandResult(success, msg);
                        },
                        runId: runId,
                        threadScope: $"story/evaluate/agent_{evaluator.Id}",
                        metadata: metadata,
                        priority: priority);
                }
                catch
                {
                    // best-effort per-evaluator
                }
            }
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>
    /// Generates ambient audio for a story using AudioCraft based on the 'ambience' fields in tts_schema.json.
    /// If dispatcherRunId is provided, progress will be reported to the CommandDispatcher.
    /// </summary>
    public async Task<(bool success, string? error)> GenerateAmbienceForStoryAsync(long storyId, string folderName, string? dispatcherRunId = null)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return (false, "Folder name is required");

        var story = GetStoryById(storyId);
        if (story == null)
            return (false, "Story not found");

        var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", folderName);
        Directory.CreateDirectory(folderPath);

        var context = new StoryCommandContext(story, folderPath, null);

        var deleteCmd = new DeleteAmbienceCommand(this);
        var (cleanupOk, cleanupMessage) = await deleteCmd.ExecuteAsync(context);
        if (!cleanupOk)
        {
            return (false, cleanupMessage ?? "Impossibile cancellare i rumori ambientali esistenti");
        }
        
        if (!string.IsNullOrWhiteSpace(dispatcherRunId))
        {
            return await GenerateAmbienceAudioInternalAsync(context, dispatcherRunId);
        }
        var (success, message) = await StartAmbienceAudioGenerationAsync(context);
        return (success, message);
    }

    public Task<(bool success, string? message)> GenerateTtsSchemaJsonAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new GenerateTtsSchemaCommand(this));
    }

    public Task<(bool success, string? message)> AssignVoicesAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new AssignVoicesCommand(this));
    }

    /// <summary>
    /// Normalizza i nomi dei personaggi nel file tts_schema.json usando la lista dei personaggi della storia.
    /// </summary>
    public Task<(bool success, string? message)> NormalizeCharacterNamesAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new NormalizeCharacterNamesCommand(this));
    }

    /// <summary>
    /// Normalizza i sentimenti nel file tts_schema.json ai valori supportati dal TTS.
    /// </summary>
    public Task<(bool success, string? message)> NormalizeSentimentsAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new NormalizeSentimentsCommand(this));
    }

    public Task<(bool success, string? message)> DeleteFinalMixAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new DeleteFinalMixCommand(this));
    }

    public Task<(bool success, string? message)> DeleteMusicAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new DeleteMusicCommand(this));
    }

    public Task<(bool success, string? message)> DeleteFxAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new DeleteFxCommand(this));
    }

    public Task<(bool success, string? message)> DeleteAmbienceAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new DeleteAmbienceCommand(this));
    }

    public Task<(bool success, string? message)> DeleteTtsSchemaAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new DeleteTtsSchemaCommand(this));
    }

    public Task<(bool success, string? message)> DeleteTtsAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new DeleteTtsCommand(this));
    }

    public Task<(bool success, string? message)> DeleteStoryTaggedAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new DeleteStoryTaggedCommand(this));
    }

    /// <summary>
    /// Esegue un test tts_schema sullo storyId specificato per tutti i modelli abilitati e aggiorna il campo TtsScore.
    /// </summary>
    public async Task<(bool success, string message)> TestTtsSchemaAllModelsAsync(long storyId, CancellationToken cancellationToken = default)
    {
        var story = _database.GetStoryById(storyId);
        if (story == null) return (false, $"Story {storyId} non trovata");
        if (_multiStepOrchestrator == null) return (false, "MultiStepOrchestrator non disponibile");

        var models = _database.ListModels()
            .Where(m => m.Enabled && !m.NoTools)
            .OrderBy(m => m.TestDurationSeconds ?? double.MaxValue)
            .ToList();
        if (models.Count == 0) return (false, "Nessun modello abilitato trovato");

        var results = new List<string>();
        var anyFail = false;

        foreach (var model in models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (ok, msg) = await RunTtsSchemaForModelAsync(story, model, cancellationToken);
                results.Add($"{model.Name}: {(ok ? "ok" : "fail")} ({msg})");
                if (!ok) anyFail = true;
            }
            catch (Exception ex)
            {
                anyFail = true;
                results.Add($"{model.Name}: fail ({ex.Message})");
                _logger?.LogError(ex, "Errore test tts_schema per il modello {Model}", model.Name);
            }
        }

        var summary = string.Join("; ", results);
        return (!anyFail, summary);
    }

    private async Task<(bool success, string message)> RunTtsSchemaForModelAsync(StoryRecord story, ModelInfo model, CancellationToken cancellationToken)
    {
        if (_multiStepOrchestrator == null)
        {
            return (false, "MultiStepOrchestrator non disponibile");
        }

        if (string.IsNullOrWhiteSpace(story.StoryTagged))
        {
            return (false, "Il testo taggato della storia e vuoto");
        }

        var ttsAgent = _database.ListAgents()
            .FirstOrDefault(a => a.IsActive && a.Role?.Equals("tts_json", StringComparison.OrdinalIgnoreCase) == true && a.MultiStepTemplateId.HasValue);

        if (ttsAgent == null)
        {
            return (false, "Nessun agente tts_json con template multi-step configurato");
        }

        var template = _database.GetStepTemplateById(ttsAgent.MultiStepTemplateId!.Value);
        if (template == null || !string.Equals(template.TaskType, "tts_schema", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Template multi-step TTS non trovato o con task_type errato");
        }

        // Prepara cartella storia
        var folderPath = EnsureStoryFolder(story);

        var configPayload = JsonSerializer.Serialize(new
        {
            workingFolder = folderPath,
            storyText = story.StoryTagged ?? string.Empty
        });

        var threadId = LogScope.CurrentThreadId ?? Environment.CurrentManagedThreadId;
        var sw = Stopwatch.StartNew();
        const string testGroup = "tts";
        int? runId = null;
        int? stepId = null;
        long? executionId = null;
        var totalSteps = CountSteps(template.StepPrompt);

        try
        {
            runId = _database.CreateTestRun(
                model.Name,
                testGroup,
                description: $"tts_schema story {story.Id}",
                passed: false,
                durationMs: null,
                notes: null,
                testFolder: folderPath);

            if (runId.HasValue)
            {
                stepId = _database.AddTestStep(
                    runId.Value,
                    1,
                    "tts_schema",
                    JsonSerializer.Serialize(new { storyId = story.Id, model = model.Name }));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Impossibile creare i record di test TTS per il modello {Model}", model.Name);
        }

        try
        {
            executionId = await _multiStepOrchestrator.StartTaskExecutionAsync(
                taskType: "tts_schema",
                entityId: story.Id,
                stepPrompt: template.StepPrompt,
                executorAgentId: ttsAgent.Id,
                checkerAgentId: null,
                configOverrides: configPayload,
                initialContext: story.StoryTagged ?? string.Empty,
                threadId: threadId,
                templateInstructions: string.IsNullOrWhiteSpace(template.Instructions) ? null : template.Instructions,
                executorModelOverrideId: model.Id);

            if (executionId.HasValue)
            {
                await _multiStepOrchestrator.ExecuteAllStepsAsync(executionId.Value, threadId, null, cancellationToken);
            }

            var schemaPath = Path.Combine(folderPath, "tts_schema.json");
            var success = File.Exists(schemaPath);
            var score = success ? 10 : 0;
            _database.UpdateModelTtsScore(model.Name, score);

            if (stepId.HasValue)
            {
                try
                {
                    var outputJson = success
                        ? JsonSerializer.Serialize(new { message = "tts_schema generato" })
                        : null;
                    var error = success ? null : "File tts_schema.json non trovato";
                    _database.UpdateTestStepResult(stepId.Value, success, outputJson, error, sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Impossibile aggiornare lo step di test TTS per il modello {Model}", model.Name);
                }
            }

            if (runId.HasValue)
            {
                try
                {
                    _database.UpdateTestRunResult(runId.Value, success, sw.ElapsedMilliseconds);
                    _database.RecalculateModelGroupScore(model.Name, testGroup);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Impossibile aggiornare il run di test TTS per il modello {Model}", model.Name);
                }
            }

            return (success, success ? "tts_schema generato" : "File tts_schema.json non trovato");
        }
        catch (Exception ex)
        {
            // Calcola punteggio parziale in base allo step raggiunto
            var partialScore = 0.0;
            if (executionId.HasValue)
            {
                try
                {
                    var exec = _database.GetTaskExecutionById(executionId.Value);
                    var completedSteps = exec != null ? Math.Max(0, exec.CurrentStep - 1) : 0;

                    var steps = _database.GetTaskExecutionSteps(executionId.Value);
                    if (steps != null && steps.Count > 0)
                    {
                        var maxStepRecorded = steps.Max(s => s.StepNumber);
                        completedSteps = Math.Max(completedSteps, maxStepRecorded);
                    }

                    if (totalSteps > 0)
                    {
                        partialScore = Math.Round((double)completedSteps / totalSteps * 10.0, 1);
                    }
                }
                catch (Exception innerEx)
                {
                    _logger?.LogWarning(innerEx, "Impossibile calcolare punteggio parziale per il modello {Model}", model.Name);
                }
            }

            _database.UpdateModelTtsScore(model.Name, partialScore);

            if (stepId.HasValue)
            {
                try
                {
                    var errorMsg = ex.Message;
                    _database.UpdateTestStepResult(stepId.Value, false, null, errorMsg, sw.ElapsedMilliseconds);
                }
                catch (Exception innerEx)
                {
                    _logger?.LogWarning(innerEx, "Impossibile aggiornare il fallimento del test TTS per il modello {Model}", model.Name);
                }
            }

            if (runId.HasValue)
            {
                try
                {
                    var errMsg = ex.Message;
                    if (partialScore > 0 && totalSteps > 0)
                    {
                        errMsg = $"Step incompleti: punteggio parziale {partialScore:0.0}/10. Dettaglio: {ex.Message}";
                    }

                    _database.UpdateTestRunResult(runId.Value, false, sw.ElapsedMilliseconds);
                    _database.UpdateTestRunNotes(runId.Value, errMsg);
                    _database.RecalculateModelGroupScore(model.Name, testGroup);
                }
                catch (Exception innerEx)
                {
                    _logger?.LogWarning(innerEx, "Impossibile chiudere il run di test TTS per il modello {Model}", model.Name);
                }
            }

            return (false, ex.Message);
        }
        finally
        {
            sw.Stop();
        }
    }

    private static int CountSteps(string stepPrompt)
    {
        if (string.IsNullOrWhiteSpace(stepPrompt)) return 0;
        var lines = stepPrompt.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var count = 0;
        foreach (var l in lines)
        {
            var trimmed = l.TrimStart();
            if (char.IsDigit(trimmed.FirstOrDefault()))
            {
                count++;
            }
        }
        return count;
    }

    private static string CleanTtsText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var cleaned = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // Skip lines that are only bracketed metadata (e.g., [PERSONAGGIO: ...])
            if (Regex.IsMatch(trimmed, @"^\[[^\]]+\]$")) continue;
            // Remove inline bracketed metadata
            var withoutTags = Regex.Replace(trimmed, @"\[[^\]]+\]", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(withoutTags))
            {
                cleaned.Add(withoutTags);
            }
        }
        var result = string.Join(" ", cleaned);
        
        // Normalize punctuation for TTS:
        // - Keep essential punctuation for pauses and intonation: . , ; : ? !
        // - Convert multiple dots (... or ..) to single dot
        // - Normalize whitespace
        result = Regex.Replace(result, @"\.{2,}", ".");  // Multiple dots ? single dot
        result = Regex.Replace(result, @"\s+", " ");  // Normalize whitespace
        return result.Trim();
    }

    private static readonly HashSet<char> TtsSchemaDisallowedTextChars = new()
    {
        '*', '-', '_', '"', '(', ')',
        // Quote-like punctuation that often degrades TTS reading
        '«', '»', '“', '”', '„', '‟'
    };

    private static string FilterTtsSchemaTextField(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            // Normalize typographic apostrophes to ASCII apostrophe.
            if (ch is '’' or '‘')
            {
                sb.Append('\'');
                continue;
            }
            if (TtsSchemaDisallowedTextChars.Contains(ch))
                continue;
            sb.Append(ch);
        }

        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static void SanitizeTtsSchemaTextFields(JsonObject root)
    {
        if (root == null) return;

        if (!root.TryGetPropertyValue("timeline", out var timelineNode))
            root.TryGetPropertyValue("Timeline", out timelineNode);

        if (timelineNode is not JsonArray timelineArray)
            return;

        foreach (var item in timelineArray.OfType<JsonObject>())
        {
            try
            {
                if (!IsPhraseEntry(item))
                    continue;

                SanitizeStringProperties(item,
                    "text", "Text",
                    "ambience", "Ambience", "ambience_description", "ambienceDescription",
                    "ambient_sound_description", "AmbientSoundDescription", "ambientSoundDescription",
                    "ambientSounds", "AmbientSounds", "ambient_sounds",
                    "fx_description", "FxDescription", "fxDescription",
                    "music_description", "MusicDescription", "musicDescription");
            }
            catch
            {
                // best-effort: never fail the save path
            }
        }
    }

    private static void SanitizeStringProperties(JsonObject obj, params string[] keys)
    {
        if (obj == null || keys == null || keys.Length == 0) return;

        var target = new HashSet<string>(keys.Where(k => !string.IsNullOrWhiteSpace(k)), StringComparer.OrdinalIgnoreCase);
        if (target.Count == 0) return;

        foreach (var kvp in obj.ToList())
        {
            if (!target.Contains(kvp.Key))
                continue;

            if (kvp.Value is JsonValue v && v.TryGetValue<string>(out var s))
            {
                obj[kvp.Key] = FilterTtsSchemaTextField(s);
            }
        }
    }

    private void SaveSanitizedTtsSchemaJson(string schemaPath, string json)
    {
        if (string.IsNullOrWhiteSpace(schemaPath)) return;

        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root != null)
            {
                SanitizeTtsSchemaTextFields(root);
                File.WriteAllText(schemaPath, root.ToJsonString(SchemaJsonOptions));
                return;
            }
        }
        catch
        {
            // fall back to raw write
        }

        File.WriteAllText(schemaPath, json);
    }

    private async Task SaveSanitizedTtsSchemaJsonAsync(string schemaPath, string json)
    {
        if (string.IsNullOrWhiteSpace(schemaPath)) return;

        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root != null)
            {
                SanitizeTtsSchemaTextFields(root);
                await File.WriteAllTextAsync(schemaPath, root.ToJsonString(SchemaJsonOptions));
                return;
            }
        }
        catch
        {
            // fall back to raw write
        }

        await File.WriteAllTextAsync(schemaPath, json);
    }

    /// <summary>
    /// Remove timeline phrase entries whose cleaned TTS text is empty (e.g. text is only "..." or brackets/metadata).
    /// This prevents empty/dialog placeholders from being persisted in tts_schema.json and avoids producing entries without audio/timing.
    /// </summary>
    private static void PruneEmptyTextPhrases(JsonArray timelineArray)
    {
        if (timelineArray == null) return;
        var toRemove = new List<JsonNode>();
        foreach (var node in timelineArray.OfType<JsonObject>())
        {
            try
            {
                if (!IsPhraseEntry(node)) continue;
                if (!TryReadPhrase(node, out var character, out var text, out var emotion)) continue;
                var clean = CleanTtsText(text);
                if (string.IsNullOrWhiteSpace(clean))
                {
                    toRemove.Add(node);
                }
            }
            catch
            {
                // best-effort: ignore parse errors
            }
        }

        foreach (var n in toRemove)
        {
            try { timelineArray.Remove(n); } catch { }
        }
    }

    private string EnsureStoryFolder(StoryRecord story)
    {
        var baseFolder = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder");
        Directory.CreateDirectory(baseFolder);

        if (string.IsNullOrWhiteSpace(story.Folder))
        {
            var paddedId = story.Id.ToString("D5");
            var folderName = $"{paddedId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
            folderName = SanitizeFolderName(folderName);
            _database.UpdateStoryFolder(story.Id, folderName);
            story.Folder = folderName;
        }

        var folderPath = Path.Combine(baseFolder, story.Folder);
        Directory.CreateDirectory(folderPath);
        return folderPath;
    }

    // Helper: delete a file if exists (best-effort)
    private void DeleteIfExists(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Unable to delete file {Path}", path);
        }
    }

    // Clean assets and flags before regenerating the TTS schema (full regeneration).
    // Deletes existing TTS/music/fx/ambience files and final mix, removes the old schema
    // and resets generated_* flags in the DB (best-effort).
    private void CleanAllGeneratedAssetsForTtsSchemaRegeneration(long storyId, string folderPath)
    {
        try
        {
            // Reset flags before regeneration
            try { _database.UpdateStoryGeneratedTtsJson(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedTts(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedMusic(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedEffects(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedAmbient(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedMixedAudio(storyId, false); } catch { }

            var schemaPath = Path.Combine(folderPath, "tts_schema.json");

            // If schema exists, parse it to find referenced files to delete
            if (File.Exists(schemaPath))
            {
                try
                {
                    var json = File.ReadAllText(schemaPath);
                    var root = JsonNode.Parse(json) as JsonObject;
                    if (root != null)
                    {
                        if (root.TryGetPropertyValue("timeline", out var timelineNode) || root.TryGetPropertyValue("Timeline", out timelineNode))
                        {
                            if (timelineNode is JsonArray arr)
                            {
                                foreach (var item in arr.OfType<JsonObject>())
                                {
                                    var fn = item.TryGetPropertyValue("fileName", out var f1) ? f1?.ToString() : null;
                                    if (string.IsNullOrWhiteSpace(fn)) fn = item.TryGetPropertyValue("FileName", out var f2) ? f2?.ToString() : fn;
                                    if (!string.IsNullOrWhiteSpace(fn)) DeleteIfExists(Path.Combine(folderPath, fn));

                                    var amb = item.TryGetPropertyValue("ambienceFile", out var a1) ? a1?.ToString() : null;
                                    if (!string.IsNullOrWhiteSpace(amb)) DeleteIfExists(Path.Combine(folderPath, amb));

                                    var fx = item.TryGetPropertyValue("fx_file", out var x1) ? x1?.ToString() : null;
                                    if (!string.IsNullOrWhiteSpace(fx)) DeleteIfExists(Path.Combine(folderPath, fx));

                                    var music = item.TryGetPropertyValue("musicFile", out var m1) ? m1?.ToString() : null;
                                    if (!string.IsNullOrWhiteSpace(music)) DeleteIfExists(Path.Combine(folderPath, music));
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Unable to parse existing tts_schema.json for cleanup in {Folder}", folderPath);
                }

                // delete the schema itself to ensure fresh generation
                DeleteIfExists(schemaPath);
            }

            // delete final mix files
            DeleteIfExists(Path.Combine(folderPath, "final_mix.mp3"));
            DeleteIfExists(Path.Combine(folderPath, "final_mix.wav"));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error during cleanup before TTS schema regeneration for story {Id}", storyId);
        }
    }

    // Clean only TTS audio files and remove timing/file refs for timeline entries.
    private void CleanBeforeTtsAudioGeneration(long storyId, string folderPath)
    {
        try
        {
            var schemaPath = Path.Combine(folderPath, "tts_schema.json");
            if (!File.Exists(schemaPath))
            {
                // Nothing to clean in schema, but still remove final mix and music/fx/ambience
                DeleteIfExists(Path.Combine(folderPath, "final_mix.mp3"));
                DeleteIfExists(Path.Combine(folderPath, "final_mix.wav"));
                try { _database.UpdateStoryGeneratedMixedAudio(storyId, false); } catch { }
                return;
            }

            var json = File.ReadAllText(schemaPath);
            var root = JsonNode.Parse(json) as JsonObject;
            if (root == null) return;

            if (root.TryGetPropertyValue("timeline", out var timelineNode) || root.TryGetPropertyValue("Timeline", out timelineNode))
            {
                if (timelineNode is JsonArray arr)
                {
                    foreach (var item in arr.OfType<JsonObject>())
                    {
                        // delete existing tts file if present
                        var fn = item.TryGetPropertyValue("fileName", out var f1) ? f1?.ToString() : null;
                        if (string.IsNullOrWhiteSpace(fn)) fn = item.TryGetPropertyValue("FileName", out var f2) ? f2?.ToString() : fn;
                        if (!string.IsNullOrWhiteSpace(fn)) DeleteIfExists(Path.Combine(folderPath, fn));

                        // remove timing and file fields so they will be recalculated
                        item.Remove("fileName");
                        item.Remove("FileName");
                        item.Remove("startMs");
                        item.Remove("StartMs");
                        item.Remove("durationMs");
                        item.Remove("DurationMs");

                        // also remove standardized snake_case ambience/fx/music references and their timing
                        item.Remove("ambient_sound_file");
                        item.Remove("ambient_sound_description");
                        item.Remove("fx_file");
                        item.Remove("fx_description");
                        item.Remove("fx_duration");
                        item.Remove("music_file");
                        item.Remove("music_duration");
                    }
                }
            }

            // Save modified schema
            try
            {
                SanitizeTtsSchemaTextFields(root);
                var updated = root.ToJsonString(SchemaJsonOptions);
                File.WriteAllText(schemaPath, updated);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Unable to save cleaned tts_schema.json for story {Id}", storyId);
            }

            // delete music/fx/ambience files and final mix
            // attempt to delete any file with known prefixes as well
            foreach (var pat in new[] { "music_", "music_auto_", "fx_", "ambience_", "ambi_", "final_mix" })
            {
                try
                {
                    var files = Directory.GetFiles(folderPath).Where(f => Path.GetFileName(f).StartsWith(pat, StringComparison.OrdinalIgnoreCase));
                    foreach (var f in files) DeleteIfExists(f);
                }
                catch { }
            }

            // update flags
            try { _database.UpdateStoryGeneratedTts(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedMusic(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedEffects(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedAmbient(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedMixedAudio(storyId, false); } catch { }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error during TTS audio cleanup for story {Id}", storyId);
        }
    }

    private void CleanMusicForRegeneration(long storyId, string folderPath)
    {
        try
        {
            var schemaPath = Path.Combine(folderPath, "tts_schema.json");
            if (File.Exists(schemaPath))
            {
                try
                {
                    var json = File.ReadAllText(schemaPath);
                    var root = JsonNode.Parse(json) as JsonObject;
                    if (root != null && (root.TryGetPropertyValue("timeline", out var timelineNode) || root.TryGetPropertyValue("Timeline", out timelineNode)))
                    {
                        if (timelineNode is JsonArray arr)
                        {
                            foreach (var item in arr.OfType<JsonObject>())
                            {
                                var music = item.TryGetPropertyValue("musicFile", out var m1) ? m1?.ToString() : null;
                                if (!string.IsNullOrWhiteSpace(music)) DeleteIfExists(Path.Combine(folderPath, music));

                                item.Remove("musicFile");
                                item.Remove("musicStartMs");
                                item.Remove("musicDuration");
                                item.Remove("musicDurationMs");
                            }
                        }
                    }

                    if (root != null)
                    {
                        SanitizeTtsSchemaTextFields(root);
                        File.WriteAllText(schemaPath, root.ToJsonString(SchemaJsonOptions));
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Unable to update schema when cleaning music for story {Id}", storyId);
                }
            }

            // delete final mix
            DeleteIfExists(Path.Combine(folderPath, "final_mix.mp3"));
            DeleteIfExists(Path.Combine(folderPath, "final_mix.wav"));

            // update flags
            try { _database.UpdateStoryGeneratedMusic(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedMixedAudio(storyId, false); } catch { }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error cleaning music for story {Id}", storyId);
        }
    }

    private void CleanFxForRegeneration(long storyId, string folderPath)
    {
        try
        {
            var schemaPath = Path.Combine(folderPath, "tts_schema.json");
            if (File.Exists(schemaPath))
            {
                try
                {
                    var json = File.ReadAllText(schemaPath);
                    var root = JsonNode.Parse(json) as JsonObject;
                    if (root != null && (root.TryGetPropertyValue("timeline", out var timelineNode) || root.TryGetPropertyValue("Timeline", out timelineNode)))
                    {
                        if (timelineNode is JsonArray arr)
                        {
                            foreach (var item in arr.OfType<JsonObject>())
                            {
                                var fx = item.TryGetPropertyValue("fx_file", out var f1) ? f1?.ToString() : null;
                                if (!string.IsNullOrWhiteSpace(fx)) DeleteIfExists(Path.Combine(folderPath, fx));

                                item.Remove("fx_file");
                            }
                        }
                    }

                    if (root != null)
                    {
                        SanitizeTtsSchemaTextFields(root);
                        File.WriteAllText(schemaPath, root.ToJsonString(SchemaJsonOptions));
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Unable to update schema when cleaning FX for story {Id}", storyId);
                }
            }

            // update flags
            try { _database.UpdateStoryGeneratedEffects(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedMixedAudio(storyId, false); } catch { }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error cleaning FX for story {Id}", storyId);
        }
    }

    private void CleanAmbienceForRegeneration(long storyId, string folderPath)
    {
        try
        {
            var schemaPath = Path.Combine(folderPath, "tts_schema.json");
            if (File.Exists(schemaPath))
            {
                try
                {
                    var json = File.ReadAllText(schemaPath);
                    var root = JsonNode.Parse(json) as JsonObject;
                    if (root != null && (root.TryGetPropertyValue("timeline", out var timelineNode) || root.TryGetPropertyValue("Timeline", out timelineNode)))
                    {
                        if (timelineNode is JsonArray arr)
                        {
                            foreach (var item in arr.OfType<JsonObject>())
                            {
                                // Clean old ambience_file references (legacy)
                                var amb = item.TryGetPropertyValue("ambienceFile", out var a1) ? a1?.ToString() : null;
                                if (!string.IsNullOrWhiteSpace(amb)) DeleteIfExists(Path.Combine(folderPath, amb));
                                amb = item.TryGetPropertyValue("ambience_file", out var a2) ? a2?.ToString() : null;
                                if (!string.IsNullOrWhiteSpace(amb)) DeleteIfExists(Path.Combine(folderPath, amb));
                                
                                // Clean new ambient_sound_file references (snake_case)
                                var asf = item.TryGetPropertyValue("ambient_sound_file", out var a3) ? a3?.ToString() : null;
                                if (!string.IsNullOrWhiteSpace(asf)) DeleteIfExists(Path.Combine(folderPath, asf));

                                item.Remove("ambienceFile");
                                item.Remove("AmbienceFile");
                                item.Remove("ambience_file");
                                item.Remove("ambient_sound_file");
                                item.Remove("ambientSoundFile");
                                item.Remove("AmbientSoundFile");
                            }
                        }
                    }

                    if (root != null)
                    {
                        SanitizeTtsSchemaTextFields(root);
                        File.WriteAllText(schemaPath, root.ToJsonString(SchemaJsonOptions));
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Unable to update schema when cleaning ambient sounds for story {Id}", storyId);
                }
            }

            // delete final mix
            DeleteIfExists(Path.Combine(folderPath, "final_mix.mp3"));
            DeleteIfExists(Path.Combine(folderPath, "final_mix.wav"));

            // update flags
            try { _database.UpdateStoryGeneratedAmbient(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedMixedAudio(storyId, false); } catch { }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error cleaning ambient sounds for story {Id}", storyId);
        }
    }

    private static readonly string[] FinalMixRootKeys = new[]
    {
        "finalMixFile",
        "finalMix",
        "finalMixMp3",
        "finalMixWav",
        "final_mix",
        "final_mix_file",
        "final_mix_mp3",
        "final_mix_wav",
        "audioMasterFile",
        "audio_master_file",
        "mixedAudioFile"
    };

    private static readonly string[] FinalMixTimelineKeys = new[]
    {
        "finalMixFile",
        "finalMix",
        "finalMixMp3",
        "finalMixWav",
        "mixFile",
        "mix_file",
        "audioMasterFile",
        "audio_master_file"
    };

    private (bool success, string? message) DeleteTtsSchemaAssets(long storyId, string folderPath)
    {
        try
        {
            CleanAllGeneratedAssetsForTtsSchemaRegeneration(storyId, folderPath);
            return (true, "Schema TTS cancellato");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Errore durante la cancellazione dello schema TTS per la storia {Id}", storyId);
            return (false, ex.Message);
        }
    }

    private bool RemoveFinalMixReferencesFromSchema(string folderPath)
    {
        var schemaPath = Path.Combine(folderPath, "tts_schema.json");
        if (!File.Exists(schemaPath))
            return false;

        try
        {
            var json = File.ReadAllText(schemaPath);
            var root = JsonNode.Parse(json) as JsonObject;
            if (root == null)
                return false;

            var modified = RemovePropertiesCaseInsensitive(root, FinalMixRootKeys);

            if (root.TryGetPropertyValue("timeline", out var timelineNode) || root.TryGetPropertyValue("Timeline", out timelineNode))
            {
                if (timelineNode is JsonArray arr)
                {
                    foreach (var item in arr.OfType<JsonObject>())
                    {
                        if (RemovePropertiesCaseInsensitive(item, FinalMixTimelineKeys))
                        {
                            modified = true;
                        }
                    }
                }
            }

            if (modified)
            {
                SanitizeTtsSchemaTextFields(root);
                File.WriteAllText(schemaPath, root.ToJsonString(SchemaJsonOptions));
            }

            return modified;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Unable to remove final mix references from schema for folder {Folder}", folderPath);
            return false;
        }
    }

    private static bool RemovePropertiesCaseInsensitive(JsonObject node, params string[] propertyNames)
    {
        if (node == null || propertyNames == null || propertyNames.Length == 0)
            return false;

        var targets = propertyNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (targets.Count == 0)
            return false;

        var keysToRemove = node
            .Where(kvp => targets.Any(target => string.Equals(kvp.Key, target, StringComparison.OrdinalIgnoreCase)))
            .Select(kvp => kvp.Key)
            .ToList();

        var removed = false;
        foreach (var key in keysToRemove)
        {
            removed |= node.Remove(key);
        }

        return removed;
    }

    private (bool success, string? message) DeleteFinalMixAssets(long storyId, string folderPath)
    {
        try
        {
            RemoveFinalMixReferencesFromSchema(folderPath);

            DeleteIfExists(Path.Combine(folderPath, "final_mix.mp3"));
            DeleteIfExists(Path.Combine(folderPath, "final_mix.wav"));

            try { _database.UpdateStoryGeneratedMixedAudio(storyId, false); } catch { }

            return (true, "Mix finale cancellato");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Errore durante la cancellazione del mix finale per la storia {Id}", storyId);
            return (false, ex.Message);
        }
    }

    private static string SanitizeFolderName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        foreach (var c in invalidChars)
        {
            name = name.Replace(c, '_');
        }
        return name.Trim().Replace(" ", "_").ToLowerInvariant();
    }

    /// <summary>
    /// Prefix all existing story folders with the story id (if not already prefixed),
    /// renaming directories on disk and updating the DB `Folder` field.
    /// Returns the number of folders renamed.
    /// </summary>
    public async Task<int> PrefixAllStoryFoldersWithIdAsync()
    {
        var baseFolder = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder");
        Directory.CreateDirectory(baseFolder);

        var stories = _database.GetAllStories() ?? new List<StoryRecord>();
        int updated = 0;

        foreach (var story in stories)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(story.Folder))
                    continue;

                var currentFolder = story.Folder;
                // Normalize various existing prefixes to the new 5-digit padded format.
                var idPrefixNumeric = $"{story.Id}_"; // e.g., "3_foo"
                var idPrefixStory = $"story_{story.Id}_"; // e.g., "story_3_foo"
                var idPrefixPadded = $"{story.Id.ToString("D5")}_"; // e.g., "00003_foo"

                string targetFolderName;

                if (currentFolder.StartsWith(idPrefixPadded, StringComparison.OrdinalIgnoreCase))
                {
                    // already in desired format
                    continue;
                }
                else if (currentFolder.StartsWith(idPrefixNumeric, StringComparison.OrdinalIgnoreCase))
                {
                    // strip numeric prefix and re-prefix with padded id
                    var rest = currentFolder.Substring(idPrefixNumeric.Length);
                    targetFolderName = SanitizeFolderName($"{story.Id.ToString("D5")}_{rest}");
                }
                else if (currentFolder.StartsWith(idPrefixStory, StringComparison.OrdinalIgnoreCase))
                {
                    var rest = currentFolder.Substring(idPrefixStory.Length);
                    targetFolderName = SanitizeFolderName($"{story.Id.ToString("D5")}_{rest}");
                }
                else
                {
                    // no recognized prefix, just add padded id before existing name
                    targetFolderName = SanitizeFolderName($"{story.Id.ToString("D5")}_{currentFolder}");
                }

                var oldPath = Path.Combine(baseFolder, currentFolder);
                if (!Directory.Exists(oldPath))
                {
                    _database.UpdateStoryFolder(story.Id, targetFolderName);
                    updated++;
                    continue;
                }

                var newPath = Path.Combine(baseFolder, targetFolderName);

                // If target exists, append a suffix to avoid conflicts
                if (Directory.Exists(newPath))
                {
                    newPath = Path.Combine(baseFolder, SanitizeFolderName($"{targetFolderName}_{DateTime.UtcNow:yyyyMMddHHmmss}"));
                }

                Directory.Move(oldPath, newPath);
                _database.UpdateStoryFolder(story.Id, Path.GetFileName(newPath));
                updated++;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to rename folder for story {Id}", story.Id);
            }
        }

        await Task.CompletedTask;
        return updated;
    }

    private static IEnumerable<string>? ParseAgentSkills(Agent agent)
    {
        if (agent == null || string.IsNullOrWhiteSpace(agent.Skills))
            return null;

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(agent.Skills);
            if (parsed == null || parsed.Count == 0)
                return null;
            return parsed.Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s));
        }
        catch
        {
            return null;
        }
    }

    private string? ComposeSystemMessage(Agent agent)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(agent.Prompt))
            parts.Add(agent.Prompt);

        if (!string.IsNullOrWhiteSpace(agent.ExecutionPlan))
        {
            var plan = LoadExecutionPlan(agent.ExecutionPlan);
            if (!string.IsNullOrWhiteSpace(plan))
                parts.Add(plan);
        }

        if (!string.IsNullOrWhiteSpace(agent.Instructions))
            parts.Add(agent.Instructions);

        if (parts.Count == 0)
        {
            return "You are a TTS schema generator. Use only the available tools to build the narrator and characters timeline and confirm at the end.";
        }

        return string.Join("\n\n", parts);
    }

    private string BuildTtsJsonPrompt(StoryRecord story)
    {
        // For the TTS JSON generator we now do NOT pass the story text inline.
        // Instead, the model must use the read_story_part function to retrieve segments.
        // This allows the model to process large stories in chunks and reduces token overhead.
        var builder = new StringBuilder();
        builder.AppendLine("You are generating a TTS schema (timeline of narration and character dialogue) for a story.");
        builder.AppendLine("Follow the agent instructions. Use read_story_part to fetch the story (part_index from 0 upward until is_last=true) and build the schema with add_narration / add_phrase, then call confirm once finished.");
        builder.AppendLine("Reproduce the text faithfully and assign each sentence to narration or a character; do not invent content.");
        return builder.ToString();
    }

    private string BuildStoryEvaluationPrompt(StoryRecord story, bool isCoherenceEvaluation = false)
    {
        var builder = new StringBuilder();
        
        if (isCoherenceEvaluation)
        {
            // Prompt for chunk-based coherence evaluation
            builder.AppendLine("You are analyzing a story's coherence using chunk-based fact extraction and comparison.");
            builder.AppendLine("You must NOT copy the full story text into this prompt.");
            builder.AppendLine("Follow this workflow for each chunk:");
            builder.AppendLine("1. Use read_story_part(part_index=N) to read the chunk (start with 0)");
            builder.AppendLine("2. Extract objective facts and save with extract_chunk_facts");
            builder.AppendLine("3. Retrieve previous facts with get_chunk_facts and get_all_previous_facts");
            builder.AppendLine("4. Compare facts and calculate coherence scores with calculate_coherence");
            builder.AppendLine("5. Continue until you receive \"is_last\": true");
            builder.AppendLine("When all chunks are processed, call finalize_global_coherence to save the final score.");
            builder.AppendLine("Be thorough and document all inconsistencies found.");
        }
        else
        {
            // Prompt for standard evaluation with EvaluatorTool
            builder.AppendLine("You are evaluating a story and must record your judgement using the available tools.");
            builder.AppendLine("You must NOT copy the full story text into this prompt.");
            builder.AppendLine("Instead, use the read_story_part function to retrieve segments of the story (start with part_index=0 and continue requesting parts until you receive \"is_last\": true).");
            builder.AppendLine("Do not invent story content; rely only on the chunks returned by read_story_part.");
            builder.AppendLine("After you have reviewed the necessary sections, call the evaluate_full_story function exactly once. The story_id is managed internally; do not pass it.");
            builder.AppendLine("If you finish your review but fail to call evaluate_full_story, the orchestrator will remind you and ask again up to 3 times - you MUST call the function before the evaluation completes.");
            builder.AppendLine("Populate the following scores (0-10): narrative_coherence_score, originality_score, emotional_impact_score, action_score.");
            builder.AppendLine("All score fields MUST be integers between 0 and 10 (use 0 if you cannot determine a score). Do NOT send strings like \"None\".");
            builder.AppendLine("Also include the corresponding *_defects values (empty string or \"None\" is acceptable if there are no defects).");
            builder.AppendLine("Do not return an overall evaluation text - the system will compute the aggregate score automatically.");
            builder.AppendLine("Ensure every score and defect field is present in the final tool call, even if the defect description is empty.");
        }
        
        return builder.ToString();
    }

    private string BuildActionPacingPrompt(StoryRecord story)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are evaluating ACTION and PACING of a story chunk-by-chunk using the available tools.");
        builder.AppendLine("Workflow:");
        builder.AppendLine("1) Read chunk with read_story_part(part_index=N) starting from 0 and increment until is_last=true (do NOT invent chunks).");
        builder.AppendLine("2) Extract action/pacing profile with extract_action_profile(chunk_number=N, chunk_text=returned content).");
        builder.AppendLine("3) Compute scores (0.0-1.0):");
        builder.AppendLine("   - action_score: quantity/quality of action");
        builder.AppendLine("   - pacing_score: narrative rhythm of the chunk");
        builder.AppendLine("4) Record scores with calculate_action_pacing(chunk_number=N, action_score=..., pacing_score=..., notes=\"short notes\").");
        builder.AppendLine("5) Repeat until you receive is_last=true.");
        builder.AppendLine("6) Finalize with finalize_global_pacing(pacing_score=..., notes=\"brief summary\").");
        builder.AppendLine();
        builder.AppendLine("Important:");
        builder.AppendLine("- Always use the tools; do not answer in free text unless summarizing.");
        builder.AppendLine("- Do not skip finalize_global_pacing at the end.");
        builder.AppendLine();
        builder.AppendLine("Story metadata:");
        builder.AppendLine($"ID: {story.Id}");
        builder.AppendLine($"Prompt: {story.Prompt}");
        return builder.ToString();
    }

    private string? LoadExecutionPlan(string? planName)
    {
        if (string.IsNullOrWhiteSpace(planName))
            return null;

        try
        {
            var planPath = Path.Combine(Directory.GetCurrentDirectory(), "execution_plans", planName);
            if (File.Exists(planPath))
            {
                return File.ReadAllText(planPath);
            }
        }
        catch { }

        return null;
    }

    private IStoryCommand? CreateCommandForStatus(StoryStatus status)
    {
        if (status == null || string.IsNullOrWhiteSpace(status.OperationType))
            return null;

        var opType = status.OperationType.ToLowerInvariant();
        switch (opType)
        {
            case "agent_call":
                var agentType = status.AgentType?.ToLowerInvariant();
                return agentType switch
                {
                    "tts_json" or "tts" => new GenerateTtsSchemaCommand(this),
                    "tts_voice" or "voice" => new AssignVoicesCommand(this),
                    "evaluator" or "story_evaluator" or "writer_evaluator" => new EvaluateStoryCommand(this),
                    "revisor" => new ReviseStoryCommand(this),
                    "formatter" => new TagStoryCommand(this),
                    _ => new NotImplementedCommand($"agent_call:{agentType ?? "unknown"}")
                };
            case "function_call":
                var functionName = status.FunctionName?.ToLowerInvariant();
                return functionName switch
                {
                    "evaluate_story" => new EvaluateStoryCommand(this),
                    "generate_tts_audio" or "tts_audio" or "build_tts_audio" or "generate_voice_tts" or "generate_voices" => new GenerateTtsAudioCommand(this),
                    "generate_ambience_audio" or "ambience_audio" or "generate_ambient" or "ambient_sounds" => new GenerateAmbienceAudioCommand(this),
                    "generate_fx_audio" or "fx_audio" or "generate_fx" or "sound_effects" => new GenerateFxAudioCommand(this),
                    "generate_music" or "music_audio" or "generate_music_audio" => new GenerateMusicCommand(this),
                    "generate_audio_master" or "audio_master" or "mix_audio" or "mix_final" or "final_mix" => new MixFinalAudioCommand(this),
                    "assign_voices" or "voice_assignment" => new AssignVoicesCommand(this),
                    "prepare_tts_schema" => new PrepareTtsSchemaCommand(this),
                    _ => new FunctionCallCommand(this, status)
                };
            default:
                return null;
        }
    }

    private interface IStoryCommand
    {
        bool RequireStoryText { get; }
        bool EnsureFolder { get; }
        bool HandlesStatusTransition { get; }
        Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context);
    }

    private sealed record StoryCommandContext(StoryRecord Story, string FolderPath, StoryStatus? TargetStatus);

    private sealed class GenerateTtsSchemaCommand : IStoryCommand
    {
        private readonly StoriesService _service;

        public GenerateTtsSchemaCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public async Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            var story = context.Story;
            var folderPath = context.FolderPath;

            if (string.IsNullOrWhiteSpace(story.StoryTagged))
            {
                return (false, "Il testo taggato della storia e vuoto");
            }

            var deleteCmd = new DeleteTtsSchemaCommand(_service);
            var (cleanupOk, cleanupMessage) = await deleteCmd.ExecuteAsync(context);
            if (!cleanupOk)
            {
                return (false, cleanupMessage ?? "Impossibile cancellare il precedente schema TTS");
            }

            try
            {
                var generator = new TtsSchemaGenerator(_service._customLogger, _service._database);
                var schema = generator.GenerateFromStoryText(story.StoryTagged);

                if (schema.Timeline.Count == 0)
                {
                    return (false, "Nessuna frase trovata nel testo. Assicurati che il testo contenga tag come [NARRATORE], [personaggio, emozione] o [PERSONAGGIO: Nome] [EMOZIONE: emozione].");
                }

                generator.AssignVoices(schema);

                var schemaPath = Path.Combine(folderPath, "tts_schema.json");
                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var json = JsonSerializer.Serialize(schema, jsonOptions);
                _service.SaveSanitizedTtsSchemaJson(schemaPath, json);

                // New requirement: resolve MUSIC blocks to actual file names immediately during schema generation.
                // This picks files from series_folder/{series.folder}/music or data/music_stories and writes music_file/musicFile.
                try
                {
                    var existingJson = await File.ReadAllTextAsync(schemaPath);
                    var rootNode = JsonNode.Parse(existingJson) as JsonObject;
                    if (rootNode != null)
                    {
                        _service.AssignMusicFilesFromLibrary(rootNode, story, folderPath);
                        SanitizeTtsSchemaTextFields(rootNode);
                        await File.WriteAllTextAsync(schemaPath, rootNode.ToJsonString(SchemaJsonOptions));
                    }
                }
                catch (Exception exMusic)
                {
                    _service._logger?.LogWarning(exMusic, "Unable to assign music files during tts_schema generation for story {Id}", story.Id);
                }

                try { _service._database.UpdateStoryGeneratedTtsJson(story.Id, true); } catch { }

                _service._database.UpdateStoryById(story.Id, statusId: TtsSchemaReadyStatusId, updateStatus: true);

                _service._logger?.LogInformation(
                    "TTS schema generato per storia {StoryId}: {Characters} personaggi, {Phrases} frasi",
                    story.Id, schema.Characters.Count, schema.Timeline.Count);

                // Requirement: if tts_schema.json generation succeeds, enqueue TTS audio generation with lower priority.
                // This is best-effort and never blocks the schema command.
                try
                {
                    _service.EnqueueGenerateTtsAudioCommand(story.Id, trigger: "tts_schema_generated", priority: 3);
                }
                catch
                {
                    // ignore
                }

                return (true, $"Schema TTS generato: {schema.Characters.Count} personaggi, {schema.Timeline.Count} frasi");
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante la generazione del TTS schema per la storia {Id}", story.Id);
                return (false, ex.Message);
            }
        }
    }

    private void AssignMusicFilesFromLibrary(JsonObject rootNode, StoryRecord story, string folderPath)
    {
        if (rootNode == null) return;
        if (story == null) return;
        if (string.IsNullOrWhiteSpace(folderPath)) return;

        // Get timeline
        if (!rootNode.TryGetPropertyValue("timeline", out var timelineNode))
            rootNode.TryGetPropertyValue("Timeline", out timelineNode);
        if (timelineNode is not JsonArray timelineArray)
            return;

        var phraseEntries = timelineArray.OfType<JsonObject>().Where(IsPhraseEntry).ToList();
        if (phraseEntries.Count == 0) return;

        var musicLibraryFolder = GetMusicLibraryFolderForStory(story);
        if (string.IsNullOrWhiteSpace(musicLibraryFolder) || !Directory.Exists(musicLibraryFolder))
            return;

        var libraryFiles = Directory
            .GetFiles(musicLibraryFolder)
            .Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (libraryFiles.Count == 0) return;

        var index = BuildMusicLibraryIndex(libraryFiles);
        var usedDestFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in phraseEntries)
        {
            var existing = ReadString(entry, "music_file") ?? ReadString(entry, "musicFile") ?? ReadString(entry, "MusicFile");
            if (!string.IsNullOrWhiteSpace(existing)) usedDestFileNames.Add(existing);
        }

        for (var i = 0; i < phraseEntries.Count; i++)
        {
            var entry = phraseEntries[i];
            var musicDesc = ReadString(entry, "music_description") ?? ReadString(entry, "musicDescription") ?? ReadString(entry, "MusicDescription");
            if (string.IsNullOrWhiteSpace(musicDesc))
                continue;

            if (string.Equals(musicDesc.Trim(), "silence", StringComparison.OrdinalIgnoreCase))
            {
                entry["music_file"] = null;
                entry["musicFile"] = null;
                continue;
            }

            var currentMusicFile = ReadString(entry, "music_file") ?? ReadString(entry, "musicFile") ?? ReadString(entry, "MusicFile");
            if (!string.IsNullOrWhiteSpace(currentMusicFile) && File.Exists(Path.Combine(folderPath, currentMusicFile)))
                continue;

            var requestedType = InferMusicTypeFromDescription(musicDesc, index.Keys);
            if (string.Equals(requestedType, "silence", StringComparison.OrdinalIgnoreCase))
            {
                entry["music_file"] = null;
                entry["musicFile"] = null;
                continue;
            }

            var selected = SelectMusicFileDeterministic(index, requestedType, usedDestFileNames, seedA: story.Id, seedB: i);
            if (string.IsNullOrWhiteSpace(selected) || !File.Exists(selected))
                continue;

            var destFileName = Path.GetFileName(selected);
            if (string.IsNullOrWhiteSpace(destFileName))
                continue;

            var destPath = Path.Combine(folderPath, destFileName);
            try
            {
                if (!File.Exists(destPath))
                    File.Copy(selected, destPath, overwrite: false);
            }
            catch
            {
                continue;
            }

            entry["music_file"] = destFileName;
            entry["musicFile"] = destFileName;
            usedDestFileNames.Add(destFileName);
        }
    }

    /// <summary>
    /// Command to normalize character names in tts_schema.json using the story's character list.
    /// </summary>
    private sealed class NormalizeCharacterNamesCommand : IStoryCommand
    {
        private readonly StoriesService _service;

        public NormalizeCharacterNamesCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            var story = context.Story;
            var folderPath = context.FolderPath;

            // Check if story has character data
            if (string.IsNullOrWhiteSpace(story.Characters))
            {
                return Task.FromResult<(bool, string?)>((false, "La storia non ha una lista di personaggi definita nel campo Characters. Inseriscila dalla pagina Modifica storia."));
            }

            // Load character list from story with detailed error
            var (storyCharacters, parseError) = StoryCharacterParser.TryFromJson(story.Characters);
            if (parseError != null)
            {
                return Task.FromResult<(bool, string?)>((false, $"Errore nel parsing della lista personaggi: {parseError}"));
            }
            if (storyCharacters.Count == 0)
            {
                return Task.FromResult<(bool, string?)>((false, $"La lista personaggi della storia è vuota o non valida. JSON attuale: {story.Characters.Substring(0, Math.Min(200, story.Characters.Length))}..."));
            }

            // Check if tts_schema.json exists
            var schemaPath = Path.Combine(folderPath, "tts_schema.json");
            if (!File.Exists(schemaPath))
            {
                return Task.FromResult<(bool, string?)>((false, "File tts_schema.json non trovato. Genera prima lo schema TTS."));
            }

            try
            {
                // Load existing schema
                var jsonContent = File.ReadAllText(schemaPath);
                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                var schema = JsonSerializer.Deserialize<TtsSchema>(jsonContent, jsonOptions);

                if (schema == null)
                {
                    return Task.FromResult<(bool, string?)>((false, "Impossibile deserializzare tts_schema.json"));
                }

                var normalizedCount = 0;
                var characterMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // Build mapping from old names to canonical names
                foreach (var ttsChar in schema.Characters)
                {
                    var matched = StoryCharacterParser.FindCharacter(storyCharacters, ttsChar.Name);
                    if (matched != null && !ttsChar.Name.Equals(matched.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        characterMapping[ttsChar.Name] = matched.Name;
                        _service._customLogger?.Log("Information", "NormalizeNames",
                            $"Mapping '{ttsChar.Name}' -> '{matched.Name}'");
                    }
                }

                if (characterMapping.Count == 0)
                {
                    return Task.FromResult<(bool, string?)>((true, "Nessuna normalizzazione necessaria: tutti i nomi sono già canonici."));
                }

                // Update character names in the Characters list
                var newCharacters = new List<TtsCharacter>();
                var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var ttsChar in schema.Characters)
                {
                    var canonicalName = characterMapping.TryGetValue(ttsChar.Name, out var mapped) ? mapped : ttsChar.Name;

                    // Skip duplicates that would result from merging
                    if (seenNames.Contains(canonicalName))
                    {
                        _service._customLogger?.Log("Debug", "NormalizeNames",
                            $"Skipping duplicate character '{ttsChar.Name}' (merged into '{canonicalName}')");
                        continue;
                    }

                    seenNames.Add(canonicalName);

                    // Update gender from story character if available
                    var storyChar = StoryCharacterParser.FindCharacter(storyCharacters, canonicalName);
                    var updatedChar = new TtsCharacter
                    {
                        Name = canonicalName,
                        VoiceId = ttsChar.VoiceId,
                        EmotionDefault = ttsChar.EmotionDefault,
                        Gender = storyChar?.Gender ?? ttsChar.Gender
                    };

                    newCharacters.Add(updatedChar);
                    if (!ttsChar.Name.Equals(canonicalName, StringComparison.Ordinal))
                    {
                        normalizedCount++;
                    }
                }

                schema.Characters = newCharacters;

                // Update character references in timeline
                foreach (var item in schema.Timeline)
                {
                    if (item is JsonElement jsonElement)
                    {
                        // Timeline items are JsonElements, need special handling
                        // This will be handled when we re-serialize
                    }
                    else if (item is TtsPhrase phrase)
                    {
                        if (characterMapping.TryGetValue(phrase.Character, out var newName))
                        {
                            phrase.Character = newName;
                        }
                    }
                }

                // Re-process timeline to normalize character names in phrases
                var updatedTimeline = new List<object>();
                foreach (var item in schema.Timeline)
                {
                    if (item is JsonElement jsonElement)
                    {
                        var phrase = JsonSerializer.Deserialize<TtsPhrase>(jsonElement.GetRawText(), jsonOptions);
                        if (phrase != null)
                        {
                            if (characterMapping.TryGetValue(phrase.Character, out var newCharName))
                            {
                                phrase.Character = newCharName;
                            }
                            updatedTimeline.Add(phrase);
                        }
                    }
                    else if (item is TtsPhrase phrase)
                    {
                        if (characterMapping.TryGetValue(phrase.Character, out var newCharName))
                        {
                            phrase.Character = newCharName;
                        }
                        updatedTimeline.Add(phrase);
                    }
                    else
                    {
                        updatedTimeline.Add(item);
                    }
                }
                schema.Timeline = updatedTimeline;

                // Save the updated schema
                var outputOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var updatedJson = JsonSerializer.Serialize(schema, outputOptions);
                _service.SaveSanitizedTtsSchemaJson(schemaPath, updatedJson);

                _service._logger?.LogInformation(
                    "Normalized {Count} character names in tts_schema.json for story {StoryId}",
                    normalizedCount, story.Id);

                return Task.FromResult<(bool, string?)>((true,
                    $"Normalizzati {normalizedCount} nomi personaggi. Schema aggiornato con {schema.Characters.Count} personaggi."));
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante la normalizzazione dei nomi per la storia {Id}", story.Id);
                return Task.FromResult<(bool, string?)>((false, ex.Message));
            }
        }
    }

    /// <summary>
    /// Command that normalizes emotions/sentiments in tts_schema.json to TTS-supported values.
    /// Supported sentiments: neutral, happy, sad, angry, fearful, disgusted, surprised
    /// </summary>
    private sealed class NormalizeSentimentsCommand : IStoryCommand
    {
        private readonly StoriesService _service;

        public NormalizeSentimentsCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public async Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            try
            {
                var story = context.Story;
                var folderPath = context.FolderPath;

                var schemaPath = Path.Combine(folderPath, "tts_schema.json");
                if (!File.Exists(schemaPath))
                    return (false, "File tts_schema.json non trovato. Genera prima lo schema TTS.");

                // Load TTS schema
                var schemaJson = await File.ReadAllTextAsync(schemaPath);
                var schema = JsonSerializer.Deserialize<TtsSchema>(schemaJson, SchemaJsonOptions);
                if (schema == null)
                    return (false, "Impossibile deserializzare tts_schema.json");

                if (schema.Timeline == null || schema.Timeline.Count == 0)
                    return (false, "Schema TTS vuoto o senza timeline.");

                // Get or create SentimentMappingService
                if (_service._sentimentMappingService == null)
                    return (false, "SentimentMappingService non disponibile");

                // Normalize sentiments
                var (normalized, total) = await _service._sentimentMappingService.NormalizeTtsSchemaAsync(schema);

                // Save updated schema
                var outputOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var updatedJson = JsonSerializer.Serialize(schema, outputOptions);
                await _service.SaveSanitizedTtsSchemaJsonAsync(schemaPath, updatedJson);

                _service._logger?.LogInformation(
                    "Normalized {Normalized}/{Total} sentiments in tts_schema.json for story {StoryId}",
                    normalized, total, story.Id);

                return (true, $"Normalizzati {normalized} sentimenti su {total} frasi.");
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante la normalizzazione dei sentimenti per la storia {Id}", context.Story.Id);
                return (false, ex.Message);
            }
        }
    }

    /// <summary>
    /// Command that assigns TTS voices to characters in a story's tts_schema.json.
    /// Narrator gets a random voice with archetype "narratore".
    /// Characters get voices matching gender and closest age, preferring higher scores.
    /// Each character must have a distinct voice.
    /// </summary>
    private sealed class AssignVoicesCommand : IStoryCommand
    {
        private readonly StoriesService _service;

        public AssignVoicesCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false; // Only needs Characters JSON
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            try
            {
                var story = context.Story;
                var folderPath = context.FolderPath;

                var schemaPath = Path.Combine(folderPath, "tts_schema.json");
                if (!File.Exists(schemaPath))
                    return Task.FromResult<(bool, string?)>((false, "File tts_schema.json mancante: genera prima lo schema TTS"));

                // Load TTS schema
                var schemaJson = File.ReadAllText(schemaPath);
                var schema = JsonSerializer.Deserialize<TtsSchema>(schemaJson, SchemaJsonOptions);
                if (schema?.Characters == null || schema.Characters.Count == 0)
                    return Task.FromResult<(bool, string?)>((false, "Nessun personaggio definito nello schema TTS"));

                // Load available voices from database (only enabled voices for assignment)
                var allVoices = _service._database.ListTtsVoices(onlyEnabled: true);
                if (allVoices == null || allVoices.Count == 0)
                    return Task.FromResult<(bool, string?)>((false, "Nessuna voce disponibile nella tabella tts_voices"));

                // Load story characters for age/gender info
                var storyCharacters = new List<StoryCharacter>();
                if (!string.IsNullOrWhiteSpace(story.Characters))
                {
                    try
                    {
                        storyCharacters = StoryCharacterParser.FromJson(story.Characters);
                    }
                    catch
                    {
                        // Best effort - proceed without character metadata
                    }
                }

                // Track used voice IDs to ensure uniqueness
                var usedVoiceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int assignedCount = 0;

                // 1. Assign narrator voice first (random from archetype "narratore")
                var narrator = schema.Characters.FirstOrDefault(c => 
                    string.Equals(c.Name, "Narratore", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Name, "Narrator", StringComparison.OrdinalIgnoreCase));
                
                if (narrator != null)
                {
                    var narratorVoices = allVoices
                        .Where(v => !string.IsNullOrWhiteSpace(v.Archetype) && 
                                   v.Archetype.Equals("narratore", StringComparison.OrdinalIgnoreCase) &&
                                   !string.IsNullOrWhiteSpace(v.VoiceId))
                        .ToList();

                    if (narratorVoices.Count > 0)
                    {
                        var selectedNarratorVoice = narratorVoices[Random.Shared.Next(narratorVoices.Count)];
                        narrator.VoiceId = selectedNarratorVoice.VoiceId;
                        narrator.Voice = selectedNarratorVoice.Name;
                        narrator.Gender = selectedNarratorVoice.Gender ?? "";
                        usedVoiceIds.Add(selectedNarratorVoice.VoiceId);
                        assignedCount++;
                    }
                    else
                    {
                        // Fallback: pick any male voice with highest score
                        var fallbackVoice = PickBestAvailableVoice("male", null, allVoices, usedVoiceIds);
                        if (fallbackVoice != null)
                        {
                            narrator.VoiceId = fallbackVoice.VoiceId;
                            narrator.Voice = fallbackVoice.Name;
                            narrator.Gender = fallbackVoice.Gender ?? "";
                            usedVoiceIds.Add(fallbackVoice.VoiceId);
                            assignedCount++;
                        }
                    }
                }

                // 2. Assign voices to other characters
                foreach (var character in schema.Characters)
                {
                    // Skip narrator (already assigned) and characters with existing voice
                    if (string.Equals(character.Name, "Narratore", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(character.Name, "Narrator", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!string.IsNullOrWhiteSpace(character.VoiceId))
                    {
                        usedVoiceIds.Add(character.VoiceId);
                        continue;
                    }

                    // Find matching story character for age/gender info
                    var storyChar = storyCharacters.FirstOrDefault(sc => 
                        string.Equals(sc.Name, character.Name, StringComparison.OrdinalIgnoreCase) ||
                        (sc.Aliases?.Any(a => string.Equals(a, character.Name, StringComparison.OrdinalIgnoreCase)) == true));
                    
                    // Determine gender (from story character or TTS character)
                    var gender = storyChar?.Gender ?? character.Gender;
                    if (string.IsNullOrWhiteSpace(gender))
                        gender = "male"; // Default fallback

                    // Determine age (from story character)
                    var age = storyChar?.Age;

                    // Pick best voice matching criteria
                    var selectedVoice = PickBestAvailableVoice(gender, age, allVoices, usedVoiceIds);
                    if (selectedVoice != null)
                    {
                        character.VoiceId = selectedVoice.VoiceId;
                        character.Voice = selectedVoice.Name;
                        if (string.IsNullOrWhiteSpace(character.Gender))
                            character.Gender = selectedVoice.Gender ?? "";
                        usedVoiceIds.Add(selectedVoice.VoiceId);
                        assignedCount++;
                    }
                    else
                    {
                        _service._logger?.LogWarning(
                            "No available voice for character {Name} (gender={Gender}, age={Age})", 
                            character.Name, gender, age ?? "unknown");
                    }
                }

                // Validate: all characters must have a voice
                var missingVoices = schema.Characters
                    .Where(c => string.IsNullOrWhiteSpace(c.VoiceId))
                    .Select(c => c.Name ?? "<senza nome>")
                    .ToList();

                if (missingVoices.Any())
                {
                    return Task.FromResult<(bool, string?)>((false, 
                        $"Non è stato possibile assegnare voci a: {string.Join(", ", missingVoices)}. " +
                        $"Aggiungi altre voci nella tabella tts_voices."));
                }

                // Check for duplicate voices
                var duplicates = schema.Characters
                    .Where(c => !string.IsNullOrWhiteSpace(c.VoiceId))
                    .GroupBy(c => c.VoiceId!, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => $"{g.Key}: {string.Join(", ", g.Select(c => c.Name ?? "?"))}")
                    .ToList();

                if (duplicates.Any())
                {
                    _service._logger?.LogWarning("Duplicate voices assigned: {Duplicates}", string.Join("; ", duplicates));
                }

                // Save updated schema
                var outputOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var updatedJson = JsonSerializer.Serialize(schema, outputOptions);
                _service.SaveSanitizedTtsSchemaJson(schemaPath, updatedJson);

                _service._logger?.LogInformation(
                    "Assigned {Count} voices to characters in story {StoryId}", 
                    assignedCount, story.Id);

                return Task.FromResult<(bool, string?)>((true, 
                    $"Assegnate {assignedCount} voci a {schema.Characters.Count} personaggi."));
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante l'assegnazione voci per la storia {Id}", context.Story.Id);
                return Task.FromResult<(bool, string?)>((false, ex.Message));
            }
        }

        /// <summary>
        /// Picks the best available voice matching gender and age criteria.
        /// Priority: same gender > closest age > highest score
        /// </summary>
        private static TinyGenerator.Models.TtsVoice? PickBestAvailableVoice(
            string gender, 
            string? targetAge, 
            List<TinyGenerator.Models.TtsVoice> allVoices, 
            HashSet<string> usedVoiceIds)
        {
            // Filter by gender and exclude already used voices
            var candidates = allVoices
                .Where(v => !string.IsNullOrWhiteSpace(v.VoiceId) &&
                           !usedVoiceIds.Contains(v.VoiceId) &&
                           string.Equals(v.Gender, gender, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // If no candidates of same gender, try all unused voices
            if (candidates.Count == 0)
            {
                candidates = allVoices
                    .Where(v => !string.IsNullOrWhiteSpace(v.VoiceId) &&
                               !usedVoiceIds.Contains(v.VoiceId))
                    .ToList();
            }

            if (candidates.Count == 0)
                return null;

            // Parse target age to numeric if possible
            int? targetAgeNum = ParseAgeToNumber(targetAge);

            // Score each candidate
            var scoredCandidates = candidates
                .Select(v => new
                {
                    Voice = v,
                    Score = v.Score ?? 0,
                    AgeDiff = CalculateAgeDifference(v.Age, targetAgeNum)
                })
                .OrderBy(x => x.AgeDiff)        // Closest age first
                .ThenByDescending(x => x.Score) // Then highest score
                .ToList();

            return scoredCandidates.First().Voice;
        }

        /// <summary>
        /// Parses age string to a numeric value. Handles numeric ages and descriptive ages.
        /// </summary>
        private static int? ParseAgeToNumber(string? age)
        {
            if (string.IsNullOrWhiteSpace(age))
                return null;

            // Try direct numeric parse
            if (int.TryParse(age, out int numericAge))
                return numericAge;

            // Handle descriptive ages
            var ageLower = age.ToLowerInvariant();
            return ageLower switch
            {
                "bambino" or "child" or "kid" => 8,
                "ragazzo" or "giovane" or "young" or "teen" or "teenager" => 18,
                "adulto" or "adult" => 35,
                "mezza età" or "middle-aged" or "middle aged" => 50,
                "anziano" or "elderly" or "old" => 70,
                _ => null
            };
        }

        /// <summary>
        /// Calculates age difference. Returns 0 if no comparison possible.
        /// </summary>
        private static int CalculateAgeDifference(string? voiceAge, int? targetAge)
        {
            if (!targetAge.HasValue)
                return 0; // No preference, treat as equal

            var voiceAgeNum = ParseAgeToNumber(voiceAge);
            if (!voiceAgeNum.HasValue)
                return 100; // Penalize voices without age info

            return Math.Abs(voiceAgeNum.Value - targetAge.Value);
        }
    }

    private async Task<bool> ApplyVoiceAssignmentFallbacksAsync(string schemaPath)
    {
        try
        {
            if (!File.Exists(schemaPath)) return false;
            var content = await File.ReadAllTextAsync(schemaPath);
            var schema = JsonSerializer.Deserialize<TtsSchema>(content, SchemaJsonOptions);
            if (schema?.Characters == null || schema.Characters.Count == 0)
                return false;

            var catalogVoices = _database.ListTtsVoices(onlyEnabled: true);
            if (catalogVoices == null || catalogVoices.Count == 0)
                return false;

            var updated = false;
            var usedVoiceIds = new HashSet<string>(
                schema.Characters
                    .Where(c => !string.IsNullOrWhiteSpace(c.VoiceId))
                    .Select(c => c.VoiceId!),
                StringComparer.OrdinalIgnoreCase);

            var narrator = schema.Characters.FirstOrDefault(c => string.Equals(c.Name, "narratore", StringComparison.OrdinalIgnoreCase));
            if (narrator != null && string.IsNullOrWhiteSpace(narrator.VoiceId))
            {
                var narratorVoice = FindVoiceByNameOrId(catalogVoices, NarratorFallbackVoiceName);
                if (narratorVoice != null)
                {
                    ApplyVoiceToCharacter(narrator, narratorVoice);
                    usedVoiceIds.Add(narratorVoice.VoiceId);
                    updated = true;
                }
                else
                {
                    _logger?.LogWarning("Voce di fallback {VoiceName} non trovata nella tabella tts_voices", NarratorFallbackVoiceName);
                }
            }

            foreach (var character in schema.Characters)
            {
                if (!string.IsNullOrWhiteSpace(character.VoiceId))
                    continue;
                if (string.IsNullOrWhiteSpace(character.Gender))
                    continue;

                var fallbackVoice = PickVoiceForGender(character.Gender, catalogVoices, usedVoiceIds);
                if (fallbackVoice != null)
                {
                    ApplyVoiceToCharacter(character, fallbackVoice);
                    usedVoiceIds.Add(fallbackVoice.VoiceId);
                    updated = true;
                }
            }

            if (updated)
            {
                var normalized = JsonSerializer.Serialize(schema, SchemaJsonOptions);
                await SaveSanitizedTtsSchemaJsonAsync(schemaPath, normalized);
            }

            return updated;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Errore durante ApplyVoiceAssignmentFallbacksAsync per schema {SchemaPath}", schemaPath);
            return false;
        }
    }

    private sealed class DeleteTtsSchemaCommand : IStoryCommand
    {
        private readonly StoriesService _service;

        public DeleteTtsSchemaCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            return Task.FromResult<(bool success, string? message)>(_service.DeleteTtsSchemaAssets(context.Story.Id, context.FolderPath));
        }
    }

    private sealed class DeleteFinalMixCommand : IStoryCommand
    {
        private readonly StoriesService _service;

        public DeleteFinalMixCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            return Task.FromResult(_service.DeleteFinalMixAssets(context.Story.Id, context.FolderPath));
        }
    }

    private sealed class DeleteMusicCommand : IStoryCommand
    {
        private readonly StoriesService _service;

        public DeleteMusicCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            try
            {
                _service.CleanMusicForRegeneration(context.Story.Id, context.FolderPath);
                var (mixSuccess, mixMessage) = _service.DeleteFinalMixAssets(context.Story.Id, context.FolderPath);
                string? message = mixSuccess
                    ? "Musica cancellata e mix finale rimosso"
                    : string.IsNullOrWhiteSpace(mixMessage)
                        ? "Musica cancellata con avvisi sul mix finale"
                        : $"Musica cancellata. {mixMessage}";
                return Task.FromResult<(bool success, string? message)>((mixSuccess, message));
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante la cancellazione della musica per la storia {Id}", context.Story.Id);
                return Task.FromResult<(bool success, string? message)>((false, ex.Message));
            }
        }
    }

    private sealed class DeleteFxCommand : IStoryCommand
    {
        private readonly StoriesService _service;

        public DeleteFxCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            try
            {
                _service.CleanFxForRegeneration(context.Story.Id, context.FolderPath);
                var (mixSuccess, mixMessage) = _service.DeleteFinalMixAssets(context.Story.Id, context.FolderPath);
                string? message = mixSuccess
                    ? "Effetti sonori cancellati e mix finale aggiornato"
                    : string.IsNullOrWhiteSpace(mixMessage)
                        ? "Effetti cancellati con avvisi sul mix finale"
                        : $"Effetti cancellati. {mixMessage}";
                return Task.FromResult<(bool success, string? message)>((mixSuccess, message));
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante la cancellazione degli effetti per la storia {Id}", context.Story.Id);
                return Task.FromResult<(bool success, string? message)>((false, ex.Message));
            }
        }
    }

    private sealed class DeleteAmbienceCommand : IStoryCommand
    {
        private readonly StoriesService _service;

        public DeleteAmbienceCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            try
            {
                _service.CleanAmbienceForRegeneration(context.Story.Id, context.FolderPath);
                var (mixSuccess, mixMessage) = _service.DeleteFinalMixAssets(context.Story.Id, context.FolderPath);
                string? message = mixSuccess
                    ? "Rumori ambientali cancellati e mix finale aggiornato"
                    : string.IsNullOrWhiteSpace(mixMessage)
                        ? "Rumori ambientali cancellati con avvisi sul mix finale"
                        : $"Rumori ambientali cancellati. {mixMessage}";
                return Task.FromResult<(bool success, string? message)>((mixSuccess, message));
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante la cancellazione dei rumori ambientali per la storia {Id}", context.Story.Id);
                return Task.FromResult<(bool success, string? message)>((false, ex.Message));
            }
        }
    }

    private sealed class DeleteTtsCommand : IStoryCommand
    {
        private readonly StoriesService _service;

        public DeleteTtsCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public async Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            try
            {
                _service.CleanBeforeTtsAudioGeneration(context.Story.Id, context.FolderPath);

                var cascadeContext = new StoryCommandContext(context.Story, context.FolderPath, null);
                var cascadeCommands = new IStoryCommand[]
                {
                    new DeleteAmbienceCommand(_service),
                    new DeleteMusicCommand(_service),
                    new DeleteFxCommand(_service),
                    new DeleteFinalMixCommand(_service)
                };

                var messages = new List<string> { "Tracce TTS cancellate" };
                var allOk = true;

                foreach (var cmd in cascadeCommands)
                {
                    var (ok, msg) = await cmd.ExecuteAsync(cascadeContext);
                    allOk &= ok;
                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        messages.Add(msg);
                    }
                }

                return (allOk, string.Join(" | ", messages));
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante la cancellazione TTS per la storia {Id}", context.Story.Id);
                return (false, ex.Message);
            }
        }
    }

    private sealed class DeleteStoryTaggedCommand : IStoryCommand
    {
        private readonly StoriesService _service;

        public DeleteStoryTaggedCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public async Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            try
            {
                var storyId = context.Story.Id;
                var cleared = _service._database.ClearStoryTagged(storyId);
                if (!cleared)
                {
                    return (false, "Impossibile cancellare story_tagged dal database");
                }

                context.Story.StoryTagged = string.Empty;
                context.Story.StoryTaggedVersion = null;
                context.Story.FormatterModelId = null;
                context.Story.FormatterPromptHash = null;

                var cascadeContext = new StoryCommandContext(context.Story, context.FolderPath, null);
                var (ttsOk, ttsMessage) = await new DeleteTtsCommand(_service).ExecuteAsync(cascadeContext);

                var message = string.IsNullOrWhiteSpace(ttsMessage)
                    ? "Campo story_tagged cancellato"
                    : $"Campo story_tagged cancellato. {ttsMessage}";

                return (ttsOk, message);
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante la cancellazione del campo story_tagged per la storia {Id}", context.Story.Id);
                return (false, ex.Message);
            }
        }
    }

    private sealed class EvaluateStoryCommand : IStoryCommand
    {
        private readonly StoriesService _service;

        public EvaluateStoryCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => true;
        public bool EnsureFolder => false;
        public bool HandlesStatusTransition => false;

        public async Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            var story = context.Story;
            var evaluators = _service._database.ListAgents()
                .Where(a => a.IsActive && !string.IsNullOrWhiteSpace(a.Role) &&
                    (a.Role.Equals("story_evaluator", StringComparison.OrdinalIgnoreCase) ||
                     a.Role.Equals("evaluator", StringComparison.OrdinalIgnoreCase) ||
                     a.Role.Equals("writer_evaluator", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(a => a.Id)
                .ToList();

            if (evaluators.Count == 0)
            {
                return (false, "Nessun agente valutatore configurato");
            }

            // Execute evaluators in parallel directly (not via dispatcher) to avoid deadlock
            // when this command is already running inside the dispatcher
            return await RunParallelAsync(story, evaluators);
        }

        private async Task<(bool success, string? message)> RunParallelAsync(StoryRecord story, List<Agent> evaluators)
        {
            // If a dispatcher is available, enqueue one command per evaluator and return immediately.
            if (_service._commandDispatcher != null)
            {
                var enqueued = new List<string>();
                foreach (var evaluator in evaluators)
                {
                    try
                    {
                        var runId = $"evaluate_story_{story.Id}_agent_{evaluator.Id}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                        var meta = new Dictionary<string, string>
                        {
                            ["storyId"] = story.Id.ToString(),
                            ["agentId"] = evaluator.Id.ToString(),
                            ["agentName"] = evaluator.Name ?? string.Empty,
                            ["operation"] = "evaluate_story"
                        };

                        _service._commandDispatcher.Enqueue(
                            "evaluate_story",
                            async ctx =>
                            {
                                try
                                {
                                    var (success, score, error) = await _service.EvaluateStoryWithAgentAsync(story.Id, evaluator.Id);
                                    var msg = success ? $"Valutazione completata. Score: {score:F2}" : $"Valutazione fallita: {error}";
                                    return new CommandResult(success, msg);
                                }
                                catch (Exception ex)
                                {
                                    return new CommandResult(false, ex.Message);
                                }
                            },
                            runId: runId,
                            threadScope: $"story/evaluate/agent_{evaluator.Id}",
                            metadata: meta);

                        enqueued.Add($"{evaluator.Name ?? ("Evaluator " + evaluator.Id)} ({runId})");
                    }
                    catch (Exception ex)
                    {
                        _service._logger?.LogWarning(ex, "Failed to enqueue evaluation for story {StoryId} agent {AgentId}", story.Id, evaluator.Id);
                    }
                }

                var msg = enqueued.Count > 0 ? $"Valutazioni accodate: {string.Join("; ", enqueued)}" : "Nessuna valutazione accodata";
                return (enqueued.Count > 0, msg);
            }

            // Fallback: if no dispatcher is available, run in parallel inline (previous behaviour)
            var tasks = evaluators.Select(async evaluator =>
            {
                try
                {
                    var (success, score, error) = await _service.EvaluateStoryWithAgentAsync(story.Id, evaluator.Id);
                    var label = string.IsNullOrWhiteSpace(evaluator.Name) ? $"Evaluator {evaluator.Id}" : evaluator.Name;
                    return success
                        ? (success: true, message: $"{label}: punteggio {score:F2}")
                        : (success: false, message: $"{label}: errore {error ?? "sconosciuto"}");
                }
                catch (Exception ex)
                {
                    var label = string.IsNullOrWhiteSpace(evaluator.Name) ? $"Evaluator {evaluator.Id}" : evaluator.Name;
                    return (success: false, message: $"{label}: eccezione {ex.Message}");
                }
            }).ToList();

            var results = await Task.WhenAll(tasks);
            var allOk = results.All(r => r.success);
            var joined = string.Join("; ", results.Select(r => r.message));

            return allOk
                ? (true, $"Valutazione completata. {joined}")
                : (false, $"Valutazione parziale. {joined}");
        }
    }

    private sealed class ReviseStoryCommand : IStoryCommand
    {
        private readonly StoriesService _service;

        public ReviseStoryCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => true;
        public bool EnsureFolder => false;
        public bool HandlesStatusTransition => false;

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            var runId = _service.EnqueueReviseStoryCommand(context.Story.Id, trigger: "status_flow", priority: 2, force: true);
            return Task.FromResult<(bool success, string? message)>(string.IsNullOrWhiteSpace(runId)
                ? (false, "Revisione non accodata")
                : (true, $"Revisione accodata (run {runId})"));
        }
    }

    private sealed class TagStoryCommand : IStoryCommand
    {
        private readonly StoriesService _service;

        public TagStoryCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => true;
        public bool EnsureFolder => false;
        public bool HandlesStatusTransition => false;

        public async Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            if (_service._kernelFactory == null)
            {
                return (false, "Kernel factory non disponibile");
            }

            var cmd = new TransformStoryRawToTaggedCommand(
                context.Story.Id,
                _service._database,
                _service._kernelFactory,
                _service,
                _service._customLogger,
                _service._commandDispatcher);

            var result = await cmd.ExecuteAsync();
            return (result.Success, result.Message);
        }
    }

    private sealed class PrepareTtsSchemaCommand : IStoryCommand
    {
        private readonly StoriesService _service;

        public PrepareTtsSchemaCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => true;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public async Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            var sb = new StringBuilder();
            var overallSuccess = true;

            try
            {
                var (ttsOk, ttsMsg) = await _service.GenerateTtsSchemaJsonAsync(context.Story.Id);
                sb.AppendLine($"GenerateTtsSchema: {ttsMsg}");
                if (!ttsOk) overallSuccess = false;
            }
            catch (Exception ex)
            {
                sb.AppendLine("GenerateTtsSchema: exception " + ex.Message);
                overallSuccess = false;
            }

            try
            {
                var (normCharOk, normCharMsg) = await _service.NormalizeCharacterNamesAsync(context.Story.Id);
                sb.AppendLine($"NormalizeCharacterNames: {normCharMsg}");
                if (!normCharOk) overallSuccess = false;
            }
            catch (Exception ex)
            {
                sb.AppendLine("NormalizeCharacterNames: exception " + ex.Message);
                overallSuccess = false;
            }

            try
            {
                var (assignOk, assignMsg) = await _service.AssignVoicesAsync(context.Story.Id);
                sb.AppendLine($"AssignVoices: {assignMsg}");
                if (!assignOk) overallSuccess = false;
            }
            catch (Exception ex)
            {
                sb.AppendLine("AssignVoices: exception " + ex.Message);
                overallSuccess = false;
            }

            try
            {
                var (normSentOk, normSentMsg) = await _service.NormalizeSentimentsAsync(context.Story.Id);
                sb.AppendLine($"NormalizeSentiments: {normSentMsg}");
                if (!normSentOk) overallSuccess = false;
            }
            catch (Exception ex)
            {
                sb.AppendLine("NormalizeSentiments: exception " + ex.Message);
                overallSuccess = false;
            }

            return (overallSuccess, sb.ToString());
        }
    }

    private static void ApplyVoiceToCharacter(TtsCharacter character, TinyGenerator.Models.TtsVoice voice)
    {
        character.Voice = voice.Name;
        character.VoiceId = voice.VoiceId;
        if (string.IsNullOrWhiteSpace(character.Gender) && !string.IsNullOrWhiteSpace(voice.Gender))
        {
            character.Gender = voice.Gender;
        }
    }

    private TinyGenerator.Models.TtsVoice? FindVoiceByNameOrId(List<TinyGenerator.Models.TtsVoice> voices, string preferredName)
    {
        if (voices == null || voices.Count == 0) return null;
        var byName = voices.FirstOrDefault(v => string.Equals(v.Name, preferredName, StringComparison.OrdinalIgnoreCase));
        if (byName != null) return byName;

        return voices.FirstOrDefault(v =>
            !string.IsNullOrWhiteSpace(v.VoiceId) &&
            v.VoiceId.IndexOf(preferredName, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private TinyGenerator.Models.TtsVoice? PickVoiceForGender(string gender, List<TinyGenerator.Models.TtsVoice> voices, HashSet<string> usedVoiceIds)
    {
        if (string.IsNullOrWhiteSpace(gender) || voices == null || voices.Count == 0)
            return null;

        var matches = voices
            .Where(v => !string.IsNullOrWhiteSpace(v.VoiceId) &&
                        string.Equals(v.Gender ?? string.Empty, gender, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            return null;

        var unused = matches
            .Where(v => !usedVoiceIds.Contains(v.VoiceId!))
            .ToList();

        var pool = unused.Count > 0 ? unused : matches;
        var maxScore = pool.Max(v => v.Score ?? 0);
        var bestCandidates = pool
            .Where(v => Math.Abs((v.Score ?? 0) - maxScore) < 0.0001)
            .ToList();
        if (bestCandidates.Count == 0)
        {
            bestCandidates = pool;
        }

        var index = Random.Shared.Next(bestCandidates.Count);
        return bestCandidates[index];
    }

    private sealed class GenerateTtsAudioCommand : IStoryCommand
    {
        private readonly StoriesService _service;

        public GenerateTtsAudioCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => true;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => true;

        public async Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            // Unify: always enqueue the dispatcher command (storyId only) instead of executing directly.
            // Status transition is handled by the dispatcher handler via targetStatusId metadata.
            var enqueue = _service.TryEnqueueGenerateTtsAudioCommandInternal(
                context.Story.Id,
                trigger: "status_transition",
                priority: 3,
                targetStatusId: context.TargetStatus?.Id);

            return (enqueue.Enqueued, enqueue.Enqueued
                ? $"Generazione audio TTS accodata (run {enqueue.RunId})."
                : enqueue.Message);
        }
    }

    private Task<(bool success, string? message)> StartTtsAudioGenerationAsync(StoryCommandContext context)
    {
        var storyId = context.Story.Id;
        var runId = $"ttsaudio_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        _customLogger?.Start(runId);
        _customLogger?.Append(runId, $"[{storyId}] Avvio generazione tracce audio nella cartella {context.FolderPath}");

        _ = Task.Run(async () =>
        {
            try
            {
                var (success, message) = await GenerateTtsAudioInternalAsync(context, runId);

                if (success && context.TargetStatus?.Id > 0)
                {
                    try
                    {
                        _database.UpdateStoryById(storyId, statusId: context.TargetStatus.Id, updateStatus: true);
                        _customLogger?.Append(runId, $"[{storyId}] Stato aggiornato a {context.TargetStatus.Code ?? context.TargetStatus.Id.ToString()}");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Impossibile aggiornare lo stato della storia {Id}", storyId);
                        _customLogger?.Append(runId, $"[{storyId}] Aggiornamento stato fallito: {ex.Message}");
                    }
                }

                await (_customLogger?.MarkCompletedAsync(runId, message ?? (success ? "Generazione audio completata" : "Errore generazione audio"))
                    ?? Task.CompletedTask);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Errore non gestito durante la generazione audio TTS per la storia {Id}", storyId);
                _customLogger?.Append(runId, $"[{storyId}] Errore inatteso: {ex.Message}");
                await (_customLogger?.MarkCompletedAsync(runId, $"Errore: {ex.Message}") ?? Task.CompletedTask);
            }
        });

        return Task.FromResult<(bool success, string? message)>((true, $"Generazione audio avviata (run {runId}). Monitora i log per i dettagli.")); 
    }

    private async Task<(bool success, string? message)> GenerateTtsAudioInternalAsync(StoryCommandContext context, string runId)
    {
        var story = context.Story;
        var folderPath = context.FolderPath;
        var schemaPath = Path.Combine(folderPath, "tts_schema.json");

        if (!File.Exists(schemaPath))
        {
            var err = "File tts_schema.json non trovato: genera prima lo schema";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        JsonObject? rootNode;
        try
        {
            var json = await File.ReadAllTextAsync(schemaPath);
            rootNode = JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex)
        {
            var err = $"Impossibile leggere tts_schema.json: {ex.Message}";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        if (rootNode == null)
        {
            var err = "Formato tts_schema.json non valido";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Try both casing variants for characters
        if (!rootNode.TryGetPropertyValue("characters", out var charactersNode))
            rootNode.TryGetPropertyValue("Characters", out charactersNode);
        if (charactersNode is not JsonArray charactersArray)
        {
            var err = "Lista di personaggi mancante nello schema TTS";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Try both casing variants for timeline
        if (!rootNode.TryGetPropertyValue("timeline", out var timelineNode))
            rootNode.TryGetPropertyValue("Timeline", out timelineNode);
        if (timelineNode is not JsonArray timelineArray)
        {
            var err = "Timeline mancante nello schema TTS";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Remove phrase entries that contain only placeholder/empty TTS text (e.g., "...")
        PruneEmptyTextPhrases(timelineArray);

        var characters = BuildCharacterMap(charactersArray);
        if (characters.Count == 0)
        {
            var err = "Nessun personaggio definito nello schema TTS";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        if (characters.Values.All(c => string.IsNullOrWhiteSpace(c.VoiceId)))
        {
            var err = "Nessun personaggio ha una voce assegnata: eseguire prima l'assegnazione delle voci";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var phraseEntries = timelineArray.OfType<JsonObject>().Where(IsPhraseEntry).ToList();
        if (phraseEntries.Count == 0)
        {
            var err = "La timeline non contiene frasi da sintetizzare";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        Directory.CreateDirectory(folderPath);

        int phraseCounter = 0;
        int fileCounter = 1;
        int currentMs = 0;
        var usedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in timelineArray.OfType<JsonObject>())
        {
            if (IsPauseEntry(entry, out var pauseMs))
            {
                currentMs += pauseMs;
                continue;
            }

                if (!TryReadPhrase(entry, out var characterName, out var text, out var emotion))
                    continue;

            phraseCounter++;

            if (!characters.TryGetValue(characterName, out var character) || string.IsNullOrWhiteSpace(character.VoiceId))
            {
                var err = $"Il personaggio '{characterName}' non ha una voce assegnata";
                _customLogger?.Append(runId, $"[{story.Id}] {err}");
                return (false, err);
            }

            var fileName = BuildAudioFileName(fileCounter++, characterName, usedFiles);
            var filePath = Path.Combine(folderPath, fileName);

            _customLogger?.Append(runId, $"[{story.Id}] Generazione frase {phraseCounter}/{phraseEntries.Count} ({characterName}) -> {fileName}");

            byte[] audioBytes;
            int? durationFromResult;
            string cleanText;
            try
            {
                cleanText = CleanTtsText(text);
                if (string.IsNullOrWhiteSpace(cleanText))
                {
                    _customLogger?.Append(runId, $"[{story.Id}] Salto frase vuota dopo pulizia (character={characterName})");
                    continue;
                }

                (audioBytes, durationFromResult) = await GenerateAudioBytesAsync(character.VoiceId!, cleanText, emotion);
            }
            catch (Exception ex)
            {
                var err = $"Errore durante la sintesi della frase '{characterName}': {ex.Message}";
                _logger?.LogError(ex, "Errore TTS per la storia {Id}", story.Id);
                _customLogger?.Append(runId, $"[{story.Id}] {err}");
                return (false, err);
            }

            try
            {
                await File.WriteAllBytesAsync(filePath, audioBytes);
                
                // Save TTS text to .txt file with same name
                var txtFilePath = Path.ChangeExtension(filePath, ".txt");
                await File.WriteAllTextAsync(txtFilePath, $"[{characterName}, {emotion}]\n{cleanText}");
            }
            catch (Exception ex)
            {
                var err = $"Impossibile salvare il file {fileName}: {ex.Message}";
                _logger?.LogError(ex, "Impossibile salvare il file {File} per la storia {Id}", fileName, story.Id);
                _customLogger?.Append(runId, $"[{story.Id}] {err}");
                return (false, err);
            }

            var durationMs = durationFromResult ?? TryGetWavDuration(audioBytes) ?? 0;
            var startMs = currentMs;
            var endMs = durationMs > 0 ? startMs + durationMs : startMs;

            entry["fileName"] = fileName;
            entry["durationMs"] = durationMs;
            entry["startMs"] = startMs;
            entry["endMs"] = endMs;

            currentMs = endMs + DefaultPhraseGapMs;
            _customLogger?.Append(runId, $"[{story.Id}] Frase completata: {fileName} ({durationMs} ms)");
        }

        try
        {
            SanitizeTtsSchemaTextFields(rootNode);
            var updated = rootNode.ToJsonString(SchemaJsonOptions);
            await File.WriteAllTextAsync(schemaPath, updated);
            // Mark story as having generated TTS audio (best-effort)
            try
            {
                _database.UpdateStoryGeneratedTts(story.Id, true);
            }
            catch { }
        }
        catch (Exception ex)
        {
            var err = $"Impossibile aggiornare tts_schema.json: {ex.Message}";
            _logger?.LogError(ex, "Errore salvataggio schema TTS per la storia {Id}", story.Id);
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var successMsg = $"Generazione audio completata ({phraseCounter} frasi)";
        _customLogger?.Append(runId, $"[{story.Id}] {successMsg}");
        return (true, successMsg);
    }

    /// <summary>
    /// Generates TTS audio synchronously and reports progress to the CommandDispatcher.
    /// </summary>
    private async Task<(bool success, string? message)> GenerateTtsAudioWithProgressAsync(StoryCommandContext context, string dispatcherRunId)
    {
        var story = context.Story;
        var folderPath = context.FolderPath;
        var schemaPath = Path.Combine(folderPath, "tts_schema.json");

        _customLogger?.Append(dispatcherRunId, $"[{story.Id}] Avvio generazione tracce audio nella cartella {folderPath}");

        if (!File.Exists(schemaPath))
        {
            var err = "File tts_schema.json non trovato: genera prima lo schema";
            _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
            return (false, err);
        }

        JsonObject? rootNode;
        try
        {
            var json = await File.ReadAllTextAsync(schemaPath);
            rootNode = JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex)
        {
            var err = $"Impossibile leggere tts_schema.json: {ex.Message}";
            _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
            return (false, err);
        }

        if (rootNode == null)
        {
            var err = "Formato tts_schema.json non valido";
            _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Try both casings for characters and timeline
        if (!rootNode.TryGetPropertyValue("characters", out var charactersNode))
            rootNode.TryGetPropertyValue("Characters", out charactersNode);
        if (charactersNode is not JsonArray charactersArray)
        {
            var err = "Lista di personaggi mancante nello schema TTS";
            _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
            return (false, err);
        }

        if (!rootNode.TryGetPropertyValue("timeline", out var timelineNode))
            rootNode.TryGetPropertyValue("Timeline", out timelineNode);
        if (timelineNode is not JsonArray timelineArray)
        {
            var err = "Timeline mancante nello schema TTS";
            _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Remove phrase entries that contain only placeholder/empty TTS text (e.g., "...")
        PruneEmptyTextPhrases(timelineArray);

        var characters = BuildCharacterMap(charactersArray);
        if (characters.Count == 0)
        {
            var err = "Nessun personaggio definito nello schema TTS";
            _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
            return (false, err);
        }

        if (characters.Values.All(c => string.IsNullOrWhiteSpace(c.VoiceId)))
        {
            var err = "Nessun personaggio ha una voce assegnata: eseguire prima l'assegnazione delle voci";
            _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var phraseEntries = timelineArray.OfType<JsonObject>().Where(IsPhraseEntry).ToList();
        if (phraseEntries.Count == 0)
        {
            var err = "La timeline non contiene frasi da sintetizzare";
            _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
            return (false, err);
        }

        Directory.CreateDirectory(folderPath);

        int phraseCounter = 0;
        int fileCounter = 1;
        int currentMs = 0;
        var usedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Set initial progress (0/total)
        _commandDispatcher?.UpdateStep(dispatcherRunId, 0, phraseEntries.Count);

        foreach (var entry in timelineArray.OfType<JsonObject>())
        {
            if (IsPauseEntry(entry, out var pauseMs))
            {
                currentMs += pauseMs;
                continue;
            }

            if (!TryReadPhrase(entry, out var characterName, out var text, out var emotion))
                continue;

            phraseCounter++;

            // Update progress in CommandDispatcher
            _commandDispatcher?.UpdateStep(dispatcherRunId, phraseCounter, phraseEntries.Count);

            if (!characters.TryGetValue(characterName, out var character) || string.IsNullOrWhiteSpace(character.VoiceId))
            {
                var err = $"Il personaggio '{characterName}' non ha una voce assegnata";
                _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
                return (false, err);
            }

            var fileName = BuildAudioFileName(fileCounter++, characterName, usedFiles);
            var filePath = Path.Combine(folderPath, fileName);

            _customLogger?.Append(dispatcherRunId, $"[{story.Id}] Generazione frase {phraseCounter}/{phraseEntries.Count} ({characterName}) -> {fileName}");

            byte[] audioBytes;
            int? durationFromResult;
            try
            {
                var cleanText = CleanTtsText(text);
                if (string.IsNullOrWhiteSpace(cleanText))
                {
                    _customLogger?.Append(dispatcherRunId, $"[{story.Id}] Salto frase vuota dopo pulizia (character={characterName})");
                    continue;
                }

                (audioBytes, durationFromResult) = await GenerateAudioBytesAsync(character.VoiceId!, cleanText, emotion);
            }
            catch (Exception ex)
            {
                var err = $"Errore durante la sintesi della frase '{characterName}': {ex.Message}";
                _logger?.LogError(ex, "Errore TTS per la storia {Id}", story.Id);
                _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
                return (false, err);
            }

            try
            {
                await File.WriteAllBytesAsync(filePath, audioBytes);
            }
            catch (Exception ex)
            {
                var err = $"Impossibile salvare il file {fileName}: {ex.Message}";
                _logger?.LogError(ex, "Impossibile salvare il file {File} per la storia {Id}", fileName, story.Id);
                _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
                return (false, err);
            }

            var durationMs = durationFromResult ?? TryGetWavDuration(audioBytes) ?? 0;
            var startMs = currentMs;
            var endMs = currentMs + durationMs;
            currentMs = endMs + DefaultPhraseGapMs;

            entry["fileName"] = fileName;
            entry["durationMs"] = durationMs;
            entry["startMs"] = startMs;
            entry["endMs"] = endMs;

            _customLogger?.Append(dispatcherRunId, $"[{story.Id}] Frase completata: {fileName} ({durationMs} ms)");
        }

        try
        {
            SanitizeTtsSchemaTextFields(rootNode);
            var updated = rootNode.ToJsonString(SchemaJsonOptions);
            await File.WriteAllTextAsync(schemaPath, updated);
            // Mark story as having generated TTS audio (best-effort)
            try
            {
                _database.UpdateStoryGeneratedTts(story.Id, true);
            }
            catch { }
        }
        catch (Exception ex)
        {
            var err = $"Impossibile aggiornare tts_schema.json: {ex.Message}";
            _logger?.LogError(ex, "Errore salvataggio schema TTS per la storia {Id}", story.Id);
            _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var successMsg = $"Generazione audio completata ({phraseCounter} frasi)";
        _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {successMsg}");
        return (true, successMsg);
    }

    // =====================================================================
    // Generate Ambience Audio Command
    // =====================================================================

    private sealed class GenerateAmbienceAudioCommand : IStoryCommand
    {
        private readonly StoriesService _service;

        public GenerateAmbienceAudioCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => true;

        public async Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            var deleteCmd = new DeleteAmbienceCommand(_service);
            var (cleanupOk, cleanupMessage) = await deleteCmd.ExecuteAsync(context);
            if (!cleanupOk)
            {
                return (false, cleanupMessage ?? "Impossibile cancellare i rumori ambientali esistenti");
            }

            return await _service.StartAmbienceAudioGenerationAsync(context);
        }
    }

    private Task<(bool success, string? message)> StartAmbienceAudioGenerationAsync(StoryCommandContext context)
    {
        var storyId = context.Story.Id;
        var runId = $"ambience_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        _customLogger?.Start(runId);
        _customLogger?.Append(runId, $"[{storyId}] Avvio generazione audio ambientale nella cartella {context.FolderPath}");

        _ = Task.Run(async () =>
        {
            try
            {
                var (success, message) = await GenerateAmbienceAudioInternalAsync(context, runId);

                if (success && context.TargetStatus?.Id > 0)
                {
                    try
                    {
                        _database.UpdateStoryById(storyId, statusId: context.TargetStatus.Id, updateStatus: true);
                        _customLogger?.Append(runId, $"[{storyId}] Stato aggiornato a {context.TargetStatus.Code ?? context.TargetStatus.Id.ToString()}");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Impossibile aggiornare lo stato della storia {Id}", storyId);
                        _customLogger?.Append(runId, $"[{storyId}] Aggiornamento stato fallito: {ex.Message}");
                    }
                }

                await (_customLogger?.MarkCompletedAsync(runId, message ?? (success ? "Generazione audio ambientale completata" : "Errore generazione audio ambientale"))
                    ?? Task.CompletedTask);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Errore non gestito durante la generazione audio ambientale per la storia {Id}", storyId);
                _customLogger?.Append(runId, $"[{storyId}] Errore inatteso: {ex.Message}");
                await (_customLogger?.MarkCompletedAsync(runId, $"Errore: {ex.Message}") ?? Task.CompletedTask);
            }
        });

        return Task.FromResult<(bool success, string? message)>((true, $"Generazione audio ambientale avviata (run {runId}). Monitora i log per i dettagli."));
    }

    private async Task<(bool success, string? message)> GenerateAmbienceAudioInternalAsync(StoryCommandContext context, string runId)
    {
        var story = context.Story;
        var folderPath = context.FolderPath;
        var schemaPath = Path.Combine(folderPath, "tts_schema.json");

        if (!File.Exists(schemaPath))
        {
            var err = "File tts_schema.json non trovato: genera prima lo schema";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        JsonObject? rootNode;
        try
        {
            var json = await File.ReadAllTextAsync(schemaPath);
            rootNode = JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex)
        {
            var err = $"Impossibile leggere tts_schema.json: {ex.Message}";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        if (rootNode == null)
        {
            var err = "Formato tts_schema.json non valido";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Get timeline
        if (!rootNode.TryGetPropertyValue("timeline", out var timelineNode))
            rootNode.TryGetPropertyValue("Timeline", out timelineNode);
        if (timelineNode is not JsonArray timelineArray)
        {
            var err = "Timeline mancante nello schema TTS";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var phraseEntries = timelineArray.OfType<JsonObject>().Where(IsPhraseEntry).ToList();
        if (phraseEntries.Count == 0)
        {
            var err = "La timeline non contiene frasi";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Build list of ambient sound segments: group consecutive phrases with same ambient_sounds
        var ambienceSegments = ExtractAmbienceSegments(phraseEntries);
        if (ambienceSegments.Count == 0)
        {
            var msg = "Nessun segmento ambient sounds trovato nella timeline (nessuna propriet� 'ambient_sounds' presente - usa il tag [RUMORI: ...])";
            _customLogger?.Append(runId, $"[{story.Id}] {msg}");
            return (true, msg);
        }

        _customLogger?.Append(runId, $"[{story.Id}] Trovati {ambienceSegments.Count} segmenti ambient sounds da generare");

        // Create AudioCraft tool
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var audioCraft = new AudioCraftTool(httpClient, forceCpu: false, logger: _customLogger);

        Directory.CreateDirectory(folderPath);

        int segmentCounter = 0;
        foreach (var segment in ambienceSegments)
        {
            segmentCounter++;
            var durationSeconds = (int)Math.Ceiling(segment.DurationMs / 1000.0);
            if (durationSeconds < 1) durationSeconds = 1;
            if (durationSeconds > 30) durationSeconds = 30; // AudioCraft limit

            _customLogger?.Append(runId, $"[{story.Id}] Generazione segmento {segmentCounter}/{ambienceSegments.Count}: '{segment.AmbiencePrompt}' ({durationSeconds}s)");

            // Update dispatcher progress if available
            if (_commandDispatcher != null)
            {
                _commandDispatcher.UpdateStep(runId, segmentCounter, ambienceSegments.Count);
            }

            try
            {
                // Call AudioCraft to generate ambient sound
                var audioRequest = new
                {
                    operation = "generate_sound",
                    prompt = segment.AmbiencePrompt,
                    duration = durationSeconds,
                    model = "facebook/audiogen-medium"
                };
                var requestJson = JsonSerializer.Serialize(audioRequest);
                var resultJson = await audioCraft.ExecuteAsync(requestJson);

                // Parse result to get filename
                var resultNode = JsonNode.Parse(resultJson) as JsonObject;
                var resultField = resultNode?["result"]?.ToString();

                _customLogger?.Append(runId, $"[{story.Id}] AudioCraft response: {resultJson?.Substring(0, Math.Min(500, resultJson?.Length ?? 0))}");

                string? generatedFileName = null;
                
                // First check LastGeneratedSoundFile (set by AudioCraftTool)
                if (!string.IsNullOrWhiteSpace(audioCraft.LastGeneratedSoundFile))
                {
                    generatedFileName = audioCraft.LastGeneratedSoundFile;
                }
                
                // If not found, try to parse from result field
                if (string.IsNullOrWhiteSpace(generatedFileName) && !string.IsNullOrWhiteSpace(resultField))
                {
                    // Try to extract filename from result (format varies)
                    try
                    {
                        var resultInner = JsonNode.Parse(resultField) as JsonObject;
                        generatedFileName = resultInner?["file"]?.ToString() 
                            ?? resultInner?["filename"]?.ToString()
                            ?? resultInner?["output"]?.ToString();
                    }
                    catch
                    {
                        // resultField might not be valid JSON, check if it's a plain filename
                        if (resultField.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                            resultField.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                        {
                            generatedFileName = resultField.Trim();
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(generatedFileName))
                {
                    _customLogger?.Append(runId, $"[{story.Id}] Avviso: impossibile determinare il file generato per il segmento {segmentCounter}. Result: {resultJson}");
                    continue;
                }

                // Download file from AudioCraft server
                var localFileName = $"ambience_{segmentCounter:D3}.wav";
                var localFilePath = Path.Combine(folderPath, localFileName);

                try
                {
                    var downloadResponse = await httpClient.GetAsync($"http://localhost:8003/download/{generatedFileName}");
                    if (downloadResponse.IsSuccessStatusCode)
                    {
                        var audioBytes = await downloadResponse.Content.ReadAsByteArrayAsync();
                        await File.WriteAllBytesAsync(localFilePath, audioBytes);
                        _customLogger?.Append(runId, $"[{story.Id}] Salvato: {localFileName}");
                        
                        // Save prompt to .txt file with same name
                        var promptFileName = Path.ChangeExtension(localFileName, ".txt");
                        var promptFilePath = Path.Combine(folderPath, promptFileName);
                        await File.WriteAllTextAsync(promptFilePath, segment.AmbiencePrompt);
                        _customLogger?.Append(runId, $"[{story.Id}] Salvato prompt: {promptFileName}");
                    }
                    else
                    {
                        _customLogger?.Append(runId, $"[{story.Id}] Avviso: impossibile scaricare {generatedFileName}");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _customLogger?.Append(runId, $"[{story.Id}] Avviso: errore download {generatedFileName}: {ex.Message}");
                    continue;
                }

                // Update tts_schema.json: add ambient_sound_file to each phrase in this segment
                foreach (var entryIndex in segment.EntryIndices)
                {
                    if (entryIndex >= 0 && entryIndex < phraseEntries.Count)
                    {
                        phraseEntries[entryIndex]["ambient_sound_file"] = localFileName;
                    }
                }
            }
            catch (Exception ex)
            {
                _customLogger?.Append(runId, $"[{story.Id}] Errore generazione segmento {segmentCounter}: {ex.Message}");
                // Continue with next segment
            }
        }

        // Save updated schema
        try
        {
            SanitizeTtsSchemaTextFields(rootNode);
            var updated = rootNode.ToJsonString(SchemaJsonOptions);
            await File.WriteAllTextAsync(schemaPath, updated);
            // Mark story as having generated ambience audio (best-effort)
            try { _database.UpdateStoryGeneratedAmbient(story.Id, true); } catch { }
        }
        catch (Exception ex)
        {
            var err = $"Impossibile aggiornare tts_schema.json: {ex.Message}";
            _logger?.LogError(ex, "Errore salvataggio schema TTS per la storia {Id}", story.Id);
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var successMsg = $"Generazione audio ambientale completata ({segmentCounter} segmenti)";
        _customLogger?.Append(runId, $"[{story.Id}] {successMsg}");
        return (true, successMsg);
    }

    private sealed record AmbienceSegment(string AmbiencePrompt, int StartMs, int DurationMs, List<int> EntryIndices);

    /// <summary>
    /// Extracts ambient sound segments from timeline entries.
    /// Uses the 'ambient_sounds' property (from [RUMORI: ...] tag) instead of 'ambience'.
    /// The 'ambience' property is now reserved for future image generation.
    /// </summary>
    private static List<AmbienceSegment> ExtractAmbienceSegments(List<JsonObject> entries)
    {
        var segments = new List<AmbienceSegment>();
        string? currentAmbientSounds = null;
        int segmentStartMs = 0;
        int currentEndMs = 0;
        var currentEntryIndices = new List<int>();

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            // Read from ambient_sound_description (standardized [RUMORI: ...] tag)
            var ambientSounds = ReadString(entry, "ambient_sound_description");
            
            // Read startMs and endMs using TryReadNumber
            int startMs = 0;
            int endMs = 0;
            if (TryReadNumber(entry, "startMs", out var startVal) || TryReadNumber(entry, "StartMs", out startVal))
                startMs = (int)startVal;
            if (TryReadNumber(entry, "endMs", out var endVal) || TryReadNumber(entry, "EndMs", out endVal))
                endMs = (int)endVal;
            if (endMs == 0) endMs = startMs;

            // If ambient sounds change, save the current segment
            if (!string.IsNullOrWhiteSpace(currentAmbientSounds) && 
                (ambientSounds != currentAmbientSounds || string.IsNullOrWhiteSpace(ambientSounds)))
            {
                var duration = currentEndMs - segmentStartMs;
                if (duration > 0 && currentEntryIndices.Count > 0)
                {
                    segments.Add(new AmbienceSegment(currentAmbientSounds!, segmentStartMs, duration, new List<int>(currentEntryIndices)));
                }
                currentAmbientSounds = null;
                currentEntryIndices.Clear();
            }

            // Start or continue a segment
            if (!string.IsNullOrWhiteSpace(ambientSounds))
            {
                if (currentAmbientSounds == null)
                {
                    currentAmbientSounds = ambientSounds;
                    segmentStartMs = startMs;
                }
                currentEndMs = endMs > 0 ? endMs : startMs;
                currentEntryIndices.Add(i);
            }
        }

        // Add final segment if any
        if (!string.IsNullOrWhiteSpace(currentAmbientSounds) && currentEntryIndices.Count > 0)
        {
            var duration = currentEndMs - segmentStartMs;
            if (duration > 0)
            {
                segments.Add(new AmbienceSegment(currentAmbientSounds!, segmentStartMs, duration, new List<int>(currentEntryIndices)));
            }
        }

        return segments;
    }

    // =====================================================================
    // Generate FX Audio Command (Sound Effects)
    // =====================================================================

    private sealed class GenerateFxAudioCommand : IStoryCommand
    {
        private readonly StoriesService _service;

        public GenerateFxAudioCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => true;

        public async Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            var deleteCmd = new DeleteFxCommand(_service);
            var (cleanupOk, cleanupMessage) = await deleteCmd.ExecuteAsync(context);
            if (!cleanupOk)
            {
                return (false, cleanupMessage ?? "Impossibile cancellare gli effetti sonori esistenti");
            }

            return await _service.StartFxAudioGenerationAsync(context);
        }
    }

    private Task<(bool success, string? message)> StartFxAudioGenerationAsync(StoryCommandContext context)
    {
        var storyId = context.Story.Id;
        var runId = $"fx_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        _customLogger?.Start(runId);
        _customLogger?.Append(runId, $"[{storyId}] Avvio generazione effetti sonori nella cartella {context.FolderPath}");

        _ = Task.Run(async () =>
        {
            try
            {
                var (success, message) = await GenerateFxAudioInternalAsync(context, runId);

                if (success && context.TargetStatus?.Id > 0)
                {
                    try
                    {
                        _database.UpdateStoryById(storyId, statusId: context.TargetStatus.Id, updateStatus: true);
                        _customLogger?.Append(runId, $"[{storyId}] Stato aggiornato a {context.TargetStatus.Code ?? context.TargetStatus.Id.ToString()}");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Impossibile aggiornare lo stato della storia {Id}", storyId);
                        _customLogger?.Append(runId, $"[{storyId}] Aggiornamento stato fallito: {ex.Message}");
                    }
                }

                await (_customLogger?.MarkCompletedAsync(runId, message ?? (success ? "Generazione effetti sonori completata" : "Errore generazione effetti sonori"))
                    ?? Task.CompletedTask);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Errore non gestito durante la generazione effetti sonori per la storia {Id}", storyId);
                _customLogger?.Append(runId, $"[{storyId}] Errore inatteso: {ex.Message}");
                await (_customLogger?.MarkCompletedAsync(runId, $"Errore: {ex.Message}") ?? Task.CompletedTask);
            }
        });

        return Task.FromResult<(bool success, string? message)>((true, $"Generazione effetti sonori avviata (run {runId}). Monitora i log per i dettagli."));
    }

    private async Task<(bool success, string? message)> GenerateFxAudioInternalAsync(StoryCommandContext context, string runId)
    {
        var story = context.Story;
        var folderPath = context.FolderPath;
        var schemaPath = Path.Combine(folderPath, "tts_schema.json");

        if (!File.Exists(schemaPath))
        {
            var err = "File tts_schema.json non trovato: genera prima lo schema";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        JsonObject? rootNode;
        try
        {
            var json = await File.ReadAllTextAsync(schemaPath);
            rootNode = JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex)
        {
            var err = $"Impossibile leggere tts_schema.json: {ex.Message}";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        if (rootNode == null)
        {
            var err = "Formato tts_schema.json non valido";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Get timeline
        if (!rootNode.TryGetPropertyValue("timeline", out var timelineNode))
            rootNode.TryGetPropertyValue("Timeline", out timelineNode);
        if (timelineNode is not JsonArray timelineArray)
        {
            var err = "Timeline mancante nello schema TTS";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var phraseEntries = timelineArray.OfType<JsonObject>().Where(IsPhraseEntry).ToList();
        if (phraseEntries.Count == 0)
        {
            var err = "La timeline non contiene frasi";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Find entries with FX properties
        var fxEntries = new List<(int Index, JsonObject Entry, string Description, int Duration)>();
        for (int i = 0; i < phraseEntries.Count; i++)
        {
            var entry = phraseEntries[i];
            var fxDesc = ReadString(entry, "fxDescription") ?? ReadString(entry, "FxDescription") ?? ReadString(entry, "fx_description");
            if (!string.IsNullOrWhiteSpace(fxDesc))
            {
                int fxDuration = 5; // default
                if (TryReadNumber(entry, "fxDuration", out var dur) || TryReadNumber(entry, "FxDuration", out dur) || TryReadNumber(entry, "fx_duration", out dur))
                    fxDuration = (int)dur;
                fxEntries.Add((i, entry, fxDesc, fxDuration));
            }
        }

        if (fxEntries.Count == 0)
        {
            var msg = "Nessun effetto sonoro da generare (nessuna propriet� 'fxDescription' presente)";
            _customLogger?.Append(runId, $"[{story.Id}] {msg}");
            return (true, msg);
        }

        _customLogger?.Append(runId, $"[{story.Id}] Trovati {fxEntries.Count} effetti sonori da generare");

        // Create AudioCraft tool
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var audioCraft = new AudioCraftTool(httpClient, forceCpu: false, logger: _customLogger);

        Directory.CreateDirectory(folderPath);

        int fxCounter = 0;
        foreach (var (index, entry, description, duration) in fxEntries)
        {
            fxCounter++;
            var durationSeconds = duration;
            if (durationSeconds < 1) durationSeconds = 1;
            if (durationSeconds > 30) durationSeconds = 30; // AudioCraft limit

            _customLogger?.Append(runId, $"[{story.Id}] Generazione FX {fxCounter}/{fxEntries.Count}: '{description}' ({durationSeconds}s)");

            // Update dispatcher progress if available
            if (_commandDispatcher != null)
            {
                _commandDispatcher.UpdateStep(runId, fxCounter, fxEntries.Count);
            }

            try
            {
                // Call AudioCraft to generate sound effect
                var audioRequest = new
                {
                    operation = "generate_sound",
                    prompt = description,
                    duration = durationSeconds,
                    model = "facebook/audiogen-medium"
                };
                var requestJson = JsonSerializer.Serialize(audioRequest);
                var resultJson = await audioCraft.ExecuteAsync(requestJson);

                // Parse result to get filename
                var resultNode = JsonNode.Parse(resultJson) as JsonObject;
                var resultField = resultNode?["result"]?.ToString();

                _customLogger?.Append(runId, $"[{story.Id}] AudioCraft FX response: {resultJson?.Substring(0, Math.Min(500, resultJson?.Length ?? 0))}");

                string? generatedFileName = null;
                
                // First check LastGeneratedSoundFile (set by AudioCraftTool)
                if (!string.IsNullOrWhiteSpace(audioCraft.LastGeneratedSoundFile))
                {
                    generatedFileName = audioCraft.LastGeneratedSoundFile;
                }
                
                // If not found, try to parse from result field
                if (string.IsNullOrWhiteSpace(generatedFileName) && !string.IsNullOrWhiteSpace(resultField))
                {
                    // Try to extract filename from result
                    try
                    {
                        var resultInner = JsonNode.Parse(resultField) as JsonObject;
                        generatedFileName = resultInner?["file"]?.ToString()
                            ?? resultInner?["filename"]?.ToString()
                            ?? resultInner?["file_path"]?.ToString()
                            ?? resultInner?["file_url"]?.ToString();
                        
                        // Extract just filename from path/url
                        if (!string.IsNullOrWhiteSpace(generatedFileName))
                        {
                            generatedFileName = Path.GetFileName(generatedFileName.Replace("/download/", ""));
                        }
                    }
                    catch
                    {
                        // resultField might not be valid JSON, check if it's a plain filename
                        if (resultField.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                            resultField.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                        {
                            generatedFileName = resultField.Trim();
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(generatedFileName))
                {
                    _customLogger?.Append(runId, $"[{story.Id}] Avviso: impossibile determinare il file generato per FX {fxCounter}. Result: {resultJson}");
                    continue;
                }

                // Download file from AudioCraft server
                var localFileName = $"fx_{fxCounter:D3}.wav";
                var localFilePath = Path.Combine(folderPath, localFileName);

                try
                {
                    var downloadResponse = await httpClient.GetAsync($"http://localhost:8003/download/{generatedFileName}");
                    if (downloadResponse.IsSuccessStatusCode)
                    {
                        var audioBytes = await downloadResponse.Content.ReadAsByteArrayAsync();
                        await File.WriteAllBytesAsync(localFilePath, audioBytes);
                        _customLogger?.Append(runId, $"[{story.Id}] Salvato: {localFileName}");
                        
                        // Save prompt to .txt file with same name
                        var promptFileName = Path.ChangeExtension(localFileName, ".txt");
                        var promptFilePath = Path.Combine(folderPath, promptFileName);
                        await File.WriteAllTextAsync(promptFilePath, description);
                        _customLogger?.Append(runId, $"[{story.Id}] Salvato prompt: {promptFileName}");
                    }
                    else
                    {
                        _customLogger?.Append(runId, $"[{story.Id}] Avviso: impossibile scaricare {generatedFileName} (status {downloadResponse.StatusCode})");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _customLogger?.Append(runId, $"[{story.Id}] Avviso: errore download {generatedFileName}: {ex.Message}");
                    continue;
                }

                // Update tts_schema.json: add fx_file property
                entry["fx_file"] = localFileName;
            }
            catch (Exception ex)
            {
                _customLogger?.Append(runId, $"[{story.Id}] Errore generazione FX {fxCounter}: {ex.Message}");
                // Continue with next FX
            }
        }

        // Save updated schema
        try
        {
            SanitizeTtsSchemaTextFields(rootNode);
            var updated = rootNode.ToJsonString(SchemaJsonOptions);
            await File.WriteAllTextAsync(schemaPath, updated);
            // Mark story as having generated FX (best-effort)
            try { _database.UpdateStoryGeneratedEffects(story.Id, true); } catch { }
        }
        catch (Exception ex)
        {
            var err = $"Impossibile aggiornare tts_schema.json: {ex.Message}";
            _logger?.LogError(ex, "Errore salvataggio schema TTS per la storia {Id}", story.Id);
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var successMsg = $"Generazione effetti sonori completata ({fxCounter} effetti)";
        _customLogger?.Append(runId, $"[{story.Id}] {successMsg}");
        return (true, successMsg);
    }

    /// <summary>
    /// Generates FX audio for a story using AudioCraft based on the 'fxDescription' fields in tts_schema.json.
    /// If dispatcherRunId is provided, progress will be reported to the CommandDispatcher.
    /// </summary>
    public async Task<(bool success, string? error)> GenerateFxForStoryAsync(long storyId, string folderName, string? dispatcherRunId = null)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return (false, "Folder name is required");

        var story = GetStoryById(storyId);
        if (story == null)
            return (false, "Story not found");

        var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", folderName);
        Directory.CreateDirectory(folderPath);

        var context = new StoryCommandContext(story, folderPath, null);
        var deleteCmd = new DeleteFxCommand(this);
        var (cleanupOk, cleanupMessage) = await deleteCmd.ExecuteAsync(context);
        if (!cleanupOk)
        {
            return (false, cleanupMessage ?? "Impossibile cancellare gli effetti sonori esistenti");
        }
        
        if (!string.IsNullOrWhiteSpace(dispatcherRunId))
        {
            return await GenerateFxAudioInternalAsync(context, dispatcherRunId);
        }
        var (success, message) = await StartFxAudioGenerationAsync(context);
        return (success, message);
    }

    // ==================== GENERATE MUSIC COMMAND ====================

    private sealed class GenerateMusicCommand : IStoryCommand
    {
        private readonly StoriesService _service;

        public GenerateMusicCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public async Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            var deleteCmd = new DeleteMusicCommand(_service);
            var (cleanupOk, cleanupMessage) = await deleteCmd.ExecuteAsync(context);
            if (!cleanupOk)
            {
                return (false, cleanupMessage ?? "Impossibile cancellare la musica esistente");
            }

            return await _service.StartMusicGenerationAsync(context);
        }
    }

    private Task<(bool success, string? message)> StartMusicGenerationAsync(StoryCommandContext context)
    {
        var storyId = context.Story.Id;
        var runId = $"music_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        _customLogger?.Start(runId);
        _customLogger?.Append(runId, $"[{storyId}] Avvio generazione musica nella cartella {context.FolderPath}");

        _ = Task.Run(async () =>
        {
            try
            {
                var (success, message) = await GenerateMusicInternalAsync(context, runId);
                await (_customLogger?.MarkCompletedAsync(runId, message ?? (success ? "Generazione musica completata" : "Errore generazione musica"))
                    ?? Task.CompletedTask);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Errore non gestito durante la generazione musica per la storia {Id}", storyId);
                _customLogger?.Append(runId, $"[{storyId}] Errore inatteso: {ex.Message}");
                await (_customLogger?.MarkCompletedAsync(runId, $"Errore: {ex.Message}") ?? Task.CompletedTask);
            }
        });

        return Task.FromResult<(bool success, string? message)>((true, $"Generazione musica avviata (run {runId}). Monitora i log per i dettagli."));
    }

    private async Task<(bool success, string? message)> GenerateMusicInternalAsync(StoryCommandContext context, string runId)
    {
        var story = context.Story;
        var folderPath = context.FolderPath;
        var schemaPath = Path.Combine(folderPath, "tts_schema.json");

        // Step 1: Verify tts_schema.json exists
        if (!File.Exists(schemaPath))
        {
            var err = "File tts_schema.json non trovato: genera prima lo schema TTS";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        JsonObject? rootNode;
        try
        {
            var json = await File.ReadAllTextAsync(schemaPath);
            rootNode = JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex)
        {
            var err = $"Impossibile leggere tts_schema.json: {ex.Message}";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        if (rootNode == null)
        {
            var err = "Formato tts_schema.json non valido";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Get timeline
        if (!rootNode.TryGetPropertyValue("timeline", out var timelineNode))
            rootNode.TryGetPropertyValue("Timeline", out timelineNode);
        if (timelineNode is not JsonArray timelineArray)
        {
            var err = "Timeline mancante nello schema TTS";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var phraseEntries = timelineArray.OfType<JsonObject>().Where(IsPhraseEntry).ToList();
        if (phraseEntries.Count == 0)
        {
            var err = "La timeline non contiene frasi";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Step 2: Assign music files from curated library (series_folder or data/music_stories)
        var musicLibraryFolder = GetMusicLibraryFolderForStory(story);
        if (string.IsNullOrWhiteSpace(musicLibraryFolder) || !Directory.Exists(musicLibraryFolder))
        {
            var err = story.SerieId.HasValue
                ? $"Cartella musica serie non trovata: {musicLibraryFolder}"
                : $"Cartella musica di fallback non trovata: {musicLibraryFolder}";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var libraryFiles = Directory
            .GetFiles(musicLibraryFolder)
            .Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (libraryFiles.Count == 0)
        {
            var err = $"Nessun file musica trovato in {musicLibraryFolder}";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var index = BuildMusicLibraryIndex(libraryFiles);

        var usedDestFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in phraseEntries)
        {
            var mf = ReadString(existing, "music_file") ?? ReadString(existing, "musicFile") ?? ReadString(existing, "MusicFile");
            if (!string.IsNullOrWhiteSpace(mf)) usedDestFileNames.Add(mf);
        }

        var assigned = 0;
        for (var i = 0; i < phraseEntries.Count; i++)
        {
            var entry = phraseEntries[i];
            var musicDesc = ReadString(entry, "music_description") ?? ReadString(entry, "musicDescription") ?? ReadString(entry, "MusicDescription");
            if (string.IsNullOrWhiteSpace(musicDesc))
                continue;

            var currentMusicFile = ReadString(entry, "music_file") ?? ReadString(entry, "musicFile") ?? ReadString(entry, "MusicFile");
            if (!string.IsNullOrWhiteSpace(currentMusicFile) && File.Exists(Path.Combine(folderPath, currentMusicFile)))
                continue; // already assigned and present

            var requestedType = InferMusicTypeFromDescription(musicDesc, index.Keys);
            var selected = SelectMusicFileDeterministic(index, requestedType, usedDestFileNames, seedA: story.Id, seedB: i);
            if (string.IsNullOrWhiteSpace(selected) || !File.Exists(selected))
            {
                _customLogger?.Append(runId, $"[{story.Id}] [WARN] Nessun file musica selezionabile per type='{requestedType}' desc='{musicDesc}'");
                continue;
            }

            var destFileName = Path.GetFileName(selected);
            var destPath = Path.Combine(folderPath, destFileName);
            try
            {
                if (!File.Exists(destPath))
                {
                    File.Copy(selected, destPath, overwrite: false);
                }
            }
            catch (Exception ex)
            {
                _customLogger?.Append(runId, $"[{story.Id}] [WARN] Copia musica fallita ({destFileName}): {ex.Message}");
                continue;
            }

            entry["music_file"] = destFileName;
            entry["musicFile"] = destFileName;
            usedDestFileNames.Add(destFileName);
            assigned++;
        }

        // Step 3: Save updated schema
        try
        {
            SanitizeTtsSchemaTextFields(rootNode);
            var updated = rootNode.ToJsonString(SchemaJsonOptions);
            await File.WriteAllTextAsync(schemaPath, updated);
            // Mark story as having generated music (best-effort)
            try { _database.UpdateStoryGeneratedMusic(story.Id, true); } catch { }
        }
        catch (Exception ex)
        {
            var err = $"Impossibile aggiornare tts_schema.json: {ex.Message}";
            _logger?.LogError(ex, "Errore salvataggio schema TTS per la storia {Id}", story.Id);
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var successMsg = assigned > 0
            ? $"Musica assegnata da libreria ({assigned} voci in timeline)"
            : "Nessuna musica assegnata (nessuna richiesta o nessun file selezionabile)";
        _customLogger?.Append(runId, $"[{story.Id}] {successMsg}");
        return (true, successMsg);
    }

    private string GetMusicLibraryFolderForStory(StoryRecord story)
    {
        if (story.SerieId.HasValue)
        {
            try
            {
                var serie = _database.GetSeriesById(story.SerieId.Value);
                var folder = (serie?.Folder ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    return Path.Combine(Directory.GetCurrentDirectory(), "series_folder", folder, "music");
                }
            }
            catch
            {
                // best-effort
            }

            // If story is in a series but folder is missing, still fall back to global folder.
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "data", "music_stories");
    }

    private static Dictionary<string, List<string>> BuildMusicLibraryIndex(List<string> files)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        map["any"] = new List<string>();

        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file)) continue;
            map["any"].Add(file);

            var name = Path.GetFileNameWithoutExtension(file) ?? string.Empty;
            name = name.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var sep = name.IndexOfAny(new[] { '_', '-' });
            var prefix = sep > 0 ? name.Substring(0, sep) : name;
            prefix = prefix.Trim();
            if (string.IsNullOrWhiteSpace(prefix)) continue;

            // Only index known music types that correspond to actual audio files; everything else goes into "any" only.
            if (!KnownMusicFileTypes.Contains(prefix))
                continue;

            if (!map.TryGetValue(prefix, out var list))
            {
                list = new List<string>();
                map[prefix] = list;
            }
            list.Add(file);
        }

        return map;
    }

    private static readonly HashSet<string> KnownMusicTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "opening",
        "ending",
        "transition",
        "mystery",
        "suspense",
        "tension",
        "love",
        "combat",
        "activity",
        "exploration",
        "victory",
        "defeat",
        "aftermath",
        "ambient",
        "silence"
    };

    private static readonly HashSet<string> KnownMusicFileTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "opening",
        "ending",
        "transition",
        "mystery",
        "suspense",
        "tension",
        "love",
        "combat",
        "activity",
        "exploration",
        "victory",
        "defeat",
        "aftermath",
        "ambient"
    };

    private static string InferMusicTypeFromDescription(string description, IEnumerable<string> knownTypes)
    {
        if (string.IsNullOrWhiteSpace(description)) return "any";

        // If the description explicitly includes a known type, use it even if the library currently has no files for that type.
        foreach (var t in KnownMusicTypes)
        {
            if (Regex.IsMatch(description, $@"\b{Regex.Escape(t)}\b", RegexOptions.IgnoreCase))
                return t;
        }

        foreach (var t in knownTypes)
        {
            if (string.Equals(t, "any", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(t)) continue;
            if (Regex.IsMatch(description, $@"\b{Regex.Escape(t)}\b", RegexOptions.IgnoreCase))
                return t;
        }

        return "any";
    }

    private static string? SelectMusicFileDeterministic(
        Dictionary<string, List<string>> index,
        string requestedType,
        HashSet<string> usedDestFileNames,
        long seedA,
        int seedB)
    {
        if (index == null || index.Count == 0) return null;

        List<string> candidates;
        if (!string.IsNullOrWhiteSpace(requestedType) && index.TryGetValue(requestedType, out var typed) && typed.Count > 0)
            candidates = typed;
        else if (index.TryGetValue("any", out var any) && any.Count > 0)
            candidates = any;
        else
            candidates = index.Values.FirstOrDefault(v => v.Count > 0) ?? new List<string>();

        if (candidates.Count == 0) return null;

        // Prefer unused within the episode/run.
        var unused = candidates
            .Where(f => !usedDestFileNames.Contains(Path.GetFileName(f) ?? string.Empty))
            .ToList();
        var pool = unused.Count > 0 ? unused : candidates;

        var seed = unchecked((int)(seedA ^ (seedA >> 32) ^ seedB));
        var rnd = new Random(seed);
        return pool[rnd.Next(pool.Count)];
    }

    /// <summary>
    /// Generates music for a story using AudioCraft MusicGen based on the 'musicDescription' fields in tts_schema.json.
    /// If dispatcherRunId is provided, progress will be reported to the CommandDispatcher.
    /// </summary>
    public async Task<(bool success, string? error)> GenerateMusicForStoryAsync(long storyId, string folderName, string? dispatcherRunId = null)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return (false, "Folder name is required");

        var story = GetStoryById(storyId);
        if (story == null)
            return (false, "Story not found");

        var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", folderName);
        Directory.CreateDirectory(folderPath);

        var context = new StoryCommandContext(story, folderPath, null);

        var deleteCmd = new DeleteMusicCommand(this);
        var (cleanupOk, cleanupMessage) = await deleteCmd.ExecuteAsync(context);
        if (!cleanupOk)
        {
            return (false, cleanupMessage ?? "Impossibile cancellare la musica esistente");
        }
        
        if (!string.IsNullOrWhiteSpace(dispatcherRunId))
        {
            return await GenerateMusicInternalAsync(context, dispatcherRunId);
        }
        var (success, message) = await StartMusicGenerationAsync(context);
        return (success, message);
    }

    // ==================== MIX FINAL AUDIO COMMAND ====================

    private sealed class MixFinalAudioCommand : IStoryCommand
    {
        private readonly StoriesService _service;

        public MixFinalAudioCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => true;

        public async Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            var deleteCmd = new DeleteFinalMixCommand(_service);
            var (cleanupOk, cleanupMessage) = await deleteCmd.ExecuteAsync(context);
            if (!cleanupOk)
            {
                return (false, cleanupMessage ?? "Impossibile cancellare il mix finale esistente");
            }

            return await _service.StartMixFinalAudioAsync(context);
        }
    }

    private Task<(bool success, string? message)> StartMixFinalAudioAsync(StoryCommandContext context)
    {
        var storyId = context.Story.Id;
        var runId = $"mixaudio_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        _customLogger?.Start(runId);
        _customLogger?.Append(runId, $"[{storyId}] Avvio mixaggio audio finale nella cartella {context.FolderPath}");

        _ = Task.Run(async () =>
        {
            try
            {
                var (success, message) = await MixFinalAudioInternalAsync(context, runId);

                if (success && context.TargetStatus?.Id > 0)
                {
                    try
                    {
                        _database.UpdateStoryById(storyId, statusId: context.TargetStatus.Id, updateStatus: true);
                        _customLogger?.Append(runId, $"[{storyId}] Stato aggiornato a {context.TargetStatus.Code ?? context.TargetStatus.Id.ToString()}");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Impossibile aggiornare lo stato della storia {Id}", storyId);
                        _customLogger?.Append(runId, $"[{storyId}] Aggiornamento stato fallito: {ex.Message}");
                    }
                }

                await (_customLogger?.MarkCompletedAsync(runId, message ?? (success ? "Mixaggio audio completato" : "Errore mixaggio audio"))
                    ?? Task.CompletedTask);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Errore non gestito durante il mixaggio audio per la storia {Id}", storyId);
                _customLogger?.Append(runId, $"[{storyId}] Errore inatteso: {ex.Message}");
                await (_customLogger?.MarkCompletedAsync(runId, $"Errore: {ex.Message}") ?? Task.CompletedTask);
            }
        });

        return Task.FromResult<(bool success, string? message)>((true, $"Mixaggio audio avviato (run {runId}). Monitora i log per i dettagli."));
    }

    private async Task<(bool success, string? message)> MixFinalAudioInternalAsync(StoryCommandContext context, string runId)
    {
        var story = context.Story;
        var folderPath = context.FolderPath;
        var schemaPath = Path.Combine(folderPath, "tts_schema.json");

        // Step 1: Verify tts_schema.json exists
        if (!File.Exists(schemaPath))
        {
            var err = "File tts_schema.json non trovato: genera prima lo schema TTS";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        JsonObject? rootNode;
        try
        {
            var json = await File.ReadAllTextAsync(schemaPath);
            rootNode = JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex)
        {
            var err = $"Impossibile leggere tts_schema.json: {ex.Message}";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        if (rootNode == null)
        {
            var err = "Formato tts_schema.json non valido";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Get timeline
        if (!rootNode.TryGetPropertyValue("timeline", out var timelineNode))
            rootNode.TryGetPropertyValue("Timeline", out timelineNode);
        if (timelineNode is not JsonArray timelineArray)
        {
            var err = "Timeline mancante nello schema TTS";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var phraseEntries = timelineArray.OfType<JsonObject>().Where(IsPhraseEntry).ToList();
        if (phraseEntries.Count == 0)
        {
            var err = "La timeline non contiene frasi";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        _customLogger?.Append(runId, $"[{story.Id}] Trovate {phraseEntries.Count} frasi nella timeline");

        // Step 2: Check for missing TTS audio files
        var missingTts = new List<int>();
        var ttsFiles = new List<(int Index, string FileName, int StartMs, int DurationMs)>();
        
        for (int i = 0; i < phraseEntries.Count; i++)
        {
            var entry = phraseEntries[i];
            var fileName = ReadString(entry, "fileName") ?? ReadString(entry, "FileName") ?? ReadString(entry, "file_name");
            
            if (string.IsNullOrWhiteSpace(fileName))
            {
                missingTts.Add(i + 1);
                continue;
            }

            var filePath = Path.Combine(folderPath, fileName);
            if (!File.Exists(filePath))
            {
                missingTts.Add(i + 1);
                continue;
            }

            int startMs = 0;
            int durationMs = 0;
            if (TryReadNumber(entry, "startMs", out var s) || TryReadNumber(entry, "StartMs", out s) || TryReadNumber(entry, "start_ms", out s))
                startMs = (int)s;
            if (TryReadNumber(entry, "durationMs", out var d) || TryReadNumber(entry, "DurationMs", out d) || TryReadNumber(entry, "duration_ms", out d))
                durationMs = (int)d;

            ttsFiles.Add((i, fileName, startMs, durationMs));
        }

        if (missingTts.Count > 0)
        {
            var err = $"File TTS mancanti per le frasi: {string.Join(", ", missingTts)}. Il mix richiede i file TTS esistenti per mantenere la sincronizzazione; genera prima i TTS.";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Step 3: Check for missing ambient sound files (from [RUMORI: ...] tag)
        var ambientSoundsNeeded = phraseEntries.Any(e => 
            !string.IsNullOrWhiteSpace(ReadString(e, "ambient_sound_description")));
        
        if (ambientSoundsNeeded)
        {
            var missingAmbientSounds = phraseEntries.Where(e =>
            {
                var ambientSounds = ReadString(e, "ambient_sound_description");
                if (string.IsNullOrWhiteSpace(ambientSounds)) return false;
                var ambientSoundFile = ReadString(e, "ambient_sound_file");
                if (string.IsNullOrWhiteSpace(ambientSoundFile)) return true;
                return !File.Exists(Path.Combine(folderPath, ambientSoundFile));
            }).ToList();

            if (missingAmbientSounds.Count > 0)
            {
                var missingList = missingAmbientSounds.Select(e => ReadString(e, "ambient_sounds") ?? ReadString(e, "ambientSounds") ?? ReadString(e, "AmbientSounds")).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                var err = $"File ambient sound mancanti per alcune frasi; genere necessario prima del mix. Missing count: {missingAmbientSounds.Count}";
                _customLogger?.Append(runId, $"[{story.Id}] {err}");
                return (false, err);
            }
        }

        // Step 4: Check for missing FX audio files
        var fxNeeded = phraseEntries.Any(e => 
            !string.IsNullOrWhiteSpace(ReadString(e, "fx_description")));
        
        if (fxNeeded)
        {
            var missingFx = phraseEntries.Where(e =>
            {
                var fxDesc = ReadString(e, "fx_description");
                if (string.IsNullOrWhiteSpace(fxDesc)) return false;
                var fxFile = ReadString(e, "fx_file");
                if (string.IsNullOrWhiteSpace(fxFile)) return true;
                return !File.Exists(Path.Combine(folderPath, fxFile));
            }).ToList();

            if (missingFx.Count > 0)
            {
                var err = $"File FX mancanti per alcune frasi (count: {missingFx.Count}). Generazione automatica non consentita durante il mix; genera prima gli FX.";
                _customLogger?.Append(runId, $"[{story.Id}] {err}");
                return (false, err);
            }
        }

        // Step 4.5: Check for missing music files and generate if needed (music can be regenerated)
        var musicNeeded = phraseEntries.Any(e => !string.IsNullOrWhiteSpace(ReadString(e, "music_description")));
        if (musicNeeded)
        {
            var missingMusic = phraseEntries.Where(e =>
            {
                var mdesc = ReadString(e, "music_description");
                if (string.IsNullOrWhiteSpace(mdesc)) return false;
                var musicFile = ReadString(e, "music_file");
                if (string.IsNullOrWhiteSpace(musicFile)) return true;
                return !File.Exists(Path.Combine(folderPath, musicFile));
            }).ToList();

            if (missingMusic.Count > 0)
            {
                var err = $"File music mancanti per alcune frasi (count: {missingMusic.Count}). Generazione automatica non consentita durante il mix; genera prima la musica.";
                _customLogger?.Append(runId, $"[{story.Id}] {err}");
                return (false, err);
            }
        }

        _customLogger?.Append(runId, $"[{story.Id}] Tutti i file audio presenti. Avvio mixaggio...");


        var ttsTrackFiles = new List<(string FilePath, int StartMs)>();
        var ambienceTrackFiles = new List<(string FilePath, int StartMs, int DurationMs)>();
        var fxTrackFiles = new List<(string FilePath, int StartMs)>();
        var musicTrackFiles = new List<(string FilePath, int StartMs, int DurationMs)>();

        // === OPENING INTRO (handled ONLY in mix, not in tts_schema.json) ===
        // 15s opening music + narrator announcement at +5s. Then shift the rest of the story by +15s.
        var introShiftMs = 0;
        try
        {
            var musicLibraryFolder = GetMusicLibraryFolderForStory(story);
            if (!string.IsNullOrWhiteSpace(musicLibraryFolder) && Directory.Exists(musicLibraryFolder))
            {
                var libraryFiles = Directory
                    .GetFiles(musicLibraryFolder)
                    .Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var index = BuildMusicLibraryIndex(libraryFiles);
                var selectedOpening = SelectMusicFileDeterministic(
                    index,
                    requestedType: "opening",
                    usedDestFileNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    seedA: story.Id,
                    seedB: -100);

                if (!string.IsNullOrWhiteSpace(selectedOpening) && File.Exists(selectedOpening))
                {
                    musicTrackFiles.Add((selectedOpening, 0, 15000));
                    introShiftMs = 15000;

                    // Narrator announcement
                    var narratorVoice = (ReadString(rootNode, "narrator_voice_id")
                        ?? ReadString(rootNode, "narratorVoiceId")
                        ?? ReadString(rootNode, "narrator_voice")
                        ?? ReadString(rootNode, "narratorVoice")
                        ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(narratorVoice)) narratorVoice = NarratorFallbackVoiceName;

                    var storyTitle = (story.Title ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(storyTitle) && _ttsService != null)
                    {
                        string? seriesTitle = null;
                        if (story.SerieId.HasValue)
                        {
                            try
                            {
                                seriesTitle = _database.GetSeriesById(story.SerieId.Value)?.Titolo;
                            }
                            catch
                            {
                                seriesTitle = null;
                            }
                        }

                        var episodeNumber = story.SerieEpisode;

                        var announcement = !string.IsNullOrWhiteSpace(seriesTitle) && episodeNumber.HasValue
                            ? $"{seriesTitle}. Episodio {episodeNumber.Value}. {storyTitle}."
                            : !string.IsNullOrWhiteSpace(seriesTitle)
                                ? $"{seriesTitle}. {storyTitle}."
                                : $"{storyTitle}.";

                        var openingTtsFile = Path.Combine(folderPath, "tts_opening.wav");
                        if (!File.Exists(openingTtsFile))
                        {
                            var ttsSafe = ProsodyNormalizer.NormalizeForTTS(announcement);
                            var ttsResult = await _ttsService.SynthesizeAsync(narratorVoice, ttsSafe);
                            if (ttsResult != null && !string.IsNullOrWhiteSpace(ttsResult.AudioBase64))
                            {
                                var audioBytes = Convert.FromBase64String(ttsResult.AudioBase64);
                                await File.WriteAllBytesAsync(openingTtsFile, audioBytes);
                            }
                        }

                        if (File.Exists(openingTtsFile))
                        {
                            ttsTrackFiles.Add((openingTtsFile, 5000));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _customLogger?.Append(runId, $"[{story.Id}] [WARN] Opening intro fallito: {ex.Message}");
        }

        // Accoda il resto della storia come al solito (shift startMs di +introShiftMs)
        int currentTimeMs = introShiftMs;
        foreach (var entry in phraseEntries)
        {
            // TTS file
            var ttsFileName = ReadString(entry, "fileName") ?? ReadString(entry, "FileName") ?? ReadString(entry, "file_name");
            if (!string.IsNullOrWhiteSpace(ttsFileName))
            {
                var ttsFilePath = Path.Combine(folderPath, ttsFileName);
                if (File.Exists(ttsFilePath))
                {
                    int startMs = currentTimeMs;
                    if (TryReadNumber(entry, "startMs", out var s) || TryReadNumber(entry, "StartMs", out s) || TryReadNumber(entry, "start_ms", out s))
                        startMs = (int)s + introShiftMs;
                    ttsTrackFiles.Add((ttsFilePath, startMs));

                    // Update current time based on duration
                    int durationMs = 2000; // default gap
                    if (TryReadNumber(entry, "durationMs", out var d) || TryReadNumber(entry, "DurationMs", out d) || TryReadNumber(entry, "duration_ms", out d))
                        durationMs = (int)d;
                    currentTimeMs = startMs + durationMs;
                }
            }

            // Ambient sound file (from [RUMORI: ...] tag, stored in ambient_sound_file or ambience_file)
            var ambientSoundFile = ReadString(entry, "ambient_sound_file") ?? ReadString(entry, "ambientSoundFile") ?? ReadString(entry, "AmbientSoundFile")
                ?? ReadString(entry, "ambience_file") ?? ReadString(entry, "ambienceFile") ?? ReadString(entry, "AmbienceFile");
            if (!string.IsNullOrWhiteSpace(ambientSoundFile))
            {
                var ambientSoundFilePath = Path.Combine(folderPath, ambientSoundFile);
                if (File.Exists(ambientSoundFilePath))
                {
                    int startMs = introShiftMs;
                    if (TryReadNumber(entry, "startMs", out var s) || TryReadNumber(entry, "StartMs", out s) || TryReadNumber(entry, "start_ms", out s))
                        startMs = (int)s + introShiftMs;
                    int durationMs = 30000; // default 30s for ambient sounds
                    ambienceTrackFiles.Add((ambientSoundFilePath, startMs, durationMs));
                    _customLogger?.Append(runId, $"[{story.Id}] Ambient sound aggiunto: {ambientSoundFile} @ {startMs}ms");
                }
                else
                {
                    _customLogger?.Append(runId, $"[{story.Id}] [WARN] File ambient sound NON TROVATO: {ambientSoundFilePath}");
                }
            }

            // FX file - starts at middle of phrase duration
            var fxFile = ReadString(entry, "fx_file") ?? ReadString(entry, "fxFile") ?? ReadString(entry, "FxFile");
            if (!string.IsNullOrWhiteSpace(fxFile))
            {
                var fxFilePath = Path.Combine(folderPath, fxFile);
                if (File.Exists(fxFilePath))
                {
                    int startMs = introShiftMs;
                    int phraseDurationMs = 0;
                    if (TryReadNumber(entry, "startMs", out var s) || TryReadNumber(entry, "StartMs", out s) || TryReadNumber(entry, "start_ms", out s))
                        startMs = (int)s + introShiftMs;
                    if (TryReadNumber(entry, "durationMs", out var d) || TryReadNumber(entry, "DurationMs", out d) || TryReadNumber(entry, "duration_ms", out d))
                        phraseDurationMs = (int)d;
                    int fxStartMs = startMs + (phraseDurationMs / 2);
                    fxTrackFiles.Add((fxFilePath, fxStartMs));
                }
            }

            // Music file - starts 2 seconds after phrase start
            var musicFile = ReadString(entry, "musicFile") ?? ReadString(entry, "music_file") ?? ReadString(entry, "MusicFile");
            if (!string.IsNullOrWhiteSpace(musicFile))
            {
                var musicFilePath = Path.Combine(folderPath, musicFile);
                if (File.Exists(musicFilePath))
                {
                    int startMs = introShiftMs;
                    if (TryReadNumber(entry, "startMs", out var s) || TryReadNumber(entry, "StartMs", out s) || TryReadNumber(entry, "start_ms", out s))
                        startMs = (int)s + introShiftMs;
                    int durationMs = 20000; // default 20s for music
                    if (TryReadNumber(entry, "musicDuration", out var md) || TryReadNumber(entry, "MusicDuration", out md) || TryReadNumber(entry, "music_duration", out md))
                        durationMs = (int)md * 1000;
                    int musicStartMs = startMs + 2000;
                    if (TryReadNumber(entry, "musicStartMs", out var msOverride))
                    {
                        musicStartMs = (int)msOverride + introShiftMs;
                    }
                    musicTrackFiles.Add((musicFilePath, musicStartMs, durationMs));
                }
            }
        }

        // === ENDING OUTRO (handled ONLY in mix, not in tts_schema.json) ===
        // Append 20s ending music at the end of the story.
        try
        {
            var musicLibraryFolder = GetMusicLibraryFolderForStory(story);
            if (!string.IsNullOrWhiteSpace(musicLibraryFolder) && Directory.Exists(musicLibraryFolder))
            {
                var libraryFiles = Directory
                    .GetFiles(musicLibraryFolder)
                    .Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var index = BuildMusicLibraryIndex(libraryFiles);
                var selectedEnding = SelectMusicFileDeterministic(index, requestedType: "ending", usedDestFileNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase), seedA: story.Id, seedB: -200);
                if (!string.IsNullOrWhiteSpace(selectedEnding) && File.Exists(selectedEnding))
                {
                    musicTrackFiles.Add((selectedEnding, currentTimeMs, 20000));
                    currentTimeMs += 20000;
                }
            }
        }
        catch (Exception ex)
        {
            _customLogger?.Append(runId, $"[{story.Id}] [WARN] Ending outro fallito: {ex.Message}");
        }

        _customLogger?.Append(runId, $"[{story.Id}] [DEBUG] Riepilogo file raccolti: {ttsTrackFiles.Count} TTS, {ambienceTrackFiles.Count} ambience, {fxTrackFiles.Count} FX, {musicTrackFiles.Count} music");

        // Step 6: Generate ffmpeg command for mixing
        var outputFile = Path.Combine(folderPath, "final_mix.wav");
        var (ffmpegSuccess, ffmpegError) = await MixAudioWithFfmpegAsync(
            folderPath, 
            ttsTrackFiles, 
            ambienceTrackFiles, 
            fxTrackFiles, 
            musicTrackFiles,
            outputFile, 
            runId,
            story.Id);

        if (!ffmpegSuccess)
        {
            return (false, ffmpegError);
        }

        // Also create mp3 version
        var mp3OutputFile = Path.Combine(folderPath, "final_mix.mp3");
        try
        {
            var mp3Process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-y -i \"{outputFile}\" -codec:a libmp3lame -q:a 0 -b:a 320k \"{mp3OutputFile}\"",  // q:a 0 = massima qualit� VBR, -b:a 320k = bitrate massimo
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            mp3Process.Start();
            
            // Read streams asynchronously and wait with timeout
            var mp3StderrTask = mp3Process.StandardError.ReadToEndAsync();
            var mp3StdoutTask = mp3Process.StandardOutput.ReadToEndAsync();
            
            using var mp3Cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try
            {
                await mp3Process.WaitForExitAsync(mp3Cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { mp3Process.Kill(true); } catch { }
                _customLogger?.Append(runId, $"[{story.Id}] Timeout durante creazione MP3");
            }
            
            await mp3StderrTask;
            await mp3StdoutTask;
            
            if (mp3Process.ExitCode == 0)
            {
                _customLogger?.Append(runId, $"[{story.Id}] File MP3 creato: final_mix.mp3");
            }
        }
        catch (Exception ex)
        {
            _customLogger?.Append(runId, $"[{story.Id}] Avviso: impossibile creare MP3: {ex.Message}");
        }

        var successMsg = $"Mixaggio audio completato: final_mix.wav ({ttsTrackFiles.Count} voci, {ambienceTrackFiles.Count} ambience, {fxTrackFiles.Count} FX)";
        _customLogger?.Append(runId, $"[{story.Id}] {successMsg}");
        // Aggiorna lo stato della storia a `audio_master_generated` quando il mix finale e stato creato
        try
        {
            var audioMasterStatus = GetStoryStatusByCode("audio_master_generated");
            if (audioMasterStatus != null)
            {
                var updated = _database.UpdateStoryById(story.Id, statusId: audioMasterStatus.Id, updateStatus: true);
                if (updated)
                {
                    _customLogger?.Append(runId, $"[{story.Id}] Stato aggiornato a {audioMasterStatus.Code}");
                }
                else
                {
                    _customLogger?.Append(runId, $"[{story.Id}] Aggiornamento stato fallito: UpdateStoryById returned false");
                }
            }
            else
            {
                _customLogger?.Append(runId, $"[{story.Id}] Avviso: status code 'audio_master_generated' non trovato nel DB");
            }
        }
        catch (Exception ex)
        {
            _customLogger?.Append(runId, $"[{story.Id}] Errore aggiornamento stato audio master: {ex.Message}");
        }
        try { _database.UpdateStoryGeneratedMixedAudio(story.Id, true); } catch { }
        return (true, successMsg);
    }

    private async Task<(bool success, string? error)> MixAudioWithFfmpegAsync(
        string folderPath,
        List<(string FilePath, int StartMs)> ttsFiles,
        List<(string FilePath, int StartMs, int DurationMs)> ambienceFiles,
        List<(string FilePath, int StartMs)> fxFiles,
        List<(string FilePath, int StartMs, int DurationMs)> musicFiles,
        string outputFile,
        string runId,
        long storyId)
    {
        // NEW STRATEGY: Create 3 separate tracks, then mix them
        // Track 1: TTS voices (may require batching if >50 files)
        // Track 2: Ambience + FX (usually few files, no batching needed)
        // Track 3: Music (usually few files, no batching needed)
        // Final: Mix the 3 tracks together
        
        _customLogger?.Append(runId, $"[{storyId}] Riepilogo: {ttsFiles.Count} TTS, {ambienceFiles.Count} ambience, {fxFiles.Count} FX, {musicFiles.Count} music");
        
        try
        {
            var trackFiles = new List<string>();
            
            // ===== TRACK 1: TTS VOICES =====
            var ttsTrackFile = Path.Combine(folderPath, $"track_tts_{storyId}.wav");
            if (ttsFiles.Count > 0)
            {
                _customLogger?.Append(runId, $"[{storyId}] Generazione traccia TTS...");
                var ttsResult = await CreateTtsTrackAsync(folderPath, ttsFiles, ttsTrackFile, runId, storyId);
                if (!ttsResult.success)
                {
                    return ttsResult;
                }
                trackFiles.Add(ttsTrackFile);
            }
            
            // ===== TRACK 2: AMBIENCE + FX =====
            var ambienceFxTrackFile = Path.Combine(folderPath, $"track_ambience_fx_{storyId}.wav");
            if (ambienceFiles.Count > 0 || fxFiles.Count > 0)
            {
                _customLogger?.Append(runId, $"[{storyId}] Generazione traccia ambience+FX...");
                var ambienceFxResult = await CreateAmbienceFxTrackAsync(folderPath, ambienceFiles, fxFiles, ambienceFxTrackFile, runId, storyId);
                if (!ambienceFxResult.success)
                {
                    TryDeleteFile(ttsTrackFile);
                    return ambienceFxResult;
                }
                trackFiles.Add(ambienceFxTrackFile);
            }
            
            // ===== TRACK 3: MUSIC =====
            var musicTrackFile = Path.Combine(folderPath, $"track_music_{storyId}.wav");
            if (musicFiles.Count > 0)
            {
                _customLogger?.Append(runId, $"[{storyId}] Generazione traccia music...");
                var musicResult = await CreateMusicTrackAsync(folderPath, musicFiles, musicTrackFile, runId, storyId);
                if (!musicResult.success)
                {
                    TryDeleteFile(ttsTrackFile);
                    TryDeleteFile(ambienceFxTrackFile);
                    return musicResult;
                }
                trackFiles.Add(musicTrackFile);
            }
            
            // ===== FINAL MIX: Combine the 3 tracks =====
            if (trackFiles.Count == 0)
            {
                return (false, "Nessuna traccia audio da mixare");
            }
            
            if (trackFiles.Count == 1)
            {
                // Solo una traccia, copia diretta
                File.Copy(trackFiles[0], outputFile, overwrite: true);
                TryDeleteFile(trackFiles[0]);
            }
            else
            {
                _customLogger?.Append(runId, $"[{storyId}] Mix finale di {trackFiles.Count} tracce...");
                var finalResult = await MixFinalTracksAsync(trackFiles, outputFile, runId, storyId);
                
                // Cleanup track files
                foreach (var track in trackFiles)
                {
                    TryDeleteFile(track);
                }
                
                if (!finalResult.success)
                {
                    return finalResult;
                }
            }
            
            if (!File.Exists(outputFile))
            {
                return (false, "ffmpeg non ha creato il file di output");
            }

            var fileInfo = new FileInfo(outputFile);
            _customLogger?.Append(runId, $"[{storyId}] File finale creato: {Path.GetFileName(outputFile)} ({fileInfo.Length / 1024} KB)");
            return (true, null);
        }
        catch (Exception ex)
        {
            _customLogger?.Append(runId, $"[{storyId}] Eccezione durante il mix: {ex.Message}");
            return (false, $"Eccezione durante il mix: {ex.Message}");
        }
    }
    
    private async Task<(bool success, string? error)> CreateTtsTrackAsync(
        string folderPath,
        List<(string FilePath, int StartMs)> ttsFiles,
        string outputFile,
        string runId,
        long storyId)
    {
        const int MaxInputsPerBatch = 50;
        
        if (ttsFiles.Count <= MaxInputsPerBatch)
        {
            // Direct mix - all TTS files fit in one command
            var inputArgs = new StringBuilder();
            var filterArgs = new StringBuilder();
            var labels = new List<string>();
            
            for (int i = 0; i < ttsFiles.Count; i++)
            {
                var (filePath, startMs) = ttsFiles[i];
                inputArgs.Append($" -i \"{filePath}\"");
                var label = $"tts{i}";
                filterArgs.Append($"[{i}]adelay={startMs}|{startMs}[{label}];");
                labels.Add($"[{label}]");
            }
            
            foreach (var lbl in labels) filterArgs.Append(lbl);
            filterArgs.Append($"amix=inputs={labels.Count}:duration=longest:normalize=0[out]");
            
            var filterFile = Path.Combine(folderPath, $"filter_tts_{storyId}.txt");
            await File.WriteAllTextAsync(filterFile, filterArgs.ToString());
            
            var result = await RunFfmpegProcessAsync(inputArgs.ToString(), filterFile, outputFile, runId, storyId);
            TryDeleteFile(filterFile);
            return result;
        }
        else
        {
            // Batch processing for many TTS files
            _customLogger?.Append(runId, $"[{storyId}] TTS: batching {ttsFiles.Count} files...");
            
            var batchFiles = new List<string>();
            for (int i = 0; i < ttsFiles.Count; i += MaxInputsPerBatch)
            {
                var batch = ttsFiles.Skip(i).Take(MaxInputsPerBatch).ToList();
                var batchFile = Path.Combine(folderPath, $"batch_tts_{storyId}_{i / MaxInputsPerBatch}.wav");
                
                var batchResult = await CreateTtsBatchAsync(folderPath, batch, batchFile, runId, storyId, i / MaxInputsPerBatch);
                if (!batchResult.success)
                {
                    foreach (var f in batchFiles) TryDeleteFile(f);
                    return batchResult;
                }
                batchFiles.Add(batchFile);
            }
            
            // Merge all batches
            var mergeResult = await MergeAudioFilesAsync(batchFiles, outputFile, runId, storyId);
            foreach (var f in batchFiles) TryDeleteFile(f);
            return mergeResult;
        }
    }
    
    private async Task<(bool success, string? error)> CreateTtsBatchAsync(
        string folderPath,
        List<(string FilePath, int StartMs)> files,
        string outputFile,
        string runId,
        long storyId,
        int batchIndex)
    {
        var inputArgs = new StringBuilder();
        var filterArgs = new StringBuilder();
        var labels = new List<string>();
        
        _customLogger?.Append(runId, $"[{storyId}] TTS batch {batchIndex}: {files.Count} files");
        
        for (int i = 0; i < files.Count; i++)
        {
            var (filePath, startMs) = files[i];
            inputArgs.Append($" -i \"{filePath}\"");
            var label = $"t{i}";
            filterArgs.Append($"[{i}]adelay={startMs}|{startMs}[{label}];");
            labels.Add($"[{label}]");
        }
        
        foreach (var lbl in labels) filterArgs.Append(lbl);
        filterArgs.Append($"amix=inputs={labels.Count}:duration=longest:normalize=0[out]");
        
        var filterFile = Path.Combine(folderPath, $"filter_tts_batch_{storyId}_{batchIndex}.txt");
        await File.WriteAllTextAsync(filterFile, filterArgs.ToString());
        
        var result = await RunFfmpegProcessAsync(inputArgs.ToString(), filterFile, outputFile, runId, storyId);
        TryDeleteFile(filterFile);
        return result;
    }
    
    private async Task<(bool success, string? error)> CreateAmbienceFxTrackAsync(
        string folderPath,
        List<(string FilePath, int StartMs, int DurationMs)> ambienceFiles,
        List<(string FilePath, int StartMs)> fxFiles,
        string outputFile,
        string runId,
        long storyId)
    {
        var inputArgs = new StringBuilder();
        var filterArgs = new StringBuilder();
        var labels = new List<string>();
        int idx = 0;
        
        // Add ambience with timing and volume
        foreach (var (filePath, startMs, durationMs) in ambienceFiles)
        {
            inputArgs.Append($" -i \"{filePath}\"");
            var label = $"amb{idx}";
            var endSeconds = (durationMs / 1000.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            filterArgs.Append($"[{idx}]atrim=start=0:end={endSeconds},adelay={startMs}|{startMs},volume=0.30[{label}];");
            labels.Add($"[{label}]");
            idx++;
        }
        
        // Add FX with timing and volume
        foreach (var (filePath, startMs) in fxFiles)
        {
            inputArgs.Append($" -i \"{filePath}\"");
            var label = $"fx{idx}";
            filterArgs.Append($"[{idx}]adelay={startMs}|{startMs},volume=0.90[{label}];");
            labels.Add($"[{label}]");
            idx++;
        }
        
        if (labels.Count == 0)
        {
            return (false, "Nessun file ambience/FX");
        }
        
        foreach (var lbl in labels) filterArgs.Append(lbl);
        filterArgs.Append($"amix=inputs={labels.Count}:duration=longest:normalize=0[out]");
        
        var filterFile = Path.Combine(folderPath, $"filter_ambience_fx_{storyId}.txt");
        await File.WriteAllTextAsync(filterFile, filterArgs.ToString());
        
        var result = await RunFfmpegProcessAsync(inputArgs.ToString(), filterFile, outputFile, runId, storyId);
        TryDeleteFile(filterFile);
        return result;
    }
    
    private async Task<(bool success, string? error)> CreateMusicTrackAsync(
        string folderPath,
        List<(string FilePath, int StartMs, int DurationMs)> musicFiles,
        string outputFile,
        string runId,
        long storyId)
    {
        var inputArgs = new StringBuilder();
        var filterArgs = new StringBuilder();
        var labels = new List<string>();
        
        for (int i = 0; i < musicFiles.Count; i++)
        {
            var (filePath, startMs, durationMs) = musicFiles[i];
            inputArgs.Append($" -i \"{filePath}\"");
            var label = $"m{i}";
            var endSeconds = (durationMs / 1000.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

            var durationSeconds = durationMs / 1000.0;
            var fadeDurSeconds = Math.Min(2.0, Math.Max(0.0, durationSeconds));
            var fadeStartSeconds = Math.Max(0.0, durationSeconds - fadeDurSeconds);
            var fadeStart = fadeStartSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            var fadeDur = fadeDurSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

            filterArgs.Append($"[{i}]atrim=start=0:end={endSeconds},afade=t=out:st={fadeStart}:d={fadeDur},adelay={startMs}|{startMs},volume=0.70[{label}];");
            labels.Add($"[{label}]");
        }
        
        if (labels.Count == 0)
        {
            return (false, "Nessun file music");
        }
        
        foreach (var lbl in labels) filterArgs.Append(lbl);
        filterArgs.Append($"amix=inputs={labels.Count}:duration=longest:normalize=0[out]");
        
        var filterFile = Path.Combine(folderPath, $"filter_music_{storyId}.txt");
        await File.WriteAllTextAsync(filterFile, filterArgs.ToString());
        
        var result = await RunFfmpegProcessAsync(inputArgs.ToString(), filterFile, outputFile, runId, storyId);
        TryDeleteFile(filterFile);
        return result;
    }
    
    private async Task<(bool success, string? error)> MixFinalTracksAsync(
        List<string> trackFiles,
        string outputFile,
        string runId,
        long storyId)
    {
        // Mix the 3 tracks: TTS (full volume), Ambience+FX (already has volume), Music (already has volume)
        var inputArgs = new StringBuilder();
        var filterArgs = new StringBuilder();
        
        for (int i = 0; i < trackFiles.Count; i++)
        {
            inputArgs.Append($" -i \"{trackFiles[i]}\"");
            filterArgs.Append($"[{i}]");
        }
        
        // Mix with equal weights, then normalize
        filterArgs.Append($"amix=inputs={trackFiles.Count}:duration=longest:dropout_transition=2:normalize=0[mixed];[mixed]dynaudnorm=p=0.95:s=3[out]");
        
        var filterFile = Path.Combine(Path.GetDirectoryName(outputFile)!, $"filter_final_{storyId}.txt");
        await File.WriteAllTextAsync(filterFile, filterArgs.ToString());
        
        var result = await RunFfmpegProcessAsync(inputArgs.ToString(), filterFile, outputFile, runId, storyId);
        TryDeleteFile(filterFile);
        return result;
    }
    
    private async Task<(bool success, string? error)> MergeAudioFilesAsync(
        List<string> audioFiles,
        string outputFile,
        string runId,
        long storyId)
    {
        // Simple concatenation of audio files (no timing, already positioned)
        var inputArgs = new StringBuilder();
        var filterArgs = new StringBuilder();
        
        for (int i = 0; i < audioFiles.Count; i++)
        {
            inputArgs.Append($" -i \"{audioFiles[i]}\"");
            filterArgs.Append($"[{i}]");
        }
        
        filterArgs.Append($"amix=inputs={audioFiles.Count}:duration=longest:normalize=0[out]");
        
        var filterFile = Path.Combine(Path.GetDirectoryName(outputFile)!, $"filter_merge_{storyId}.txt");
        await File.WriteAllTextAsync(filterFile, filterArgs.ToString());
        
        var result = await RunFfmpegProcessAsync(inputArgs.ToString(), filterFile, outputFile, runId, storyId);
        TryDeleteFile(filterFile);
        return result;
    }
    
    private async Task<(bool success, string? error)> RunFfmpegProcessAsync(
        string inputArgs, 
        string filterScriptFile, 
        string outputFile, 
        string runId, 
        long storyId)
    {
        try
        {
            // Extract input file paths from inputArgs string (format: " -i "path1" -i "path2" ...")
            var inputFiles = new List<string>();
            var regex = new Regex(@"-i\s+""([^""]+)""");
            foreach (Match match in regex.Matches(inputArgs))
            {
                inputFiles.Add(match.Groups[1].Value);
            }
            
            _customLogger?.Append(runId, $"[{storyId}] ffmpeg con {inputFiles.Count} input files");
            
            // Build argument list for ffmpeg using ArgumentList (avoids command line length issues)
            var process = new Process();
            process.StartInfo.FileName = "ffmpeg";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            
            // Add arguments one by one using ArgumentList
            process.StartInfo.ArgumentList.Add("-y");
            
            foreach (var inputFile in inputFiles)
            {
                process.StartInfo.ArgumentList.Add("-i");
                process.StartInfo.ArgumentList.Add(inputFile);
            }
            
            process.StartInfo.ArgumentList.Add("-filter_complex_script");
            process.StartInfo.ArgumentList.Add(filterScriptFile);
            process.StartInfo.ArgumentList.Add("-map");
            process.StartInfo.ArgumentList.Add("[out]");
            process.StartInfo.ArgumentList.Add("-ac");
            process.StartInfo.ArgumentList.Add("2");
            process.StartInfo.ArgumentList.Add("-ar");
            process.StartInfo.ArgumentList.Add("48000");  // Mantenere qualit� audio 48kHz (stesso del TTS)
            process.StartInfo.ArgumentList.Add("-b:a");
            process.StartInfo.ArgumentList.Add("320k");   // Bitrate alto per qualit� master
            process.StartInfo.ArgumentList.Add(outputFile);
            
            process.Start();
            
            // Read stderr asynchronously to avoid deadlock when buffer fills up
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            
            // Wait for process with timeout to avoid hanging indefinitely
            var processExited = true;
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                processExited = false;
            }
            
            var stderr = await stderrTask;
            await stdoutTask; // Read stdout to prevent buffer issues

            if (!processExited)
            {
                _customLogger?.Append(runId, $"[{storyId}] Timeout ffmpeg (30 minuti)");
                return (false, "Timeout ffmpeg: il processo ha impiegato pi� di 30 minuti");
            }

            if (process.ExitCode != 0)
            {
                _customLogger?.Append(runId, $"[{storyId}] Errore ffmpeg: {stderr}");
                return (false, $"Errore ffmpeg (exit code {process.ExitCode}): {stderr.Substring(0, Math.Min(500, stderr.Length))}");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _customLogger?.Append(runId, $"[{storyId}] Eccezione ffmpeg: {ex.Message}");
            return (false, $"Eccezione durante l'esecuzione di ffmpeg: {ex.Message}");
        }
    }
    
    
    private void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    /// <summary>
    /// Mix final audio for a story by combining TTS, ambience, and FX tracks.
    /// If dispatcherRunId is provided, progress will be reported to the CommandDispatcher.
    /// </summary>
    public async Task<(bool success, string? error)> MixFinalAudioForStoryAsync(long storyId, string folderName, string? dispatcherRunId = null)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return (false, "Folder name is required");

        var story = GetStoryById(storyId);
        if (story == null)
            return (false, "Story not found");

        var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", folderName);
        Directory.CreateDirectory(folderPath);

        var context = new StoryCommandContext(story, folderPath, null);
        var deleteCmd = new DeleteFinalMixCommand(this);
        var (cleanupOk, cleanupMessage) = await deleteCmd.ExecuteAsync(context);
        if (!cleanupOk)
        {
            return (false, cleanupMessage ?? "Impossibile cancellare il mix finale esistente");
        }
        
        if (!string.IsNullOrWhiteSpace(dispatcherRunId))
        {
            return await MixFinalAudioInternalAsync(context, dispatcherRunId);
        }
        var (success, message) = await StartMixFinalAudioAsync(context);
        return (success, message);
    }

    private static Dictionary<string, CharacterVoiceInfo> BuildCharacterMap(JsonArray charactersArray)
    {
        var map = new Dictionary<string, CharacterVoiceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in charactersArray.OfType<JsonObject>())
        {
            var name = ReadString(node, "Name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var voiceId = ReadString(node, "VoiceId");
            var gender = ReadString(node, "Gender");

            map[name] = new CharacterVoiceInfo(name, voiceId, gender);
        }

        return map;
    }

    private static bool IsPhraseEntry(JsonObject entry)
    {
        // Try both casings: "character" and "Character"
        if (entry.TryGetPropertyValue("character", out var charNode) && charNode != null)
            return true;
        return entry.TryGetPropertyValue("Character", out charNode) && charNode != null;
    }

    private static bool IsPauseEntry(JsonObject entry, out int pauseMs)
    {
        pauseMs = 0;
        if (TryReadNumber(entry, "Seconds", out var seconds) || TryReadNumber(entry, "seconds", out seconds))
        {
            pauseMs = (int)Math.Max(0, Math.Round(seconds * 1000.0));
            return pauseMs > 0;
        }

        return false;
    }

    private static bool TryReadPhrase(JsonObject entry, out string character, out string text, out string emotion)
    {
        character = text = emotion = string.Empty;
        var charName = ReadString(entry, "Character");
        if (string.IsNullOrWhiteSpace(charName))
            return false;

        character = charName.Trim();
        text = ReadString(entry, "Text") ?? string.Empty;
        emotion = ReadString(entry, "Emotion") ?? "neutral";
        if (string.IsNullOrWhiteSpace(emotion))
            emotion = "neutral";
        emotion = emotion.Trim();
        return true;
    }

    private static string BuildAudioFileName(int index, string characterName, HashSet<string> usedFiles)
    {
        var sanitized = SanitizeForFile(characterName);
        var candidate = $"{index:000}_{sanitized}.wav";
        int suffix = 1;
        while (!usedFiles.Add(candidate))
        {
            candidate = $"{index:000}_{sanitized}_{suffix++}.wav";
        }
        return candidate;
    }

    private static string SanitizeForFile(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "phrase";

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);

        foreach (var c in name)
        {
            if (invalid.Contains(c) || char.IsWhiteSpace(c))
            {
                builder.Append('_');
            }
            else if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append('_');
            }
        }

        var result = builder.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(result) ? "phrase" : result;
    }

    // TTS API supported emotions
    private static readonly HashSet<string> ValidTtsEmotions = new(StringComparer.OrdinalIgnoreCase)
    {
        "neutral", "happy", "sad", "angry", "fearful", "disgusted", "surprised"
    };

    private async Task<(byte[] audioBytes, int? durationMs)> GenerateAudioBytesAsync(string voiceId, string text, string emotion)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Il testo della frase e vuoto");

        // Normalize emotion to valid TTS value or fallback to neutral
        var normalizedEmotion = NormalizeEmotionForTts(emotion);
        
        // Normalize text for TTS before sending to service
        var ttsSafeText = ProsodyNormalizer.NormalizeForTTS(text);

        var synthesis = await _ttsService.SynthesizeAsync(voiceId, ttsSafeText, "it", normalizedEmotion);
        if (synthesis == null)
            throw new InvalidOperationException("Il servizio TTS non ha restituito alcun risultato");

        byte[] audioBytes;
        if (!string.IsNullOrWhiteSpace(synthesis.AudioBase64))
        {
            audioBytes = Convert.FromBase64String(synthesis.AudioBase64);
        }
        else if (!string.IsNullOrWhiteSpace(synthesis.AudioUrl))
        {
            audioBytes = await _ttsService.DownloadAudioAsync(synthesis.AudioUrl);
        }
        else
        {
            throw new InvalidOperationException("Il servizio TTS non ha restituito dati audio");
        }

        int? durationMs = null;
        if (synthesis.DurationSeconds.HasValue)
        {
            durationMs = (int)Math.Max(0, Math.Round(synthesis.DurationSeconds.Value * 1000.0));
        }

        return (audioBytes, durationMs);
    }

    /// <summary>
    /// Normalizes emotion string to a valid TTS API value.
    /// Falls back to "neutral" if not recognized.
    /// </summary>
    private static string NormalizeEmotionForTts(string? emotion)
    {
        if (string.IsNullOrWhiteSpace(emotion))
            return "neutral";

        var trimmed = emotion.Trim().ToLowerInvariant();

        // Already valid
        if (ValidTtsEmotions.Contains(trimmed))
            return trimmed;

        // Common Italian -> English mappings
        return trimmed switch
        {
            "felice" or "gioioso" or "allegro" or "entusiasta" or "euforico" => "happy",
            "triste" or "malinconico" or "afflitto" or "depresso" => "sad",
            "arrabbiato" or "furioso" or "irato" or "adirato" or "irritato" => "angry",
            "spaventato" or "impaurito" or "terrorizzato" or "ansioso" or "preoccupato" => "fearful",
            "disgustato" or "nauseato" or "schifato" => "disgusted",
            "sorpreso" or "stupito" or "meravigliato" or "sbalordito" or "incredulo" => "surprised",
            "neutrale" or "calmo" or "sereno" or "tranquillo" or "riflessivo" or "pensieroso" 
                or "solenne" or "grave" or "serio" or "determinato" or "deciso" => "neutral",
            _ => "neutral" // Fallback for unrecognized emotions
        };
    }

    private static int? TryGetWavDuration(byte[] audioBytes)
    {
        try
        {
            using var stream = new MemoryStream(audioBytes, writable: false);
            using var reader = new BinaryReader(stream);

            if (reader.ReadUInt32() != 0x46464952) // "RIFF"
                return null;

            reader.ReadUInt32(); // chunk size
            if (reader.ReadUInt32() != 0x45564157) // "WAVE"
                return null;

            int byteRate = 0;
            int dataSize = 0;

            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            {
                var chunkId = reader.ReadUInt32();
                var chunkSize = reader.ReadInt32();

                if (chunkSize < 0 || reader.BaseStream.Position + chunkSize > reader.BaseStream.Length)
                    return null;

                if (chunkId == 0x20746d66) // "fmt "
                {
                    var fmtData = reader.ReadBytes(chunkSize);
                    if (chunkSize >= 12)
                    {
                        byteRate = BitConverter.ToInt32(fmtData, 8);
                    }
                }
                else if (chunkId == 0x61746164) // "data"
                {
                    dataSize = chunkSize;
                    reader.BaseStream.Seek(chunkSize, SeekOrigin.Current);
                    break;
                }
                else
                {
                    reader.BaseStream.Seek(chunkSize, SeekOrigin.Current);
                }

                if ((chunkSize & 1) == 1)
                {
                    reader.BaseStream.Seek(1, SeekOrigin.Current);
                }
            }

            if (byteRate > 0 && dataSize > 0)
            {
                var seconds = (double)dataSize / byteRate;
                return (int)Math.Max(0, Math.Round(seconds * 1000.0));
            }
        }
        catch
        {
            // ignore and fall back to duration from API
        }

        return null;
    }

    private static bool TryReadNumber(JsonObject entry, string propertyName, out double value)
    {
        value = 0;
        // Try both casings: as-is and lowercase
        if (!entry.TryGetPropertyValue(propertyName, out var node) || node == null)
        {
            var lowerName = char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);
            if (!entry.TryGetPropertyValue(lowerName, out node) || node == null)
                return false;
        }

        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<double>(out value))
                return true;

            if (jsonValue.TryGetValue<int>(out var intValue))
            {
                value = intValue;
                return true;
            }
        }

        return double.TryParse(node.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static string? ReadString(JsonObject entry, string propertyName)
    {
        // Try both casings: as-is and lowercase
        if (!entry.TryGetPropertyValue(propertyName, out var node) || node == null)
        {
            var lowerName = char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);
            if (!entry.TryGetPropertyValue(lowerName, out node) || node == null)
                return null;
        }

        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<string>(out var value))
                return value;
        }

        return node.ToString();
    }

    private sealed record CharacterVoiceInfo(string Name, string? VoiceId, string? Gender);

    private sealed class FunctionCallCommand : IStoryCommand
    {
        private readonly StoriesService _service;
        private readonly StoryStatus _status;

        public FunctionCallCommand(StoriesService service, StoryStatus status)
        {
            _service = service;
            _status = status;
        }

        public bool RequireStoryText => true;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            var functionName = _status.FunctionName ?? _status.Code ?? $"status_{_status.Id}";
            _service._logger?.LogWarning("FunctionCallCommand: function {Function} not implemented yet", functionName);
            return Task.FromResult<(bool success, string? message)>((false, $"Funzione '{functionName}' non ancora implementata.")); 
        }
    }

    private sealed class NotImplementedCommand : IStoryCommand
    {
        private readonly string _message;

        public NotImplementedCommand(string reason)
        {
            _message = $"Operazione non implementata ({reason}).";
        }

        public bool RequireStoryText => true;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
            => Task.FromResult<(bool success, string? message)>((false, _message));
    }

    internal static class StoriesServiceDefaults
    {
        public const string EvaluatorTextProtocolInstructions =
@"Sei un valutatore narrativo tecnico, rigoroso e non compiacente.

    Devi restituire la valutazione completa nel formato seguente, senza aggiungere nulla prima o dopo.

    Formato OBBLIGATORIO:

    Coerenza narrativa
    <numero da 1 a 5>
    <breve spiegazione tecnica (1-2 frasi)>

    Originalita
    <numero da 1 a 5>
    <breve spiegazione tecnica (1-2 frasi)>

    Impatto emotivo
    <numero da 1 a 5>
    <breve spiegazione tecnica (1-2 frasi)>

    Azione
    <numero da 1 a 5>
    <breve spiegazione tecnica (1-2 frasi)>

    ========================
    VINCOLI ASSOLUTI
    ========================

    - NON riassumere la storia
    - NON suggerire miglioramenti
    - NON usare markup o elenchi
    - NON usare linguaggio morale o emotivo
    - NON aggiungere spiegazioni fuori formato";
        public static string GetDefaultTtsStructuredInstructions() => @"Leggi attentamente il testo del chunk e trascrivilo integralmente nel formato seguente, senza riassumere o saltare frasi, senza aggiungere note o testo extra.

Usa SOLO queste sezioni ripetute nell'ordine del testo:

[NARRATORE]
Testo narrativo cos� come appare nel chunk

[PERSONAGGIO: NomePersonaggio | EMOZIONE: emotion]
Battuta di dialogo cos� come appare nel chunk

Regole:
- NON cambiare lingua, NON abbreviare, NON riassumere.
- Se non e chiaramente un dialogo, usa NARRATORE.
- EMOZIONE: usa una tra neutral, happy, sad, angry, fearful, disgusted, surprised (default neutral se non indicata).
- Non aggiungere spiegazioni o altro testo fuori dai blocchi.
- Copri tutto il chunk, pi� blocchi uno dopo l'altro finch� il chunk e esaurito.";
    }
}





