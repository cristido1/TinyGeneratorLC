
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TinyGenerator.Models;
using TinyGenerator.Services.Text;

namespace TinyGenerator.Services.Commands;

public sealed class CanonExtractorCommand : ICommand
{
    private readonly long _storyId;
    private readonly int _serieId;
    private readonly int _episodeNumber;
    private readonly string _runId;
    private readonly DatabaseService _database;
    private readonly ILangChainKernelFactory _kernelFactory;
    private readonly ICommandEnqueuer? _dispatcher;
    private readonly ICustomLogger? _logger;
    private readonly IServiceScopeFactory? _scopeFactory;

    public CanonExtractorCommand(
        long storyId,
        int serieId,
        int episodeNumber,
        string runId,
        DatabaseService database,
        ILangChainKernelFactory kernelFactory,
        ICommandEnqueuer? dispatcher,
        ICustomLogger? logger,
        IServiceScopeFactory? scopeFactory)
    {
        _storyId = storyId;
        _serieId = serieId;
        _episodeNumber = episodeNumber;
        _runId = runId;
        _database = database;
        _kernelFactory = kernelFactory;
        _dispatcher = dispatcher;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default)
    {
        var story = _database.GetStoryById(_storyId);
        if (story == null)
        {
            return new CommandResult(false, $"Story {_storyId} non trovata");
        }

        var episode = _database.GetSeriesEpisodeBySerieAndNumber(_serieId, _episodeNumber);
        if (episode == null)
        {
            return new CommandResult(false, $"Episodio serie non trovato (serie {_serieId}, episodio {_episodeNumber})");
        }

        var currentState = _database.GetCurrentSeriesState(_serieId);
        var stateInJson = currentState?.WorldStateJson ?? "{}";
        _database.EnsureSeriesEpisodeStateInJson(_serieId, _episodeNumber, stateInJson);

        var agent = GetActiveAgent(CommandRoleCodes.CanonExtractor);
        if (agent == null)
        {
            return new CommandResult(false, $"Nessun agente attivo con ruolo {CommandRoleCodes.CanonExtractor}");
        }

        var storyText = !string.IsNullOrWhiteSpace(story.StoryRevised) ? story.StoryRevised : story.StoryRaw;
        if (string.IsNullOrWhiteSpace(storyText))
        {
            return new CommandResult(false, "Testo episodio vuoto o mancante");
        }

        var prompt = BuildCanonExtractorPrompt(storyText);
        var response = await CallAgentAsync(agent, prompt, roleOverride: CommandRoleCodes.CanonExtractor, ct: ct);
        if (!response.Success)
        {
            return new CommandResult(false, response.Error ?? "CanonExtractor failed");
        }

        var rawCanon = response.Text ?? string.Empty;
        if (BracketTagParser.TryGetTagContent(rawCanon, "CANON_EVENTS", out var canonEventsContent))
        {
            // Persist as TAG block (no JSON), used by downstream prompts.
            var payload = $"[CANON_EVENTS]\n{canonEventsContent}\n[/CANON_EVENTS]";
            _database.UpdateSeriesEpisodeStateFields(_serieId, _episodeNumber, canonEvents: payload);
        }
        else
        {
            // Backward-compatible JSON mode.
            if (!StateDrivenPipelineHelpers.TryExtractJson(rawCanon, JsonValueKind.Array, out var canonJson, out var jsonErr))
            {
                return new CommandResult(false, $"CanonExtractor output non valido: {jsonErr}");
            }
            _database.UpdateSeriesEpisodeStateFields(_serieId, _episodeNumber, canonEvents: canonJson);
        }

        EnqueueStateDeltaBuilder();
        return new CommandResult(true, $"Canon events estratti ({_serieId}/{_episodeNumber})");
    }

    private void EnqueueStateDeltaBuilder()
    {
        if (_dispatcher == null) return;
        var runId = StateDrivenPipelineHelpers.NewRunId(CommandRoleCodes.StateDeltaBuilder, _storyId);
        _dispatcher.Enqueue(
            new DelegateCommand(
                CommandRoleCodes.StateDeltaBuilder,
                ctx =>
                {
                    var cmd = new StateDeltaBuilderCommand(
                        _storyId,
                        _serieId,
                        _episodeNumber,
                        runId,
                        _database,
                        _kernelFactory,
                        _dispatcher,
                        _logger,
                        _scopeFactory);
                    return cmd.ExecuteAsync(ctx.CancellationToken);
                },
                priority: 3),
            runId: runId,
            threadScope: $"series/{_serieId}/episode/{_episodeNumber}",
            metadata: StateDrivenPipelineHelpers.BuildMetadata(_storyId, _serieId, _episodeNumber, CommandRoleCodes.StateDeltaBuilder),
            priority: 3);
    }

    private Agent? GetActiveAgent(string role)
    {
        return StateDrivenPipelineHelpers.ResolveActiveAgent(_database, role);
    }

    private async Task<AgentResponse> CallAgentAsync(Agent agent, string prompt, string roleOverride, CancellationToken ct)
    {
        var modelName = StateDrivenPipelineHelpers.ResolveModelName(_database, agent);
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return AgentResponse.Fail($"Agente {agent.Name} senza modello configurato");
        }

        var systemPrompt = StateDrivenPipelineHelpers.BuildSystemPrompt(agent);
        var response = await StateDrivenPipelineHelpers.CallModelAsync(
            _kernelFactory,
            _scopeFactory,
            modelName,
            agent,
            roleOverride,
            systemPrompt,
            prompt,
            ct);

