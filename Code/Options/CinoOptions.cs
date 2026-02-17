namespace TinyGenerator.Services;

public sealed class CinoOptions
{
    public int TargetScore { get; set; } = 85;
    public int MaxDurationSeconds { get; set; } = 300;
    public double MinLengthGrowthPercent { get; set; } = 30.0;
    public int LengthPenaltyNoPenaltyChars { get; set; } = 20000;
    public bool UseResponseChecker { get; set; } = true;
    public bool EnableEmbeddingSemanticRepetitionCheck { get; set; } = false;
}
