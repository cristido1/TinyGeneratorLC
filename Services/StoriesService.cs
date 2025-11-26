using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TinyGenerator.Models;

namespace TinyGenerator.Services;

public sealed class StoriesService
{
    private readonly DatabaseService _database;
    private readonly ILogger<StoriesService>? _logger;
    private readonly TtsService _ttsService;
    private readonly ILangChainKernelFactory? _kernelFactory;
    private readonly ICustomLogger? _customLogger;
    private readonly ProgressService? _progress;

    public StoriesService(
        DatabaseService database, 
        TtsService ttsService,
        ILangChainKernelFactory? kernelFactory = null,
        ICustomLogger? customLogger = null,
        ProgressService? progress = null,
        ILogger<StoriesService>? logger = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _ttsService = ttsService ?? throw new ArgumentNullException(nameof(ttsService));
        _kernelFactory = kernelFactory;
        _customLogger = customLogger;
        _progress = progress;
        _logger = logger;
    }

    public long SaveGeneration(string prompt, StoryGeneratorService.GenerationResult r, string? memoryKey = null)
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

    public List<StoryEvaluation> GetEvaluationsForStory(long storyId)
    {
        return _database.GetStoryEvaluations(storyId);
    }

    public void SaveChapter(string memoryKey, int chapterNumber, string content)
    {
        _database.SaveChapter(memoryKey, chapterNumber, content);
    }

    /// <summary>
    /// DEPRECATED SK - This method uses deprecated AgentService
    /// </summary>
    public async Task<(bool success, double score, string? error)> EvaluateStoryWithAgentAsync(long storyId, int agentId)
    {
        return (false, 0, "EvaluateStoryWithAgentAsync is deprecated - use LangChain-based evaluation instead");
    }

