using Microsoft.Extensions.Options;
using TinyGenerator.Models;
using System.Text;

namespace TinyGenerator.Services.Commands;

public sealed class RunNreCommand : ICommand
{
    private readonly DatabaseService _database;
    private readonly NreEngine _engine;
    private readonly ICustomLogger? _logger;
    private readonly NarrativeRuntimeEngineOptions _options;
    private readonly ICommandDispatcher? _dispatcher;
    private readonly StoriesService? _storiesService;
    private readonly ICallCenter? _callCenter;
    private readonly string _title;
    private readonly EngineRequest _request;

    public RunNreCommand(
        string title,
        EngineRequest request,
        DatabaseService database,
        NreEngine engine,
        IOptions<NarrativeRuntimeEngineOptions>? options = null,
        ICustomLogger? logger = null,
        ICommandDispatcher? dispatcher = null,
        StoriesService? storiesService = null,
        ICallCenter? callCenter = null)
    {
        _title = string.IsNullOrWhiteSpace(title) ? "NRE Story" : title.Trim();
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _logger = logger;
        _options = options?.Value ?? new NarrativeRuntimeEngineOptions();
        _dispatcher = dispatcher;
        _storiesService = storiesService;
        _callCenter = callCenter;
    }

    public event EventHandler<CommandProgressEventArgs>? Progress;

