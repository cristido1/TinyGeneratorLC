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
        MultiStepOrchestrationService? multiStepOrchestrator = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _ttsService = ttsService ?? throw new ArgumentNullException(nameof(ttsService));
        _kernelFactory = kernelFactory;
        _customLogger = customLogger;
        _logger = logger;
        _commandDispatcher = commandDispatcher;
        _multiStepOrchestrator = multiStepOrchestrator;
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
        using var scope = LogScope.Push($"story_evaluation_{story.Id}", evaluationOpId);

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
        using var scope = LogScope.Push($"action_evaluation_{story.Id}", evaluationOpId);

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
    /// Generates TTS audio for a story and saves it to the specified folder
    /// </summary>
    public async Task<(bool success, string? error)> GenerateTtsForStoryAsync(long storyId, string folderName)
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
        var (success, message) = await StartTtsAudioGenerationAsync(context);
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
        return string.Join(" ", cleaned);
    }

    private string EnsureStoryFolder(StoryRecord story)
    {
        var baseFolder = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder");
        Directory.CreateDirectory(baseFolder);

        if (string.IsNullOrWhiteSpace(story.Folder))
        {
            var folderName = $"story_{story.Id}_{DateTime.UtcNow:yyyyMMddHHmmss}";
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
            builder.AppendLine("If you finish your review but fail to call evaluate_full_story, the orchestrator will remind you and ask again up to 3 times — you MUST call the function before the evaluation completes.");
            builder.AppendLine("Populate the following scores (0-10): narrative_coherence_score, originality_score, emotional_impact_score, action_score.");
            builder.AppendLine("All score fields MUST be integers between 0 and 10 (use 0 if you cannot determine a score). Do NOT send strings like \"None\".");
            builder.AppendLine("Also include the corresponding *_defects values (empty string or \"None\" is acceptable if there are no defects).");
            builder.AppendLine("Do not return an overall evaluation text – the system will compute the aggregate score automatically.");
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
        builder.AppendLine("3) Compute scores (0.0–1.0):");
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

        public async Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            if (_service._kernelFactory == null)
                return (false, "Kernel factory non disponibile");

            var story = context.Story;
            var ttsAgent = _service._database.ListAgents()
                .FirstOrDefault(a => a.IsActive && a.Role?.Equals("tts_json", StringComparison.OrdinalIgnoreCase) == true);

            if (ttsAgent == null)
                return (false, "Nessun agente con ruolo tts_json trovato");

            var modelName = ttsAgent.ModelId.HasValue
                ? _service._database.GetModelInfoById(ttsAgent.ModelId.Value)?.Name
                : null;

            if (string.IsNullOrWhiteSpace(modelName))
                return (false, "Il modello associato all'agente tts_json non è configurato");

            var allowedPlugins = ParseAgentSkills(ttsAgent)?.ToList() ?? new List<string>();
           // if (!allowedPlugins.Any())
            //{
             //   _service._logger?.LogWarning("GenerateTtsSchemaCommand: agent {AgentId} has no tools configured", ttsAgent.Id);
              //  return (false, "L'agente tts_json non ha strumenti abilitati");
            //}

            var folderPath = context.FolderPath;

            var multiStepResult = await TryExecuteMultiStepTtsAsync(story, ttsAgent, folderPath);
            if (multiStepResult.HasValue)
            {
                return multiStepResult.Value;
            }
            
            // Build system message: use agent's prompt + instructions if available
            string? systemMessage = null;
            if (!string.IsNullOrWhiteSpace(ttsAgent.Instructions))
                systemMessage = ttsAgent.Instructions;
            else
                systemMessage = _service.ComposeSystemMessage(ttsAgent);
            
            // Build user prompt: use agent's Prompt if available, otherwise use the default
            string userPrompt;
            if (!string.IsNullOrWhiteSpace(ttsAgent.Prompt))
                userPrompt = ttsAgent.Prompt;
            else
                userPrompt = _service.BuildTtsJsonPrompt(story);

            HybridLangChainOrchestrator orchestrator;
            try
            {
                orchestrator = _service._kernelFactory.CreateOrchestrator(
                    modelName,
                    allowedPlugins,
                    ttsAgent.Id,
                    folderPath,
                    story.Story);
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Impossibile creare l'orchestrator per JSON TTS");
                return (false, $"Errore creazione orchestrator: {ex.Message}");
            }

            // Set CurrentStoryId on TtsSchemaTool so read_story_part can fetch the story
            var ttsSchemaaTool = orchestrator.GetTool<TtsSchemaTool>("ttsschema");
            if (ttsSchemaaTool != null)
            {
                ttsSchemaaTool.CurrentStoryId = story.Id;
            }

            LangChainChatBridge chatBridge;
            try
            {
                chatBridge = _service._kernelFactory.CreateChatBridge(modelName, ttsAgent.Temperature, ttsAgent.TopP);
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Impossibile creare il bridge verso il modello {Model}", modelName);
                return (false, $"Errore connessione modello: {ex.Message}");
            }

            var runId = $"ttsjson_{story.Id}_{DateTime.UtcNow:yyyyMMddHHmmss}";
            _service._customLogger?.Start(runId);

            try
            {
                var reactLoop = new ReActLoopOrchestrator(
                    orchestrator,
                    _service._customLogger,
                    runId: runId,
                    modelBridge: chatBridge,
                    systemMessage: systemMessage,
                    agentRole: ttsAgent.Role);

                var reactResult = await reactLoop.ExecuteAsync(userPrompt);

                if (!reactResult.Success)
                {
                    var error = reactResult.Error ?? "Esecuzione agente fallita";
                    return (false, error);
                }

                var schemaPath = Path.Combine(folderPath, "tts_schema.json");
                if (!File.Exists(schemaPath))
                {
                    return (false, "L'agente non ha generato il file tts_schema.json");
                }

                _service._database.UpdateStoryById(story.Id, statusId: TtsSchemaReadyStatusId, updateStatus: true);

                return (true, $"Schema TTS generato: {schemaPath}");
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante la generazione del JSON TTS per la storia {Id}", story.Id);
                return (false, ex.Message);
            }
            finally
            {
                _service._customLogger?.MarkCompleted(runId);
            }
        }

        private async Task<(bool success, string? message)?> TryExecuteMultiStepTtsAsync(
            StoryRecord story,
            Agent ttsAgent,
            string folderPath)
        {
            var orchestratorService = _service._multiStepOrchestrator;
            if (orchestratorService == null || !ttsAgent.MultiStepTemplateId.HasValue)
            {
                return null;
            }

            var template = _service._database.GetStepTemplateById(ttsAgent.MultiStepTemplateId.Value);
            if (template == null)
            {
                _service._logger?.LogWarning("GenerateTtsSchemaCommand: template {TemplateId} non trovato", ttsAgent.MultiStepTemplateId.Value);
                return null;
            }

            if (!string.Equals(template.TaskType, "tts_schema", StringComparison.OrdinalIgnoreCase))
            {
                _service._logger?.LogWarning("GenerateTtsSchemaCommand: template {TemplateName} non è di tipo tts_schema", template.Name);
                return null;
            }

            // Ensure instructions are stored on the template (no hardcoded defaults)
            if (string.IsNullOrWhiteSpace(template.Instructions) &&
                string.Equals(template.Name, "tts_schema_chunk_fixed20", StringComparison.OrdinalIgnoreCase))
            {
                template.Instructions = StoriesServiceDefaults.GetDefaultTtsStructuredInstructions();
                template.UpdatedAt = DateTime.UtcNow.ToString("o");
                _service._database.UpdateStepTemplate(template);
            }

            var configPayload = JsonSerializer.Serialize(new
            {
                workingFolder = folderPath,
                storyText = story.Story ?? string.Empty
            });

            var threadId = unchecked((int)(story.Id % int.MaxValue));
            var runId = $"ttsjson_multistep_{story.Id}_{DateTime.UtcNow:yyyyMMddHHmmss}";
            _service._customLogger?.Start(runId);

            try
            {
                var executionId = await orchestratorService.StartTaskExecutionAsync(
                    taskType: "tts_schema",
                    entityId: story.Id,
                    stepPrompt: template.StepPrompt,
                    executorAgentId: ttsAgent.Id,
                    checkerAgentId: null,
                    configOverrides: configPayload,
                    initialContext: story.Story ?? string.Empty,
                    threadId: threadId,
                    templateInstructions: string.IsNullOrWhiteSpace(template.Instructions) ? null : template.Instructions);

                await orchestratorService.ExecuteAllStepsAsync(executionId, threadId);

                var schemaPath = Path.Combine(folderPath, "tts_schema.json");
                if (!File.Exists(schemaPath))
                {
                    return (false, "L'agente non ha generato il file tts_schema.json");
                }

                _service._database.UpdateStoryById(story.Id, statusId: TtsSchemaReadyStatusId, updateStatus: true);
                return (true, $"Schema TTS generato: {schemaPath}");
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore multistep TTS per la storia {StoryId}", story.Id);
                return (false, $"Errore multistep TTS: {ex.Message}");
            }
            finally
            {
                _service._customLogger?.MarkCompleted(runId);
            }
        }
    }

    private sealed class AssignVoicesCommand : IStoryCommand
    {
        private const int MaxAttempts = 3;

        private readonly StoriesService _service;

        public AssignVoicesCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => true;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public async Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            if (_service._kernelFactory == null)
                return (false, "Kernel factory non disponibile");

            var story = context.Story;
            var folderPath = context.FolderPath;

            var schemaPath = Path.Combine(folderPath, "tts_schema.json");
            if (!File.Exists(schemaPath))
                return (false, "File tts_schema.json mancante: genera prima lo schema TTS");

            var voiceAgent = _service._database.ListAgents()
                .FirstOrDefault(a => a.IsActive && a.Role?.Equals("tts_voice", StringComparison.OrdinalIgnoreCase) == true);
            if (voiceAgent == null)
                return (false, "Nessun agente con ruolo tts_voice trovato");

            var modelName = voiceAgent.ModelId.HasValue
                ? _service._database.GetModelInfoById(voiceAgent.ModelId.Value)?.Name
                : null;

            if (string.IsNullOrWhiteSpace(modelName))
                return (false, "Il modello associato all'agente tts_voice non è configurato");

            var allowedPlugins = ParseAgentSkills(voiceAgent)?.ToList() ?? new List<string>();
            if (!allowedPlugins.Any())
            {
                _service._logger?.LogWarning("AssignVoicesCommand: agent {AgentId} has no tools configured", voiceAgent.Id);
                return (false, "L'agente tts_voice non ha strumenti abilitati");
            }

            var systemMessage = !string.IsNullOrWhiteSpace(voiceAgent.Instructions)
                ? voiceAgent.Instructions
                : "You are a specialist that assigns gender and TTS voices using the available tools.";
            var runId = $"ttsvoice_{story.Id}_{DateTime.UtcNow:yyyyMMddHHmmss}";
            _service._customLogger?.Start(runId);

            var voiceCatalog = await _service._ttsService.GetVoicesAsync();
            var knownVoiceIds = new HashSet<string>(
                voiceCatalog.Where(v => !string.IsNullOrWhiteSpace(v.Id)).Select(v => v.Id!),
                StringComparer.OrdinalIgnoreCase);

            string? feedback = null;

            try
            {
                for (int attempt = 1; attempt <= MaxAttempts; attempt++)
                {
                    HybridLangChainOrchestrator orchestrator;
                    try
                    {
                        orchestrator = _service._kernelFactory.CreateOrchestrator(
                            modelName,
                            allowedPlugins,
                            voiceAgent.Id,
                            folderPath,
                            story.Story);
                    }
                    catch (Exception ex)
                    {
                        _service._logger?.LogError(ex, "Impossibile creare l'orchestrator per assegnazione voci");
                        return (false, $"Errore creazione orchestrator: {ex.Message}");
                    }

                    LangChainChatBridge chatBridge;
                    try
                    {
                        chatBridge = _service._kernelFactory.CreateChatBridge(modelName, voiceAgent.Temperature, voiceAgent.TopP);
                    }
                    catch (Exception ex)
                    {
                        _service._logger?.LogError(ex, "Impossibile creare il bridge verso il modello {Model}", modelName);
                        return (false, $"Errore connessione modello: {ex.Message}");
                    }

                    var basePrompt = !string.IsNullOrWhiteSpace(voiceAgent.Prompt)
                        ? voiceAgent.Prompt!
                        : "Assign a unique gender and TTS voice to every character by using read_json, read_voices and set_voice.";
                    if (!string.IsNullOrWhiteSpace(feedback))
                    {
                        basePrompt += $"\n\n[PREVIOUS_ATTEMPT_FEEDBACK]\n{feedback}";
                    }

                    var reactLoop = new ReActLoopOrchestrator(
                        orchestrator,
                        _service._customLogger,
                        runId: runId,
                        modelBridge: chatBridge,
                        systemMessage: systemMessage,
                        agentRole: voiceAgent.Role);

                    var reactResult = await reactLoop.ExecuteAsync(basePrompt);
                    if (!reactResult.Success)
                    {
                        var error = reactResult.Error ?? "Assegnazione voci fallita";
                        return (false, error);
                    }

                    var fallbackApplied = await _service.ApplyVoiceAssignmentFallbacksAsync(schemaPath);
                    if (fallbackApplied)
                    {
                        _service._customLogger?.Append(runId, $"[{story.Id}] Applicate voci di fallback dal catalogo tts_voices.");
                    }

                    var (valid, validationError) = await ValidateAssignedVoicesAsync(schemaPath, knownVoiceIds);
                    if (valid)
                    {
                        await NormalizeSchemaFileAsync(schemaPath);
                        return (true, "Assegnazione voci completata. Verifica il file tts_schema.json aggiornato.");
                    }

                    feedback = $"ERRORE: {validationError}. Assegna una voce esistente, unica e coerente a ogni personaggio.";
                    _service._logger?.LogWarning("Validazione assegnazione voci fallita per la storia {StoryId} (tentativo {Attempt}): {Error}", story.Id, attempt, validationError);
                }

                return (false, feedback ?? "Assegnazione voci non riuscita.");
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante l'assegnazione voci per la storia {Id}", story.Id);
                return (false, ex.Message);
            }
            finally
            {
                _service._customLogger?.MarkCompleted(runId);
            }
        }

        private static async Task<(bool success, string? error)> ValidateAssignedVoicesAsync(string schemaPath, HashSet<string> knownVoiceIds)
        {
            if (!File.Exists(schemaPath))
                return (false, "File tts_schema.json mancante");

            try
            {
                var content = await File.ReadAllTextAsync(schemaPath);
                var schema = JsonSerializer.Deserialize<TtsSchema>(content, SchemaJsonOptions);
                if (schema?.Characters == null || schema.Characters.Count == 0)
                    return (false, "Nessun personaggio definito nello schema");

                var missing = schema.Characters
                    .Where(c => string.IsNullOrWhiteSpace(c.VoiceId) || string.IsNullOrWhiteSpace(c.Gender))
                    .Select(c => c.Name ?? "<senza nome>")
                    .ToList();
                if (missing.Any())
                {
                    return (false, $"Personaggi senza voce o genere: {string.Join(", ", missing)}");
                }

                var duplicates = schema.Characters
                    .Where(c => !string.IsNullOrWhiteSpace(c.VoiceId))
                    .GroupBy(c => c.VoiceId!, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => $"{g.Key}: {string.Join(", ", g.Select(c => c.Name ?? "?"))}")
                    .ToList();
                if (duplicates.Any())
                {
                    return (false, $"Voci duplicate: {string.Join("; ", duplicates)}");
                }

                if (knownVoiceIds.Count > 0)
                {
                    var unknown = schema.Characters
                        .Where(c => !string.IsNullOrWhiteSpace(c.VoiceId) && !knownVoiceIds.Contains(c.VoiceId!))
                        .Select(c => $"{c.Name ?? "?"} ({c.VoiceId})")
                        .ToList();
                    if (unknown.Any())
                    {
                        return (false, $"Voci non presenti nel catalogo: {string.Join(", ", unknown)}");
                    }
                }

                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, $"Errore durante la validazione: {ex.Message}");
            }
        }

        private static async Task NormalizeSchemaFileAsync(string schemaPath)
        {
            try
            {
                var content = await File.ReadAllTextAsync(schemaPath);
                var schema = JsonSerializer.Deserialize<TtsSchema>(content, SchemaJsonOptions);
                if (schema == null) return;
                var normalized = JsonSerializer.Serialize(schema, SchemaJsonOptions);
                await File.WriteAllTextAsync(schemaPath, normalized);
            }
            catch
            {
                // best effort only
            }
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

            if (_service._commandDispatcher == null)
            {
                return await RunSequentialAsync(story, evaluators);
            }

            var handles = new List<CommandHandle>();
            foreach (var evaluator in evaluators)
            {
                var runId = $"story_eval_{story.Id}_{evaluator.Id}";
                var scope = $"story/{story.Id}/evaluation";
                var metadata = new Dictionary<string, string>
                {
                    ["storyId"] = story.Id.ToString(),
                    ["evaluatorId"] = evaluator.Id.ToString(),
                    ["evaluatorName"] = evaluator.Name ?? $"eval_{evaluator.Id}"
                };
                var handle = _service._commandDispatcher.Enqueue(
                    "story_evaluation",
                    async ctx =>
                    {
                        var (success, score, error) = await _service.EvaluateStoryWithAgentAsync(story.Id, evaluator.Id);
                        var label = string.IsNullOrWhiteSpace(evaluator.Name) ? $"Evaluator {evaluator.Id}" : evaluator.Name;
                        var message = success
                            ? $"{label}: punteggio {score:F2}"
                            : $"{label}: errore {error ?? "sconosciuto"}";
                        return new CommandResult(success, message);
                    },
                    runId: runId,
                    threadScope: scope,
                    metadata: metadata);
                handles.Add(handle);
            }

            var results = await Task.WhenAll(handles.Select(h => h.CompletionTask));
            var allOk = results.All(r => r.Success);
            var joined = string.Join("; ", results.Select(r => r.Message ?? (r.Success ? "OK" : "Errore")));

            return allOk
                ? (true, $"Valutazione completata. {joined}")
                : (false, $"Valutazione parziale. {joined}");
        }

        private async Task<(bool success, string? message)> RunSequentialAsync(StoryRecord story, List<Agent> evaluators)
        {
            var messages = new List<string>();
            var allOk = true;

            foreach (var evaluator in evaluators)
            {
                var (success, score, error) = await _service.EvaluateStoryWithAgentAsync(story.Id, evaluator.Id);
                var label = string.IsNullOrWhiteSpace(evaluator.Name) ? $"Evaluator {evaluator.Id}" : evaluator.Name;
                if (success)
                {
                    messages.Add($"{label}: punteggio {score:F2}");
                }
                else
                {
                    allOk = false;
                    messages.Add($"{label}: errore {error ?? "sconosciuto"}");
                }
            }

            var joined = string.Join("; ", messages);
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

        if (!rootNode.TryGetPropertyValue("Characters", out var charactersNode) || charactersNode is not JsonArray charactersArray)
        {
            var err = "Lista di personaggi mancante nello schema TTS";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        if (!rootNode.TryGetPropertyValue("Timeline", out var timelineNode) || timelineNode is not JsonArray timelineArray)
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

            entry["FileName"] = fileName;
            entry["DurationMs"] = durationMs;
            entry["StartMs"] = startMs;
            entry["EndMs"] = endMs;

            currentMs = endMs + DefaultPhraseGapMs;
            _customLogger?.Append(runId, $"[{story.Id}] Frase completata: {fileName} ({durationMs} ms)");
        }

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

        var successMsg = $"Generazione audio completata ({phraseCounter} frasi)";
        _customLogger?.Append(runId, $"[{story.Id}] {successMsg}");
        return (true, successMsg);
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
        return entry.TryGetPropertyValue("Character", out var charNode) && charNode != null;
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

    private async Task<(byte[] audioBytes, int? durationMs)> GenerateAudioBytesAsync(string voiceId, string text, string emotion)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Il testo della frase è vuoto");

        var synthesis = await _ttsService.SynthesizeAsync(voiceId, text, "it", emotion);
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
        if (!entry.TryGetPropertyValue(propertyName, out var node) || node == null)
            return false;

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
        if (!entry.TryGetPropertyValue(propertyName, out var node) || node == null)
            return null;

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

Usa SOLO queste sezioni ripetute nell’ordine del testo:

[NARRATORE]
Testo narrativo così come appare nel chunk

[PERSONAGGIO: NomePersonaggio | EMOZIONE: emotion]
Battuta di dialogo così come appare nel chunk

Regole:
- NON cambiare lingua, NON abbreviare, NON riassumere.
- Se non è chiaramente un dialogo, usa NARRATORE.
- EMOZIONE: usa una tra neutral, happy, sad, angry, fearful, disgusted, surprised (default neutral se non indicata).
- Non aggiungere spiegazioni o altro testo fuori dai blocchi.
- Copri tutto il chunk, più blocchi uno dopo l’altro finché il chunk è esaurito.";
    }
}