    /// <summary>
    /// Generates TTS audio for a story and saves it to the specified folder
    /// </summary>
    public async Task<(bool success, string? error)> GenerateTtsForStoryAsync(long storyId, string folderName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folderName))
                return (false, "Folder name is required");

            var story = GetStoryById(storyId);
            if (story == null)
                return (false, "Story not found");

            if (string.IsNullOrWhiteSpace(story.Story))
                return (false, "Story has no content");

            // Get available voices
            var voices = await _ttsService.GetVoicesAsync();
            if (voices == null || voices.Count == 0)
                return (false, "No TTS voices available");

            // Use first Italian voice or first available voice
            var voice = voices.FirstOrDefault(v => v.Language?.StartsWith("it", StringComparison.OrdinalIgnoreCase) == true)
                ?? voices.First();

            // Create output directory
            var outputDir = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "wwwroot", "audio", folderName);
            System.IO.Directory.CreateDirectory(outputDir);

            // Synthesize audio
            var result = await _ttsService.SynthesizeAsync(voice.Id, story.Story, "it");
            
            if (result == null)
                return (false, "TTS synthesis failed");

            // Save audio file
            var audioFileName = $"story_{storyId}.mp3";
            var audioFilePath = System.IO.Path.Combine(outputDir, audioFileName);

            if (!string.IsNullOrWhiteSpace(result.AudioBase64))
            {
                var audioBytes = Convert.FromBase64String(result.AudioBase64);
                await System.IO.File.WriteAllBytesAsync(audioFilePath, audioBytes);
            }
            else if (!string.IsNullOrWhiteSpace(result.AudioUrl))
            {
                // Download from URL if base64 not provided
                using var httpClient = new System.Net.Http.HttpClient();
                var audioBytes = await httpClient.GetByteArrayAsync(result.AudioUrl);
                await System.IO.File.WriteAllBytesAsync(audioFilePath, audioBytes);
            }
            else
            {
                return (false, "No audio data in TTS response");
            }

            _logger?.LogInformation("Generated TTS for story {StoryId} to {Path}", storyId, audioFilePath);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to generate TTS for story {StoryId}", storyId);
            return (false, ex.Message);
        }
    }

    public async Task<(bool success, string? message)> GenerateTtsSchemaJsonAsync(long storyId)
    {
        if (_kernelFactory == null)
            return (false, "Kernel factory non disponibile");

        var story = GetStoryById(storyId);
        if (story == null)
            return (false, "Storia non trovata");

        if (string.IsNullOrWhiteSpace(story.Story))
            return (false, "La storia non contiene testo");

        if (string.IsNullOrWhiteSpace(story.Folder))
            return (false, "La storia non ha una cartella associata (rieseguire l'assegnazione cartelle)");

        var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", story.Folder);
        Directory.CreateDirectory(folderPath);

        var ttsAgent = _database.ListAgents()
            .FirstOrDefault(a => a.IsActive && a.Role?.Equals("tts_json", StringComparison.OrdinalIgnoreCase) == true);

        if (ttsAgent == null)
            return (false, "Nessun agente con ruolo tts_json trovato");

        string? modelName = null;
        if (ttsAgent.ModelId.HasValue)
        {
            modelName = _database.GetModelInfoById(ttsAgent.ModelId.Value)?.Name;
        }

        if (string.IsNullOrWhiteSpace(modelName))
            return (false, "Il modello associato all'agente tts_json non è configurato");

        var allowedPlugins = ParseAgentSkills(ttsAgent)?.ToList() ?? new List<string>();
        if (!allowedPlugins.Any())
        {
            allowedPlugins.Add("ttsschema");
        }
        else if (!allowedPlugins.Contains("ttsschema", StringComparer.OrdinalIgnoreCase))
        {
            allowedPlugins.Add("ttsschema");
        }

        if (!allowedPlugins.Any(p => p.Equals("voicechoser", StringComparison.OrdinalIgnoreCase) ||
                                      p.Equals("voicechooser", StringComparison.OrdinalIgnoreCase)))
        {
            allowedPlugins.Add("voicechoser");
        }

        string? systemMessage = ComposeSystemMessage(ttsAgent);

        HybridLangChainOrchestrator orchestrator;
        try
        {
            orchestrator = _kernelFactory.CreateOrchestrator(
                modelName,
                allowedPlugins,
                ttsAgent.Id,
                folderPath,
                story.Story);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Impossibile creare l'orchestrator per JSON TTS");
            return (false, $"Errore creazione orchestrator: {ex.Message}");
        }

        LangChainChatBridge chatBridge;
        try
        {
            chatBridge = _kernelFactory.CreateChatBridge(modelName);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Impossibile creare il bridge verso il modello {Model}", modelName);
            return (false, $"Errore connessione modello: {ex.Message}");
        }

        var userPrompt = BuildTtsJsonPrompt(story);
        var runId = $"ttsjson_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        _progress?.Start(runId);

        try
        {
            var reactLoop = new ReActLoopOrchestrator(
                orchestrator,
                _customLogger,
                progress: _progress,
                runId: runId,
                modelBridge: chatBridge,
                systemMessage: systemMessage);

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

            return (true, $"Schema TTS generato: {schemaPath}");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Errore durante la generazione del JSON TTS per la storia {Id}", storyId);
            return (false, ex.Message);
        }
        finally
        {
            _progress?.MarkCompleted(runId);
        }
    }

    public async Task<(bool success, string? message)> AssignVoicesAsync(long storyId)
    {
        if (_kernelFactory == null)
            return (false, "Kernel factory non disponibile");

        var story = GetStoryById(storyId);
        if (story == null)
            return (false, "Storia non trovata");

        if (string.IsNullOrWhiteSpace(story.Folder))
            return (false, "La storia non ha una cartella associata");

        var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", story.Folder);
        Directory.CreateDirectory(folderPath);
        var schemaPath = Path.Combine(folderPath, "tts_schema.json");
        if (!File.Exists(schemaPath))
            return (false, "File tts_schema.json mancante: genera prima lo schema TTS");

        var voiceSourcePath = Path.Combine(folderPath, "tts_storia.json");
        if (!File.Exists(voiceSourcePath))
            return (false, "File tts_storia.json non trovato nella cartella della storia");

        var voiceAgent = _database.ListAgents()
            .FirstOrDefault(a => a.IsActive && a.Role?.Equals("tts_voice", StringComparison.OrdinalIgnoreCase) == true);
        if (voiceAgent == null)
            return (false, "Nessun agente con ruolo tts_voice trovato");

        string? modelName = null;
        if (voiceAgent.ModelId.HasValue)
        {
            modelName = _database.GetModelInfoById(voiceAgent.ModelId.Value)?.Name;
        }

        if (string.IsNullOrWhiteSpace(modelName))
            return (false, "Il modello associato all'agente tts_voice non è configurato");

        var allowedPlugins = ParseAgentSkills(voiceAgent)?.ToList() ?? new List<string>();
        if (!allowedPlugins.Any(p => p.Equals("voicechoser", StringComparison.OrdinalIgnoreCase) ||
                                      p.Equals("voicechooser", StringComparison.OrdinalIgnoreCase)))
        {
            allowedPlugins.Add("voicechoser");
        }

        if (!allowedPlugins.Any(p => p.Equals("ttsschema", StringComparison.OrdinalIgnoreCase)))
        {
            allowedPlugins.Add("ttsschema");
        }

        HybridLangChainOrchestrator orchestrator;
        try
        {
            orchestrator = _kernelFactory.CreateOrchestrator(
                modelName,
                allowedPlugins,
                voiceAgent.Id,
                folderPath,
                story.Story);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Impossibile creare l'orchestrator per assegnazione voci");
            return (false, $"Errore creazione orchestrator: {ex.Message}");
        }

        LangChainChatBridge chatBridge;
        try
        {
            chatBridge = _kernelFactory.CreateChatBridge(modelName);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Impossibile creare il bridge verso il modello {Model}", modelName);
            return (false, $"Errore connessione modello: {ex.Message}");
        }

        var systemMessage = ComposeSystemMessage(voiceAgent);
        var prompt = BuildVoiceAssignmentPrompt(story, folderPath, voiceSourcePath);
        var runId = $"ttsvoice_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        _progress?.Start(runId);

        try
        {
            var reactLoop = new ReActLoopOrchestrator(
                orchestrator,
                _customLogger,
                progress: _progress,
                runId: runId,
                modelBridge: chatBridge,
                systemMessage: systemMessage);

            var reactResult = await reactLoop.ExecuteAsync(prompt);
            if (!reactResult.Success)
            {
                var error = reactResult.Error ?? "Assegnazione voci fallita";
                return (false, error);
            }

            return (true, "Assegnazione voci completata. Verifica il file tts_schema.json aggiornato.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Errore durante l'assegnazione voci per la storia {Id}", storyId);
            return (false, ex.Message);
        }
        finally
        {
            _progress?.MarkCompleted(runId);
        }
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
        // For the TTS JSON generator we now pass only the plain story text as user input.
        // Any instructions or workflow details must come from the agent's system message/config.
        return story.Story ?? string.Empty;
    }

    private string BuildVoiceAssignmentPrompt(StoryRecord story, string folderPath, string voiceSourcePath)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Devi assegnare una voce coerente a ogni personaggio della storia con ID {story.Id}.");
        builder.AppendLine("Leggi il file tts_schema.json per recuperare l'elenco dei personaggi e usa set_voice per aggiornare Gender e VoiceId.");
        builder.AppendLine("Consulta read_voices per conoscere le voci disponibili e selezionare quella più adatta.");
        builder.AppendLine($"Cartella di lavoro: {folderPath}");
        builder.AppendLine("Assicurati di impostare SEMPRE sia il genere che la voce di ciascun personaggio, incluso il Narratore.");
        builder.AppendLine();

        try
        {
            var voiceSource = File.ReadAllText(voiceSourcePath);
            builder.AppendLine("[TTS_STORIA.JSON]");
            builder.AppendLine(voiceSource);
            builder.AppendLine();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Impossibile leggere tts_storia.json per la storia {Id}", story.Id);
        }

        builder.AppendLine("Testo completo della storia (usalo per capire tono e personalità dei personaggi):");
        var chunks = ChunkText(story.Story, 3500).ToList();
        for (int i = 0; i < chunks.Count; i++)
        {
            builder.AppendLine($"[STORIA {i + 1}/{chunks.Count}]");
            builder.AppendLine(chunks[i]);
            builder.AppendLine();
        }
        builder.AppendLine("[FINE STORIA]");
        builder.AppendLine("Procedi assegnando le voci e conferma l'aggiornamento dello schema.");
        return builder.ToString();
    }

    private static IEnumerable<string> ChunkText(string text, int chunkSize)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield return string.Empty;
            yield break;
        }

        for (int i = 0; i < text.Length; i += chunkSize)
        {
            var size = Math.Min(chunkSize, text.Length - i);
            yield return text.Substring(i, size);
        }
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
}
