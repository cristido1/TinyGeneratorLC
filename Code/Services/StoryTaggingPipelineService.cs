using System;
using System.Collections.Generic;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands;

public sealed class StoryTaggingPipelineService : IStoryTaggingPipelineService
{
    private readonly DatabaseService _database;

    public StoryTaggingPipelineService(DatabaseService database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public StoryTaggingPreparationResult PrepareAmbientTagging(long storyId, CommandTuningOptions.AmbientExpertTuning tuning)
    {
        var story = _database.GetStoryById(storyId);
        if (story == null)
        {
            throw new InvalidOperationException($"Story {storyId} not found");
        }

        var sourceText = !string.IsNullOrWhiteSpace(story.StoryRevised)
            ? story.StoryRevised
            : story.StoryRaw;

        if (string.IsNullOrWhiteSpace(sourceText))
        {
            throw new InvalidOperationException($"Story {storyId} has no revised text");
        }

        var rowsBuild = StoryTaggingService.BuildStoryRows(sourceText);
        var storyRows = string.IsNullOrWhiteSpace(story.StoryRows) ? rowsBuild.StoryRows : story.StoryRows;
        if (string.IsNullOrWhiteSpace(storyRows))
        {
            storyRows = rowsBuild.StoryRows;
        }

        var rows = StoryTaggingService.ParseStoryRows(storyRows);
        if (rows.Count == 0)
        {
            throw new InvalidOperationException("No rows produced from story text");
        }

        var chunks = StoryTaggingService.SplitRowsIntoChunks(
            rows,
            tuning.MinTokensPerChunk,
            tuning.MaxTokensPerChunk,
            tuning.TargetTokensPerChunk);

        if (chunks.Count == 0)
        {
            throw new InvalidOperationException("No chunks produced from story rows");
        }

        return new StoryTaggingPreparationResult(story, sourceText, storyRows, rows, chunks);
    }

    public void PersistInitialRows(StoryTaggingPreparationResult preparation)
    {
        _database.UpdateStoryRowsAndTags(preparation.Story.Id, preparation.StoryRows, preparation.Story.StoryTags);
    }

    public IReadOnlyList<StoryTaggingService.StoryTagEntry> ParseAmbientMapping(string mappingText)
    {
        return StoryTaggingService.ParseAmbientMapping(mappingText);
    }

    public bool SaveAmbientTaggingResult(
        StoryTaggingPreparationResult preparation,
        IReadOnlyList<StoryTaggingService.StoryTagEntry> ambientTags,
        out string? error)
    {
        var existingTags = StoryTaggingService.LoadStoryTags(preparation.Story.StoryTags);
        existingTags.RemoveAll(t => t.Type == StoryTaggingService.TagTypeAmbient);
        existingTags.AddRange(ambientTags);

        var storyTagsJson = StoryTaggingService.SerializeStoryTags(existingTags);
        _database.UpdateStoryRowsAndTags(preparation.Story.Id, preparation.StoryRows, storyTagsJson);

        var rebuiltTagged = StoryTaggingService.BuildStoryTagged(preparation.SourceText, existingTags);
        if (string.IsNullOrWhiteSpace(rebuiltTagged))
        {
            error = "Rebuilt tagged story is empty";
            return false;
        }

        var saved = _database.UpdateStoryTaggedContent(preparation.Story.Id, rebuiltTagged);
        if (!saved)
        {
            error = $"Failed to persist tagged story for {preparation.Story.Id}";
            return false;
        }

        // TODO: centralize post-save validation/metrics for all tagging pipelines.
        error = null;
        return true;
    }
}

