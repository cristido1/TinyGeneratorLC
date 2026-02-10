namespace TinyGenerator.Services.Commands;

public interface IAgentResolutionService
{
    ResolvedAgent Resolve(string roleCode);
}

