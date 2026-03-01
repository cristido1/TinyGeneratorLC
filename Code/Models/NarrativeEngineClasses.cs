using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using TinyGenerator.Models;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public class EngineRequest
{
    public string EngineName { get; set; } = "NRE";
    public string Method { get; set; } = "state_driven";
    public string StructureMode { get; set; } = "standard";
    public string CostSeverity { get; set; } = "medium";
    public string CombatIntensity { get; set; } = "normal";
    public int MaxSteps { get; set; } = 10;
    public bool SnapshotOnFailure { get; set; } = true;
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public string? UserPrompt { get; set; }
    public string? ResourceHints { get; set; }
    public long? SeriesId { get; set; }
    public int? SeriesEpisodeNumber { get; set; }
}

public class EngineContext
{
    public EngineRequest Request { get; set; } = new();
    public long StoryId { get; set; }
    public string StoryFolder { get; set; } = string.Empty;
    public string SnapshotsFolder { get; set; } = string.Empty;
    public CancellationToken CancellationToken { get; set; }
    public Action<CommandProgressEventArgs>? ReportProgress { get; set; }
    public EngineTelemetry Telemetry { get; set; } = new();
    public List<EngineEvent> Trace { get; set; } = new();
    public int ThreadId { get; set; }
    public Agent? PlannerAgent { get; set; }
    public Agent? WriterAgent { get; set; }
    public Agent? EvaluatorAgent { get; set; }
    public Agent? ResourceManagerAgent { get; set; }
    public Action<EngineLiveStatus>? ReportLiveStatus { get; set; }
}

public class EngineLiveStatus
{
    public string? OperationName { get; set; }
    public string? AgentName { get; set; }
    public string? ModelName { get; set; }
    public int? CurrentStep { get; set; }
    public int? MaxStep { get; set; }
    public string? StepDescription { get; set; }
}

public class EngineResult
{
    public long StoryId { get; set; }
    public bool Succeeded { get; set; }
    public string StopReason { get; set; } = string.Empty;
    public string? ErrorSummary { get; set; }
    public EngineTelemetry Telemetry { get; set; } = new();
    public string? FinalText { get; set; }
    public string? SnapshotFilePath { get; set; }
}

public class EngineTelemetry
{
    public int StepCount { get; set; }
    // Retry consumati dentro il CallCenter per la singola/e chiamata/e agente
    // (somma di Attempts - 1).
    public int RetryCount { get; set; }
    // Retry a livello NRE: quante volte l'engine riesegue uno step dopo
    // un failure definitivo restituito dal CallCenter.
    public int EngineRetryCount { get; set; }
    public long TotalLatencyMs { get; set; }
    public int? TotalPromptTokens { get; set; }
    public int? TotalOutputTokens { get; set; }
    public string? LastModelName { get; set; }
    public string? LastAgentName { get; set; }
}

public class EngineEvent
{
    public DateTime Ts { get; set; } = DateTime.UtcNow;
    public string Type { get; set; } = string.Empty;
    public string? Message { get; set; }
    public string? DataJson { get; set; }
}

public class NarrativeState
{
    public int CurrentStepIndex { get; set; }
    public string? CurrentPhase { get; set; }
    public string? CurrentPov { get; set; }
    public int FailureCount { get; set; }
}

public class NarrativeBlock
{
    public int Index { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? Phase { get; set; }
    public string? Pov { get; set; }
}

public class NarrativePhase
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Objective { get; set; } = string.Empty;
    public string Conflict { get; set; } = string.Empty;
    public int TensionLevel { get; set; }
}

public class NreFailureSnapshotPayload
{
    public string SnapshotSchemaVersion { get; set; } = "nre_failure_v1";
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string EngineName { get; set; } = "NRE";
    public string Method { get; set; } = "state_driven";
    public string RunId { get; set; } = string.Empty;
    public EngineRequest EngineRequest { get; set; } = new();
    public long StoryId { get; set; }
    public NarrativeState NarrativeState { get; set; } = new();
    public List<NarrativeBlock> NarrativeBlocks { get; set; } = new();
    public EngineTelemetry EngineTelemetry { get; set; } = new();
    public List<EngineEvent> Trace { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public string? ExceptionStack { get; set; }
    public string? LastAgentName { get; set; }
    public string? LastModelName { get; set; }
    public string? LastError { get; set; }
    public string? LastAgentInteraction { get; set; }
}

public class SnapshotWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
    private readonly IOptionsMonitor<NarrativeRuntimeEngineOptions>? _options;

    public SnapshotWriter(IOptionsMonitor<NarrativeRuntimeEngineOptions>? options = null)
    {
        _options = options;
    }

    public async Task<string> WriteFailureSnapshotAsync(EngineContext ctx, object snapshotPayload)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(snapshotPayload);

        var options = _options?.CurrentValue ?? new NarrativeRuntimeEngineOptions();
        var snapshotOptions = options.Snapshot ?? new NarrativeRuntimeEngineSnapshotOptions();

        var folder = string.IsNullOrWhiteSpace(ctx.SnapshotsFolder)
            ? Path.Combine(ctx.StoryFolder ?? string.Empty, NormalizeFolderName(snapshotOptions.FolderName))
            : ctx.SnapshotsFolder;

        Directory.CreateDirectory(folder);

        var method = SanitizeFileToken(ctx.Request?.Method ?? "unknown");
        var step = Math.Max(0, ctx.Telemetry?.StepCount ?? 0);
        var filePrefix = SanitizeFileToken(snapshotOptions.FilePrefix);
        var failureSuffix = SanitizeFileToken(snapshotOptions.FailureSuffix);
        var timestampFormat = string.IsNullOrWhiteSpace(snapshotOptions.TimestampFormat)
            ? "yyyyMMdd_HHmmss"
            : snapshotOptions.TimestampFormat;
        var fileName =
            $"{filePrefix}_{method}__{ctx.StoryId}__{DateTime.Now.ToString(timestampFormat)}__step{step:00}__{failureSuffix}.json";
        var path = Path.Combine(folder, fileName);

        var json = JsonSerializer.Serialize(snapshotPayload, JsonOptions);
        await File.WriteAllTextAsync(path, json, ctx.CancellationToken).ConfigureAwait(false);
        return path;
    }

    private static string SanitizeFileToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().ToLowerInvariant().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0 || char.IsWhiteSpace(chars[i]))
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }

    private static string NormalizeFolderName(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "snapshots" : value.Trim();
    }
}

public class NreEngine : IEngine
{
    private readonly SnapshotWriter _snapshotWriter;
    private readonly IOptionsMonitor<NarrativeRuntimeEngineOptions>? _options;
    private readonly ICallCenter? _callCenter;
    private readonly TextValidationService? _textValidationService;

