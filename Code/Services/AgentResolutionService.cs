using System;
using System.Linq;
using System.Text;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands;

public sealed class AgentResolutionService : IAgentResolutionService
{
    private readonly DatabaseService _database;

    public AgentResolutionService(DatabaseService database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public ResolvedAgent Resolve(string roleCode)
    {
        var agent = _database.ListAgents()
            .FirstOrDefault(a => a.IsActive && string.Equals(a.Role, roleCode, StringComparison.OrdinalIgnoreCase));

        if (agent == null)
        {
            throw new InvalidOperationException($"No active {roleCode} agent found");
        }

        if (!agent.ModelId.HasValue)
        {
            throw new InvalidOperationException($"Agent {agent.Name} has no model configured");
        }

        var modelId = agent.ModelId.Value;
        var modelInfo = _database.GetModelInfoById(modelId);
        if (string.IsNullOrWhiteSpace(modelInfo?.Name))
        {
            throw new InvalidOperationException($"Model not found for agent {agent.Name}");
        }

        var baseSystemPrompt = BuildSystemPrompt(agent);
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { modelInfo.Name };

        // TODO: support policy-based agent selection (e.g. capability tags, weighted routing).
        return new ResolvedAgent(agent, modelId, modelInfo.Name, baseSystemPrompt, tried);
    }

    private static string? BuildSystemPrompt(Models.Agent agent)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(agent.Prompt))
        {
            sb.AppendLine(agent.Prompt);
        }

        if (!string.IsNullOrWhiteSpace(agent.Instructions))
        {
            sb.AppendLine(agent.Instructions);
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }
}

