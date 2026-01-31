namespace TinyGenerator.Services;

public sealed class StateDrivenStoryGenerationOptions
{
    public bool EnableDegenerativePunctuationCheck { get; set; } = true;
    public int MaxDegenerativePunctuationMatches { get; set; } = 2;

    public bool EnableDialogueLoopCheck { get; set; } = true;

    public bool EnableEmotionalLoopCheck { get; set; } = true;

    public bool EnableActionPresenceCheck { get; set; } = true;

    public bool EnableSimilarSentenceRepetitionCheck { get; set; } = true;
    public double SimilarSentenceSimilarityThreshold { get; set; } = 0.8;
    public int SimilarSentenceRepeatLimit { get; set; } = 2;

    public bool EnableParagraphActionGapCheck { get; set; } = true;
    public int ParagraphActionGapThreshold { get; set; } = 2;

    public bool EnableHistoricalLoopDetection { get; set; } = true;
    public double HistoricalLoopSimilarityThreshold { get; set; } = 0.85;
    public int HistoricalLoopRepeatThreshold { get; set; } = 6;
    public double HistoricalLoopRepeatRatioThreshold { get; set; } = 0.5;
    public int HistoricalLoopHistorySentenceCount { get; set; } = 5;
}
