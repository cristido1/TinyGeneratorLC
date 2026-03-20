using TinyGenerator.Models;

namespace TinyGenerator.Services;

public interface ISystemPromptBuilder
{
    string Build(Agent agent, string? roleCode);
}

public sealed class DefaultSystemPromptBuilder : ISystemPromptBuilder
{
    private readonly DatabaseService _database;

    public DefaultSystemPromptBuilder(DatabaseService database)
    {
        _database = database;
    }

    public string Build(Agent agent, string? roleCode)
    {
        var basePrompt = !string.IsNullOrWhiteSpace(agent.SystemPrompt)
            ? agent.SystemPrompt.Trim()
            : !string.IsNullOrWhiteSpace(agent.UserPrompt)
                ? agent.UserPrompt.Trim()
                : "Rispondi in modo utile e coerente con la richiesta.";

        var modelName = ResolveModelName(agent);
        var errors = _database.ListTopModelRoleErrors(agent.ModelId, modelName, roleCode, 10, agent.Id);
        if (errors.Count == 0)
        {
            return basePrompt;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(basePrompt);
        sb.AppendLine();
        sb.AppendLine("IN PASSATO HAI COMMESSO QUESTI ERRORI, NON RIPETERLI:");
        foreach (var err in errors)
        {
            sb.AppendLine($"- {err.ErrorText}");
        }

        return sb.ToString().TrimEnd();
    }

    private string? ResolveModelName(Agent agent)
    {
        if (agent.ModelId.HasValue && agent.ModelId.Value > 0)
        {
            var byId = _database.ResolveModelCallNameById(agent.ModelId.Value);
            if (!string.IsNullOrWhiteSpace(byId))
            {
                return byId.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(agent.ModelName))
        {
            return _database.ResolveModelCallName(agent.ModelName) ?? agent.ModelName.Trim();
        }

        return null;
    }
}
