namespace TinyGenerator.Services;

public sealed class RepetitionDetectionOptions
{
    public int NGramSize { get; set; } = 3;
    public int LocalWindow { get; set; } = 5;
    public int RecentMemorySize { get; set; } = 50;
    public int ChunkSizeSentences { get; set; } = 6;
    public double LocalThreshold { get; set; } = 0.70;
    public double MemoryThreshold { get; set; } = 0.70;
    public double ChunkThreshold { get; set; } = 0.80;
    public double HardFailThreshold { get; set; } = 0.85;
    public double PenaltyMedium { get; set; } = 0.75;
    public double PenaltyLow { get; set; } = 0.65;
    public bool RemoveStopWords { get; set; } = true;
}
