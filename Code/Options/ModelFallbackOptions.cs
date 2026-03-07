namespace TinyGenerator.Configuration;

public sealed class ModelFallbackOptions
{
    // UCB-like exploration constant (typical range: 0.5 - 1.5)
    public double ExplorationConstant { get; set; } = 1.0;
}
