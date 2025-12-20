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
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TinyGenerator.Models;
using TinyGenerator.Skills;

namespace TinyGenerator.Services;

public sealed class StoriesService
{
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
    private static readonly JsonSerializerOptions SchemaJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

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

    public long InsertSingleStory(string prompt, string story, int? modelId = null, int? agentId = null, double score = 0.0, string? eval = null, int approved = 0, int? statusId = null, string? memoryKey = null)
    {
        return _database.InsertSingleStory(prompt, story, modelId, agentId, score, eval, approved, statusId, memoryKey);
    }

    public bool UpdateStoryById(long id, string? story = null, int? modelId = null, int? agentId = null, int? statusId = null, bool updateStatus = false)
    {
        return _database.UpdateStoryById(id, story, modelId, agentId, statusId, updateStatus);
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
                    _customLogger?.Append(null, $"[Scan] Folder '{folderName}' contains final_mix.wav but no DB story found.");
                    continue;
                }

                if (audioStatus == null)
                {
                    _customLogger?.Append(null, "[Scan] Status code 'audio_master_generated' not found in DB.");
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
                    _customLogger?.Append(null, $"[Scan] Story {story.Id} ('{folderName}') status updated to {audioStatus.Code}.");
                }
                else
                {
                    _customLogger?.Append(null, $"[Scan] Failed to update story {story.Id} ('{folderName}').");
                }
            }
            catch (Exception ex)
            {
                _customLogger?.Append(null, $"[Scan] Exception scanning '{dir}': {ex.Message}");
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
        if (command.RequireStoryText && string.IsNullOrWhiteSpace(story.Story))
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
        if (story == null || string.IsNullOrWhiteSpace(story.Story))
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
            
            var chatBridge = _kernelFactory.CreateChatBridge(modelName, agent.Temperature, agent.TopP);

            var systemMessage = !string.IsNullOrWhiteSpace(agent.Instructions)
                ? agent.Instructions
                : ComposeSystemMessage(agent);

            // Use different prompt based on agent role
            bool isCoherenceEvaluation = agent.Role?.Equals("coherence_evaluator", StringComparison.OrdinalIgnoreCase) ?? false;
            var prompt = BuildStoryEvaluationPrompt(story, isCoherenceEvaluation);

            var beforeEvaluations = _database.GetStoryEvaluations(storyId)
                .Select(e => e.Id)
                .ToHashSet();

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
        if (story == null || string.IsNullOrWhiteSpace(story.Story))
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

            var chatBridge = _kernelFactory.CreateChatBridge(modelName, agent.Temperature, agent.TopP);

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
        if (string.IsNullOrWhiteSpace(folderName))
            return (false, "Folder name is required");

        var story = GetStoryById(storyId);
        if (story == null)
            return (false, "Story not found");

        // Usa il nuovo flusso basato su tts_schema.json e timeline
        var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", folderName);
        Directory.CreateDirectory(folderPath);

        var context = new StoryCommandContext(story, folderPath, null);
        // Run synchronously when dispatcherRunId is provided so we can report progress
        if (!string.IsNullOrWhiteSpace(dispatcherRunId))
        {
            return await GenerateTtsAudioWithProgressAsync(context, dispatcherRunId);
        }
        var (success, message) = await StartTtsAudioGenerationAsync(context);
        return (success, message);
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
            storyText = story.Story ?? string.Empty
        });

        var threadId = unchecked((int)(story.Id % int.MaxValue));
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
                initialContext: story.Story ?? string.Empty,
                threadId: threadId,
                templateInstructions: string.IsNullOrWhiteSpace(template.Instructions) ? null : template.Instructions,
                executorModelOverride: model.Name);

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
        
        // Remove punctuation that TTS might read as words (e.g., "punto", "punto punto")
        // Keep punctuation that affects intonation: , ; : ? !
        // Remove: . .. ... and multiple dots, also standalone punctuation
        result = Regex.Replace(result, @"\.{2,}", " ");  // Replace multiple dots with space
        result = Regex.Replace(result, @"(?<!\d)\.(?!\d)", " ");  // Remove single dots except in numbers
        result = Regex.Replace(result, @"\s+", " ");  // Normalize whitespace
        return result.Trim();
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
                    // nothing to rename on disk, but still update db to the target name
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
            builder.AppendLine("After you have reviewed the necessary sections, call the evaluate_full_story function exactly once with the provided story_id.");
            builder.AppendLine("If you finish your review but fail to call evaluate_full_story, the orchestrator will remind you and ask again up to 3 times â€” you MUST call the function before the evaluation completes.");
            builder.AppendLine("Populate the following scores (0-10): narrative_coherence_score, originality_score, emotional_impact_score, action_score.");
            builder.AppendLine("All score fields MUST be integers between 0 and 10 (use 0 if you cannot determine a score). Do NOT send strings like \"None\".");
            builder.AppendLine("Also include the corresponding *_defects values (empty string or \"None\" is acceptable if there are no defects).");
            builder.AppendLine("Do not return an overall evaluation text â€“ the system will compute the aggregate score automatically.");
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
        builder.AppendLine("3) Compute scores (0.0â€“1.0):");
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
                    _ => new NotImplementedCommand($"agent_call:{agentType ?? "unknown"}")
                };
            case "function_call":
                var functionName = status.FunctionName?.ToLowerInvariant();
                return functionName switch
                {
                    "generate_tts_audio" or "tts_audio" or "build_tts_audio" or "generate_voice_tts" or "generate_voices" => new GenerateTtsAudioCommand(this),
                    "generate_ambience_audio" or "ambience_audio" or "generate_ambient" or "ambient_sounds" => new GenerateAmbienceAudioCommand(this),
                    "generate_fx_audio" or "fx_audio" or "generate_fx" or "sound_effects" => new GenerateFxAudioCommand(this),
                    "generate_music" or "music_audio" or "generate_music_audio" => new GenerateMusicCommand(this),
                    "generate_audio_master" or "audio_master" or "mix_audio" or "mix_final" or "final_mix" => new MixFinalAudioCommand(this),
                    "assign_voices" or "voice_assignment" => new AssignVoicesCommand(this),
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

        public bool RequireStoryText => true;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            var story = context.Story;
            var folderPath = context.FolderPath;

            if (string.IsNullOrWhiteSpace(story.Story))
            {
                return Task.FromResult<(bool, string?)>((false, "Il testo della storia Ã¨ vuoto"));
            }

            try
            {
                // Use direct parsing instead of agent
                var generator = new TtsSchemaGenerator(_service._customLogger, _service._database);
                var schema = generator.GenerateFromStoryText(story.Story);

                if (schema.Timeline.Count == 0)
                {
                    return Task.FromResult<(bool, string?)>((false, "Nessuna frase trovata nel testo. Assicurati che il testo contenga tag come [NARRATORE], [personaggio, emozione]"));
                }

                // Assign voices to characters
                generator.AssignVoices(schema);

                // Save the schema
                var schemaPath = Path.Combine(folderPath, "tts_schema.json");
                var jsonOptions = new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                var json = JsonSerializer.Serialize(schema, jsonOptions);
                File.WriteAllText(schemaPath, json);

                _service._database.UpdateStoryById(story.Id, statusId: TtsSchemaReadyStatusId, updateStatus: true);

                _service._logger?.LogInformation(
                    "TTS schema generato per storia {StoryId}: {Characters} personaggi, {Phrases} frasi",
                    story.Id, schema.Characters.Count, schema.Timeline.Count);

                return Task.FromResult<(bool, string?)>((true, $"Schema TTS generato: {schema.Characters.Count} personaggi, {schema.Timeline.Count} frasi"));
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante la generazione del TTS schema per la storia {Id}", story.Id);
                return Task.FromResult<(bool, string?)>((false, ex.Message));
            }
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
                return Task.FromResult<(bool, string?)>((false, $"La lista personaggi della storia Ã¨ vuota o non valida. JSON attuale: {story.Characters.Substring(0, Math.Min(200, story.Characters.Length))}..."));
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
                    return Task.FromResult<(bool, string?)>((true, "Nessuna normalizzazione necessaria: tutti i nomi sono giÃ  canonici."));
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
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                var updatedJson = JsonSerializer.Serialize(schema, outputOptions);
                File.WriteAllText(schemaPath, updatedJson);

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
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                var updatedJson = JsonSerializer.Serialize(schema, outputOptions);
                await File.WriteAllTextAsync(schemaPath, updatedJson);

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

                // Load available voices from database
                var allVoices = _service._database.ListTtsVoices();
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
                        $"Non Ã¨ stato possibile assegnare voci a: {string.Join(", ", missingVoices)}. " +
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
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                var updatedJson = JsonSerializer.Serialize(schema, outputOptions);
                File.WriteAllText(schemaPath, updatedJson);

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
                "mezza etÃ " or "middle-aged" or "middle aged" => 50,
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

            var catalogVoices = _database.ListTtsVoices();
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
                await File.WriteAllTextAsync(schemaPath, normalized);
            }

            return updated;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Errore durante ApplyVoiceAssignmentFallbacksAsync per schema {SchemaPath}", schemaPath);
            return false;
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

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            return _service.StartTtsAudioGenerationAsync(context);
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
            try
            {
                var cleanText = CleanTtsText(text);
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

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            return _service.StartAmbienceAudioGenerationAsync(context);
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

        // Build list of ambience segments: group consecutive phrases with same ambience
        var ambienceSegments = ExtractAmbienceSegments(phraseEntries);
        if (ambienceSegments.Count == 0)
        {
            var msg = "Nessun segmento ambientale trovato nella timeline (nessuna proprietà 'ambience' presente)";
            _customLogger?.Append(runId, $"[{story.Id}] {msg}");
            return (true, msg);
        }

        _customLogger?.Append(runId, $"[{story.Id}] Trovati {ambienceSegments.Count} segmenti ambientali da generare");

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

                // Update tts_schema.json: add ambience_file to each phrase in this segment
                foreach (var entryIndex in segment.EntryIndices)
                {
                    if (entryIndex >= 0 && entryIndex < phraseEntries.Count)
                    {
                        phraseEntries[entryIndex]["ambience_file"] = localFileName;
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
            var updated = rootNode.ToJsonString(SchemaJsonOptions);
            await File.WriteAllTextAsync(schemaPath, updated);
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

    private static List<AmbienceSegment> ExtractAmbienceSegments(List<JsonObject> entries)
    {
        var segments = new List<AmbienceSegment>();
        string? currentAmbience = null;
        int segmentStartMs = 0;
        int currentEndMs = 0;
        var currentEntryIndices = new List<int>();

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var ambience = ReadString(entry, "ambience") ?? ReadString(entry, "Ambience");
            
            // Read startMs and endMs using TryReadNumber
            int startMs = 0;
            int endMs = 0;
            if (TryReadNumber(entry, "startMs", out var startVal) || TryReadNumber(entry, "StartMs", out startVal))
                startMs = (int)startVal;
            if (TryReadNumber(entry, "endMs", out var endVal) || TryReadNumber(entry, "EndMs", out endVal))
                endMs = (int)endVal;
            if (endMs == 0) endMs = startMs;

            // If ambience changes, save the current segment
            if (!string.IsNullOrWhiteSpace(currentAmbience) && 
                (ambience != currentAmbience || string.IsNullOrWhiteSpace(ambience)))
            {
                var duration = currentEndMs - segmentStartMs;
                if (duration > 0 && currentEntryIndices.Count > 0)
                {
                    segments.Add(new AmbienceSegment(currentAmbience!, segmentStartMs, duration, new List<int>(currentEntryIndices)));
                }
                currentAmbience = null;
                currentEntryIndices.Clear();
            }

            // Start or continue a segment
            if (!string.IsNullOrWhiteSpace(ambience))
            {
                if (currentAmbience == null)
                {
                    currentAmbience = ambience;
                    segmentStartMs = startMs;
                }
                currentEndMs = endMs > 0 ? endMs : startMs;
                currentEntryIndices.Add(i);
            }
        }

        // Add final segment if any
        if (!string.IsNullOrWhiteSpace(currentAmbience) && currentEntryIndices.Count > 0)
        {
            var duration = currentEndMs - segmentStartMs;
            if (duration > 0)
            {
                segments.Add(new AmbienceSegment(currentAmbience!, segmentStartMs, duration, new List<int>(currentEntryIndices)));
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

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            return _service.StartFxAudioGenerationAsync(context);
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
            var msg = "Nessun effetto sonoro da generare (nessuna proprietà 'fxDescription' presente)";
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
            var updated = rootNode.ToJsonString(SchemaJsonOptions);
            await File.WriteAllTextAsync(schemaPath, updated);
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

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            return _service.StartMusicGenerationAsync(context);
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

        // Step 2: Find entries with musicDescription and generate music
        var musicService = new AudioCraftService();
        int musicCounter = 0;
        string? lastMusicDescription = null;

        // If no writer-specified music instructions exist, generate music automatically
        var anyMusicRequested = phraseEntries.Any(e =>
            !string.IsNullOrWhiteSpace(ReadString(e, "musicDescription") ?? ReadString(e, "MusicDescription") ?? ReadString(e, "music_description")));

        if (!anyMusicRequested)
        {
            _customLogger?.Append(runId, $"[{story.Id}] Nessuna indicazione musica trovata: generazione automatica in base alle emozioni");

            // Emotion keyword sets (both English and common Italian terms)
            var angrySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "angry", "arrabbiato", "furioso", "rabido" };
            var fearfulSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "fearful", "spaventato", "pauroso", "terrorizzato" };
            var sadSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sad", "triste", "addolorato" };
            var happySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "happy", "felice", "allegro", "contento" };

            // Helper to read phrase emotion and start/duration
            int ReadStartMs(JsonObject e)
            {
                if (TryReadNumber(e, "startMs", out var s) || TryReadNumber(e, "StartMs", out s) || TryReadNumber(e, "start_ms", out s))
                    return (int)s;
                return 0;
            }
            int ReadDurationMs(JsonObject e)
            {
                if (TryReadNumber(e, "durationMs", out var d) || TryReadNumber(e, "DurationMs", out d) || TryReadNumber(e, "duration_ms", out d))
                    return (int)d;
                return 10000; // fallback 10s per phrase
            }

            // Find first angry/fearful, then first sad, then first happy
            JsonObject? angryTarget = null;
            JsonObject? sadTarget = null;
            JsonObject? happyTarget = null;

            foreach (var entry in phraseEntries)
            {
                if (TryReadPhrase(entry, out var ch, out var txt, out var emotion))
                {
                    if (angryTarget == null && (angrySet.Contains(emotion) || fearfulSet.Contains(emotion)))
                        angryTarget = entry;
                    if (sadTarget == null && sadSet.Contains(emotion))
                        sadTarget = entry;
                    if (happyTarget == null && happySet.Contains(emotion))
                        happyTarget = entry;
                    if (angryTarget != null && sadTarget != null && happyTarget != null)
                        break;
                }
            }

            var autoTargets = new List<(JsonObject Entry, string Prompt)>();
            if (angryTarget != null)
                autoTargets.Add((angryTarget, "musica orchestrale ritmica triller di 10 secondi"));
            if (sadTarget != null)
                autoTargets.Add((sadTarget, "musica orchestrale triste di 10 secondi"));
            if (happyTarget != null)
                autoTargets.Add((happyTarget, "musica orchestrale allegra di 10 secondi"));

            // If no emotion targets found, create periodic background music every 30 seconds
            if (autoTargets.Count == 0)
            {
                _customLogger?.Append(runId, $"[{story.Id}] Nessuna emozione trovata: generazione musica di sottofondo ogni 30s");

                // Estimate story duration by looking at phrase start+duration; fallback to phrases*10s
                int estimatedTotalMs = 0;
                foreach (var entry in phraseEntries)
                {
                    var s = ReadStartMs(entry);
                    var d = ReadDurationMs(entry);
                    estimatedTotalMs = Math.Max(estimatedTotalMs, s + d);
                }
                if (estimatedTotalMs == 0)
                    estimatedTotalMs = Math.Max(phraseEntries.Count * 10000, 60000); // at least 1 minute

                for (int t = 0; t < estimatedTotalMs; t += 30000)
                {
                    // Find the phrase that starts at or before t, otherwise the first phrase
                    var candidate = phraseEntries.LastOrDefault(pe => ReadStartMs(pe) <= t) ?? phraseEntries.First();
                    autoTargets.Add((candidate, "musica orchestrale leggera di 10 secondi"));
                }
            }

            // Generate music for autoTargets, avoiding overlaps
            long lastMusicEndMs = -1;
            foreach (var (entry, prompt) in autoTargets)
            {
                try
                {
                    musicCounter++;
                    _customLogger?.Append(runId, $"[{story.Id}] Generazione musica automatica {musicCounter}: {prompt}");
                    var (success, fileUrl, error) = await musicService.GenerateMusicAsync(prompt, 10.0f);
                    if (!success || string.IsNullOrWhiteSpace(fileUrl))
                    {
                        _customLogger?.Append(runId, $"[{story.Id}] Avviso: impossibile generare musica automatica per '{prompt}': {error}");
                        continue;
                    }

                    var localFileName = $"music_auto_{musicCounter:D3}.wav";
                    var localFilePath = Path.Combine(folderPath, localFileName);
                    var audioBytes = await musicService.DownloadFileAsync(fileUrl);
                    if (audioBytes == null)
                    {
                        _customLogger?.Append(runId, $"[{story.Id}] Avviso: impossibile scaricare musica da {fileUrl}");
                        continue;
                    }
                    await File.WriteAllBytesAsync(localFilePath, audioBytes);
                    _customLogger?.Append(runId, $"[{story.Id}] Salvata musica automatica: {localFileName}");

                    // Attach to entry
                    entry["musicFile"] = localFileName;
                    entry["musicDuration"] = 10; // seconds

                    // Compute desired start (phrase start + 2000ms)
                    var phraseStart = ReadStartMs(entry);
                    long desiredStart = phraseStart + 2000;
                    if (lastMusicEndMs >= 0 && desiredStart < lastMusicEndMs + 100)
                    {
                        desiredStart = lastMusicEndMs + 100; // small gap
                    }
                    entry["musicStartMs"] = desiredStart;
                    lastMusicEndMs = desiredStart + 10000; // 10s
                }
                catch (Exception ex)
                {
                    _customLogger?.Append(runId, $"[{story.Id}] Errore generazione musica automatica: {ex.Message}");
                }
            }
        }

        // Generate music for explicit musicDescription entries (as before)
        foreach (var entry in phraseEntries)
        {
            try
            {
                var musicDesc = ReadString(entry, "musicDescription") ?? ReadString(entry, "MusicDescription") ?? ReadString(entry, "music_description");
                
                if (string.IsNullOrWhiteSpace(musicDesc))
                    continue;

                // Check if this is the same as the previous music request (skip duplicates)
                if (musicDesc.Equals(lastMusicDescription, StringComparison.OrdinalIgnoreCase))
                {
                    _customLogger?.Append(runId, $"[{story.Id}] Skipping duplicate music request: {musicDesc}");
                    continue;
                }

                lastMusicDescription = musicDesc;
                musicCounter++;

                _customLogger?.Append(runId, $"[{story.Id}] Generazione musica {musicCounter}: {musicDesc}");

                // Generate music using MusicGen (10 seconds fixed duration)
                var (success, fileUrl, error) = await musicService.GenerateMusicAsync(musicDesc, 10.0f);
                
                if (!success || string.IsNullOrWhiteSpace(fileUrl))
                {
                    _customLogger?.Append(runId, $"[{story.Id}] Avviso: impossibile generare musica per '{musicDesc}': {error}");
                    continue;
                }

                // Download the generated music file
                var localFileName = $"music_{musicCounter:D3}.wav";
                var localFilePath = Path.Combine(folderPath, localFileName);

                var audioBytes = await musicService.DownloadFileAsync(fileUrl);
                if (audioBytes == null)
                {
                    _customLogger?.Append(runId, $"[{story.Id}] Avviso: impossibile scaricare musica da {fileUrl}");
                    continue;
                }

                await File.WriteAllBytesAsync(localFilePath, audioBytes);
                _customLogger?.Append(runId, $"[{story.Id}] Salvata musica: {localFileName}");

                // Update tts_schema.json: add musicFile property if not already set by auto generation
                if (!entry.ContainsKey("musicFile"))
                    entry["musicFile"] = localFileName;
                if (!entry.ContainsKey("musicDuration"))
                {
                    entry["musicDuration"] = 10;
                }
            }
            catch (Exception ex)
            {
                _customLogger?.Append(runId, $"[{story.Id}] Errore generazione musica {musicCounter}: {ex.Message}");
                // Continue with next music request
            }
        }

        // Step 3: Save updated schema
        try
        {
            var updated = rootNode.ToJsonString(SchemaJsonOptions);
            await File.WriteAllTextAsync(schemaPath, updated);
        }
        catch (Exception ex)
        {
            var err = $"Impossibile aggiornare tts_schema.json: {ex.Message}";
            _logger?.LogError(ex, "Errore salvataggio schema TTS per la storia {Id}", story.Id);
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var successMsg = $"Generazione musica completata ({musicCounter} tracce)";
        _customLogger?.Append(runId, $"[{story.Id}] {successMsg}");
        return (true, successMsg);
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

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            return _service.StartMixFinalAudioAsync(context);
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
            _customLogger?.Append(runId, $"[{story.Id}] File TTS mancanti per le frasi: {string.Join(", ", missingTts)}. Avvio generazione TTS...");
            
            // Launch TTS generation and wait
            var ttsContext = new StoryCommandContext(story, folderPath, null);
            var (ttsSuccess, ttsMessage) = await GenerateTtsAudioInternalAsync(ttsContext, runId);
            if (!ttsSuccess)
            {
                var err = $"Generazione TTS fallita: {ttsMessage}";
                _customLogger?.Append(runId, $"[{story.Id}] {err}");
                return (false, err);
            }
            
            // Re-read schema after TTS generation
            try
            {
                var json = await File.ReadAllTextAsync(schemaPath);
                rootNode = JsonNode.Parse(json) as JsonObject;
                if (!rootNode!.TryGetPropertyValue("timeline", out timelineNode))
                    rootNode.TryGetPropertyValue("Timeline", out timelineNode);
                timelineArray = (timelineNode as JsonArray) ?? new JsonArray();
                phraseEntries = timelineArray.OfType<JsonObject>().Where(IsPhraseEntry).ToList();
            }
            catch (Exception ex)
            {
                return (false, $"Errore rilettura schema dopo TTS: {ex.Message}");
            }
        }

        // Step 3: Check for missing ambience audio files
        var ambienceNeeded = phraseEntries.Any(e => 
            !string.IsNullOrWhiteSpace(ReadString(e, "ambience") ?? ReadString(e, "Ambience")));
        
        if (ambienceNeeded)
        {
            var missingAmbience = phraseEntries.Where(e =>
            {
                var ambience = ReadString(e, "ambience") ?? ReadString(e, "Ambience");
                if (string.IsNullOrWhiteSpace(ambience)) return false;
                var ambienceFile = ReadString(e, "ambience_file") ?? ReadString(e, "ambienceFile") ?? ReadString(e, "AmbienceFile");
                if (string.IsNullOrWhiteSpace(ambienceFile)) return true;
                return !File.Exists(Path.Combine(folderPath, ambienceFile));
            }).ToList();

            if (missingAmbience.Count > 0)
            {
                _customLogger?.Append(runId, $"[{story.Id}] {missingAmbience.Count} file ambience mancanti. Avvio generazione ambience...");
                
                var ambienceContext = new StoryCommandContext(story, folderPath, null);
                var (ambiSuccess, ambiMessage) = await GenerateAmbienceAudioInternalAsync(ambienceContext, runId);
                if (!ambiSuccess)
                {
                    var err = $"Generazione ambience fallita: {ambiMessage}";
                    _customLogger?.Append(runId, $"[{story.Id}] {err}");
                    return (false, err);
                }

                // Re-read schema
                try
                {
                    var json = await File.ReadAllTextAsync(schemaPath);
                    rootNode = JsonNode.Parse(json) as JsonObject;
                    if (!rootNode!.TryGetPropertyValue("timeline", out timelineNode))
                        rootNode.TryGetPropertyValue("Timeline", out timelineNode);
                    timelineArray = (timelineNode as JsonArray) ?? new JsonArray();
                    phraseEntries = timelineArray.OfType<JsonObject>().Where(IsPhraseEntry).ToList();
                }
                catch (Exception ex)
                {
                    return (false, $"Errore rilettura schema dopo ambience: {ex.Message}");
                }
            }
        }

        // Step 4: Check for missing FX audio files
        var fxNeeded = phraseEntries.Any(e => 
            !string.IsNullOrWhiteSpace(ReadString(e, "fxDescription") ?? ReadString(e, "FxDescription") ?? ReadString(e, "fx_description")));
        
        if (fxNeeded)
        {
            var missingFx = phraseEntries.Where(e =>
            {
                var fxDesc = ReadString(e, "fxDescription") ?? ReadString(e, "FxDescription") ?? ReadString(e, "fx_description");
                if (string.IsNullOrWhiteSpace(fxDesc)) return false;
                var fxFile = ReadString(e, "fx_file") ?? ReadString(e, "fxFile") ?? ReadString(e, "FxFile");
                if (string.IsNullOrWhiteSpace(fxFile)) return true;
                return !File.Exists(Path.Combine(folderPath, fxFile));
            }).ToList();

            if (missingFx.Count > 0)
            {
                _customLogger?.Append(runId, $"[{story.Id}] {missingFx.Count} file FX mancanti. Avvio generazione FX...");
                
                var fxContext = new StoryCommandContext(story, folderPath, null);
                var (fxSuccess, fxMessage) = await GenerateFxAudioInternalAsync(fxContext, runId);
                if (!fxSuccess)
                {
                    var err = $"Generazione FX fallita: {fxMessage}";
                    _customLogger?.Append(runId, $"[{story.Id}] {err}");
                    return (false, err);
                }

                // Re-read schema
                try
                {
                    var json = await File.ReadAllTextAsync(schemaPath);
                    rootNode = JsonNode.Parse(json) as JsonObject;
                    if (!rootNode!.TryGetPropertyValue("timeline", out timelineNode))
                        rootNode.TryGetPropertyValue("Timeline", out timelineNode);
                    timelineArray = (timelineNode as JsonArray) ?? new JsonArray();
                    phraseEntries = timelineArray.OfType<JsonObject>().Where(IsPhraseEntry).ToList();
                }
                catch (Exception ex)
                {
                    return (false, $"Errore rilettura schema dopo FX: {ex.Message}");
                }
            }
        }

        _customLogger?.Append(runId, $"[{story.Id}] Tutti i file audio presenti. Avvio mixaggio...");

        // Step 5: Build file lists for mixing
        var ttsTrackFiles = new List<(string FilePath, int StartMs)>();
        var ambienceTrackFiles = new List<(string FilePath, int StartMs, int DurationMs)>();
        var fxTrackFiles = new List<(string FilePath, int StartMs)>();
        var musicTrackFiles = new List<(string FilePath, int StartMs, int DurationMs)>();

        int currentTimeMs = 0;
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
                        startMs = (int)s;
                    ttsTrackFiles.Add((ttsFilePath, startMs));

                    // Update current time based on duration
                    int durationMs = 2000; // default gap
                    if (TryReadNumber(entry, "durationMs", out var d) || TryReadNumber(entry, "DurationMs", out d) || TryReadNumber(entry, "duration_ms", out d))
                        durationMs = (int)d;
                    currentTimeMs = startMs + durationMs;
                }
            }

            // Ambience file
            var ambienceFile = ReadString(entry, "ambience_file") ?? ReadString(entry, "ambienceFile") ?? ReadString(entry, "AmbienceFile");
            if (!string.IsNullOrWhiteSpace(ambienceFile))
            {
                var ambienceFilePath = Path.Combine(folderPath, ambienceFile);
                if (File.Exists(ambienceFilePath))
                {
                    int startMs = 0;
                    if (TryReadNumber(entry, "startMs", out var s) || TryReadNumber(entry, "StartMs", out s) || TryReadNumber(entry, "start_ms", out s))
                        startMs = (int)s;
                    int durationMs = 30000; // default 30s for ambience
                    ambienceTrackFiles.Add((ambienceFilePath, startMs, durationMs));
                }
            }

            // FX file - starts at middle of phrase duration
            var fxFile = ReadString(entry, "fx_file") ?? ReadString(entry, "fxFile") ?? ReadString(entry, "FxFile");
            if (!string.IsNullOrWhiteSpace(fxFile))
            {
                var fxFilePath = Path.Combine(folderPath, fxFile);
                if (File.Exists(fxFilePath))
                {
                    int startMs = 0;
                    int phraseDurationMs = 0;
                    if (TryReadNumber(entry, "startMs", out var s) || TryReadNumber(entry, "StartMs", out s) || TryReadNumber(entry, "start_ms", out s))
                        startMs = (int)s;
                    if (TryReadNumber(entry, "durationMs", out var d) || TryReadNumber(entry, "DurationMs", out d) || TryReadNumber(entry, "duration_ms", out d))
                        phraseDurationMs = (int)d;
                    
                    // FX starts at middle of phrase
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
                    int startMs = 0;
                    if (TryReadNumber(entry, "startMs", out var s) || TryReadNumber(entry, "StartMs", out s) || TryReadNumber(entry, "start_ms", out s))
                        startMs = (int)s;
                    int durationMs = 10000; // default 10s for music
                    if (TryReadNumber(entry, "musicDuration", out var md) || TryReadNumber(entry, "MusicDuration", out md) || TryReadNumber(entry, "music_duration", out md))
                        durationMs = (int)md * 1000; // Convert seconds to ms
                    
                    // Music start: prefer explicit 'musicStartMs' if provided, otherwise 2 seconds after phrase start
                    int musicStartMs = startMs + 2000;
                    if (TryReadNumber(entry, "musicStartMs", out var msOverride))
                    {
                        musicStartMs = (int)msOverride;
                    }
                    musicTrackFiles.Add((musicFilePath, musicStartMs, durationMs));
                }
            }
        }

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
                    Arguments = $"-y -i \"{outputFile}\" -codec:a libmp3lame -qscale:a 2 \"{mp3OutputFile}\"",
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
        // Aggiorna lo stato della storia a `audio_master_generated` quando il mix finale è stato creato
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
        // Build ffmpeg filter_complex for mixing all tracks
        // We'll create a complex filter that:
        // 1. Delays each TTS file to its start position
        // 2. Mixes ambience at lower volume in background
        // 3. Adds FX at their positions
        // 4. Mixes everything together
        
        // Use temporary files for inputs and filter_complex to avoid Windows command line length limit
        var inputListFile = Path.Combine(folderPath, $"ffmpeg_inputs_{storyId}.txt");
        var filterScriptFile = Path.Combine(folderPath, $"ffmpeg_filter_{storyId}.txt");

        var filterArgs = new StringBuilder();
        var inputIndex = 0;

        // Calculate total duration based on last TTS file
        int totalDurationMs = 0;
        foreach (var (_, startMs) in ttsFiles)
        {
            totalDurationMs = Math.Max(totalDurationMs, startMs + 5000); // Add 5s buffer
        }

        // Build input list file for ffmpeg concat demuxer won't work here,
        // so we'll build -i arguments in a different way using a shell script or direct process args
        // Instead, we'll use -filter_complex_script to handle the filter, but inputs must still be on command line
        // 
        // Alternative approach: Build the command in smaller batches using intermediate files
        // For very large mixes, we'll use a staged approach

        var allInputFiles = new List<string>();
        
        // Add TTS inputs
        var ttsLabels = new List<string>();
        foreach (var (filePath, startMs) in ttsFiles)
        {
            allInputFiles.Add(filePath);
            var label = $"tts{inputIndex}";
            var delayMs = startMs;
            filterArgs.Append($"[{inputIndex}]adelay={delayMs}|{delayMs}[{label}];");
            ttsLabels.Add($"[{label}]");
            inputIndex++;
        }

        // Add ambience inputs (at lower volume, looped if needed)
        var ambienceLabels = new List<string>();
        var uniqueAmbienceFiles = ambienceFiles.Select(a => a.FilePath).Distinct().ToList();
        foreach (var filePath in uniqueAmbienceFiles)
        {
            allInputFiles.Add(filePath);
            var label = $"amb{inputIndex}";
            // Loop ambience to cover full duration and reduce volume significantly
            // atrim syntax: start=0:end=seconds - use invariant culture for decimal separator
            var endSeconds = (totalDurationMs / 1000.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            filterArgs.Append($"[{inputIndex}]aloop=loop=-1:size=2e+09,atrim=start=0:end={endSeconds},volume=0.08[{label}];");
            ambienceLabels.Add($"[{label}]");
            inputIndex++;
        }

        // Add FX inputs
        var fxLabels = new List<string>();
        foreach (var (filePath, startMs) in fxFiles)
        {
            allInputFiles.Add(filePath);
            var label = $"fx{inputIndex}";
            var delayMs = startMs;
            filterArgs.Append($"[{inputIndex}]adelay={delayMs}|{delayMs},volume=0.6[{label}];");
            fxLabels.Add($"[{label}]");
            inputIndex++;
        }

        // Add Music inputs (at medium volume, trimmed to duration)
        var musicLabels = new List<string>();
        foreach (var (filePath, startMs, durationMs) in musicFiles)
        {
            allInputFiles.Add(filePath);
            var label = $"music{inputIndex}";
            var delayMs = startMs;
            var endSeconds = (durationMs / 1000.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            filterArgs.Append($"[{inputIndex}]atrim=start=0:end={endSeconds},adelay={delayMs}|{delayMs},volume=0.25[{label}];");
            musicLabels.Add($"[{label}]");
            inputIndex++;
        }

        // Mix all together with weights: TTS=1.0, Ambience=0.5 (already reduced volume), FX=0.8, Music=0.3
        var allLabels = ttsLabels.Concat(ambienceLabels).Concat(fxLabels).Concat(musicLabels).ToList();

        if (allLabels.Count == 0)
        {
            return (false, "Nessun file audio da mixare");
        }

        if (allLabels.Count == 1)
        {
            // Single file, just copy
            filterArgs.Append($"{allLabels[0]}acopy[out]");
        }
        else
        {
            // Build weights: TTS=1.0, Ambience=0.5, FX=0.8, Music=0.3
            var weights = new List<string>();
            for (int w = 0; w < ttsLabels.Count; w++) weights.Add("1");
            for (int w = 0; w < ambienceLabels.Count; w++) weights.Add("0.5");
            for (int w = 0; w < fxLabels.Count; w++) weights.Add("0.8");
            for (int w = 0; w < musicLabels.Count; w++) weights.Add("0.3");
            var weightsStr = string.Join(" ", weights);

            // Mix all streams with weights, then apply dynamic normalization
            foreach (var lbl in allLabels)
            {
                filterArgs.Append(lbl);
            }
            filterArgs.Append($"amix=inputs={allLabels.Count}:duration=longest:dropout_transition=2:weights={weightsStr}:normalize=0[mixed];[mixed]dynaudnorm=p=0.95:s=3[out]");
        }

        _customLogger?.Append(runId, $"[{storyId}] Esecuzione ffmpeg con {ttsFiles.Count} TTS, {uniqueAmbienceFiles.Count} ambience, {fxFiles.Count} FX, {musicFiles.Count} music");

        try
        {
            // Build input arguments - if too many files, use staged mixing
            // Windows has a ~32KB limit on command line via CreateProcess, but practical limit is lower
            // With paths ~100 chars each + "-i " prefix, 50 files ≈ 5500 chars which is safe
            const int MaxInputsPerBatch = 50;
            
            if (allInputFiles.Count <= MaxInputsPerBatch)
            {
                // Write input files list (for reference/debugging)
                await File.WriteAllLinesAsync(inputListFile, allInputFiles);
            
                // Write filter_complex script to file to avoid command line length limit
                await File.WriteAllTextAsync(filterScriptFile, filterArgs.ToString());
                
                // Single pass - all inputs fit in command line
                var inputArgsBuilder = new StringBuilder();
                foreach (var file in allInputFiles)
                {
                    inputArgsBuilder.Append($" -i \"{file}\"");
                }
                
                var result = await RunFfmpegProcessAsync(inputArgsBuilder.ToString(), filterScriptFile, outputFile, runId, storyId);
                
                // Cleanup temp files
                TryDeleteFile(inputListFile);
                TryDeleteFile(filterScriptFile);
                
                if (!result.success)
                {
                    return result;
                }
            }
            else
            {
                // Staged mixing for large number of files
                _customLogger?.Append(runId, $"[{storyId}] Usando mixing a stadi per {allInputFiles.Count} file");
                
                var intermediateFiles = new List<string>();
                var batchIndex = 0;
                
                // Process TTS files in batches first
                for (int i = 0; i < ttsFiles.Count; i += MaxInputsPerBatch)
                {
                    var batchFiles = ttsFiles.Skip(i).Take(MaxInputsPerBatch).ToList();
                    var batchOutput = Path.Combine(folderPath, $"batch_tts_{storyId}_{batchIndex}.wav");
                    
                    var batchResult = await MixBatchFilesAsync(folderPath, batchFiles, batchOutput, runId, storyId, batchIndex);
                    if (!batchResult.success)
                    {
                        // Cleanup intermediate files
                        foreach (var f in intermediateFiles) TryDeleteFile(f);
                        return batchResult;
                    }
                    
                    intermediateFiles.Add(batchOutput);
                    batchIndex++;
                }
                
                // Add FX files in batches
                for (int i = 0; i < fxFiles.Count; i += MaxInputsPerBatch)
                {
                    var batchFiles = fxFiles.Skip(i).Take(MaxInputsPerBatch).ToList();
                    var batchOutput = Path.Combine(folderPath, $"batch_fx_{storyId}_{batchIndex}.wav");
                    
                    var batchResult = await MixBatchFilesAsync(folderPath, batchFiles, batchOutput, runId, storyId, batchIndex);
                    if (!batchResult.success)
                    {
                        foreach (var f in intermediateFiles) TryDeleteFile(f);
                        return batchResult;
                    }
                    
                    intermediateFiles.Add(batchOutput);
                    batchIndex++;
                }
                
                // Final mix: intermediate files + ambience
                var finalInputs = new StringBuilder();
                var finalFilter = new StringBuilder();
                var finalIndex = 0;
                var finalLabels = new List<string>();
                
                foreach (var intFile in intermediateFiles)
                {
                    finalInputs.Append($" -i \"{intFile}\"");
                    var label = $"int{finalIndex}";
                    finalFilter.Append($"[{finalIndex}]acopy[{label}];");
                    finalLabels.Add($"[{label}]");
                    finalIndex++;
                }
                
                // Add ambience
                foreach (var filePath in uniqueAmbienceFiles)
                {
                    finalInputs.Append($" -i \"{filePath}\"");
                    var label = $"amb{finalIndex}";
                    // atrim syntax: start=0:end=seconds - use invariant culture for decimal separator
                    // Reduce ambience volume significantly (0.05) so TTS/FX tracks dominate
                    var endSeconds = (totalDurationMs / 1000.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    finalFilter.Append($"[{finalIndex}]aloop=loop=-1:size=2e+09,atrim=start=0:end={endSeconds},volume=0.05[{label}];");
                    finalLabels.Add($"[{label}]");
                    finalIndex++;
                }
                
                // Build weights: high for TTS/FX batches (1.0), low for ambience (already reduced volume)
                var weights = new List<string>();
                for (int w = 0; w < intermediateFiles.Count; w++) weights.Add("1");
                for (int w = 0; w < uniqueAmbienceFiles.Count; w++) weights.Add("0.3");
                var weightsStr = string.Join(" ", weights);
                
                foreach (var lbl in finalLabels)
                {
                    finalFilter.Append(lbl);
                }
                // Use weights to prioritize TTS/FX over ambience, then normalize
                finalFilter.Append($"amix=inputs={finalLabels.Count}:duration=longest:dropout_transition=2:weights={weightsStr}:normalize=0[mixed];[mixed]dynaudnorm=p=0.95:s=3[out]");
                
                var finalFilterFile = Path.Combine(folderPath, $"ffmpeg_final_filter_{storyId}.txt");
                await File.WriteAllTextAsync(finalFilterFile, finalFilter.ToString());
                
                var finalResult = await RunFfmpegProcessAsync(finalInputs.ToString(), finalFilterFile, outputFile, runId, storyId);
                
                // Cleanup intermediate files (keep filter file on error for debugging)
                foreach (var f in intermediateFiles) TryDeleteFile(f);
                
                if (!finalResult.success)
                {
                    _customLogger?.Append(runId, $"[{storyId}] Filter file conservato per debug: {finalFilterFile}");
                    return finalResult;
                }
                
                TryDeleteFile(finalFilterFile);
            }

            if (!File.Exists(outputFile))
            {
                return (false, "ffmpeg non ha creato il file di output");
            }

            var fileInfo = new FileInfo(outputFile);
            _customLogger?.Append(runId, $"[{storyId}] File finale creato: {outputFile} ({fileInfo.Length / 1024} KB)");
            return (true, null);
        }
        catch (Exception ex)
        {
            _customLogger?.Append(runId, $"[{storyId}] Eccezione ffmpeg: {ex.Message}");
            return (false, $"Eccezione durante l'esecuzione di ffmpeg: {ex.Message}");
        }
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
            process.StartInfo.ArgumentList.Add("44100");
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
                return (false, "Timeout ffmpeg: il processo ha impiegato più di 30 minuti");
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
    
    private async Task<(bool success, string? error)> MixBatchFilesAsync(
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
        
        for (int i = 0; i < files.Count; i++)
        {
            var (filePath, startMs) = files[i];
            inputArgs.Append($" -i \"{filePath}\"");
            var label = $"s{i}";
            filterArgs.Append($"[{i}]adelay={startMs}|{startMs}[{label}];");
            labels.Add($"[{label}]");
        }
        
        foreach (var lbl in labels)
        {
            filterArgs.Append(lbl);
        }
        // Disable normalize to preserve original volume levels in batch
        filterArgs.Append($"amix=inputs={labels.Count}:duration=longest:dropout_transition=2:normalize=0[out]");
        
        var batchFilterFile = Path.Combine(folderPath, $"ffmpeg_batch_{storyId}_{batchIndex}.txt");
        await File.WriteAllTextAsync(batchFilterFile, filterArgs.ToString());
        
        _customLogger?.Append(runId, $"[{storyId}] Batch {batchIndex}: {files.Count} files, filter length: {filterArgs.Length} chars");
        
        var result = await RunFfmpegProcessAsync(inputArgs.ToString(), batchFilterFile, outputFile, runId, storyId);
        
        // Keep filter file on error for debugging
        if (result.success)
        {
            TryDeleteFile(batchFilterFile);
        }
        else
        {
            _customLogger?.Append(runId, $"[{storyId}] Batch filter conservato: {batchFilterFile}");
        }
        
        return result;
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
            throw new InvalidOperationException("Il testo della frase Ã¨ vuoto");

        // Normalize emotion to valid TTS value or fallback to neutral
        var normalizedEmotion = NormalizeEmotionForTts(emotion);

        var synthesis = await _ttsService.SynthesizeAsync(voiceId, text, "it", normalizedEmotion);
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
        public static string GetDefaultTtsStructuredInstructions() => @"Leggi attentamente il testo del chunk e trascrivilo integralmente nel formato seguente, senza riassumere o saltare frasi, senza aggiungere note o testo extra.

Usa SOLO queste sezioni ripetute nell'ordine del testo:

[NARRATORE]
Testo narrativo così come appare nel chunk

[PERSONAGGIO: NomePersonaggio | EMOZIONE: emotion]
Battuta di dialogo così come appare nel chunk

Regole:
- NON cambiare lingua, NON abbreviare, NON riassumere.
- Se non è chiaramente un dialogo, usa NARRATORE.
- EMOZIONE: usa una tra neutral, happy, sad, angry, fearful, disgusted, surprised (default neutral se non indicata).
- Non aggiungere spiegazioni o altro testo fuori dai blocchi.
- Copri tutto il chunk, più blocchi uno dopo l'altro finché il chunk è esaurito.";
    }
}