        StateDrivenPipelineHelpers.LogNarrativeAgentCall(_database, _storyId, roleOverride, agent, response, useResponseChecker: true);
        if (!response.Success)
        {
            _logger?.Append(_runId, $"CanonExtractor fallito: {response.Error}", "error");
        }
        return response;
    }

    private static string BuildCanonExtractorPrompt(string storyText)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ESTRAI EVENTI CANONICI IN JSON.");
        sb.AppendLine();
        sb.AppendLine("Vincoli:");
        sb.AppendLine("- Ordina gli eventi in sequenza temporale.");
        sb.AppendLine("- Restituisci SOLO un array JSON valido, senza markdown, senza testo extra.");
        sb.AppendLine("- Ogni elemento include obbligatoriamente: order, title, summary, characters (array), location, outcome.");
        sb.AppendLine("- 'order' deve essere intero progressivo (1,2,3...).");
        sb.AppendLine("- 'characters' deve essere sempre un array JSON (anche vuoto).");
        sb.AppendLine();
        sb.AppendLine("Formato richiesto:");
        sb.AppendLine("[");
        sb.AppendLine("  {\"order\":1,\"title\":\"...\",\"summary\":\"...\",\"characters\":[\"A\",\"B\"],\"location\":\"...\",\"outcome\":\"...\"}");
        sb.AppendLine("]");
        sb.AppendLine();
        sb.AppendLine("EPISODIO:");
        sb.AppendLine(storyText);
        return sb.ToString();
    }
}

public sealed class StateDeltaBuilderCommand : ICommand
{
    private readonly long _storyId;
    private readonly int _serieId;
    private readonly int _episodeNumber;
    private readonly string _runId;
    private readonly DatabaseService _database;
    private readonly ILangChainKernelFactory _kernelFactory;
    private readonly ICommandEnqueuer? _dispatcher;
    private readonly ICustomLogger? _logger;
    private readonly IServiceScopeFactory? _scopeFactory;

    public StateDeltaBuilderCommand(
        long storyId,
        int serieId,
        int episodeNumber,
        string runId,
        DatabaseService database,
        ILangChainKernelFactory kernelFactory,
        ICommandEnqueuer? dispatcher,
        ICustomLogger? logger,
        IServiceScopeFactory? scopeFactory)
    {
        _storyId = storyId;
        _serieId = serieId;
        _episodeNumber = episodeNumber;
        _runId = runId;
        _database = database;
        _kernelFactory = kernelFactory;
        _dispatcher = dispatcher;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default)
    {
        var episode = _database.GetSeriesEpisodeBySerieAndNumber(_serieId, _episodeNumber);
        if (episode == null)
        {
            return new CommandResult(false, $"Episodio serie non trovato (serie {_serieId}, episodio {_episodeNumber})");
        }

        if (string.IsNullOrWhiteSpace(episode.CanonEvents))
        {
            return new CommandResult(false, "Canon events mancanti: eseguire prima canon_extractor");
        }

        var currentState = _database.GetCurrentSeriesState(_serieId);
        var stateSummary = currentState?.StateSummary ?? "{}";
        var stateInJson = episode.StateInJson ?? currentState?.WorldStateJson ?? "{}";

        var agent = GetActiveAgent(CommandRoleCodes.StateDeltaBuilder);
        if (agent == null)
        {
            return new CommandResult(false, $"Nessun agente attivo con ruolo {CommandRoleCodes.StateDeltaBuilder}");
        }

        var prompt = BuildDeltaPrompt(stateSummary, stateInJson, episode.CanonEvents ?? "[]");
        var response = await CallAgentAsync(agent, prompt, roleOverride: CommandRoleCodes.StateDeltaBuilder, ct: ct);
        if (!response.Success)
        {
            return new CommandResult(false, response.Error ?? "StateDeltaBuilder failed");
        }

        var rawDelta = response.Text ?? string.Empty;
        if (!StateDrivenPipelineHelpers.TryExtractJson(rawDelta, JsonValueKind.Object, out var deltaJson, out var jsonErr))
        {
            return new CommandResult(false, $"StateDeltaBuilder output non valido: {jsonErr}");
        }
        _database.UpdateSeriesEpisodeStateFields(_serieId, _episodeNumber, deltaJson: deltaJson);

        EnqueueContinuityValidator();
        return new CommandResult(true, $"Delta costruito (serie {_serieId}, episodio {_episodeNumber})");
    }

    private void EnqueueContinuityValidator()
    {
        if (_dispatcher == null) return;
        var runId = StateDrivenPipelineHelpers.NewRunId(CommandRoleCodes.ContinuityValidator, _storyId);
        _dispatcher.Enqueue(
            new DelegateCommand(
                CommandRoleCodes.ContinuityValidator,
                ctx =>
                {
                    var cmd = new ContinuityValidatorCommand(
                        _storyId,
                        _serieId,
                        _episodeNumber,
                        runId,
                        _database,
                        _kernelFactory,
                        _dispatcher,
                        _logger,
                        _scopeFactory);
                    return cmd.ExecuteAsync(ctx.CancellationToken);
                },
                priority: 3),
            runId: runId,
            threadScope: $"series/{_serieId}/episode/{_episodeNumber}",
            metadata: StateDrivenPipelineHelpers.BuildMetadata(_storyId, _serieId, _episodeNumber, CommandRoleCodes.ContinuityValidator),
            priority: 3);
    }

    private Agent? GetActiveAgent(string role)
    {
        return StateDrivenPipelineHelpers.ResolveActiveAgent(_database, role);
    }

    private async Task<AgentResponse> CallAgentAsync(Agent agent, string prompt, string roleOverride, CancellationToken ct)
    {
        var modelName = StateDrivenPipelineHelpers.ResolveModelName(_database, agent);
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return AgentResponse.Fail($"Agente {agent.Name} senza modello configurato");
        }