    public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default, string? runId = null)
    {
        if (string.IsNullOrWhiteSpace(_request.UserPrompt))
        {
            return new CommandResult(false, "Prompt NRE obbligatorio.");
        }

        var effectiveRunId = string.IsNullOrWhiteSpace(runId)
            ? $"nre_{DateTime.UtcNow:yyyyMMddHHmmssfff}"
            : runId.Trim();

        _logger?.Start(effectiveRunId);
        _logger?.Append(effectiveRunId, "🧠 Avvio Narrative Runtime Engine...");

        var plannerAgent = ResolveAgent(_options.PlannerAgentName);
        var planEvaluatorAgent = ResolveAgent(_options.PlanEvaluatorAgentName);
        var writerAgent = ResolveAgent(_options.WriterAgentName);
        var evaluatorAgent = ResolveAgent(_options.EvaluatorAgentName);
        var resourceInitializerAgent = ResolveAgent(_options.ResourceInitializerAgentName);
        var resourceManagerAgent = ResolveAgent(_options.ResourceManagerAgentName);

        if (plannerAgent == null) return new CommandResult(false, $"Agente NRE planner non trovato: '{_options.PlannerAgentName}'.");
        if (planEvaluatorAgent == null) return new CommandResult(false, $"Agente NRE plan_evaluator non trovato: '{_options.PlanEvaluatorAgentName}'.");
        if (writerAgent == null) return new CommandResult(false, $"Agente NRE writer non trovato: '{_options.WriterAgentName}'.");
        if (evaluatorAgent == null) return new CommandResult(false, $"Agente NRE evaluator non trovato: '{_options.EvaluatorAgentName}'.");
        if (resourceInitializerAgent == null) return new CommandResult(false, $"Agente NRE resource_initializer non trovato: '{_options.ResourceInitializerAgentName}'.");
        if (resourceManagerAgent == null) return new CommandResult(false, $"Agente NRE resource_manager non trovato: '{_options.ResourceManagerAgentName}'.");

        _logger?.Append(effectiveRunId, $"Planner: {plannerAgent.Name}");
        _logger?.Append(effectiveRunId, $"PlanEvaluator: {planEvaluatorAgent.Name}");
        _logger?.Append(effectiveRunId, $"Writer: {writerAgent.Name}");
        _logger?.Append(effectiveRunId, $"Evaluator: {evaluatorAgent.Name}");
        _logger?.Append(effectiveRunId, $"ResourceInitializer: {resourceInitializerAgent.Name}");
        _logger?.Append(effectiveRunId, $"ResourceManager: {resourceManagerAgent.Name}");

        var runningStatusId = _database.GetStoryStatusByCode(_options.StoryStatuses.Running)?.Id;
        var doneStatusId = _database.GetStoryStatusByCode(_options.StoryStatuses.Done)?.Id;
        var failedStatusId = _database.GetStoryStatusByCode(_options.StoryStatuses.Failed)?.Id;

        var storyId = _database.InsertSingleStory(
            prompt: _request.UserPrompt!,
            story: string.Empty,
            modelId: writerAgent.ModelId,
            agentId: writerAgent.Id,
            title: _title,
            serieId: _request.SeriesId.HasValue ? checked((int)_request.SeriesId.Value) : null,
            serieEpisode: _request.SeriesEpisodeNumber);

        if (storyId <= 0)
        {
            return new CommandResult(false, "Impossibile creare la story NRE.");
        }

        if (runningStatusId.HasValue)
        {
            _database.UpdateStoryById(storyId, statusId: runningStatusId.Value, updateStatus: true);
        }

        var story = _database.GetStoryById(storyId);
        if (story == null)
        {
            return new CommandResult(false, $"Story {storyId} creata ma non rileggibile.");
        }

        var folderName = string.IsNullOrWhiteSpace(story.Folder)
            ? $"nre_{storyId:D5}"
            : story.Folder!;
        var storyFolder = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", folderName);
        var snapshotsFolderName = string.IsNullOrWhiteSpace(_options.Snapshot?.FolderName) ? "snapshots" : _options.Snapshot.FolderName.Trim();
        var snapshotsFolder = Path.Combine(storyFolder, snapshotsFolderName);
        Directory.CreateDirectory(storyFolder);
        Directory.CreateDirectory(snapshotsFolder);

        if (!string.Equals(story.Folder, folderName, StringComparison.Ordinal))
        {
            _database.UpdateStoryFolder(storyId, folderName);
        }

        _logger?.Append(effectiveRunId, $"Story creata: {storyId}");
        _logger?.Append(effectiveRunId, $"Folder: {folderName}");
        _dispatcher?.UpdateStep(effectiveRunId, 0, Math.Max(1, _request.MaxSteps), "Preparazione NRE");

        var engineContext = new EngineContext
        {
            Request = new EngineRequest
            {
                EngineName = string.IsNullOrWhiteSpace(_request.EngineName) ? _options.EngineName : _request.EngineName,
                Method = string.IsNullOrWhiteSpace(_request.Method) ? _options.DefaultMethod : _request.Method,
                StructureMode = string.IsNullOrWhiteSpace(_request.StructureMode) ? "standard" : _request.StructureMode.Trim(),
                CostSeverity = string.IsNullOrWhiteSpace(_request.CostSeverity) ? "medium" : _request.CostSeverity.Trim(),
                CombatIntensity = string.IsNullOrWhiteSpace(_request.CombatIntensity) ? "normal" : _request.CombatIntensity.Trim(),
                MaxSteps = _request.MaxSteps <= 0 ? _options.DefaultMaxSteps : _request.MaxSteps,
                SnapshotOnFailure = _request.SnapshotOnFailure,
                RunId = string.IsNullOrWhiteSpace(_request.RunId) ? effectiveRunId : _request.RunId,
                UserPrompt = _request.UserPrompt,
                ResourceHints = _request.ResourceHints,
                PreApprovedPlanSummary = _request.PreApprovedPlanSummary,
                SeriesId = _request.SeriesId,
                SeriesEpisodeNumber = _request.SeriesEpisodeNumber
            },
            StoryId = storyId,
            StoryTitle = string.IsNullOrWhiteSpace(story.Title) ? _title : story.Title,
            StoryFolder = storyFolder,
            SnapshotsFolder = snapshotsFolder,
            CancellationToken = ct,
            ReportProgress = args =>
            {
                Progress?.Invoke(this, args);
                _logger?.Append(effectiveRunId, $"Step {args.Current}/{args.Max} - {args.Description}");
            },
            ReportLiveStatus = live =>
            {
                if (live == null)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(live.OperationName))
                {
                    _dispatcher?.UpdateOperationName(effectiveRunId, live.OperationName);
                }

                if (live.CurrentStep.HasValue && live.MaxStep.HasValue)
                {
                    _dispatcher?.UpdateStep(
                        effectiveRunId,
                        Math.Max(0, live.CurrentStep.Value),
                        Math.Max(1, live.MaxStep.Value),
                        live.StepDescription);
                }
                else if (!string.IsNullOrWhiteSpace(live.StepDescription))
                {
                    _dispatcher?.UpdateStep(effectiveRunId, 0, Math.Max(1, _request.MaxSteps), live.StepDescription);
                }

                if (!string.IsNullOrWhiteSpace(live.AgentName) || !string.IsNullOrWhiteSpace(live.ModelName))
                {
                    _dispatcher?.UpdateAgentModel(effectiveRunId, live.AgentName, live.ModelName);
                }
            },
            Telemetry = new EngineTelemetry(),
            Trace = new List<EngineEvent>(),
            PersistPlannerSummary = summary =>
            {
                if (string.IsNullOrWhiteSpace(summary))
                {
                    return;
                }

                try
                {
                    _database.UpdateStoryNrePlanSummary(storyId, summary);
                    _logger?.Append(effectiveRunId, $"Piano NRE salvato subito (storyId={storyId}).");
                }
                catch (Exception ex)
                {
                    _logger?.Append(effectiveRunId, $"[WARN] Salvataggio anticipato nre_plan_summary fallito: {ex.Message}", "warning");
                }
            },
            PlannerAgent = plannerAgent,
            PlanEvaluatorAgent = planEvaluatorAgent,
            WriterAgent = writerAgent,
            EvaluatorAgent = evaluatorAgent,
            ResourceInitializerAgent = resourceInitializerAgent,
            ResourceManagerAgent = resourceManagerAgent,
            ThreadId = 0
        };

        try
        {
            var result = await _engine.RunAsync(engineContext).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result.PlannerSummary))
            {
                try
                {
                    _database.UpdateStoryNrePlanSummary(storyId, result.PlannerSummary);
                }
                catch (Exception ex)
                {
                    _logger?.Append(effectiveRunId, $"[WARN] Impossibile salvare nre_plan_summary: {ex.Message}", "warning");
                }
            }
            if (result.Succeeded && !string.IsNullOrWhiteSpace(result.FinalText))
            {
                _database.UpdateStoryById(
                    storyId,
                    story: result.FinalText,
                    statusId: doneStatusId,
                    updateStatus: doneStatusId.HasValue);

                _logger?.Append(effectiveRunId, $"✅ NRE completato. storyId={storyId}", "success");
                _logger?.Append(effectiveRunId, $"StopReason={result.StopReason}; steps={result.Telemetry.StepCount}; ccRetries={result.Telemetry.RetryCount}; engineRetries={result.Telemetry.EngineRetryCount}");
                var reviseRunId = _storiesService?.EnqueueReviseStoryCommand(
                    storyId,
                    trigger: "nre_completed",
                    priority: 2,
                    force: true);
                if (!string.IsNullOrWhiteSpace(reviseRunId))
                {
                    _logger?.Append(effectiveRunId, $"Revisione accodata: {reviseRunId}");
                }
                else
                {
                    _logger?.Append(effectiveRunId, "Revisione non accodata (dispatcher non disponibile?).", "warning");
                }
                if (_logger != null) await _logger.MarkCompletedAsync(effectiveRunId, $"storyId={storyId}");
                return new CommandResult(true, $"NRE completed (storyId={storyId}, reviseQueued={(string.IsNullOrWhiteSpace(reviseRunId) ? "false" : "true")})");
            }
            var msg = result.ErrorSummary ?? $"NRE failed ({result.StopReason})";
            if (!string.IsNullOrWhiteSpace(result.SnapshotFilePath))
            {
                msg = $"{msg} | snapshot={result.SnapshotFilePath}";
            }
            var failedMsg = await HandleFailedGenerationAsync(
                storyId,
                effectiveRunId,
                failedStatusId,
                msg,
                ct).ConfigureAwait(false);
            return new CommandResult(false, failedMsg);
        }
        catch (OperationCanceledException)
        {
            var failedMsg = await HandleFailedGenerationAsync(
                storyId,
                effectiveRunId,
                failedStatusId,
                "cancelled",
                ct).ConfigureAwait(false);
            return new CommandResult(false, failedMsg);
        }
        catch (Exception ex)
        {
            var failedMsg = await HandleFailedGenerationAsync(
                storyId,
                effectiveRunId,
                failedStatusId,
                $"Errore NRE: {ex.Message}",
                ct).ConfigureAwait(false);
            return new CommandResult(false, failedMsg);
        }
    }

    private async Task<string> HandleFailedGenerationAsync(
        long storyId,
        string runId,
        int? failedStatusId,
        string failureReason,
        CancellationToken ct)
    {
        if (failedStatusId.HasValue)
        {
            try
            {
                _database.UpdateStoryById(storyId, statusId: failedStatusId.Value, updateStatus: true);
            }
            catch
            {
                // best-effort
            }
        }

        var analyzerHint = await TryAnalyzeFailureReasonAsync(storyId, runId, failureReason, ct).ConfigureAwait(false);
        var finalReason = string.IsNullOrWhiteSpace(analyzerHint)
            ? failureReason
            : $"{failureReason} | analisi={analyzerHint}";

        _logger?.Append(runId, $"FAILED generazione storia (storyId={storyId}) - motivo: {finalReason}", "error");

        var deleted = false;
        try
        {
            deleted = _database.DeleteStoryPhysicallyById(storyId);
        }
        catch (Exception ex)
        {
            _logger?.Append(runId, $"FAILED generazione storia - errore cancellazione storyId={storyId}: {ex.Message}", "error");
        }

        if (deleted)
        {
            _logger?.Append(runId, $"Story {storyId} eliminata dopo fallimento generazione.", "warning");
        }
        else
        {
            _logger?.Append(runId, $"Story {storyId} NON eliminata dopo fallimento (delete failed or record missing).", "warning");
        }

        if (_logger != null)
        {
            await _logger.MarkCompletedAsync(runId, $"FAILED generazione storia: {finalReason}").ConfigureAwait(false);
        }

        return finalReason;
    }

    private async Task<string?> TryAnalyzeFailureReasonAsync(long storyId, string runId, string failureReason, CancellationToken ct)
    {
        if (_callCenter == null)
        {
            return null;
        }

        var agent = ResolveFailureAnalyzerAgent();
        if (agent == null)
        {
            return null;
        }

        try
        {
            var system = !string.IsNullOrWhiteSpace(agent.Instructions)
                ? agent.Instructions!.Trim()
                : (!string.IsNullOrWhiteSpace(agent.Prompt) ? agent.Prompt!.Trim() : "Analizza il failure context e fornisci motivo tecnico e azione suggerita.");

            var history = new ChatHistory();
            history.AddSystem(system);
            history.AddUser(BuildFailureAnalysisInput(storyId, runId, failureReason));

            var options = new CallOptions
            {
                Operation = "nre_failure_analysis",
                Timeout = TimeSpan.FromSeconds(90),
                MaxRetries = 1,
                UseResponseChecker = false,
                AllowFallback = true,
                AskFailExplanation = false,
                SystemPromptOverride = system
            };
            options.DeterministicChecks.Add(new CheckAlwaysSuccess());

            var callResult = await _callCenter.CallAgentAsync(
                storyId: storyId,
                threadId: ("nre_failure_analysis:" + storyId).GetHashCode(StringComparison.Ordinal),
                agent: agent,
                history: history,
                options: options,
                cancellationToken: ct).ConfigureAwait(false);

            if (!callResult.Success || string.IsNullOrWhiteSpace(callResult.ResponseText))
            {
                return null;
            }

            return callResult.ResponseText.Trim();
        }
        catch
        {
            return null;
        }
    }

    private Agent? ResolveFailureAnalyzerAgent()
    {
        var agents = _database.ListAgents().Where(a => a.IsActive).ToList();
        return agents.FirstOrDefault(a => string.Equals(a.Role, "log_analyzer", StringComparison.OrdinalIgnoreCase))
               ?? agents.FirstOrDefault(a => string.Equals(a.Role, "utility_agent", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildFailureAnalysisInput(long storyId, string runId, string failureReason)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Analizza il seguente fallimento di generazione storia NRE.");
        sb.AppendLine($"story_id: {storyId}");
        sb.AppendLine($"run_id: {runId}");
        sb.AppendLine("failure_reason:");
        sb.AppendLine(failureReason ?? string.Empty);
        sb.AppendLine();
        sb.AppendLine("Rispondi con:");
        sb.AppendLine("Failure reason: <motivo tecnico sintetico>");
        sb.AppendLine("Suggested action: <azione concreta>");
        return sb.ToString();
    }

    private Agent? ResolveAgent(string preferredName)
    {
        var agents = _database.ListAgents().Where(a => a.IsActive).ToList();

        var exactName = agents.FirstOrDefault(a =>
            string.Equals(a.Description, preferredName, StringComparison.OrdinalIgnoreCase));
        if (exactName != null) return exactName;

        var exactRole = agents.FirstOrDefault(a =>
            string.Equals(a.Role, preferredName, StringComparison.OrdinalIgnoreCase));
        if (exactRole != null) return exactRole;

        return agents.FirstOrDefault(a =>
            a.Description.Contains(preferredName, StringComparison.OrdinalIgnoreCase) ||
            a.Role.Contains(preferredName, StringComparison.OrdinalIgnoreCase));
    }
}
