namespace TinyGenerator.Services;

public sealed class StoryEvaluationOptions
{
    // When story length reaches this threshold, no length penalty is applied.
    public int LengthPenaltyNoPenaltyChars { get; set; } = 10000;
}