        var systemPrompt = StateDrivenPipelineHelpers.BuildSystemPrompt(agent);
        var response = await StateDrivenPipelineHelpers.CallModelAsync(
            _kernelFactory,
            _scopeFactory,
            modelName,
            agent,
            roleOverride,
            systemPrompt,
            prompt,
            ct);

        StateDrivenPipelineHelpers.LogNarrativeAgentCall(_database, _storyId, roleOverride, agent, response, useResponseChecker: true);
        if (!response.Success)
        {
            _logger?.Append(_runId, $"StateDeltaBuilder fallito: {response.Error}", "error");
        }
        return response;
    }

    private static string BuildDeltaPrompt(string stateSummaryCompact, string stateInJson, string canonEventsJson)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AGGIORNA STATO SERIE.");
        sb.AppendLine();
        sb.AppendLine("Vincoli:");
        sb.AppendLine("- Restituisci SOLO il JSON richiesto dalla request.");
        sb.AppendLine("- Nessun markdown, nessun testo extra.");
        sb.AppendLine("- Compila i campi delta, open_threads, last_major_event.");
        sb.AppendLine();
        sb.AppendLine("STATE_SUMMARY_COMPACT:");
        sb.AppendLine(stateSummaryCompact);
        sb.AppendLine();
        sb.AppendLine("STATE_IN_JSON:");
        sb.AppendLine(stateInJson);
        sb.AppendLine();
        sb.AppendLine("CANON_EVENTS:");
        sb.AppendLine(canonEventsJson);
        return sb.ToString();
    }
}

public sealed class ContinuityValidatorCommand : ICommand
{
    private readonly long _storyId;
    private readonly int _serieId;
    private readonly int _episodeNumber;
    private readonly string _runId;
    private readonly DatabaseService _database;
    private readonly ILangChainKernelFactory _kernelFactory;
    private readonly ICommandEnqueuer? _dispatcher;
    private readonly ICustomLogger? _logger;
    private readonly IServiceScopeFactory? _scopeFactory;

    public ContinuityValidatorCommand(
        long storyId,
        int serieId,
        int episodeNumber,
        string runId,
        DatabaseService database,
        ILangChainKernelFactory kernelFactory,
        ICommandEnqueuer? dispatcher,
        ICustomLogger? logger,
        IServiceScopeFactory? scopeFactory)
    {
        _storyId = storyId;
        _serieId = serieId;
        _episodeNumber = episodeNumber;
        _runId = runId;
        _database = database;
        _kernelFactory = kernelFactory;
        _dispatcher = dispatcher;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default)
    {
        var episode = _database.GetSeriesEpisodeBySerieAndNumber(_serieId, _episodeNumber);
        if (episode == null)
        {
            return new CommandResult(false, $"Episodio serie non trovato (serie {_serieId}, episodio {_episodeNumber})");
        }

        if (string.IsNullOrWhiteSpace(episode.CanonEvents) || string.IsNullOrWhiteSpace(episode.DeltaJson))
        {
            return new CommandResult(false, "Mancano canon_events o delta_json per la continuita'");
        }

        var currentState = _database.GetCurrentSeriesState(_serieId);
        var stateSummary = currentState?.StateSummary ?? "{}";

        var agent = GetActiveAgent(CommandRoleCodes.ContinuityValidator);
        if (agent == null)
        {
            return new CommandResult(false, $"Nessun agente attivo con ruolo {CommandRoleCodes.ContinuityValidator}");
        }

        var prompt = BuildContinuityPrompt(stateSummary, episode.CanonEvents ?? "[]", episode.DeltaJson ?? "{}");
        var response = await CallAgentAsync(agent, prompt, roleOverride: CommandRoleCodes.ContinuityValidator, ct: ct);
        if (!response.Success)
        {
            return new CommandResult(false, response.Error ?? "ContinuityValidator failed");
        }

        var rawIssues = (response.Text ?? string.Empty).Trim();
        if (BracketTagParser.ContainsTag(rawIssues, "ISSUES"))
        {
            _logger?.Append(_runId, $"ContinuityValidator issues (TAGS): {rawIssues}");
        }
        else
        {
            // Backward-compatible JSON mode.
            if (StateDrivenPipelineHelpers.TryExtractJson(rawIssues, JsonValueKind.Object, out var issuesJson, out _))
            {
                _logger?.Append(_runId, $"ContinuityValidator issues (JSON): {issuesJson}");
            }
            else
            {
                _logger?.Append(_runId, $"ContinuityValidator issues (raw): {rawIssues}");
            }
        }

        EnqueueStateUpdater();
        return new CommandResult(true, $"Continuity check completato (serie {_serieId}, episodio {_episodeNumber})");
    }

    private void EnqueueStateUpdater()
    {
        if (_dispatcher == null) return;
        var runId = StateDrivenPipelineHelpers.NewRunId("state_updater", _storyId);
        _dispatcher.Enqueue(
            new DelegateCommand(
                "state_updater",
                ctx =>
                {
                    var cmd = new StateUpdaterCommand(
                        _storyId,
                        _serieId,
                        _episodeNumber,
                        runId,
                        _database,
                        _dispatcher,
                        _logger,
                        _scopeFactory,
                        _kernelFactory);
                    return cmd.ExecuteAsync(ctx.CancellationToken);
                },
                priority: 3),
            runId: runId,
            threadScope: $"series/{_serieId}/episode/{_episodeNumber}",
            metadata: StateDrivenPipelineHelpers.BuildMetadata(_storyId, _serieId, _episodeNumber, "state_updater"),
            priority: 3);
    }

    private Agent? GetActiveAgent(string role)
    {
        return StateDrivenPipelineHelpers.ResolveActiveAgent(_database, role);
    }

