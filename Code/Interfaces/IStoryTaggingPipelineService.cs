using System.Collections.Generic;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands;

public interface IStoryTaggingPipelineService
{
    StoryTaggingPreparationResult PrepareAmbientTagging(long storyId, CommandTuningOptions.AmbientExpertTuning tuning);
    void PersistInitialRows(StoryTaggingPreparationResult preparation);
    IReadOnlyList<StoryTaggingService.StoryTagEntry> ParseAmbientMapping(string mappingText);
    bool SaveAmbientTaggingResult(
        StoryTaggingPreparationResult preparation,
        IReadOnlyList<StoryTaggingService.StoryTagEntry> ambientTags,
        out string? error);
}

