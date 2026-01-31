namespace TinyGenerator.Services
{
    public sealed class AutomaticOperationsOptions
    {
        public bool Enabled { get; set; } = true;
        public int IdleSeconds { get; set; } = 60;

        public AutomaticOperationOptions ReviseAndEvaluate { get; set; } = new()
        {
            Enabled = true,
            Priority = 1
        };

        public AutomaticOperationOptions EvaluateRevised { get; set; } = new()
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

        public AutomaticOperationOptions UpdateModelStats { get; set; } = new()
        {
            Enabled = true,
            Priority = 5
        };

        public AutomaticOperationOptions AutoFinalMixPipeline { get; set; } = new()
        {
            Enabled = true,
            Priority = 7
        };

        public AutoStateDrivenSeriesEpisodeOptions AutoStateDrivenSeriesEpisode { get; set; } = new()
        {
            Enabled = true,
            Priority = 7,
            IntervalMinutes = 20,
            TargetMinutes = 20,
            WordsPerMinute = 150,
            WriterAgentId = 0
        };
    }

    public class AutomaticOperationOptions
    {
        public bool Enabled { get; set; } = true;
        public int Priority { get; set; } = 2;
    }

    public sealed class AutomaticDeleteOptions : AutomaticOperationOptions
    {
        public double MinAverageScore { get; set; } = 60;
        public int MinEvaluations { get; set; } = 2;
    }

    public sealed class AutoStateDrivenSeriesEpisodeOptions : AutomaticOperationOptions
    {
        public int IntervalMinutes { get; set; } = 20;
        public int TargetMinutes { get; set; } = 20;
        public int WordsPerMinute { get; set; } = 150;
        public int WriterAgentId { get; set; } = 0;
    }
}