    private async Task<AgentResponse> CallAgentAsync(Agent agent, string prompt, string roleOverride, CancellationToken ct)
    {
        var modelName = StateDrivenPipelineHelpers.ResolveModelName(_database, agent);
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return AgentResponse.Fail($"Agente {agent.Name} senza modello configurato");
        }

        var systemPrompt = StateDrivenPipelineHelpers.BuildSystemPrompt(agent);
        var response = await StateDrivenPipelineHelpers.CallModelAsync(
            _kernelFactory,
            _scopeFactory,
            modelName,
            agent,
            roleOverride,
            systemPrompt,
            prompt,
            ct);

        StateDrivenPipelineHelpers.LogNarrativeAgentCall(_database, _storyId, roleOverride, agent, response, useResponseChecker: true);
        if (!response.Success)
        {
            _logger?.Append(_runId, $"ContinuityValidator fallito: {response.Error}", "error");
        }
        return response;
    }

    private static string BuildContinuityPrompt(string stateSummaryCompact, string canonEventsJson, string deltaJson)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VERIFICA COERENZA NARRATIVA.");
        sb.AppendLine();
        sb.AppendLine("Restituisci esclusivamente nel formato JSON richiesto dalla request.");
        sb.AppendLine("Nessun testo extra, nessun markdown, nessuna domanda all'utente.");
        sb.AppendLine("Se non rilevi problemi, restituisci comunque un JSON valido con issues vuoto.");
        sb.AppendLine("Se rilevi problemi, descrivili in modo sintetico e concreto.");
        sb.AppendLine("Severita' ammessa: low, medium, high.");
        sb.AppendLine("Esempio valido:");
        sb.AppendLine("{\"issues\":[{\"severity\":\"medium\",\"description\":\"...\"}]}");
        sb.AppendLine();
        sb.AppendLine("STATE_SUMMARY_COMPACT:");
        sb.AppendLine(stateSummaryCompact);
        sb.AppendLine();
        sb.AppendLine("CANON_EVENTS:");
        sb.AppendLine(canonEventsJson);
        sb.AppendLine();
        sb.AppendLine("DELTA_JSON:");
        sb.AppendLine(deltaJson);
        return sb.ToString();
    }
}

public sealed class StateUpdaterCommand : ICommand
{
    private readonly long _storyId;
    private readonly int _serieId;
    private readonly int _episodeNumber;
    private readonly string _runId;
    private readonly DatabaseService _database;
    private readonly ICommandEnqueuer? _dispatcher;
    private readonly ICustomLogger? _logger;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ILangChainKernelFactory? _kernelFactory;

    public StateUpdaterCommand(
        long storyId,
        int serieId,
        int episodeNumber,
        string runId,
        DatabaseService database,
        ICommandEnqueuer? dispatcher,
        ICustomLogger? logger,
        IServiceScopeFactory? scopeFactory,
        ILangChainKernelFactory? kernelFactory)
    {
        _storyId = storyId;
        _serieId = serieId;
        _episodeNumber = episodeNumber;
        _runId = runId;
        _database = database;
        _dispatcher = dispatcher;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _kernelFactory = kernelFactory;
    }

    public Task<CommandResult> ExecuteAsync(CancellationToken ct = default)
    {
        var episode = _database.GetSeriesEpisodeBySerieAndNumber(_serieId, _episodeNumber);
        if (episode == null)
        {
            return Task.FromResult(new CommandResult(false, $"Episodio serie non trovato (serie {_serieId}, episodio {_episodeNumber})"));
        }

        var deltaJson = episode.DeltaJson;
        if (string.IsNullOrWhiteSpace(deltaJson))
        {
            return Task.FromResult(new CommandResult(false, "Delta_json mancante per state_updater"));
        }

        var currentState = _database.GetCurrentSeriesState(_serieId);
        var baseStateJson = currentState?.WorldStateJson ?? "{}";

        string nextWorldState;
        string? openThreads;
        string? lastMajorEvent;

        // Preferred: TAG payload from state_delta_builder.
        if (BracketTagParser.TryGetTagContent(deltaJson, "WORLD_STATE", out var worldStateText))
        {
            nextWorldState = worldStateText;
            openThreads = BracketTagParser.GetTagContentOrNull(deltaJson, "OPEN_THREADS");
            lastMajorEvent = BracketTagParser.GetTagContentOrNull(deltaJson, "LAST_MAJOR_EVENT");
        }
        else
        {
            // Backward-compatible JSON merge.
            var mergeResult = StateDrivenPipelineHelpers.MergeStateJson(baseStateJson, deltaJson);
            if (!mergeResult.Success)
            {
                return Task.FromResult(new CommandResult(false, mergeResult.Error ?? "Merge state failed"));
            }

            nextWorldState = mergeResult.MergedWorldStateJson ?? "{}";
            openThreads = mergeResult.OpenThreadsJson;
            lastMajorEvent = mergeResult.LastMajorEvent;
        }

        var stateRecord = _database.CreateSeriesState(
            _serieId,
            worldStateJson: nextWorldState,
            openThreadsJson: openThreads,
            lastMajorEvent: lastMajorEvent,
            createdBy: "state_updater",
            sourceEpisodeId: episode.Id);

        _database.UpdateSeriesEpisodeStateFields(
            _serieId,
            _episodeNumber,
            stateInJson: episode.StateInJson ?? baseStateJson,
            deltaJson: deltaJson,
            stateOutJson: nextWorldState,
            openThreadsOut: openThreads ?? "[]");

        if (!string.IsNullOrWhiteSpace(lastMajorEvent))
        {
            _database.UpdateSeriesStateCache(_serieId, null, lastMajorEvent);
        }

        EnqueueStateCompressor(stateRecord.Id);
        return Task.FromResult(new CommandResult(true, $"StateUpdater completato (serie {_serieId}, episodio {_episodeNumber})"));
    }