    public NreEngine(
        SnapshotWriter snapshotWriter,
        ICallCenter? callCenter = null,
        IOptionsMonitor<NarrativeRuntimeEngineOptions>? options = null,
        TextValidationService? textValidationService = null)
    {
        _snapshotWriter = snapshotWriter ?? throw new ArgumentNullException(nameof(snapshotWriter));
        _callCenter = callCenter;
        _options = options;
        _textValidationService = textValidationService;
    }

    public string EngineName => GetOptions().EngineName;

    public async Task<EngineResult> RunAsync(EngineContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(ctx.Request);

        var state = new NarrativeState();
        var blocks = new List<NarrativeBlock>();

        var result = new EngineResult
        {
            StoryId = ctx.StoryId,
            Telemetry = ctx.Telemetry
        };

        try
        {
            var nreOptions = GetOptions();
            var stopReasons = nreOptions.StopReasons ?? new NarrativeRuntimeEngineStopReasonsOptions();
            var effectiveMaxSteps = ctx.Request.MaxSteps > 0 ? ctx.Request.MaxSteps : Math.Max(1, nreOptions.DefaultMaxSteps);
            var effectiveMethod = string.IsNullOrWhiteSpace(ctx.Request.Method) ? nreOptions.DefaultMethod : ctx.Request.Method;
            var effectiveStructureMode = NormalizeStructureMode(ctx.Request.StructureMode);
            var effectiveCostSeverity = NormalizeCostSeverity(ctx.Request.CostSeverity);
            var effectiveCombatIntensity = NormalizeCombatIntensity(ctx.Request.CombatIntensity);
            ctx.Request.EngineName = string.IsNullOrWhiteSpace(ctx.Request.EngineName) ? nreOptions.EngineName : ctx.Request.EngineName;
            ctx.Request.Method = effectiveMethod;
            ctx.Request.StructureMode = effectiveStructureMode;
            ctx.Request.CostSeverity = effectiveCostSeverity;
            ctx.Request.CombatIntensity = effectiveCombatIntensity;
            ctx.Request.MaxSteps = effectiveMaxSteps;

            ctx.CancellationToken.ThrowIfCancellationRequested();
            if (_callCenter == null)
            {
                throw new InvalidOperationException("ICallCenter non disponibile per NRE.");
            }
            if (ctx.PlannerAgent == null) throw new InvalidOperationException("Planner agent NRE non configurato.");
            if (ctx.WriterAgent == null) throw new InvalidOperationException("Writer agent NRE non configurato.");
            if (ctx.EvaluatorAgent == null) throw new InvalidOperationException("Evaluator agent NRE non configurato.");
            if (ctx.ResourceManagerAgent == null) throw new InvalidOperationException("Resource manager agent NRE non configurato.");
            if (string.IsNullOrWhiteSpace(ctx.Request.UserPrompt))
            {
                throw new InvalidOperationException("UserPrompt obbligatorio per NRE.");
            }

            AddTrace(ctx, "Start", $"Engine {EngineName} avviato (method={effectiveMethod})");
            ctx.ReportProgress?.Invoke(new CommandProgressEventArgs(0, Math.Max(1, effectiveMaxSteps), "NRE start"));
            ReportLive(ctx, operationName: "run_nre:planner", currentStep: 0, maxStep: effectiveMaxSteps, stepDescription: "Pianificazione narrativa");

            var phases = await BuildPlanningAsync(ctx, effectiveMaxSteps, nreOptions).ConfigureAwait(false);
            if (phases.Count == 0)
            {
                throw new InvalidOperationException("Planner NRE non ha restituito fasi valide.");
            }

            ReportLive(
                ctx,
                operationName: "run_nre:resource_manager_init",
                agentName: ctx.ResourceManagerAgent.Name,
                modelName: ResolveAgentModelName(ctx.ResourceManagerAgent),
                currentStep: 0,
                maxStep: effectiveMaxSteps,
                stepDescription: "ResourceManager INIT");

            var currentCanonStateJson = await BuildResourceManagerInitialStateAsync(
                ctx,
                phases,
                nreOptions).ConfigureAwait(false);

            foreach (var phase in phases.Take(effectiveMaxSteps))
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();
                var step = Math.Max(1, phase.Index);

                state.CurrentStepIndex = step;
                state.CurrentPhase = string.IsNullOrWhiteSpace(phase.Name) ? null : phase.Name;
                ctx.Telemetry.StepCount = step;
                AddTrace(ctx, "StepStart", phase.Name, SerializeSmall(new { phase.Index, phase.Name, phase.TensionLevel }));
                ctx.ReportProgress?.Invoke(new CommandProgressEventArgs(
                    step,
                    effectiveMaxSteps,
                    nreOptions.ProgressMessageGenerating));
                ReportLive(ctx, currentStep: step, maxStep: effectiveMaxSteps, stepDescription: $"Fase: {phase.Name}");

                var accepted = false;
                var engineAttempt = 0;
                while (!accepted)
                {
                    ctx.CancellationToken.ThrowIfCancellationRequested();
                    var writerInput = BuildWriterUserInput(
                        ctx.Request.UserPrompt!,
                        phase,
                        blocks,
                        currentCanonStateJson,
                        nreOptions.PreviousBlocksWindow,
                        nreOptions.DialogueTargetPercent,
                        nreOptions.DialogueTolerancePercentPlus,
                        nreOptions.DialogueTolerancePercentMinus);
                    var evaluatorCheckerContext = BuildEvaluatorCheckerContextInput(
                        ctx.Request.UserPrompt!,
                        phase,
                        blocks,
                        nreOptions.PreviousBlocksWindow,
                        ctx.Request.StructureMode,
                        ctx.Request.CostSeverity,
                        ctx.Request.CombatIntensity);
                    ReportLive(
                        ctx,
                        operationName: "run_nre:writer",
                        agentName: ctx.WriterAgent.Name,
                        modelName: ResolveAgentModelName(ctx.WriterAgent),
                        currentStep: step,
                        maxStep: effectiveMaxSteps,
                        stepDescription: $"Writer • {phase.Name}");
                    var writerResult = await CallAgentAsync(
                        ctx,
                        ctx.WriterAgent,
                        "nre_writer",
                        writerInput,
                        TimeSpan.FromSeconds(Math.Max(5, nreOptions.WriterCallTimeoutSeconds)),
                        nreOptions,
                        blocks,
                        agentCheckers: new List<IAgentChecker>
                        {
                            new AgentCheckerDefinition(ctx.EvaluatorAgent, Math.Max(1, nreOptions.EvaluatorMinScore))
                        },
                        checkerContextText: evaluatorCheckerContext).ConfigureAwait(false);

                    if (!writerResult.Success)
                    {
                        state.FailureCount++;
                        AddTrace(ctx, "AgentCall", "Writer failed", SerializeSmall(new { writerResult.FailureReason, writerResult.Attempts }));
                        if (engineAttempt < Math.Max(0, nreOptions.MaxEngineStepRetries))
                        {
                            engineAttempt++;
                            ctx.Telemetry.EngineRetryCount++;
                            AddTrace(ctx, "Retry", "Retry step after writer failure", SerializeSmall(new { step, engineAttempt }));
                            continue;
                        }

                        result.Succeeded = false;
                        result.StopReason = stopReasons.Exception;
                        result.ErrorSummary = $"Writer failure: {writerResult.FailureReason ?? "unknown"}";
                        result.SnapshotFilePath = await TryWriteFailureSnapshotAsync(
                            ctx,
                            state,
                            blocks,
                            result.ErrorSummary,
                            SerializeSmall(new { agent = ctx.WriterAgent.Name, input = writerInput, failure = writerResult.FailureReason }))
                            .ConfigureAwait(false);
                        AddTrace(ctx, "Stop", result.ErrorSummary);
                        return result;
                    }

                    var blockText = (writerResult.ResponseText ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(blockText))
                    {
                        state.FailureCount++;
                        if (engineAttempt < Math.Max(0, nreOptions.MaxEngineStepRetries))
                        {
                            engineAttempt++;
                            ctx.Telemetry.EngineRetryCount++;
                            AddTrace(ctx, "Retry", "Retry step after empty writer output", SerializeSmall(new { step, engineAttempt }));
                            continue;
                        }

                        result.Succeeded = false;
                        result.StopReason = stopReasons.ValidationFailed;
                        result.ErrorSummary = "Writer output vuoto.";
                        result.SnapshotFilePath = await TryWriteFailureSnapshotAsync(ctx, state, blocks, result.ErrorSummary, null)
                            .ConfigureAwait(false);
                        AddTrace(ctx, "Stop", result.ErrorSummary);
                        return result;
                    }

                    var checkerOutcome = writerResult.CheckerOutcomes?
                        .FirstOrDefault(o => string.Equals(o.CheckerAgentName, ctx.EvaluatorAgent.Name, StringComparison.OrdinalIgnoreCase));

                    var rawEvaluatorScore = checkerOutcome?.Score ?? 0;
                    var normalizedEvaluatorScore = NormalizeEvaluatorScore(rawEvaluatorScore, out var scoreScaleNote);
                    if (!string.IsNullOrWhiteSpace(scoreScaleNote))
                    {
                        AddTrace(ctx, "EvaluatorScoreScale", scoreScaleNote, SerializeSmall(new
                        {
                            rawScore = rawEvaluatorScore,
                            normalizedScore = normalizedEvaluatorScore
                        }));
                    }

                    ReportLive(
                        ctx,
                        operationName: "run_nre:resource_manager_update",
                        agentName: ctx.ResourceManagerAgent.Name,
                        modelName: ResolveAgentModelName(ctx.ResourceManagerAgent),
                        currentStep: step,
                        maxStep: effectiveMaxSteps,
                        stepDescription: $"ResourceManager UPDATE • {phase.Name}");
                    var resourceManagerUpdate = await UpdateResourceManagerStateAsync(
                        ctx,
                        phase,
                        blocks,
                        blockText,
                        currentCanonStateJson,
                        nreOptions).ConfigureAwait(false);
                    if (!resourceManagerUpdate.Success || string.IsNullOrWhiteSpace(resourceManagerUpdate.ResponseText))
                    {
                        state.FailureCount++;
                        AddTrace(ctx, "AgentCall", "Resource manager update failed", SerializeSmall(new
                        {
                            resourceManagerUpdate.FailureReason,
                            resourceManagerUpdate.Attempts
                        }));
                        if (engineAttempt < Math.Max(0, nreOptions.MaxEngineStepRetries))
                        {
                            engineAttempt++;
                            ctx.Telemetry.EngineRetryCount++;
                            AddTrace(ctx, "Retry", "Retry step after resource_manager failure", SerializeSmall(new { step, engineAttempt }));
                            continue;
                        }

                        result.Succeeded = false;
                        result.StopReason = stopReasons.Exception;
                        result.ErrorSummary = $"Resource manager failure: {resourceManagerUpdate.FailureReason ?? "unknown"}";
                        result.SnapshotFilePath = await TryWriteFailureSnapshotAsync(
                                ctx,
                                state,
                                blocks,
                                result.ErrorSummary,
                                SerializeSmall(new
                                {
                                    agent = ctx.ResourceManagerAgent.Name,
                                    failure = resourceManagerUpdate.FailureReason
                                }))
                            .ConfigureAwait(false);
                        AddTrace(ctx, "Stop", result.ErrorSummary);
                        return result;
                    }
                    currentCanonStateJson = resourceManagerUpdate.ResponseText.Trim();

                    blocks.Add(new NarrativeBlock
                    {
                        Index = blocks.Count + 1,
                        Text = blockText,
                        Phase = phase.Name,
                        Pov = state.CurrentPov
                    });
                    AddTrace(ctx, "StepOk", $"Blocco {blocks.Count} accettato", SerializeSmall(new
                    {
                        rawScore = rawEvaluatorScore,
                        score = normalizedEvaluatorScore,
                        checkerNeedsRetry = checkerOutcome?.NeedsRetry,
                        checkerIssues = checkerOutcome?.Issues,
                        phase.Name
                    }));
                    accepted = true;
                }

                if (state.FailureCount >= Math.Max(1, nreOptions.MaxAgentFailuresBeforeStop) &&
                    blocks.Count == 0)
                {
                    result.Succeeded = false;
                    result.StopReason = stopReasons.Exception;
                    result.ErrorSummary = "Soglia failure agenti raggiunta.";
                    result.SnapshotFilePath = await TryWriteFailureSnapshotAsync(ctx, state, blocks, result.ErrorSummary, null)
                        .ConfigureAwait(false);
                    AddTrace(ctx, "Stop", result.ErrorSummary);
                    return result;
                }
            }

            result.Succeeded = true;
            result.StopReason = stopReasons.Completed;
            result.FinalText = string.Join(Environment.NewLine + Environment.NewLine, blocks.Select(b => b.Text));
            ReportLive(ctx, operationName: "run_nre:completed", currentStep: effectiveMaxSteps, maxStep: effectiveMaxSteps, stepDescription: "Completato");
            AddTrace(ctx, "Stop", stopReasons.Completed, SerializeSmall(new { blocks = blocks.Count, chars = result.FinalText.Length }));
            return result;
        }
        catch (OperationCanceledException)
        {
            AddTrace(ctx, "Stop", "Cancelled");
            return new EngineResult
            {
                StoryId = ctx.StoryId,
                Succeeded = false,
                StopReason = GetOptions().StopReasons.Cancelled,
                ErrorSummary = "Operazione annullata.",
                Telemetry = ctx.Telemetry
            };
        }
        catch (Exception ex)
        {
            AddTrace(ctx, "Exception", ex.Message);
            result.Succeeded = false;
            result.StopReason = GetOptions().StopReasons.Exception;
            result.ErrorSummary = ex.Message;
            result.SnapshotFilePath = await TryWriteFailureSnapshotAsync(ctx, state, blocks, ex.Message, null, ex.ToString())
                .ConfigureAwait(false);
            return result;
        }
    }

    private async Task<List<NarrativePhase>> BuildPlanningAsync(
        EngineContext ctx,
        int maxSteps,
        NarrativeRuntimeEngineOptions nreOptions)
    {
        var plannerInput = BuildPlannerUserInput(ctx.Request.UserPrompt!, maxSteps);
        var plannerResult = await CallAgentAsync(
            ctx,
            ctx.PlannerAgent!,
            "nre_planner",
            plannerInput,
            TimeSpan.FromSeconds(Math.Max(5, nreOptions.PlannerCallTimeoutSeconds)),
            nreOptions,
            systemPromptOverride: BuildPlannerSystemPrompt(ctx.Request.StructureMode, ctx.Request.CostSeverity, ctx.Request.CombatIntensity)).ConfigureAwait(false);

        if (!plannerResult.Success)
        {
            throw new InvalidOperationException($"Planner failure: {plannerResult.FailureReason ?? "unknown"}");
        }

        if (!TryParsePlanner(plannerResult.ResponseText, out var phases, out var error))
        {
            throw new InvalidOperationException($"Planner JSON non valido: {error}");
        }

        AddTrace(ctx, "AgentCall", "Planner ok", SerializeSmall(new { phases = phases.Count }));
        return phases
            .Where(p => p != null)
            .OrderBy(p => p.Index <= 0 ? int.MaxValue : p.Index)
            .ToList();
    }

    private async Task<string> BuildResourceManagerInitialStateAsync(
        EngineContext ctx,
        IReadOnlyList<NarrativePhase> phases,
        NarrativeRuntimeEngineOptions nreOptions)
    {
        var initInput = BuildResourceManagerInitInput(ctx, phases);
        var initCall = await CallAgentAsync(
            ctx,
            ctx.ResourceManagerAgent!,
            "nre_resource_manager_init",
            initInput,
            TimeSpan.FromSeconds(Math.Max(5, nreOptions.EvaluatorCallTimeoutSeconds)),
            nreOptions).ConfigureAwait(false);

        if (!initCall.Success)
        {
            throw new InvalidOperationException($"ResourceManager INIT failure: {initCall.FailureReason ?? "unknown"}");
        }

        if (!TryExtractCanonStateJson(initCall.ResponseText, out var canonStateJson, out var initError))
        {
            throw new InvalidOperationException($"ResourceManager INIT JSON non valido: {initError}");
        }

        AddTrace(ctx, "AgentCall", "Resource manager INIT ok");
        return canonStateJson;
    }

    private async Task<CallCenterResult> UpdateResourceManagerStateAsync(
        EngineContext ctx,
        NarrativePhase phase,
        IReadOnlyList<NarrativeBlock> acceptedBlocks,
        string newBlock,
        string currentCanonStateJson,
        NarrativeRuntimeEngineOptions nreOptions)
    {
        var updateInput = BuildResourceManagerUpdateInput(
            ctx,
            phase,
            acceptedBlocks,
            newBlock,
            currentCanonStateJson);
        var updateCall = await CallAgentAsync(
            ctx,
            ctx.ResourceManagerAgent!,
            "nre_resource_manager_update",
            updateInput,
            TimeSpan.FromSeconds(Math.Max(5, nreOptions.EvaluatorCallTimeoutSeconds)),
            nreOptions).ConfigureAwait(false);

        if (!updateCall.Success)
        {
            return updateCall;
        }

        if (!TryExtractCanonStateJson(updateCall.ResponseText, out var canonStateJson, out var updateError))
        {
            updateCall.Success = false;
            updateCall.FailureReason = $"ResourceManager UPDATE JSON non valido: {updateError}";
            return updateCall;
        }

        updateCall.ResponseText = canonStateJson;
        AddTrace(ctx, "AgentCall", "Resource manager UPDATE ok");
        return updateCall;
    }

    private async Task<CallCenterResult> CallAgentAsync(
        EngineContext ctx,
        Agent agent,
        string operation,
        string userInput,
        TimeSpan timeout,
        NarrativeRuntimeEngineOptions nreOptions,
        List<NarrativeBlock>? acceptedBlocks = null,
        string? systemPromptOverride = null,
        List<IAgentChecker>? agentCheckers = null,
        string? checkerContextText = null)
    {
        var history = new ChatHistory();
        history.AddUser(userInput);
        var callOptions = new CallOptions
        {
            Operation = operation,
            Timeout = timeout,
            MaxRetries = Math.Max(0, nreOptions.CallCenterMaxRetries),
            UseResponseChecker = nreOptions.UseResponseChecker,
            AllowFallback = nreOptions.AllowFallback,
            AskFailExplanation = false,
            SystemPromptOverride = string.IsNullOrWhiteSpace(systemPromptOverride) ? null : systemPromptOverride,
            CheckerContextText = checkerContextText
        };

        if (agentCheckers != null)
        {
            foreach (var checker in agentCheckers)
            {
                if (checker != null)
                {
                    callOptions.AgentCheckers.Add(checker);
                }
            }
        }

        foreach (var check in BuildDeterministicChecksForNreCall(ctx, agent, operation, nreOptions, acceptedBlocks))
        {
            callOptions.DeterministicChecks.Add(check);
        }

        var result = await _callCenter!.CallAgentAsync(
            storyId: ctx.StoryId,
            threadId: ctx.ThreadId,
            agent: agent,
            history: history,
            options: callOptions,
            cancellationToken: ctx.CancellationToken).ConfigureAwait(false);

        ctx.Telemetry.RetryCount += Math.Max(0, result.Attempts - 1);
        ctx.Telemetry.TotalLatencyMs += Math.Max(0L, (long)result.Duration.TotalMilliseconds);
        ctx.Telemetry.LastAgentName = agent.Name;
        ctx.Telemetry.LastModelName = string.IsNullOrWhiteSpace(result.ModelUsed) ? ctx.Telemetry.LastModelName : result.ModelUsed;
        ReportLive(ctx, agentName: agent.Name, modelName: ctx.Telemetry.LastModelName);

        return result;
    }

    private static string BuildResourceManagerInitInput(
        EngineContext ctx,
        IReadOnlyList<NarrativePhase> phases)
    {
        var phaseSummary = string.Join(
            Environment.NewLine,
            phases.Take(6).Select(p => $"- {p.Index}. {p.Name}: {p.Objective} | conflict={p.Conflict}"));

        return
            "Genera lo stato iniziale delle risorse narrative e rispondi SOLO con JSON valido." + Environment.NewLine +
            "Mode=INIT." + Environment.NewLine +
            "Sono consentite risorse dedotte da prompt e vincoli utente." + Environment.NewLine +
            "In assenza di indicazioni psicologiche esplicite, deduci stato plausibile dal contesto." + Environment.NewLine + Environment.NewLine +
            "Campi tecnici gestiti dal sistema (NON restituirli nel JSON): story_id, series_id, episode_number, chunk_index, last_update_chunk." + Environment.NewLine +
            "Non restituire la sezione delta." + Environment.NewLine + Environment.NewLine +
            "Regole di output anti-troncamento:" + Environment.NewLine +
            "- restituisci JSON compatto (una sola riga, senza markdown);" + Environment.NewLine +
            "- per ogni risorsa usa solo i campi necessari allo schema; evita campi opzionali se non utili;" + Environment.NewLine +
            "- psych_notes deve essere molto breve (max 12 parole);" + Environment.NewLine +
            "- notes_json deve essere sempre un object (usa {} quando non necessario)." + Environment.NewLine + Environment.NewLine +
            $"structure_mode: {ctx.Request.StructureMode}{Environment.NewLine}" +
            $"cost_severity: {ctx.Request.CostSeverity}{Environment.NewLine}" +
            $"combat_intensity: {ctx.Request.CombatIntensity}{Environment.NewLine}" +
            $"resource_hints: {(string.IsNullOrWhiteSpace(ctx.Request.ResourceHints) ? "(none)" : ctx.Request.ResourceHints.Trim())}{Environment.NewLine}{Environment.NewLine}" +
            $"prompt:{Environment.NewLine}{ctx.Request.UserPrompt?.Trim()}{Environment.NewLine}{Environment.NewLine}" +
            $"planned_phases:{Environment.NewLine}{phaseSummary}{Environment.NewLine}{Environment.NewLine}" +
            "Output atteso: { \"canon_state\": { ... } }";
    }

    private static string BuildResourceManagerUpdateInput(
        EngineContext ctx,
        NarrativePhase phase,
        IReadOnlyList<NarrativeBlock> acceptedBlocks,
        string newBlock,
        string currentCanonStateJson)
    {
        var tail = acceptedBlocks
            .TakeLast(3)
            .Select(b => $"- {b.Text}")
            .ToList();
        var previousBlocks = tail.Count == 0 ? "(nessuno)" : string.Join(Environment.NewLine + Environment.NewLine, tail);

        return
            "Aggiorna il canon state delle risorse e rispondi SOLO con JSON valido." + Environment.NewLine +
            "Mode=UPDATE." + Environment.NewLine +
            "Puoi aggiungere risorse nuove solo se il blocco le giustifica in modo esplicito." + Environment.NewLine +
            "Se il testo comporta resurrezioni o recuperi inattesi ma coerenti e accettati dal flusso, applica lo stato conseguente." + Environment.NewLine + Environment.NewLine +
            "Campi tecnici gestiti dal sistema (NON restituirli nel JSON): story_id, series_id, episode_number, chunk_index, last_update_chunk." + Environment.NewLine +
            "Non restituire la sezione delta." + Environment.NewLine + Environment.NewLine +
            "Regole di output anti-troncamento:" + Environment.NewLine +
            "- restituisci JSON compatto (una sola riga, senza markdown);" + Environment.NewLine +
            "- aggiorna e restituisci solo dati strettamente necessari alla continuita';" + Environment.NewLine +
            "- per ogni risorsa usa solo i campi necessari allo schema; evita campi opzionali verbosi;" + Environment.NewLine +
            "- psych_notes deve essere molto breve (max 12 parole);" + Environment.NewLine +
            "- notes_json deve essere sempre un object (usa {} quando non necessario)." + Environment.NewLine + Environment.NewLine +
            $"phase_name: {phase.Name}{Environment.NewLine}" +
            $"phase_objective: {phase.Objective}{Environment.NewLine}" +
            $"phase_conflict: {phase.Conflict}{Environment.NewLine}{Environment.NewLine}" +
            $"current_canon_state_json:{Environment.NewLine}{(string.IsNullOrWhiteSpace(currentCanonStateJson) ? "{}" : currentCanonStateJson)}{Environment.NewLine}{Environment.NewLine}" +
            $"previous_blocks:{Environment.NewLine}{previousBlocks}{Environment.NewLine}{Environment.NewLine}" +
            $"new_block:{Environment.NewLine}{newBlock.Trim()}{Environment.NewLine}{Environment.NewLine}" +
            "Output atteso: { \"canon_state\": { ... } }";
    }

    private static bool TryExtractCanonStateJson(string? rawJson, out string canonStateJson, out string? error)
    {
        canonStateJson = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            error = "response vuota";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.TryGetProperty("canon_state", out var canonState) &&
                canonState.ValueKind == JsonValueKind.Object)
            {
                canonStateJson = canonState.GetRawText();
                return true;
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("resources", out _))
            {
                canonStateJson = doc.RootElement.GetRawText();
                return true;
            }

            error = "campo canon_state mancante";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private IEnumerable<IDeterministicCheck> BuildDeterministicChecksForNreCall(
        EngineContext ctx,
        Agent agent,
        string operation,
        NarrativeRuntimeEngineOptions nreOptions,
        List<NarrativeBlock>? acceptedBlocks)
    {
        var checks = new List<IDeterministicCheck>();
        var op = (operation ?? string.Empty).Trim().ToLowerInvariant();
        var agentIdentity = string.IsNullOrWhiteSpace(agent?.Name) ? "nre_writer" : agent.Name;

        if (op != "nre_writer")
        {
            return checks;
        }

        var historyBlocksWindow = Math.Max(1, nreOptions.DuplicateSentenceHistoryBlocksWindow);
        var historyText = BuildRecentBlocksHistory(acceptedBlocks, historyBlocksWindow);

        checks.Add(new CheckEmpty
        {
            Options = Options.Create<object>(new Dictionary<string, object>
            {
                ["ErrorMessage"] = $"Risposta vuota ({agentIdentity})"
            })
        });

        checks.Add(new CheckFilterBannedPhrases
        {
            Options = Options.Create<object>(new Dictionary<string, object>
            {
                ["BannedPhrasesCsv"] = nreOptions.BannedPhrasesCsv ?? string.Empty
            })
        });

        checks.Add(new CheckDialogueRatioRange
        {
            Options = Options.Create<object>(new Dictionary<string, object>
            {
                ["TargetPercent"] = nreOptions.DialogueTargetPercent,
                ["TolerancePercentPlus"] = nreOptions.DialogueTolerancePercentPlus,
                ["TolerancePercentMinus"] = nreOptions.DialogueTolerancePercentMinus,
                ["MinChars"] = 120
            })
        });

        checks.Add(new CheckNoDuplicateSentencesAcrossBlocks
        {
            Options = Options.Create<object>(new Dictionary<string, object>
            {
                ["HistoryText"] = historyText,
                ["MinWords"] = 6,
                ["SimilarityThreshold"] = 0.92d,
                ["IgnoreSentencesCsv"] = "ricevuto,sì signore,si signore"
            })
        });

        return checks;
    }

    private static string BuildRecentBlocksHistory(List<NarrativeBlock>? acceptedBlocks, int historyBlocksWindow)
    {
        if (acceptedBlocks == null || acceptedBlocks.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            acceptedBlocks
                .TakeLast(Math.Max(1, historyBlocksWindow))
                .Select(b => b.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t)));
    }

    private static string BuildPlannerUserInput(string prompt, int maxSteps)
    {
        return $"Tema: {prompt.Trim()}{Environment.NewLine}" +
               $"Numero di blocchi desiderati: {maxSteps}";
    }

    private static string BuildPlannerSystemPrompt(string? structureMode, string? costSeverity, string? combatIntensity)
    {
        var mode = NormalizeStructureMode(structureMode);
        return string.Equals(mode, "military_strict", StringComparison.OrdinalIgnoreCase)
            ? BuildMilitaryStrictPlannerSystemPrompt(
                NormalizeCostSeverity(costSeverity),
                NormalizeCombatIntensity(combatIntensity))
            : BuildStandardPlannerSystemPrompt();
    }

    private static string NormalizeStructureMode(string? value)
    {
        var mode = (value ?? string.Empty).Trim().ToLowerInvariant();
        return mode switch
        {
            "military_strict" => "military_strict",
            _ => "standard"
        };
    }

    private static string NormalizeCostSeverity(string? value)
    {
        var severity = (value ?? string.Empty).Trim().ToLowerInvariant();
        return severity switch
        {
            "low" => "low",
            "high" => "high",
            _ => "medium"
        };
    }

    private static string NormalizeCombatIntensity(string? value)
    {
        var intensity = (value ?? string.Empty).Trim().ToLowerInvariant();
        return intensity switch
        {
            "low" => "low",
            "high" => "high",
            "total_war" => "total_war",
            _ => "normal"
        };
    }

    private static string BuildStandardPlannerSystemPrompt()
    {
        return
            "Sei un narrative planner." + Environment.NewLine + Environment.NewLine +
            "Genera una struttura narrativa in JSON conforme al response_format della richiesta." + Environment.NewLine + Environment.NewLine +
            "Regole:" + Environment.NewLine +
            "- 5 o 6 fasi." + Environment.NewLine +
            "- Progressione logica e causale." + Environment.NewLine +
            "- Ogni fase deve introdurre un cambiamento reale." + Environment.NewLine +
            "- Non scrivere testo narrativo." + Environment.NewLine +
            "- Output solo JSON valido.";
    }

    private static string BuildMilitaryStrictPlannerSystemPrompt(string costSeverity, string combatIntensity)
    {
        return
            "Sei un narrative planner specializzato in narrativa militare rigorosa." + Environment.NewLine + Environment.NewLine +
            "Genera una struttura narrativa in JSON conforme al response_format della richiesta." + Environment.NewLine + Environment.NewLine +
            $"CostSeverity={NormalizeCostSeverity(costSeverity)}." + Environment.NewLine + Environment.NewLine +
            $"CombatIntensity={NormalizeCombatIntensity(combatIntensity)}." + Environment.NewLine + Environment.NewLine +
            "REGOLE OBBLIGATORIE:" + Environment.NewLine +
            "1) Devono esserci esattamente 6 fasi." + Environment.NewLine +
            "2) Progressione obbligatoria:" + Environment.NewLine +
            "   - Fase 1: situazione operativa chiara, catena di comando esplicita, obiettivo definito." + Environment.NewLine +
            "   - Fase 2: informazione incompleta o errore tattico, rischio reale." + Environment.NewLine +
            "   - Fase 3: perdita concreta, conseguenza irreversibile." + Environment.NewLine +
            "   - Fase 4: decisione disciplinare o morale con costo strategico." + Environment.NewLine +
            "   - Fase 5: escalation, minaccia superiore o rivelazione strategica." + Environment.NewLine +
            "   - Fase 6: nuovo equilibrio instabile, nessuna vittoria pulita, minaccia non risolta." + Environment.NewLine +
            "3) Ogni fase deve avere obiettivo militare esplicito e conflitto operativo concreto." + Environment.NewLine +
            "4) La tensione deve crescere rispetto alla fase precedente (per quanto possibile)." + Environment.NewLine +
            "5) Non e' permessa una risoluzione completa del conflitto." + Environment.NewLine +
            "6) Non scrivere testo narrativo. Output solo JSON valido." + Environment.NewLine + Environment.NewLine +
            "PARAMETRO COSTSEVERITY" + Environment.NewLine +
            "Il parametro CostSeverity definisce l'intensita' delle conseguenze." + Environment.NewLine +
            "LOW: danni temporanei; nessuna morte; nessuna perdita definitiva di nave; nessun sacrificio irreversibile." + Environment.NewLine +
            "MEDIUM: danneggiamento permanente di asset; compromissione irreversibile di sistemi; possibile perdita di personale secondario; ritirata forzata o perdita di posizione strategica." + Environment.NewLine +
            "HIGH: morte di almeno un personaggio rilevante O distruzione totale di un asset importante O perdita irreversibile di nave o comando; conseguenza strategica che peggiora definitivamente la posizione." + Environment.NewLine +
            "La gravita' deve riflettersi esplicitamente nel campo conflict di almeno una fase." + Environment.NewLine +
            "Non generare morte gratuita o decorativa. Ogni perdita deve avere impatto operativo concreto." + Environment.NewLine + Environment.NewLine +
            "PARAMETRO COMBATINTENSITY" + Environment.NewLine +
            "Il parametro CombatIntensity definisce la scala del conflitto." + Environment.NewLine +
            "LOW: minacce isolate; scontri limitati; nessuna distruzione massiva; perdite minime." + Environment.NewLine +
            "NORMAL: scambi di fuoco; danneggiamento significativo; possibili perdite umane o aliene; battaglia circoscritta." + Environment.NewLine +
            "HIGH: distruzione di almeno un'unita' (umana o aliena); perdite multiple; uso di armamenti pesanti; possibile abbordaggio tattico; ritirata forzata o sfondamento difensivo." + Environment.NewLine +
            "TOTAL_WAR: distruzione su larga scala; navi esplose o rese irrecuperabili; perdite elevate su entrambi i fronti; almeno un abbordaggio violento o tentato abbordaggio; uso di armi strategiche; conseguenze permanenti sul teatro operativo." + Environment.NewLine +
            "Le perdite devono essere bilanciate: non solo italiane e non solo aliene." + Environment.NewLine +
            "Ogni distruzione deve avere causa operativa chiara: manovra, errore tattico, superiorita' tecnologica o sacrificio strategico." + Environment.NewLine +
            "Vietate esplosioni casuali, morte gratuita e distruzione senza causa." + Environment.NewLine +
            "Gli abbordaggi devono avere obiettivo preciso, generare vittime o catture, e produrre conseguenze concrete.";
    }

    private static string BuildWriterUserInput(
        string prompt,
        NarrativePhase phase,
        List<NarrativeBlock> blocks,
        string? currentCanonStateJson,
        int previousBlocksWindow,
        int dialogueTargetPercent,
        int dialogueTolerancePercentPlus,
        int dialogueTolerancePercentMinus)
    {
        var tail = blocks
            .TakeLast(Math.Max(1, previousBlocksWindow))
            .Select(b => $"- {b.Text}")
            .ToList();
        var previousBlocks = tail.Count == 0 ? "(nessuno)" : string.Join(Environment.NewLine + Environment.NewLine, tail);

        return
            $"Prompt iniziale:{Environment.NewLine}{prompt.Trim()}{Environment.NewLine}{Environment.NewLine}" +
            $"Fase narrativa corrente:{Environment.NewLine}{phase.Objective}{Environment.NewLine}" +
            $"Conflitto:{Environment.NewLine}{phase.Conflict}{Environment.NewLine}{Environment.NewLine}" +
            $"Vincolo dialoghi:{Environment.NewLine}- target dialogo: {dialogueTargetPercent}%{Environment.NewLine}- tolleranza inferiore: -{dialogueTolerancePercentMinus}%{Environment.NewLine}- tolleranza superiore: +{dialogueTolerancePercentPlus}%{Environment.NewLine}- mantieni il blocco nel range [{Math.Max(0, dialogueTargetPercent - dialogueTolerancePercentMinus)}%, {Math.Min(100, dialogueTargetPercent + dialogueTolerancePercentPlus)}%] di testo dialogato.{Environment.NewLine}{Environment.NewLine}" +
            $"Canon state risorse corrente (JSON):{Environment.NewLine}{(string.IsNullOrWhiteSpace(currentCanonStateJson) ? "{}" : currentCanonStateJson)}{Environment.NewLine}{Environment.NewLine}" +
            $"Blocchi precedenti:{Environment.NewLine}{previousBlocks}";
    }

    private static string BuildEvaluatorUserInput(string prompt, NarrativePhase phase, List<NarrativeBlock> blocks, string newBlock, int previousBlocksWindow)
    {
        var tail = blocks
            .TakeLast(Math.Max(1, previousBlocksWindow))
            .Select(b => $"- {b.Text}")
            .ToList();
        var previousBlocks = tail.Count == 0 ? "(nessuno)" : string.Join(Environment.NewLine + Environment.NewLine, tail);

        return
            $"Prompt iniziale:{Environment.NewLine}{prompt.Trim()}{Environment.NewLine}{Environment.NewLine}" +
            $"Fase narrativa:{Environment.NewLine}{phase.Objective}{Environment.NewLine}{Environment.NewLine}" +
            $"Blocchi precedenti:{Environment.NewLine}{previousBlocks}{Environment.NewLine}{Environment.NewLine}" +
            $"Nuovo blocco:{Environment.NewLine}{newBlock.Trim()}";
    }

    private static string BuildEvaluatorCheckerContextInput(
        string prompt,
        NarrativePhase phase,
        List<NarrativeBlock> blocks,
        int previousBlocksWindow,
        string? structureMode,
        string? costSeverity,
        string? combatIntensity)
    {
        var tail = blocks
            .TakeLast(Math.Max(1, previousBlocksWindow))
            .Select(b => $"- {b.Text}")
            .ToList();
        var previousBlocks = tail.Count == 0 ? "(nessuno)" : string.Join(Environment.NewLine + Environment.NewLine, tail);
        var normalizedStructureMode = NormalizeStructureMode(structureMode);
        var normalizedCostSeverity = NormalizeCostSeverity(costSeverity);
        var normalizedCombatIntensity = NormalizeCombatIntensity(combatIntensity);

        return
            $"Prompt iniziale:{Environment.NewLine}{prompt.Trim()}{Environment.NewLine}{Environment.NewLine}" +
            $"Regole fase corrente:{Environment.NewLine}" +
            $"- phase_name: {phase.Name}{Environment.NewLine}" +
            $"- objective: {phase.Objective}{Environment.NewLine}" +
            $"- conflict: {phase.Conflict}{Environment.NewLine}" +
            $"- tension_level: {phase.TensionLevel}{Environment.NewLine}" +
            $"- structure_mode: {normalizedStructureMode}{Environment.NewLine}" +
            $"- cost_severity: {normalizedCostSeverity}{Environment.NewLine}" +
            $"- combat_intensity: {normalizedCombatIntensity}{Environment.NewLine}{Environment.NewLine}" +
            $"Blocchi precedenti:{Environment.NewLine}{previousBlocks}{Environment.NewLine}{Environment.NewLine}" +
            "Valuta se CandidateResponse rispetta esplicitamente le regole della fase corrente, la coerenza narrativa e i vincoli della modalita'." + Environment.NewLine +
            "CandidateResponse: verra' fornita dal CallCenter al checker.";
    }

    private static bool TryParsePlanner(string? json, out List<NarrativePhase> phases, out string? error)
    {
        phases = new List<NarrativePhase>();
        error = null;
        try
        {
            var parsed = JsonSerializer.Deserialize<PlannerResponseDto>(json ?? string.Empty, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (parsed?.Phases == null || parsed.Phases.Count == 0)
            {
                error = "phases vuoto";
                return false;
            }

            phases = parsed.Phases
                .Where(p => p != null)
                .Select(p => new NarrativePhase
                {
                    Index = p!.Index <= 0 ? 1 : p.Index,
                    Name = p.Name?.Trim() ?? $"Fase {p.Index}",
                    Objective = p.Objective?.Trim() ?? string.Empty,
                    Conflict = p.Conflict?.Trim() ?? string.Empty,
                    TensionLevel = p.TensionLevel
                })
                .Where(p => !string.IsNullOrWhiteSpace(p.Objective))
                .ToList();

            if (phases.Count == 0)
            {
                error = "nessuna fase valida";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // Pragmatic fallback: if the planner JSON was truncated (common when model hits length limit),
            // recover every fully-formed phase object we can find and continue with a partial plan.
            if (TryRecoverPlannerPhasesFromBrokenJson(json, out var recoveredPhases, out var recoveryInfo) &&
                recoveredPhases.Count > 0)
            {
                phases = recoveredPhases;
                error = recoveryInfo; // informational; caller treats parse as success.
                return true;
            }

            error = ex.Message;
            return false;
        }
    }

    private static bool TryRecoverPlannerPhasesFromBrokenJson(
        string? raw,
        out List<NarrativePhase> phases,
        out string? info)
    {
        phases = new List<NarrativePhase>();
        info = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            // Match complete planner phase objects that contain all required fields and a closed brace.
            var matches = Regex.Matches(
                raw,
                @"\{\s*""index""\s*:\s*(?<index>\d+)\s*,\s*""name""\s*:\s*""(?<name>(?:\\.|[^""])*)""\s*,\s*""objective""\s*:\s*""(?<objective>(?:\\.|[^""])*)""\s*,\s*""conflict""\s*:\s*""(?<conflict>(?:\\.|[^""])*)""\s*,\s*""tension_level""\s*:\s*(?<tension>\d+)\s*\}",
                RegexOptions.Singleline | RegexOptions.CultureInvariant);

            if (matches.Count == 0)
            {
                return false;
            }

            foreach (Match m in matches)
            {
                if (!m.Success) continue;
                if (!int.TryParse(m.Groups["index"].Value, out var idx)) continue;
                if (!int.TryParse(m.Groups["tension"].Value, out var tension)) tension = 1;

                phases.Add(new NarrativePhase
                {
                    Index = idx <= 0 ? 1 : idx,
                    Name = JsonUnescape(m.Groups["name"].Value),
                    Objective = JsonUnescape(m.Groups["objective"].Value),
                    Conflict = JsonUnescape(m.Groups["conflict"].Value),
                    TensionLevel = tension
                });
            }

            phases = phases
                .Where(p => !string.IsNullOrWhiteSpace(p.Objective))
                .OrderBy(p => p.Index)
                .ToList();

            if (phases.Count == 0)
            {
                return false;
            }

            info = $"Planner JSON troncato: recuperate {phases.Count} fasi complete.";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string JsonUnescape(string s)
    {
        try
        {
            // Reuse JSON parser for safe unescape
            return JsonSerializer.Deserialize<string>($"\"{s}\"") ?? s;
        }
        catch
        {
            return s.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\t", "\t");
        }
    }

    private static bool TryParseEvaluator(string? json, out EvaluatorResponseDto evaluation, out string? error)
    {
        evaluation = new EvaluatorResponseDto();
        error = null;
        try
        {
            var parsed = JsonSerializer.Deserialize<EvaluatorResponseDto>(json ?? string.Empty, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (parsed == null)
            {
                error = "json nullo";
                return false;
            }

            evaluation = parsed;
            evaluation.Issues ??= new List<string>();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static int NormalizeEvaluatorScore(int rawScore, out string? note)
    {
        note = null;

        if (rawScore >= 0 && rawScore <= 10)
        {
            note = "Evaluator score interpretato come scala 1..10/0..10; conversione automatica a scala 0..100 (x10).";
            return rawScore * 10;
        }

        return rawScore;
    }

    private static string? SerializeSmall(object data)
    {
        try
        {
            return JsonSerializer.Serialize(data);
        }
        catch
        {
            return null;
        }
    }

    private static void ReportLive(
        EngineContext ctx,
        string? operationName = null,
        string? agentName = null,
        string? modelName = null,
        int? currentStep = null,
        int? maxStep = null,
        string? stepDescription = null)
    {
        try
        {
            ctx.ReportLiveStatus?.Invoke(new EngineLiveStatus
            {
                OperationName = operationName,
                AgentName = agentName,
                ModelName = modelName,
                CurrentStep = currentStep,
                MaxStep = maxStep,
                StepDescription = stepDescription
            });
        }
        catch
        {
            // best-effort; UI updates must never break generation
        }
    }

    private static string? ResolveAgentModelName(Agent? agent)
    {
        if (agent == null) return null;
        return string.IsNullOrWhiteSpace(agent.ModelName) ? null : agent.ModelName.Trim();
    }

    private async Task<string?> TryWriteFailureSnapshotAsync(
        EngineContext ctx,
        NarrativeState state,
        List<NarrativeBlock> blocks,
        string? lastError,
        string? lastAgentInteraction,
        string? exceptionStack = null)
    {
        var options = GetOptions();
        var snapshotOptions = options.Snapshot ?? new NarrativeRuntimeEngineSnapshotOptions();
        var snapshotOnFailure = ctx.Request.SnapshotOnFailure;
        if (!snapshotOnFailure)
        {
            return null;
        }

        var traceLimit = Math.Max(1, snapshotOptions.SnapshotLastTraceEventsCount);
        var blocksLimit = Math.Max(1, snapshotOptions.SnapshotLastBlocksCount);

        var payload = new NreFailureSnapshotPayload
        {
            SnapshotSchemaVersion = options.SnapshotSchemaVersion,
            TimestampUtc = DateTime.UtcNow,
            EngineName = string.IsNullOrWhiteSpace(ctx.Request.EngineName) ? options.EngineName : ctx.Request.EngineName,
            Method = string.IsNullOrWhiteSpace(ctx.Request.Method) ? options.DefaultMethod : ctx.Request.Method,
            RunId = ctx.Request.RunId ?? string.Empty,
            EngineRequest = ctx.Request,
            StoryId = ctx.StoryId,
            NarrativeState = state,
            NarrativeBlocks = blocks.TakeLast(blocksLimit).ToList(),
            EngineTelemetry = ctx.Telemetry,
            Trace = ctx.Trace.TakeLast(traceLimit).ToList(),
            ErrorMessage = lastError,
            ExceptionStack = exceptionStack,
            LastAgentName = ctx.Telemetry.LastAgentName,
            LastModelName = ctx.Telemetry.LastModelName,
            LastError = lastError,
            LastAgentInteraction = lastAgentInteraction
        };

        return await _snapshotWriter.WriteFailureSnapshotAsync(ctx, payload).ConfigureAwait(false);
    }

    private void AddTrace(EngineContext ctx, string type, string? message, string? dataJson = null)
    {
        ctx.Trace ??= new List<EngineEvent>();
        ctx.Trace.Add(new EngineEvent
        {
            Ts = DateTime.UtcNow,
            Type = type,
            Message = message,
            DataJson = dataJson
        });

        var maxTraceEvents = Math.Max(1, GetOptions().Trace.MaxBufferEvents);
        if (ctx.Trace.Count > maxTraceEvents)
        {
            ctx.Trace.RemoveRange(0, ctx.Trace.Count - maxTraceEvents);
        }
    }

    private NarrativeRuntimeEngineOptions GetOptions()
    {
        return _options?.CurrentValue ?? new NarrativeRuntimeEngineOptions();
    }

    private sealed class PlannerResponseDto
    {
        [JsonPropertyName("phases")]
        public List<PlannerPhaseDto>? Phases { get; set; }
    }

    private sealed class PlannerPhaseDto
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }
        [JsonPropertyName("name")]
        public string? Name { get; set; }
        [JsonPropertyName("objective")]
        public string? Objective { get; set; }
        [JsonPropertyName("conflict")]
        public string? Conflict { get; set; }
        [JsonPropertyName("tension_level")]
        public int TensionLevel { get; set; }
    }

    private sealed class EvaluatorResponseDto
    {
        [JsonPropertyName("score")]
        public int Score { get; set; }
        [JsonPropertyName("needs_retry")]
        public bool NeedsRetry { get; set; }
        [JsonPropertyName("issues")]
        public List<string>? Issues { get; set; }
    }
}
