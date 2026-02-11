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
        return PrepareTagging(storyId, tuning.MinTokensPerChunk, tuning.MaxTokensPerChunk, tuning.TargetTokensPerChunk);
    }

    public StoryTaggingPreparationResult PrepareTagging(long storyId, int minTokens, int maxTokens, int targetTokens)
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
            minTokens,
            maxTokens,
            targetTokens);

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
        => StoryTaggingService.ParseAmbientMapping(mappingText);

    public IReadOnlyList<StoryTaggingService.StoryTagEntry> ParseFormatterMapping(
        IReadOnlyList<StoryTaggingService.StoryRow> rows,
        string mappingText)
        => StoryTaggingService.ParseFormatterMapping(rows, mappingText);

    public IReadOnlyList<StoryTaggingService.StoryTagEntry> ParseMusicMapping(string mappingText)
        => StoryTaggingService.ParseMusicMapping(mappingText);

    public IReadOnlyList<StoryTaggingService.StoryTagEntry> ParseFxMapping(string mappingText, out int invalidLines)
        => StoryTaggingService.ParseFxMapping(mappingText, out invalidLines);

    public bool SaveAmbientTaggingResult(
        StoryTaggingPreparationResult preparation,
        IReadOnlyList<StoryTaggingService.StoryTagEntry> ambientTags,
        out string? error)
    {
        return SaveTaggingResult(preparation, ambientTags, StoryTaggingService.TagTypeAmbient, out error);
    }

    public bool SaveTaggingResult(
        StoryTaggingPreparationResult preparation,
        IReadOnlyList<StoryTaggingService.StoryTagEntry> tags,
        string tagType,
        out string? error)
    {
        var existingTags = StoryTaggingService.LoadStoryTags(preparation.Story.StoryTags);
        existingTags.RemoveAll(t => string.Equals(t.Type, tagType, StringComparison.OrdinalIgnoreCase));
        existingTags.AddRange(tags);

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

    public bool SaveFormatterTaggingResult(
        StoryTaggingPreparationResult preparation,
        IReadOnlyList<StoryTaggingService.StoryTagEntry> formatterTags,
        int? formatterModelId,
        string? formatterPromptHash,
        out string? error)
    {
        var existingTags = StoryTaggingService.LoadStoryTags(preparation.Story.StoryTags);
        existingTags.RemoveAll(t => string.Equals(t.Type, StoryTaggingService.TagTypeFormatter, StringComparison.OrdinalIgnoreCase));
        existingTags.AddRange(formatterTags);

        var storyTagsJson = StoryTaggingService.SerializeStoryTags(existingTags);
        _database.UpdateStoryRowsAndTags(preparation.Story.Id, preparation.StoryRows, storyTagsJson);

        var rebuiltTagged = StoryTaggingService.BuildStoryTagged(preparation.SourceText, existingTags);
        if (string.IsNullOrWhiteSpace(rebuiltTagged))
        {
            error = "Rebuilt tagged story is empty";
            return false;
        }

        var saved = _database.UpdateStoryTagged(
            preparation.Story.Id,
            rebuiltTagged,
            formatterModelId,
            formatterPromptHash,
            null);
        if (!saved)
        {
            error = $"Failed to persist tagged story for {preparation.Story.Id}";
            return false;
        }

        error = null;
        return true;
    }
}