    private void EnqueueStateCompressor(int seriesStateId)
    {
        if (_dispatcher == null || _kernelFactory == null) return;
        var runId = StateDrivenPipelineHelpers.NewRunId(CommandRoleCodes.StateCompressor, _storyId);
        _dispatcher.Enqueue(
            new DelegateCommand(
                CommandRoleCodes.StateCompressor,
                ctx =>
                {
                    var cmd = new StateCompressorCommand(
                        _storyId,
                        _serieId,
                        _episodeNumber,
                        seriesStateId,
                        runId,
                        _database,
                        _dispatcher,
                        _logger,
                        _scopeFactory,
                        _kernelFactory);
                    return cmd.ExecuteAsync(ctx.CancellationToken);
                },
                priority: 3),
            runId: runId,
            threadScope: $"series/{_serieId}/episode/{_episodeNumber}",
            metadata: StateDrivenPipelineHelpers.BuildMetadata(_storyId, _serieId, _episodeNumber, CommandRoleCodes.StateCompressor),
            priority: 3);
    }
}

public sealed class StateCompressorCommand : ICommand
{
    private readonly long _storyId;
    private readonly int _serieId;
    private readonly int _episodeNumber;
    private readonly int _seriesStateId;
    private readonly string _runId;
    private readonly DatabaseService _database;
    private readonly ICommandEnqueuer? _dispatcher;
    private readonly ICustomLogger? _logger;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ILangChainKernelFactory? _kernelFactory;

    public StateCompressorCommand(
        long storyId,
        int serieId,
        int episodeNumber,
        int seriesStateId,
        string runId,
        DatabaseService database,
        ICommandEnqueuer? dispatcher,
        ICustomLogger? logger,
        IServiceScopeFactory? scopeFactory,
        ILangChainKernelFactory? kernelFactory)
    {
        _storyId = storyId;
        _serieId = serieId;
        _episodeNumber = episodeNumber;
        _seriesStateId = seriesStateId;
        _runId = runId;
        _database = database;
        _dispatcher = dispatcher;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _kernelFactory = kernelFactory;
    }

    public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default)
    {
        var currentState = _database.GetCurrentSeriesState(_serieId);
        if (currentState == null)
        {
            return new CommandResult(false, $"Nessuno stato corrente per serie {_serieId}");
        }

        if (_kernelFactory == null)
        {
            return new CommandResult(false, "KernelFactory non disponibile per state_compressor");
        }

        var agent = GetActiveAgent(CommandRoleCodes.StateCompressor);
        if (agent == null)
        {
            return new CommandResult(false, $"Nessun agente attivo con ruolo {CommandRoleCodes.StateCompressor}");
        }

        var series = _database.GetSeriesById(_serieId);
        var prompt = BuildStateCompressorPrompt(currentState, series);
        var response = await CallAgentAsync(agent, prompt, roleOverride: CommandRoleCodes.StateCompressor, ct: ct);
        if (!response.Success)
        {
            return new CommandResult(false, response.Error ?? "StateCompressor failed");
        }

        var rawSummary = response.Text ?? string.Empty;
        if (!StateDrivenPipelineHelpers.TryExtractJson(rawSummary, JsonValueKind.Object, out var summaryJson, out var jsonErr))
        {
            return new CommandResult(false, $"StateCompressor output non valido: {jsonErr}");
        }

        try
        {
            using var doc = JsonDocument.Parse(summaryJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("state_summary_compact", out var summaryNode) ||
                summaryNode.ValueKind != JsonValueKind.String)
            {
                return new CommandResult(false, "StateCompressor output non valido: campo 'state_summary_compact' mancante.");
            }

            var summaryText = summaryNode.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(summaryText))
            {
                return new CommandResult(false, "StateCompressor output non valido: 'state_summary_compact' vuoto.");
            }

            _database.UpdateSeriesStateSummaryAndCache(_seriesStateId, summaryText);
        }
        catch (Exception ex)
        {
            return new CommandResult(false, $"StateCompressor output non valido: {ex.Message}");
        }

        EnqueueRecapBuilder();
        return new CommandResult(true, $"StateCompressor completato (serie {_serieId}, episodio {_episodeNumber})");
    }

    private void EnqueueRecapBuilder()
    {
        if (_dispatcher == null || _kernelFactory == null) return;
        var runId = StateDrivenPipelineHelpers.NewRunId(CommandRoleCodes.RecapBuilder, _storyId);
        _dispatcher.Enqueue(
            new DelegateCommand(
                CommandRoleCodes.RecapBuilder,
                ctx =>
                {
                    var cmd = new RecapBuilderCommand(
                        _storyId,
                        _serieId,
                        _episodeNumber,
                        runId,
                        _database,
                        _kernelFactory,
                        _logger,
                        _scopeFactory);
                    return cmd.ExecuteAsync(ctx.CancellationToken);
                },
                priority: 3),
            runId: runId,
            threadScope: $"series/{_serieId}/episode/{_episodeNumber}",
            metadata: StateDrivenPipelineHelpers.BuildMetadata(_storyId, _serieId, _episodeNumber, CommandRoleCodes.RecapBuilder),
            priority: 3);
    }

    private Agent? GetActiveAgent(string role)
    {
        return StateDrivenPipelineHelpers.ResolveActiveAgent(_database, role);
    }

    private async Task<AgentResponse> CallAgentAsync(Agent agent, string prompt, string roleOverride, CancellationToken ct)
    {
        var modelName = StateDrivenPipelineHelpers.ResolveModelName(_database, agent);
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return AgentResponse.Fail($"Agente {agent.Name} senza modello configurato");
        }

        var systemPrompt = StateDrivenPipelineHelpers.BuildSystemPrompt(agent);
        var response = await StateDrivenPipelineHelpers.CallModelAsync(
            _kernelFactory!,
            _scopeFactory,
            modelName,
            agent,
            roleOverride,
            systemPrompt,
            prompt,
            ct);

        StateDrivenPipelineHelpers.LogNarrativeAgentCall(_database, _storyId, roleOverride, agent, response, useResponseChecker: true);
        if (!response.Success)
        {
            _logger?.Append(_runId, $"StateCompressor fallito: {response.Error}", "error");
        }
        return response;
    }

    private static string BuildStateCompressorPrompt(SeriesState state, Series? series)
    {
        var sb = new StringBuilder();
        sb.AppendLine("COMPRIMI STATO SERIE.");
        sb.AppendLine();
        sb.AppendLine("Vincoli:");
        sb.AppendLine("- Restituisci SOLO il JSON richiesto dalla request.");
        sb.AppendLine("- Nessun markdown, nessun testo extra.");
        sb.AppendLine("- Compila il campo state_summary_compact con un riassunto compatto max 1200-1500 parole.");
        sb.AppendLine();
        sb.AppendLine("WORLD_STATE_JSON:");
        sb.AppendLine(state.WorldStateJson ?? "{}");
        sb.AppendLine();
        sb.AppendLine("OPEN_THREADS_JSON:");
        sb.AppendLine(state.OpenThreadsJson ?? "[]");
        sb.AppendLine();
        sb.AppendLine($"LAST_MAJOR_EVENT: {state.LastMajorEvent ?? string.Empty}");
        sb.AppendLine();

        if (series != null)
        {
            if (!string.IsNullOrWhiteSpace(series.CosaNonDeveMaiSuccedere))
            {
                sb.AppendLine($"HARD_BAN: {series.CosaNonDeveMaiSuccedere}");
            }
            if (!string.IsNullOrWhiteSpace(series.TemiObbligatori))
            {
                sb.AppendLine($"TEMI_OBBLIGATORI: {series.TemiObbligatori}");
            }
            if (series.WorldRulesLocked)
            {
                sb.AppendLine("WORLD_RULES_LOCKED: true");
            }
        }

        return sb.ToString();
    }
}

