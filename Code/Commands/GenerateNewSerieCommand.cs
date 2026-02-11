using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands;

public sealed class GenerateNewSerieCommand : ICommand
{
    private readonly string _prompt;
    private readonly DatabaseService _database;
    private readonly IAgentResolutionService _agentResolutionService;
    private readonly ICustomLogger? _logger;
    private readonly IAgentCallService? _modelExecution;
    private readonly SeriesGenerationOptions _options;

    public GenerateNewSerieCommand(
        string prompt,
        DatabaseService database,
        ILangChainKernelFactory kernelFactory,
        IOptionsMonitor<SeriesGenerationOptions>? optionsMonitor = null,
        ICustomLogger? logger = null,
        IServiceScopeFactory? scopeFactory = null,
        ICommandEnqueuer? dispatcher = null,
        IAgentCallService? modelExecution = null,
        IAgentResolutionService? agentResolutionService = null)
    {
        _prompt = prompt ?? string.Empty;
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _agentResolutionService = agentResolutionService ?? new AgentResolutionService(database);
        _logger = logger;
        _modelExecution = modelExecution;
        _options = optionsMonitor?.CurrentValue ?? new SeriesGenerationOptions();

        _ = kernelFactory;
        _ = scopeFactory;
        _ = dispatcher;
    }

    public async Task<CommandResult> ExecuteAsync(string? runId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_prompt))
        {
            return new CommandResult(false, "Prompt vuoto");
        }

        var effectiveRunId = string.IsNullOrWhiteSpace(runId)
            ? $"generate_new_serie_{DateTime.UtcNow:yyyyMMddHHmmssfff}"
            : runId.Trim();
        _logger?.Start(effectiveRunId);

        try
        {
            var agentsByRole = ResolveRequiredAgents(out var resolveError);
            if (agentsByRole == null)
            {
                return new CommandResult(false, resolveError ?? "Impossibile risolvere gli agenti serie");
            }

            var parser = new SeriesTagParser();
            var validationRules = new SeriesValidationRules(parser, _options);
            var agentCaller = new SeriesAgentCaller(_modelExecution, validationRules, _logger);
            var workflow = new SeriesGenerationWorkflow(_options, parser, validationRules, agentCaller);
            var assembler = new SeriesDomainAssembler(parser);
            var repository = new SeriesRepository(_database);

            var workflowResult = await workflow.ExecuteAsync(
                _prompt,
                effectiveRunId,
                agentsByRole,
                (current, max, phase, agent) => ReportStep(effectiveRunId, current, max, phase, agent),
                ct).ConfigureAwait(false);

            if (!workflowResult.Success)
            {
                return new CommandResult(false, workflowResult.Error ?? "Workflow serie fallito");
            }

            var allEpisodeStructures = string.Join("\n\n", workflowResult.EpisodeStructures);
            var buildResult = assembler.BuildSeriesData(
                workflowResult.BibleTags,
                workflowResult.CharacterTags,
                workflowResult.SeasonTags,
                allEpisodeStructures);

            var serieId = repository.Save(buildResult);
            _logger?.Append(effectiveRunId, $"Serie creata: id={serieId}, characters={buildResult.Characters.Count}, episodes={buildResult.Episodes.Count}");
            return new CommandResult(true, $"Serie creata (id {serieId})");
        }
        catch (OperationCanceledException)
        {
            return new CommandResult(false, "Operazione annullata");
        }
        catch (Exception ex)
        {
            _logger?.Append(effectiveRunId, $"Errore GenerateNewSerie: {ex.Message}", "error");
            return new CommandResult(false, $"Errore GenerateNewSerie: {ex.Message}");
        }
        finally
        {
            _logger?.MarkCompleted(effectiveRunId);
        }
    }

    public Task<CommandResult> ExecuteAsync(CancellationToken ct)
        => ExecuteAsync(runId: null, ct: ct);

    private Dictionary<string, Agent>? ResolveRequiredAgents(out string? error)
    {
        var result = new Dictionary<string, Agent>(StringComparer.OrdinalIgnoreCase);
        var requiredRoles = new[]
        {
            CommandRoleCodes.SerieBibleAgent,
            CommandRoleCodes.SerieCharacterAgent,
            CommandRoleCodes.SerieSeasonAgent,
            CommandRoleCodes.SerieEpisodeAgent,
            CommandRoleCodes.SerieValidatorAgent
        };

        foreach (var role in requiredRoles)
        {
            var agent = ResolveSeriesAgent(role, out var resolveError);
            if (agent == null)
            {
                error = resolveError ?? $"Nessun agente attivo con ruolo {role}";
                return null;
            }

            result[role] = agent;
        }

        error = null;
        return result;
    }

    private Agent? ResolveSeriesAgent(string roleCode, out string? error)
    {
        try
        {
            error = null;
            return _agentResolutionService.Resolve(roleCode).Agent;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private void ReportStep(string runId, int current, int max, string phase, Agent agent)
    {
        var modelName = ResolveModelName(agent) ?? "n/a";
        _logger?.Append(runId, $"{phase} [{current}/{max}] | {agent.Name ?? agent.Role ?? "agent"} | {modelName}");
    }

    private string? ResolveModelName(Agent agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.ModelName))
        {
            return agent.ModelName;
        }

        if (!agent.ModelId.HasValue)
        {
            return null;
        }

        return _database.GetModelInfoById(agent.ModelId.Value)?.Name;
    }
}
