using TinyGenerator.Models;

namespace TinyGenerator.Services;

public interface IAgentChecker
{
    Agent Agent { get; }
    int MinimalScore { get; }
}

public sealed class AgentCheckerDefinition : IAgentChecker
{
    public AgentCheckerDefinition(Agent agent, int minimalScore)
    {
        Agent = agent ?? throw new ArgumentNullException(nameof(agent));
        MinimalScore = Math.Clamp(minimalScore, 1, 100);
    }

    public Agent Agent { get; }
    public int MinimalScore { get; }
}