public sealed class RecapBuilderCommand : ICommand
{
    private readonly long _storyId;
    private readonly int _serieId;
    private readonly int _episodeNumber;
    private readonly string _runId;
    private readonly DatabaseService _database;
    private readonly ILangChainKernelFactory _kernelFactory;
    private readonly ICustomLogger? _logger;
    private readonly IServiceScopeFactory? _scopeFactory;

    public RecapBuilderCommand(
        long storyId,
        int serieId,
        int episodeNumber,
        string runId,
        DatabaseService database,
        ILangChainKernelFactory kernelFactory,
        ICustomLogger? logger,
        IServiceScopeFactory? scopeFactory)
    {
        _storyId = storyId;
        _serieId = serieId;
        _episodeNumber = episodeNumber;
        _runId = runId;
        _database = database;
        _kernelFactory = kernelFactory;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default)
    {
        var episode = _database.GetSeriesEpisodeBySerieAndNumber(_serieId, _episodeNumber);
        if (episode == null)
        {
            return new CommandResult(false, $"Episodio serie non trovato (serie {_serieId}, episodio {_episodeNumber})");
        }

        if (string.IsNullOrWhiteSpace(episode.CanonEvents))
        {
            return new CommandResult(false, "Canon events mancanti: impossibile generare recap");
        }

        var agent = GetActiveAgent(CommandRoleCodes.RecapBuilder);
        if (agent == null)
        {
            return new CommandResult(false, $"Nessun agente attivo con ruolo {CommandRoleCodes.RecapBuilder}");
        }

        var prompt = BuildRecapPrompt(episode.CanonEvents ?? "[]");
        var response = await CallAgentAsync(agent, prompt, roleOverride: CommandRoleCodes.RecapBuilder, ct: ct, skipResponseChecker: true);
        if (!response.Success)
        {
            return new CommandResult(false, response.Error ?? "RecapBuilder failed");
        }

        var recapText = response.Text?.Trim();
        if (string.IsNullOrWhiteSpace(recapText))
        {
            return new CommandResult(false, "RecapBuilder ha restituito testo vuoto");
        }

        _database.UpdateSeriesEpisodeStateFields(_serieId, _episodeNumber, recapText: recapText);

        return new CommandResult(true, $"Recap generato (serie {_serieId}, episodio {_episodeNumber})");
    }

    private Agent? GetActiveAgent(string role)
    {
        return StateDrivenPipelineHelpers.ResolveActiveAgent(_database, role);
    }

    private async Task<AgentResponse> CallAgentAsync(Agent agent, string prompt, string roleOverride, CancellationToken ct, bool skipResponseChecker = false)
    {
        var modelName = StateDrivenPipelineHelpers.ResolveModelName(_database, agent);
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return AgentResponse.Fail($"Agente {agent.Name} senza modello configurato");
        }

        var systemPrompt = StateDrivenPipelineHelpers.BuildSystemPrompt(agent);
        var response = await StateDrivenPipelineHelpers.CallModelAsync(
            _kernelFactory,
            _scopeFactory,
            modelName,
            agent,
            roleOverride,
            systemPrompt,
            prompt,
            ct,
            skipResponseChecker: skipResponseChecker);

