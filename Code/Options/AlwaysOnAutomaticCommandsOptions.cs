namespace TinyGenerator.Services;

public sealed class AlwaysOnAutomaticCommandsOptions
{
    public EmbeddingBackfillScheduleOptions EmbeddingBackfill { get; set; } = new();
    public UpdateModelStatsFromLogsScheduleOptions UpdateModelStatsFromLogs { get; set; } = new();
    public StorySummariesScheduleOptions StorySummaries { get; set; } = new();
    public LogErrorAnalysisScheduleOptions LogErrorAnalysis { get; set; } = new();
}

public class AutomaticScheduleOptions
{
    public int StartupDelaySeconds { get; set; } = 30;
    public int IntervalSeconds { get; set; } = 300;
}

public sealed class EmbeddingBackfillScheduleOptions : AutomaticScheduleOptions
{
    public EmbeddingBackfillScheduleOptions()
    {
        StartupDelaySeconds = 20;
        IntervalSeconds = 120;
    }
}

public sealed class UpdateModelStatsFromLogsScheduleOptions : AutomaticScheduleOptions
{
    public int BatchSize { get; set; } = 200;
    public int MaxBatchesPerRun { get; set; } = 5;

    public UpdateModelStatsFromLogsScheduleOptions()
    {
        StartupDelaySeconds = 30;
        IntervalSeconds = 60;
    }
}

public sealed class StorySummariesScheduleOptions : AutomaticScheduleOptions
{
    public int MinScore { get; set; } = 60;

    public StorySummariesScheduleOptions()
    {
        StartupDelaySeconds = 30;
        IntervalSeconds = 3600;
    }
}

public sealed class LogErrorAnalysisScheduleOptions : AutomaticScheduleOptions
{
    public int MaxThreadsPerRun { get; set; } = 5;
    public int ScanWindowThreads { get; set; } = 50;

    public LogErrorAnalysisScheduleOptions()
    {
        StartupDelaySeconds = 45;
        IntervalSeconds = 300;
    }
}
