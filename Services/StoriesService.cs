using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
// DEPRECATED: using Microsoft.SemanticKernel;
// DEPRECATED: using Microsoft.SemanticKernel.ChatCompletion;
// DEPRECATED: using Microsoft.SemanticKernel.Connectors.OpenAI;
// DEPRECATED: using OpenAI.Chat;
using TinyGenerator.Models;

namespace TinyGenerator.Services;

public sealed class StoriesService
{
    private readonly DatabaseService _database;
    private readonly ILogger<StoriesService>? _logger;
    private readonly TtsService _ttsService;
    // private readonly AgentService _agentService; // DEPRECATED

    public StoriesService(
        DatabaseService database, 
        TtsService ttsService, 
        /* AgentService agentService, */
        ILogger<StoriesService>? logger = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _ttsService = ttsService ?? throw new ArgumentNullException(nameof(ttsService));
        // _agentService = agentService ?? throw new ArgumentNullException(nameof(agentService));
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

    public long InsertSingleStory(string prompt, string story, int? modelId = null, int? agentId = null, double score = 0.0, string? eval = null, int approved = 0, string? status = null, string? memoryKey = null)
    {
        return _database.InsertSingleStory(prompt, story, modelId, agentId, score, eval, approved, status, memoryKey);
    }

    public bool UpdateStoryById(long id, string? story = null, int? modelId = null, int? agentId = null, string? status = null)
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
}