        StateDrivenPipelineHelpers.LogNarrativeAgentCall(_database, _storyId, roleOverride, agent, response, useResponseChecker: !skipResponseChecker);
        if (!response.Success)
        {
            _logger?.Append(_runId, $"RecapBuilder fallito: {response.Error}", "error");
        }
        return response;
    }

    private static string BuildRecapPrompt(string canonEventsJson)
    {
        var sb = new StringBuilder();
        sb.AppendLine("GENERA RECAP EPISODIO.");
        sb.AppendLine();
        sb.AppendLine("Vincoli:");
        sb.AppendLine("- Usa SOLO gli eventi canonici.");
        sb.AppendLine("- Output solo testo recap, senza titoli o markup.");
        sb.AppendLine("- Non chiudere con cliffhanger; finale informativo e neutro.");
        sb.AppendLine();
        sb.AppendLine("CANON_EVENTS:");
        sb.AppendLine(canonEventsJson);
        return sb.ToString();
    }
}

internal sealed record AgentResponse(
    bool Success,
    string? Text,
    string? Error,
    string? ModelUsed = null,
    int Attempts = 0,
    long? LatencyMs = null)
{
    public static AgentResponse Fail(string message) => new(false, null, message);
    public static AgentResponse Ok(string text) => new(true, text, null);
}

internal sealed record StateMergeResult(bool Success, string? MergedWorldStateJson, string? OpenThreadsJson, string? LastMajorEvent, string? Error);

