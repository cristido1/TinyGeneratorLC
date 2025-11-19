using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI.Chat;
using TinyGenerator.Models;

namespace TinyGenerator.Services;

public sealed class StoriesService
{
    private readonly DatabaseService _database;
    private readonly ILogger<StoriesService>? _logger;
    private readonly IKernelFactory _kernelFactory;
    private readonly TtsService _ttsService;
    private readonly AgentService _agentService;

    public StoriesService(
        DatabaseService database, 
        IKernelFactory kernelFactory, 
        TtsService ttsService, 
        AgentService agentService,
        ILogger<StoriesService>? logger = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
        _ttsService = ttsService ?? throw new ArgumentNullException(nameof(ttsService));
        _agentService = agentService ?? throw new ArgumentNullException(nameof(agentService));
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

    public long InsertSingleStory(string prompt, string story, long? modelId = null, int? agentId = null, double score = 0.0, string? eval = null, int approved = 0, string? status = null, string? memoryKey = null)
    {
        return _database.InsertSingleStory(prompt, story, modelId, agentId, score, eval, approved, status, memoryKey);
    }

    public bool UpdateStoryById(long id, string? story = null, long? modelId = null, int? agentId = null, string? status = null)
    {
        return _database.UpdateStoryById(id, story, modelId, agentId, status);
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
    /// Evaluates a story with a single evaluator agent
    /// </summary>
    public async Task<(bool success, double score, string? error)> EvaluateStoryWithAgentAsync(long storyId, int agentId)
    {
        try
        {
            var story = GetStoryById(storyId);
            if (story == null)
                return (false, 0, "Story not found");

            var agent = _agentService.GetAgent(agentId);
            if (agent == null)
                return (false, 0, "Agent not found");

            var evaluatorKernel = _agentService.GetConfiguredAgent(agentId);
            if (evaluatorKernel == null)
                return (false, 0, "Failed to configure agent");

            var chatService = evaluatorKernel.GetRequiredService<IChatCompletionService>();
            var settings = new OpenAIPromptExecutionSettings
            {
                Temperature = 0.0,
                MaxTokens = 4000
            };

            // Apply response format from agent configuration or default
            var schemaFile = !string.IsNullOrWhiteSpace(agent.JsonResponseFormat) 
                ? agent.JsonResponseFormat 
                : "full_evaluation.json";
            
            var schemaPath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "response_formats", schemaFile);
            if (System.IO.File.Exists(schemaPath))
            {
                try
                {
                    var schemaJson = System.IO.File.ReadAllText(schemaPath);
                    settings.ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                        jsonSchemaFormatName: System.IO.Path.GetFileNameWithoutExtension(schemaFile) + "_schema",
                        jsonSchema: BinaryData.FromString(schemaJson),
                        jsonSchemaIsStrict: true);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to set response format for agent {AgentId}", agentId);
                }
            }

            var history = new ChatHistory();
            
            if (!string.IsNullOrWhiteSpace(agent.Instructions))
                history.AddSystemMessage(agent.Instructions);

            var evaluationPrompt = $@"Please evaluate the following story across all 10 categories. For each category, provide:
- A score from 1 to 10
- A description of any defects found

Categories: narrative_coherence, structure, characterization, dialogues, pacing, originality, style, worldbuilding, thematic_coherence, emotional_impact

Also provide:
- total_score: sum of all category scores (0-100)
- overall_evaluation: a brief summary of the story's strengths and weaknesses

Story:
{story.Story}";

            history.AddUserMessage(evaluationPrompt);

            var response = await chatService.GetChatMessageContentAsync(history, settings, evaluatorKernel);
            var responseText = response?.Content ?? string.Empty;

            if (string.IsNullOrWhiteSpace(responseText))
                return (false, 0, "Empty response from evaluator");

            var evalDoc = System.Text.Json.JsonDocument.Parse(responseText);
            var root = evalDoc.RootElement;

            int GetIntProperty(string name, int defaultValue = 0)
            {
                try { return root.TryGetProperty(name, out var prop) ? prop.GetInt32() : defaultValue; }
                catch { return defaultValue; }
            }
            
            string GetStringProperty(string name, string defaultValue = "")
            {
                try { return root.TryGetProperty(name, out var prop) ? (prop.GetString() ?? defaultValue) : defaultValue; }
                catch { return defaultValue; }
            }
            
            double GetDoubleProperty(string name, double defaultValue = 0.0)
            {
                try { return root.TryGetProperty(name, out var prop) ? prop.GetDouble() : defaultValue; }
                catch { return defaultValue; }
            }

            var narrativeScore = GetIntProperty("narrative_coherence_score");
            var narrativeDefects = GetStringProperty("narrative_coherence_defects");
            var structureScore = GetIntProperty("structure_score");
            var structureDefects = GetStringProperty("structure_defects");
            var characterizationScore = GetIntProperty("characterization_score");
            var characterizationDefects = GetStringProperty("characterization_defects");
            var dialoguesScore = GetIntProperty("dialogues_score");
            var dialoguesDefects = GetStringProperty("dialogues_defects");
            var pacingScore = GetIntProperty("pacing_score");
            var pacingDefects = GetStringProperty("pacing_defects");
            var originalityScore = GetIntProperty("originality_score");
            var originalityDefects = GetStringProperty("originality_defects");
            var styleScore = GetIntProperty("style_score");
            var styleDefects = GetStringProperty("style_defects");
            var worldbuildingScore = GetIntProperty("worldbuilding_score");
            var worldbuildingDefects = GetStringProperty("worldbuilding_defects");
            var thematicScore = GetIntProperty("thematic_coherence_score");
            var thematicDefects = GetStringProperty("thematic_coherence_defects");
            var emotionalScore = GetIntProperty("emotional_impact_score");
            var emotionalDefects = GetStringProperty("emotional_impact_defects");
            var totalScore = GetDoubleProperty("total_score");
            var overallEvaluation = GetStringProperty("overall_evaluation");

            var evaluatorModelId = agent.ModelId;

            _database.AddStoryEvaluation(
                storyId,
                narrativeScore, narrativeDefects,
                structureScore, structureDefects,
                characterizationScore, characterizationDefects,
                dialoguesScore, dialoguesDefects,
                pacingScore, pacingDefects,
                originalityScore, originalityDefects,
                styleScore, styleDefects,
                worldbuildingScore, worldbuildingDefects,
                thematicScore, thematicDefects,
                emotionalScore, emotionalDefects,
                totalScore,
                overallEvaluation,
                responseText,
                evaluatorModelId,
                agent.Id
            );

            return (true, totalScore, null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to evaluate story {StoryId} with agent {AgentId}", storyId, agentId);
            return (false, 0, ex.Message);
        }
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
}
