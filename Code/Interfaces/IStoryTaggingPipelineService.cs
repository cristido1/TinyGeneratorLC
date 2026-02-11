using System.Collections.Generic;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands;

public interface IStoryTaggingPipelineService
{
    StoryTaggingPreparationResult PrepareAmbientTagging(long storyId, CommandTuningOptions.AmbientExpertTuning tuning);
    StoryTaggingPreparationResult PrepareTagging(long storyId, int minTokens, int maxTokens, int targetTokens);
    void PersistInitialRows(StoryTaggingPreparationResult preparation);
    IReadOnlyList<StoryTaggingService.StoryTagEntry> ParseAmbientMapping(string mappingText);
    IReadOnlyList<StoryTaggingService.StoryTagEntry> ParseFormatterMapping(
        IReadOnlyList<StoryTaggingService.StoryRow> rows,
        string mappingText);
    IReadOnlyList<StoryTaggingService.StoryTagEntry> ParseMusicMapping(string mappingText);
    IReadOnlyList<StoryTaggingService.StoryTagEntry> ParseFxMapping(string mappingText, out int invalidLines);
    bool SaveAmbientTaggingResult(
        StoryTaggingPreparationResult preparation,
        IReadOnlyList<StoryTaggingService.StoryTagEntry> ambientTags,
        out string? error);
    bool SaveTaggingResult(
        StoryTaggingPreparationResult preparation,
        IReadOnlyList<StoryTaggingService.StoryTagEntry> tags,
        string tagType,
        out string? error);
    bool SaveFormatterTaggingResult(
        StoryTaggingPreparationResult preparation,
        IReadOnlyList<StoryTaggingService.StoryTagEntry> formatterTags,
        int? formatterModelId,
        string? formatterPromptHash,
        out string? error);
}