internal static class StateDrivenPipelineHelpers
{
    public static Agent? ResolveActiveAgent(DatabaseService database, string roleCode)
    {
        try
        {
            return new AgentResolutionService(database).Resolve(roleCode).Agent;
        }
        catch
        {
            // Backward-compatible fallback for partial/misaligned role configuration.
            return database.ListAgents()
                .FirstOrDefault(a => a.IsActive && a.Role != null && a.Role.Equals(roleCode, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static string NewRunId(string prefix, long storyId)
        => $"{prefix}_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";

    public static IReadOnlyDictionary<string, string> BuildMetadata(long storyId, int serieId, int episodeNumber, string operation)
    {
        return new Dictionary<string, string>
        {
            ["operation"] = operation,
            ["storyId"] = storyId.ToString(),
            ["serieId"] = serieId.ToString(),
            ["episodeNumber"] = episodeNumber.ToString()
        };
    }

    public static string BuildSystemPrompt(Agent agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.Instructions)) return agent.Instructions!;
        if (!string.IsNullOrWhiteSpace(agent.Prompt)) return agent.Prompt!;
        return "Sei un assistente esperto.";
    }

    public static string? ResolveModelName(DatabaseService database, Agent agent)
    {
        if (agent == null) return null;
        if (agent.ModelId.HasValue && agent.ModelId.Value > 0)
        {
            var byId = database.ResolveModelCallNameById(agent.ModelId.Value);
            if (!string.IsNullOrWhiteSpace(byId)) return byId.Trim();
        }
        if (!string.IsNullOrWhiteSpace(agent.ModelName))
        {
            return database.ResolveModelCallName(agent.ModelName) ?? agent.ModelName.Trim();
        }
        return null;
    }

    public static async Task<AgentResponse> CallModelAsync(
        ILangChainKernelFactory kernelFactory,
        IServiceScopeFactory? scopeFactory,
        string modelName,
        Agent agent,
        string roleCode,
        string systemPrompt,
        string userPrompt,
        CancellationToken ct,
        bool allowInternalFallback = true,
        bool skipResponseChecker = false)
    {
        _ = kernelFactory;
        _ = modelName;

        ICallCenter? callCenter = null;
        if (scopeFactory != null)
        {
            using var scope = scopeFactory.CreateScope();
            callCenter = scope.ServiceProvider.GetService<ICallCenter>();
        }

        callCenter ??= ServiceLocator.Services?.GetService<ICallCenter>();

        if (callCenter == null)
        {
            return AgentResponse.Fail("ICallCenter non disponibile nel scope: chiamata centralizzata disabilitata");
        }

        var history = new ChatHistory();
        history.AddSystem(systemPrompt);
        history.AddUser(userPrompt);

        var options = new CallOptions
        {
            Operation = roleCode,
            Timeout = TimeSpan.FromSeconds(120),
            MaxRetries = 1,
            UseResponseChecker = !skipResponseChecker,
            AllowFallback = allowInternalFallback,
            AskFailExplanation = true,
            SystemPromptOverride = systemPrompt
        };
        options.DeterministicChecks.Add(new CheckEmpty
        {
            Options = Options.Create<object>(new Dictionary<string, object>
            {
                ["ErrorMessage"] = $"Risposta vuota per {roleCode}"
            })
        });

        var result = await callCenter.CallAgentAsync(
            storyId: 0,
            threadId: $"{roleCode}:{LogScope.CurrentThreadId}".GetHashCode(StringComparison.Ordinal),
            agent: agent,
            history: history,
            options: options,
            cancellationToken: ct).ConfigureAwait(false);

        if (result.Success && !string.IsNullOrWhiteSpace(result.ResponseText))
        {
            return new AgentResponse(
                true,
                result.ResponseText,
                null,
                result.ModelUsed,
                result.Attempts,
                Math.Max(0, (long)result.Duration.TotalMilliseconds));
        }

        return new AgentResponse(
            false,
            null,
            result.FailureReason ?? "Risposta vuota dal modello",
            result.ModelUsed,
            result.Attempts,
            Math.Max(0, (long)result.Duration.TotalMilliseconds));
    }

    public static void LogNarrativeAgentCall(
        DatabaseService database,
        long storyId,
        string operation,
        Agent agent,
        AgentResponse response,
        bool useResponseChecker)
    {
        if (database == null || storyId <= 0 || agent == null) return;

        var failure = response.Error ?? string.Empty;
        var deterministicResult = "PASS";
        var checkerResult = useResponseChecker ? "PASS" : "SKIPPED";

        if (!response.Success && !string.IsNullOrWhiteSpace(failure))
        {
            var isDeterministic = failure.Contains("GENERIC_ERROR:", StringComparison.OrdinalIgnoreCase)
                                  || failure.Contains("deterministic", StringComparison.OrdinalIgnoreCase)
                                  || failure.Contains("Check", StringComparison.OrdinalIgnoreCase);
            var looksChecker = failure.Contains("REGOLA", StringComparison.OrdinalIgnoreCase)
                               || failure.Contains("Rules not followed", StringComparison.OrdinalIgnoreCase)
                               || failure.Contains("Question asked", StringComparison.OrdinalIgnoreCase)
                               || failure.Contains("Mixed content", StringComparison.OrdinalIgnoreCase);

            if (isDeterministic)
            {
                deterministicResult = $"FAIL ({operation}): {TruncateForLog(failure, 400)}";
            }
            else
            {
                deterministicResult = "PASS";
            }

            if (useResponseChecker)
            {
                checkerResult = looksChecker
                    ? $"FAIL ({operation}): {TruncateForLog(failure, 400)}"
                    : "PASS";
            }
        }

        database.InsertNarrativeAgentCallLog(
            storyId: storyId,
            agentName: agent.Name,
            inputTokens: null,
            outputTokens: null,
            deterministicChecksResult: deterministicResult,
            responseCheckerResult: checkerResult,
            retryCount: Math.Max(0, response.Attempts - 1),
            latencyMs: response.LatencyMs);
    }

    private static string TruncateForLog(string text, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var trimmed = text.Trim();
        if (trimmed.Length <= maxLen) return trimmed;
        return trimmed[..maxLen] + "...";
    }

    public static bool TryExtractJson(string raw, JsonValueKind expectedKind, out string json, out string error)
    {
        json = string.Empty;
        error = string.Empty;

        var candidate = ExtractJsonPayload(raw);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = "Nessun blocco JSON trovato";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(candidate);
            if (doc.RootElement.ValueKind != expectedKind)
            {
                error = $"Tipo JSON inatteso: {doc.RootElement.ValueKind}";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        json = candidate;
        return true;
    }

    public static StateMergeResult MergeStateJson(string baseStateJson, string deltaJson)
    {
        try
        {
            var baseNode = ParseJsonNode(baseStateJson) ?? new JsonObject();
            var deltaNode = ParseJsonNode(deltaJson) ?? new JsonObject();

            JsonNode? deltaObj = deltaNode;
            string? openThreads = null;
            string? lastMajorEvent = null;

            if (deltaNode is JsonObject deltaObject)
            {
                if (deltaObject.TryGetPropertyValue("delta", out var inner) && inner is JsonObject)
                {
                    deltaObj = inner;
                }
                if (deltaObject.TryGetPropertyValue("open_threads", out var threadsNode))
                {
                    openThreads = threadsNode?.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
                }
                if (deltaObject.TryGetPropertyValue("last_major_event", out var lastMajor))
                {
                    lastMajorEvent = lastMajor?.ToString();
                }
            }

            var merged = MergeJsonNodes(baseNode, deltaObj);
            var mergedJson = merged?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "{}";
            return new StateMergeResult(true, mergedJson, openThreads, lastMajorEvent, null);
        }
        catch (Exception ex)
        {
            return new StateMergeResult(false, null, null, null, ex.Message);
        }
    }

    private static JsonNode? ParseJsonNode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    private static JsonNode? MergeJsonNodes(JsonNode? baseNode, JsonNode? deltaNode)
    {
        if (deltaNode == null) return baseNode;
        if (baseNode is JsonObject baseObj && deltaNode is JsonObject deltaObj)
        {
            foreach (var kvp in deltaObj)
            {
                var existing = baseObj.TryGetPropertyValue(kvp.Key, out var current) ? current : null;
                baseObj[kvp.Key] = MergeJsonNodes(existing, kvp.Value);
            }
            return baseObj;
        }

        return deltaNode.DeepClone();
    }

    private static string ExtractAssistantContent(string? response)
    {
        var raw = response ?? string.Empty;
        var trimmed = raw.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return string.Empty;

        if (!trimmed.StartsWith("{", StringComparison.Ordinal) && !trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return raw;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var msg))
            {
                if (msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    return content.GetString() ?? string.Empty;
                if (msg.ValueKind == JsonValueKind.String)
                    return msg.GetString() ?? string.Empty;
            }

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                var first = choices.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object)
                {
                    if (first.TryGetProperty("message", out var choiceMsg) && choiceMsg.ValueKind == JsonValueKind.Object &&
                        choiceMsg.TryGetProperty("content", out var choiceContent) && choiceContent.ValueKind == JsonValueKind.String)
                    {
                        return choiceContent.GetString() ?? string.Empty;
                    }

                    if (first.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object &&
                        delta.TryGetProperty("content", out var deltaContent) && deltaContent.ValueKind == JsonValueKind.String)
                    {
                        return deltaContent.GetString() ?? string.Empty;
                    }
                }
            }
        }
        catch
        {
        }

        return raw;
    }

    private static string ExtractJsonPayload(string raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return string.Empty;
        if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstObj = trimmed.IndexOf('{');
        var firstArr = trimmed.IndexOf('[');
        var start = -1;
        if (firstObj >= 0 && firstArr >= 0)
        {
            start = Math.Min(firstObj, firstArr);
        }
        else if (firstObj >= 0)
        {
            start = firstObj;
        }
        else if (firstArr >= 0)
        {
            start = firstArr;
        }

        if (start < 0) return string.Empty;

        var lastObj = trimmed.LastIndexOf('}');
        var lastArr = trimmed.LastIndexOf(']');
        var end = Math.Max(lastObj, lastArr);
        if (end <= start) return string.Empty;

        return trimmed.Substring(start, end - start + 1);
    }
}

