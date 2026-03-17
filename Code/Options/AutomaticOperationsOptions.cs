namespace TinyGenerator.Services
{
    public sealed class AutomaticOperationsOptions
    {
        public bool Enabled { get; set; } = true;
        public int IdleSeconds { get; set; } = 60;
        public string AutoAdvancementMode { get; set; } = "series";
        public int AutoAdvancementBurstPerPoll { get; set; } = 1;

        public AutomaticOperationOptions ReviseAndEvaluate { get; set; } = new()
        {
            Enabled = true,
            Priority = 1
        };

        public AutomaticEvaluationOptions EvaluateRevised { get; set; } = new()
        {
            Enabled = true,
            Priority = 2
        };

        public AutomaticDeleteOptions AutoDeleteLowRated { get; set; } = new()
        {
            Enabled = true,
            Priority = 3,
            MinAverageScore = 60,
            MinEvaluations = 2
        };

        public AutoCompleteAudioPipelineOptions AutoCompleteAudioPipeline { get; set; } = new()
        {
            Enabled = true,
            Priority = 8,
            MinAverageScore = 60
        };

        public AutoStateDrivenSeriesEpisodeOptions AutoStateDrivenSeriesEpisode { get; set; } = new()
        {
            Enabled = true,
            Priority = 7,
            IntervalMinutes = 20,
            TargetMinutes = 30,
            WordsPerMinute = 150,
            WriterAgentId = 0
        };

        public AutoNreStoryGenerationOptions AutoNreStoryGeneration { get; set; } = new()
        {
            Enabled = true,
            Priority = 7,
            IntervalMinutes = 20,
            MaxSteps = 15
        };
    }

    public class AutomaticOperationOptions
    {
        public bool Enabled { get; set; } = true;
        public int Priority { get; set; } = 2;
    }

    public sealed class AutomaticEvaluationOptions : AutomaticOperationOptions
    {
    }

    public sealed class AutomaticDeleteOptions : AutomaticOperationOptions
    {
        public double MinAverageScore { get; set; } = 60;
        public int MinEvaluations { get; set; } = 2;
    }

    public sealed class AutoStateDrivenSeriesEpisodeOptions : AutomaticOperationOptions
    {
        public int IntervalMinutes { get; set; } = 20;
        public int TargetMinutes { get; set; } = 30;
        public int WordsPerMinute { get; set; } = 150;
        public int WriterAgentId { get; set; } = 0;
        public int TargetQueuedCommands { get; set; } = 1;
    }

    public sealed class AutoCompleteAudioPipelineOptions : AutomaticOperationOptions
    {
        public double MinAverageScore { get; set; } = 60;
    }

    public sealed class AutoNreStoryGenerationOptions : AutomaticOperationOptions
    {
        public int IntervalMinutes { get; set; } = 20;
        public int MaxSteps { get; set; } = 15;
        public int TargetQueuedCommands { get; set; } = 1;
    }
}
