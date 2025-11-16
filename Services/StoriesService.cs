using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using TinyGenerator.Models;

namespace TinyGenerator.Services;

public sealed class StoriesService
{
    private readonly DatabaseService _database;
    private readonly ILogger<StoriesService>? _logger;

    public StoriesService(DatabaseService database, ILogger<StoriesService>? logger = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _logger = logger;
    }

    public long SaveGeneration(string prompt, StoryGeneratorService.GenerationResult r, string? memoryKey = null)
    {
        return _database.SaveGeneration(prompt, r, memoryKey);
    }

    public List<StoryRecord> GetAllStories()
    {
        return _database.GetAllStories();
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
}
