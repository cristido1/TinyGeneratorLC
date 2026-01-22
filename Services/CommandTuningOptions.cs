namespace TinyGenerator.Services;

public sealed class CommandTuningOptions
{
    public AmbientExpertTuning AmbientExpert { get; set; } = new();
    public FxExpertTuning FxExpert { get; set; } = new();
    public MusicExpertTuning MusicExpert { get; set; } = new();
    public TransformStoryRawToTaggedTuning TransformStoryRawToTagged { get; set; } = new();
    public GenerateNextChunkTuning GenerateNextChunk { get; set; } = new();
    public PlannedStoryTuning PlannedStory { get; set; } = new();
    public FullStoryPipelineTuning FullStoryPipeline { get; set; } = new();

    public sealed class AmbientExpertTuning
    {
        public int MinTokensPerChunk { get; set; } = 1000;
        public int MaxTokensPerChunk { get; set; } = 2000;
        public int TargetTokensPerChunk { get; set; } = 1500;
        public int OverlapTokens { get; set; } = 150;
        public int MaxAttemptsPerChunk { get; set; } = 3;
        public int MinAmbientTagsPerChunkRequirement { get; set; } = 2;
        public int RetryDelayBaseSeconds { get; set; } = 2;
    }

    public sealed class FxExpertTuning
    {
        public int DefaultTargetTokensPerChunk { get; set; } = 900;
        public int DefaultMaxTokensPerChunk { get; set; } = 1400;
        public int MaxAttemptsPerChunk { get; set; } = 3;
        public int MinFxTagsPerChunk { get; set; } = 1;
        public bool DiagnoseOnFinalFailure { get; set; } = true;
        public bool FinalRetryAfterDiagnosis { get; set; } = true;
        public int RetryDelayBaseSeconds { get; set; } = 2;
    }

    public sealed class MusicExpertTuning
    {
        public int MinTokensPerChunk { get; set; } = 1000;
        public int MaxTokensPerChunk { get; set; } = 2000;
        public int TargetTokensPerChunk { get; set; } = 1500;
        public int OverlapTokens { get; set; } = 150;
        public int MaxAttemptsPerChunk { get; set; } = 3;
        public int MaxMusicTagsPerChunkRequirement { get; set; } = 3;
        public int MinMusicTagsPerChunkRequirement { get; set; } = 1;
        public int RetryDelayBaseSeconds { get; set; } = 2;
    }

    public sealed class TransformStoryRawToTaggedTuning
    {
        public int MinTokensPerChunk { get; set; } = 1000;
        public int MaxTokensPerChunk { get; set; } = 2000;
        public int TargetTokensPerChunk { get; set; } = 1500;
        public int OverlapTokens { get; set; } = 150;
        public int MaxAttemptsPerChunk { get; set; } = 3;
        public int MaxOverlapChars { get; set; } = 8000;
        public int MinTagsPerChunkRequirement { get; set; } = 3;
        public int RetryDelayBaseSeconds { get; set; } = 2;
    }

    public sealed class GenerateNextChunkTuning
    {
        public int ContextTailChars { get; set; } = 800;
        public int MaxAttempts { get; set; } = 3;
    }

    public sealed class PlannedStoryTuning
    {
        public int BeatMaxRetries { get; set; } = 3;
        public int MinBeatLengthChars { get; set; } = 500;
        public int RetryDelayMilliseconds { get; set; } = 1000;
    }

    public sealed class FullStoryPipelineTuning
    {
        public int AudioPipelineTotalSteps { get; set; } = 9;
    }
}
