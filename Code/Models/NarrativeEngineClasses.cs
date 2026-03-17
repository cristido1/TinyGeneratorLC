using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using TinyGenerator.Hubs;
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
    public string? PreApprovedPlanSummary { get; set; }
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
    public string? StoryTitle { get; set; }
    public Agent? PlannerAgent { get; set; }
    public Agent? PlanEvaluatorAgent { get; set; }
    public Agent? WriterAgent { get; set; }
    public Agent? EvaluatorAgent { get; set; }
    public Agent? ResourceInitializerAgent { get; set; }
    public Agent? ResourceManagerAgent { get; set; }
    public Action<EngineLiveStatus>? ReportLiveStatus { get; set; }
    public Action<string>? PersistPlannerSummary { get; set; }
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
    public string? PlannerSummary { get; set; }
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
    private readonly IHubContext<StoryLiveHub>? _storyLiveHub;

    public NreEngine(
        SnapshotWriter snapshotWriter,
        ICallCenter? callCenter = null,
        IOptionsMonitor<NarrativeRuntimeEngineOptions>? options = null,
        TextValidationService? textValidationService = null,
        IHubContext<StoryLiveHub>? storyLiveHub = null)
    {
        _snapshotWriter = snapshotWriter ?? throw new ArgumentNullException(nameof(snapshotWriter));
        _callCenter = callCenter;
        _options = options;
        _textValidationService = textValidationService;
        _storyLiveHub = storyLiveHub;
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
            var plannerPhaseTarget = ResolvePlannerPhaseTarget(effectiveMethod, effectiveMaxSteps, nreOptions);
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
            if (ctx.PlanEvaluatorAgent == null) throw new InvalidOperationException("Plan evaluator agent NRE non configurato.");
            if (ctx.WriterAgent == null) throw new InvalidOperationException("Writer agent NRE non configurato.");
            if (ctx.EvaluatorAgent == null) throw new InvalidOperationException("Evaluator agent NRE non configurato.");
            if (ctx.ResourceInitializerAgent == null) throw new InvalidOperationException("Resource initializer agent NRE non configurato.");
            if (ctx.ResourceManagerAgent == null) throw new InvalidOperationException("Resource manager agent NRE non configurato.");
            if (string.IsNullOrWhiteSpace(ctx.Request.UserPrompt))
            {
                throw new InvalidOperationException("UserPrompt obbligatorio per NRE.");
            }

            AddTrace(ctx, "Start", $"Engine {EngineName} avviato (method={effectiveMethod})");
            ctx.ReportProgress?.Invoke(new CommandProgressEventArgs(0, Math.Max(1, effectiveMaxSteps), "NRE start"));
            await PublishStoryLiveStartedAsync(ctx, null, blocks).ConfigureAwait(false);

            List<NarrativePhase> phases;
            var hasPreApprovedPlan = !string.IsNullOrWhiteSpace(ctx.Request.PreApprovedPlanSummary);
            if (hasPreApprovedPlan)
            {
                ReportLive(
                    ctx,
                    operationName: "run_nre:planner_skipped",
                    currentStep: 0,
                    maxStep: effectiveMaxSteps,
                    stepDescription: "Piano NRE riusato (planner saltato)");

                if (!TryParsePlanner(ctx.Request.PreApprovedPlanSummary, out phases, out var planJsonError) &&
                    !TryParsePlannerSummaryText(ctx.Request.PreApprovedPlanSummary, out phases, out var planSummaryError))
                {
                    var plannerError = string.IsNullOrWhiteSpace(planSummaryError) ? planJsonError : planSummaryError;
                    throw new InvalidOperationException($"Piano pre-approvato non valido: {plannerError}");
                }
            }
            else
            {
                ReportLive(ctx, operationName: "run_nre:planner", currentStep: 0, maxStep: effectiveMaxSteps, stepDescription: "Pianificazione narrativa");
                phases = await BuildPlanningAsync(ctx, plannerPhaseTarget, nreOptions).ConfigureAwait(false);
            }

            if (phases.Count == 0)
            {
                throw new InvalidOperationException("Planner NRE non ha restituito fasi valide.");
            }

            result.PlannerSummary = BuildPlannerSummary(phases);
            if (!string.IsNullOrWhiteSpace(result.PlannerSummary))
            {
                try
                {
                    ctx.PersistPlannerSummary?.Invoke(result.PlannerSummary);
                }
                catch
                {
                    // best-effort: la persistenza anticipata del piano non deve bloccare la generazione
                }
            }
            await PublishStoryLiveConsolidatedAsync(
                ctx,
                result.PlannerSummary,
                blocks,
                currentStep: 0,
                maxStep: effectiveMaxSteps,
                phase: "planning").ConfigureAwait(false);

            ReportLive(
                ctx,
                operationName: "run_nre:resource_initializer_init",
                agentName: ctx.ResourceInitializerAgent.Name,
                modelName: ResolveAgentModelName(ctx.ResourceInitializerAgent),
                currentStep: 0,
                maxStep: effectiveMaxSteps,
                stepDescription: "ResourceInitializer INIT");

            var currentCanonStateJson = await BuildResourceManagerInitialStateAsync(
                ctx,
                phases,
                nreOptions).ConfigureAwait(false);

            if (string.Equals(effectiveMethod, "single_pass", StringComparison.OrdinalIgnoreCase))
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();
                state.CurrentStepIndex = 1;
                state.CurrentPhase = "single_pass";
                ctx.Telemetry.StepCount = 1;
                AddTrace(ctx, "StepStart", "single_pass", SerializeSmall(new { phases = phases.Count }));
                ctx.ReportProgress?.Invoke(new CommandProgressEventArgs(1, 1, nreOptions.ProgressMessageGenerating));

                var writerInput = BuildSinglePassWriterUserInput(
                    ctx.Request.UserPrompt!,
                    phases,
                    currentCanonStateJson,
                    nreOptions.DialogueTargetPercent,
                    nreOptions.DialogueTolerancePercentPlus,
                    nreOptions.DialogueTolerancePercentMinus,
                    nreOptions.WriterMaxPromptChars,
                    nreOptions.WriterMaxCanonStateChars,
                    nreOptions.WriterMaxPlanChars);
                ReportLive(
                    ctx,
                    operationName: "run_nre:writer",
                    agentName: ctx.WriterAgent.Name,
                    modelName: ResolveAgentModelName(ctx.WriterAgent),
                    currentStep: 1,
                    maxStep: 1,
                    stepDescription: "Writer • single_pass");
                var writerResult = await CallAgentAsync(
                    ctx,
                    ctx.WriterAgent,
                    "nre_writer",
                    writerInput,
                    TimeSpan.FromSeconds(Math.Max(5, nreOptions.WriterCallTimeoutSeconds)),
                    nreOptions,
                    blocks).ConfigureAwait(false);

                ReportLive(
                    ctx,
                    operationName: "run_nre:writer",
                    agentName: ctx.WriterAgent.Name,
                    modelName: string.IsNullOrWhiteSpace(writerResult.ModelUsed) ? ResolveAgentModelName(ctx.WriterAgent) : writerResult.ModelUsed,
                    currentStep: 1,
                    maxStep: 1,
                    stepDescription: "Writer • single_pass");

                if (!writerResult.Success)
                {
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
                    result.Succeeded = false;
                    result.StopReason = stopReasons.ValidationFailed;
                    result.ErrorSummary = "Writer output vuoto.";
                    result.SnapshotFilePath = await TryWriteFailureSnapshotAsync(ctx, state, blocks, result.ErrorSummary, null)
                        .ConfigureAwait(false);
                    AddTrace(ctx, "Stop", result.ErrorSummary);
                    return result;
                }

                blocks.Add(new NarrativeBlock
                {
                    Index = 1,
                    Text = blockText,
                    Phase = "single_pass",
                    Pov = state.CurrentPov
                });
                await PublishStoryLiveConsolidatedAsync(
                    ctx,
                    result.PlannerSummary,
                    blocks,
                    currentStep: 1,
                    maxStep: 1,
                    phase: "single_pass").ConfigureAwait(false);
                AddTrace(ctx, "StepOk", "Blocco single_pass accettato", SerializeSmall(new
                {
                    chars = blockText.Length
                }));

                result.Succeeded = true;
                result.StopReason = stopReasons.Completed;
                result.FinalText = blockText;
                ReportLive(ctx, operationName: "run_nre:completed", currentStep: 1, maxStep: 1, stepDescription: "Completato");
                await PublishStoryLiveCompletedAsync(ctx, result.PlannerSummary, blocks).ConfigureAwait(false);
                AddTrace(ctx, "Stop", stopReasons.Completed, SerializeSmall(new { blocks = 1, chars = result.FinalText.Length, method = "single_pass" }));
                return result;
            }

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
                        nreOptions.DialogueTolerancePercentMinus,
                        nreOptions.WriterMaxPromptChars,
                        nreOptions.WriterMaxCanonStateChars,
                        nreOptions.WriterMaxPreviousBlocksChars);
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

                    // Ensure the running-commands popup shows the effective model used by writer
                    // (including fallback model switches) as soon as the call returns.
                    ReportLive(
                        ctx,
                        operationName: "run_nre:writer",
                        agentName: ctx.WriterAgent.Name,
                        modelName: string.IsNullOrWhiteSpace(writerResult.ModelUsed) ? ResolveAgentModelName(ctx.WriterAgent) : writerResult.ModelUsed,
                        currentStep: step,
                        maxStep: effectiveMaxSteps,
                        stepDescription: $"Writer • {phase.Name}");

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
                    await PublishStoryLiveConsolidatedAsync(
                        ctx,
                        result.PlannerSummary,
                        blocks,
                        currentStep: step,
                        maxStep: effectiveMaxSteps,
                        phase: phase.Name).ConfigureAwait(false);
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
            await PublishStoryLiveCompletedAsync(ctx, result.PlannerSummary, blocks).ConfigureAwait(false);
            AddTrace(ctx, "Stop", stopReasons.Completed, SerializeSmall(new { blocks = blocks.Count, chars = result.FinalText.Length }));
            return result;
        }
        catch (OperationCanceledException)
        {
            AddTrace(ctx, "Stop", "Cancelled");
            await PublishStoryLiveFailedAsync(ctx, "Operazione annullata.", null, blocks).ConfigureAwait(false);
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
            await PublishStoryLiveFailedAsync(ctx, ex.Message, null, blocks).ConfigureAwait(false);
            result.Succeeded = false;
            result.StopReason = GetOptions().StopReasons.Exception;
            result.ErrorSummary = ex.Message;
            result.SnapshotFilePath = await TryWriteFailureSnapshotAsync(ctx, state, blocks, ex.Message, null, ex.ToString())
                .ConfigureAwait(false);
            return result;
        }
    }

    private static string BuildStoryLiveGroup(long storyId) => $"story_live_{storyId}";

    private static string BuildApprovedText(IReadOnlyList<NarrativeBlock>? blocks)
    {
        return string.Join(
            Environment.NewLine + Environment.NewLine,
            (blocks ?? Array.Empty<NarrativeBlock>())
            .Select(b => b?.Text?.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t)));
    }

    private async Task PublishStoryLiveStartedAsync(EngineContext ctx, string? planSummary, IReadOnlyList<NarrativeBlock>? blocks)
    {
        await Task.CompletedTask;
    }

    private async Task PublishStoryLiveConsolidatedAsync(
        EngineContext ctx,
        string? planSummary,
        IReadOnlyList<NarrativeBlock> blocks,
        int currentStep,
        int maxStep,
        string? phase)
    {
        await Task.CompletedTask;
    }

    private async Task PublishStoryLiveCompletedAsync(EngineContext ctx, string? planSummary, IReadOnlyList<NarrativeBlock>? blocks)
    {
        await Task.CompletedTask;
    }

    private async Task PublishStoryLiveFailedAsync(EngineContext ctx, string? error, string? planSummary, IReadOnlyList<NarrativeBlock>? blocks)
    {
        await Task.CompletedTask;
    }

    private async Task<List<NarrativePhase>> BuildPlanningAsync(
        EngineContext ctx,
        int requestedPhases,
        NarrativeRuntimeEngineOptions nreOptions)
    {
        var plannerInput = BuildPlannerUserInput(
            ctx.Request.UserPrompt!,
            requestedPhases,
            ctx.Request.StructureMode,
            ctx.Request.CostSeverity,
            ctx.Request.CombatIntensity);
        var plannerResult = await CallAgentAsync(
            ctx,
            ctx.PlannerAgent!,
            "nre_planner",
            plannerInput,
            TimeSpan.FromSeconds(Math.Max(5, nreOptions.PlannerCallTimeoutSeconds)),
            nreOptions).ConfigureAwait(false);

        if (!plannerResult.Success)
        {
            throw new InvalidOperationException($"Planner failure: {plannerResult.FailureReason ?? "unknown"}");
        }

        if (!TryParsePlanner(plannerResult.ResponseText, out var phases, out var error))
        {
            throw new InvalidOperationException($"Planner JSON non valido: {error}");
        }

        var planEvaluatorInput = BuildPlannerEvaluatorInput(
            ctx.Request.UserPrompt!,
            requestedPhases,
            ctx.Request.StructureMode,
            ctx.Request.CostSeverity,
            ctx.Request.CombatIntensity,
            phases);
        ReportLive(
            ctx,
            operationName: "run_nre:plan_evaluator",
            agentName: ctx.PlanEvaluatorAgent?.Name,
            modelName: ResolveAgentModelName(ctx.PlanEvaluatorAgent),
            currentStep: 0,
            maxStep: Math.Max(1, ctx.Request.MaxSteps),
            stepDescription: "Valutazione piano NRE");

        var planEvaluationResult = await CallAgentAsync(
            ctx,
            ctx.PlanEvaluatorAgent!,
            "nre_plan_evaluator",
            planEvaluatorInput,
            TimeSpan.FromSeconds(Math.Max(5, nreOptions.EvaluatorCallTimeoutSeconds)),
            nreOptions).ConfigureAwait(false);

        if (!planEvaluationResult.Success)
        {
            throw new InvalidOperationException($"Plan evaluator failure: {planEvaluationResult.FailureReason ?? "unknown"}");
        }

        if (!TryParseEvaluator(planEvaluationResult.ResponseText, out var planEvaluation, out var evalParseError))
        {
            MarkLatestAgentResponseAsFailed(
                ctx,
                ctx.PlanEvaluatorAgent,
                planEvaluationResult.ModelUsed,
                $"[source=nre_plan_evaluator] Plan evaluator JSON non valido: {evalParseError}");
            throw new InvalidOperationException($"Plan evaluator JSON non valido: {evalParseError}");
        }

        var minScore = Math.Max(1, nreOptions.PlanEvaluatorMinScore);
        var normalizedScore = NormalizeEvaluatorScore(planEvaluation.Score, out _);
        var rejectedByScore = normalizedScore < minScore;
        var rejectedByNeedsRetry = planEvaluation.NeedsRetry;
        if (rejectedByScore || rejectedByNeedsRetry)
        {
            var issues = (planEvaluation.Issues ?? new List<string>())
                .Where(i => !string.IsNullOrWhiteSpace(i))
                .Select(i => i.Trim())
                .ToList();
            var issuesText = issues.Count == 0 ? "nessun dettaglio" : string.Join(" | ", issues);
            var failureReason =
                $"[source=nre_plan_evaluator] Piano NRE bocciato da nre_plan_evaluator: score={normalizedScore}, min={minScore}, needs_retry={planEvaluation.NeedsRetry}. Issues: {issuesText}";
            MarkLatestAgentResponseAsFailed(
                ctx,
                ctx.PlanEvaluatorAgent,
                planEvaluationResult.ModelUsed,
                failureReason);
            throw new InvalidOperationException(
                $"Piano NRE bocciato da nre_plan_evaluator: score={normalizedScore}, min={minScore}, needs_retry={planEvaluation.NeedsRetry}. Issues: {issuesText}");
        }

        AddTrace(ctx, "AgentCall", "Plan evaluator ok", SerializeSmall(new
        {
            score = normalizedScore,
            min = minScore,
            needsRetry = planEvaluation.NeedsRetry
        }));
        AddTrace(ctx, "AgentCall", "Planner ok", SerializeSmall(new { phases = phases.Count }));
        return phases
            .Where(p => p != null)
            .OrderBy(p => p.Index <= 0 ? int.MaxValue : p.Index)
            .ToList();
    }

    private static string BuildPlannerSummary(IReadOnlyList<NarrativePhase> phases)
    {
        if (phases == null || phases.Count == 0)
        {
            return string.Empty;
        }

        static string Clean(string? text, int maxLen)
        {
            if (string.IsNullOrWhiteSpace(text)) return "-";
            var compact = Regex.Replace(text.Trim(), "\\s+", " ");
            return compact.Length <= maxLen ? compact : compact[..maxLen].TrimEnd() + "...";
        }

        var ordered = phases
            .Where(p => p != null)
            .OrderBy(p => p.Index <= 0 ? int.MaxValue : p.Index)
            .ToList();

        var lines = new List<string>(ordered.Count);
        foreach (var p in ordered)
        {
            var idx = p.Index > 0 ? p.Index : lines.Count + 1;
            lines.Add(
                $"{idx}. {Clean(p.Name, 80)} | Obiettivo: {Clean(p.Objective, 220)} | Conflitto: {Clean(p.Conflict, 220)} | Tensione: {p.TensionLevel}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async Task<string> BuildResourceManagerInitialStateAsync(
        EngineContext ctx,
        IReadOnlyList<NarrativePhase> phases,
        NarrativeRuntimeEngineOptions nreOptions)
    {
        var initInput = BuildResourceManagerInitInput(ctx, phases);
        var initCall = await CallAgentAsync(
            ctx,
            ctx.ResourceInitializerAgent!,
            "nre_resource_initializer_init",
            initInput,
            TimeSpan.FromSeconds(Math.Max(5, nreOptions.EvaluatorCallTimeoutSeconds)),
            nreOptions).ConfigureAwait(false);

        if (!initCall.Success)
        {
            throw new InvalidOperationException($"ResourceInitializer INIT failure: {initCall.FailureReason ?? "unknown"}");
        }

        if (!TryExtractCanonStateJson(initCall.ResponseText, out var canonStateJson, out var initError))
        {
            if (!TryExtractResourceDeltaJson(initCall.ResponseText, out var initDeltaJson, out _))
            {
                throw new InvalidOperationException($"ResourceInitializer INIT JSON non valido: {initError}");
            }

            canonStateJson = ApplyResourceDeltaToCanonState("{}", initDeltaJson, 0);
            if (string.IsNullOrWhiteSpace(canonStateJson))
            {
                throw new InvalidOperationException("ResourceInitializer INIT JSON non valido: impossibile costruire canon_state dal delta");
            }
        }

        AddTrace(ctx, "AgentCall", "Resource initializer INIT ok");
        return canonStateJson;
    }

    private async Task<CallCenterResult> UpdateResourceManagerStateAsync(
        EngineContext ctx,
        NarrativePhase phase,
        string newBlock,
        string currentCanonStateJson,
        NarrativeRuntimeEngineOptions nreOptions)
    {
        var updateInput = BuildResourceManagerUpdateInput(
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

        if (!TryExtractResourceDeltaJson(updateCall.ResponseText, out var deltaJson, out var updateError))
        {
            updateCall.Success = false;
            updateCall.FailureReason = $"ResourceManager UPDATE JSON delta non valido: {updateError}";
            return updateCall;
        }

        var mergedCanonStateJson = ApplyResourceDeltaToCanonState(
            currentCanonStateJson,
            deltaJson,
            Math.Max(1, phase.Index));
        if (string.IsNullOrWhiteSpace(mergedCanonStateJson))
        {
            updateCall.Success = false;
            updateCall.FailureReason = "ResourceManager UPDATE: impossibile allineare canon_state con il delta";
            return updateCall;
        }

        updateCall.ResponseText = mergedCanonStateJson;
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
        ctx.Telemetry.LastAgentName = agent.Description;
        ctx.Telemetry.LastModelName = string.IsNullOrWhiteSpace(result.ModelUsed) ? ctx.Telemetry.LastModelName : result.ModelUsed;
        ReportLive(ctx, agentName: agent.Description, modelName: ctx.Telemetry.LastModelName);

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
            "Genera lo stato iniziale COMPLETO delle risorse narrative e rispondi SOLO con JSON valido." + Environment.NewLine +
            "Sono consentite risorse dedotte da prompt e vincoli utente." + Environment.NewLine +
            "In assenza di indicazioni psicologiche esplicite, deduci stato plausibile dal contesto." + Environment.NewLine + Environment.NewLine +
            "Regole di output anti-troncamento:" + Environment.NewLine +
            "- restituisci JSON compatto (una sola riga, senza markdown);" + Environment.NewLine +
            "- root obbligatoria: updated_resources;" + Environment.NewLine +
            "- per ogni risorsa usa solo: name + (quantity oppure integrity_percent) e opzionalmente status_flag, notes_json;" + Environment.NewLine +
            "- evita qualsiasi altro campo non previsto dallo schema." + Environment.NewLine + Environment.NewLine +
            $"structure_mode: {ctx.Request.StructureMode}{Environment.NewLine}" +
            $"cost_severity: {ctx.Request.CostSeverity}{Environment.NewLine}" +
            $"combat_intensity: {ctx.Request.CombatIntensity}{Environment.NewLine}" +
            $"resource_hints: {(string.IsNullOrWhiteSpace(ctx.Request.ResourceHints) ? "(none)" : ctx.Request.ResourceHints.Trim())}{Environment.NewLine}{Environment.NewLine}" +
            $"prompt:{Environment.NewLine}{ctx.Request.UserPrompt?.Trim()}{Environment.NewLine}{Environment.NewLine}" +
            $"planned_phases:{Environment.NewLine}{phaseSummary}{Environment.NewLine}{Environment.NewLine}" +
            "Output atteso: { \"updated_resources\": [ ... ] }";
    }

    private static string BuildResourceManagerUpdateInput(
        string newBlock,
        string currentCanonStateJson)
    {
        return
            "Aggiorna le risorse rispetto all'ULTIMO chunk e rispondi SOLO con JSON valido." + Environment.NewLine +
            "Input disponibili: SOLO current_canon_state_json completo e new_block." + Environment.NewLine +
            "NON riscrivere tutto il canon_state: restituisci SOLO le risorse che cambiano." + Environment.NewLine +
            "Il sistema applichera' il delta allo stato completo." + Environment.NewLine +
            "Regole di output anti-troncamento:" + Environment.NewLine +
            "- restituisci JSON compatto (una sola riga, senza markdown);" + Environment.NewLine +
            "- restituisci solo updated_resources;" + Environment.NewLine +
            "- per ogni risorsa includi name e solo i campi effettivamente cambiati tra: quantity, integrity_percent, status_flag, notes_json;" + Environment.NewLine +
            "- non restituire altri campi." + Environment.NewLine + Environment.NewLine +
            $"current_canon_state_json:{Environment.NewLine}{(string.IsNullOrWhiteSpace(currentCanonStateJson) ? "{}" : currentCanonStateJson)}{Environment.NewLine}{Environment.NewLine}" +
            $"new_block:{Environment.NewLine}{newBlock.Trim()}{Environment.NewLine}{Environment.NewLine}" +
            "Output atteso: { \"updated_resources\": [ ... ] }";
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

    private static bool TryExtractResourceDeltaJson(string? rawJson, out string deltaJson, out string? error)
    {
        deltaJson = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            error = "response vuota";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "root JSON non object";
                return false;
            }

            if (root.TryGetProperty("updated_resources", out var updatedResources) &&
                updatedResources.ValueKind == JsonValueKind.Array)
            {
                deltaJson = root.GetRawText();
                return true;
            }

            if (root.TryGetProperty("delta", out var delta) &&
                delta.ValueKind == JsonValueKind.Object &&
                delta.TryGetProperty("updated_resources", out var legacyUpdatedResources) &&
                legacyUpdatedResources.ValueKind == JsonValueKind.Array)
            {
                deltaJson = delta.GetRawText();
                return true;
            }

            error = "campo updated_resources mancante";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string ApplyResourceDeltaToCanonState(
        string? currentCanonStateJson,
        string deltaJson,
        int chunkIndex)
    {
        var canonical = ParseCanonStateNodeOrDefault(currentCanonStateJson);
        if (canonical == null)
        {
            return string.Empty;
        }

        if (canonical["resources"] is not JsonArray canonicalResources)
        {
            canonicalResources = new JsonArray();
            canonical["resources"] = canonicalResources;
        }

        JsonObject? delta;
        try
        {
            delta = JsonNode.Parse(deltaJson) as JsonObject;
        }
        catch
        {
            return string.Empty;
        }

        if (delta == null)
        {
            return string.Empty;
        }

        var updates = delta["updated_resources"] as JsonArray;
        if (updates == null)
        {
            return canonical.ToJsonString();
        }

        var indexByName = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in canonicalResources)
        {
            if (item is not JsonObject obj) continue;
            var name = obj["name"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            indexByName[name] = obj;
        }

        foreach (var updateNode in updates)
        {
            if (updateNode is not JsonObject updateObj)
            {
                continue;
            }

            var name = updateObj["name"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!indexByName.TryGetValue(name, out var target))
            {
                target = new JsonObject
                {
                    ["name"] = name
                };
                canonicalResources.Add(target);
                indexByName[name] = target;
            }

            foreach (var kvp in updateObj)
            {
                if (string.Equals(kvp.Key, "story_id", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kvp.Key, "series_id", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kvp.Key, "episode_number", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kvp.Key, "chunk_index", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kvp.Key, "last_update_chunk", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                target[kvp.Key] = kvp.Value?.DeepClone();
            }

            target["name"] = name;
            target["last_update_chunk"] = Math.Max(0, chunkIndex);
            if (target["quantity"] == null && target["integrity_percent"] == null)
            {
                target["quantity"] = 0;
            }
        }

        return canonical.ToJsonString();
    }

    private static JsonObject? ParseCanonStateNodeOrDefault(string? currentCanonStateJson)
    {
        if (string.IsNullOrWhiteSpace(currentCanonStateJson))
        {
            return new JsonObject
            {
                ["resources"] = new JsonArray()
            };
        }

        try
        {
            var node = JsonNode.Parse(currentCanonStateJson) as JsonObject;
            if (node == null)
            {
                return null;
            }

            if (node["resources"] is not JsonArray)
            {
                node["resources"] = new JsonArray();
            }

            return node;
        }
        catch
        {
            return null;
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
        var agentIdentity = string.IsNullOrWhiteSpace(agent?.Description) ? "nre_writer" : agent.Description;

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

    private static int ResolvePlannerPhaseTarget(string method, int maxSteps, NarrativeRuntimeEngineOptions options)
    {
        var normalizedMethod = (method ?? string.Empty).Trim().ToLowerInvariant();
        var rawMultiplier = normalizedMethod switch
        {
            "single_pass" => options.SinglePassPlannerStepsMultiplier,
            "state_driven" => options.StateDrivenPlannerStepsMultiplier,
            _ => options.StateDrivenPlannerStepsMultiplier
        };

        var safeMultiplier = double.IsFinite(rawMultiplier) && rawMultiplier > 0d ? rawMultiplier : 1d;
        var safeMaxSteps = Math.Max(1, maxSteps);
        return Math.Max(1, (int)Math.Ceiling(safeMaxSteps * safeMultiplier));
    }

    private static string BuildPlannerUserInput(
        string prompt,
        int maxSteps,
        string? structureMode,
        string? costSeverity,
        string? combatIntensity)
    {
        var mode = NormalizeStructureMode(structureMode);
        var safePrompt = prompt?.Trim() ?? string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine($"Tema: {safePrompt}");
        sb.AppendLine($"Numero di fasi desiderate: {Math.Max(1, maxSteps)}");
        sb.AppendLine();
        sb.AppendLine("Vincoli runtime (da rispettare insieme alle instruction dell'agente):");
        sb.AppendLine("- Rispondi SOLO con JSON valido conforme al response_format della richiesta.");
        sb.AppendLine($"- Devono esserci esattamente {Math.Max(1, maxSteps)} fasi.");
        sb.AppendLine("- Progressione logica e causale.");
        sb.AppendLine("- Ogni fase deve introdurre un cambiamento reale.");
        sb.AppendLine("- Non scrivere testo narrativo fuori dal JSON.");

        if (string.Equals(mode, "military_strict", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine();
            sb.AppendLine("Modalita: military_strict");
            sb.AppendLine($"- CostSeverity={NormalizeCostSeverity(costSeverity)}");
            sb.AppendLine($"- CombatIntensity={NormalizeCombatIntensity(combatIntensity)}");
            sb.AppendLine("- Ogni fase deve avere obiettivo militare esplicito e conflitto operativo concreto.");
            sb.AppendLine("- La tensione deve crescere (per quanto possibile).");
            sb.AppendLine("- Non e' permessa una risoluzione completa del conflitto.");
        }

        return sb.ToString().Trim();
    }

    private static string BuildPlannerSystemPrompt(string? structureMode, string? costSeverity, string? combatIntensity, int requestedPhases)
    {
        var mode = NormalizeStructureMode(structureMode);
        return string.Equals(mode, "military_strict", StringComparison.OrdinalIgnoreCase)
            ? BuildMilitaryStrictPlannerSystemPrompt(
                NormalizeCostSeverity(costSeverity),
                NormalizeCombatIntensity(combatIntensity),
                requestedPhases)
            : BuildStandardPlannerSystemPrompt(requestedPhases);
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

    private static string BuildStandardPlannerSystemPrompt(int requestedPhases)
    {
        var phases = Math.Max(1, requestedPhases);
        return
            "Sei un narrative planner." + Environment.NewLine + Environment.NewLine +
            "Genera una struttura narrativa in JSON conforme al response_format della richiesta." + Environment.NewLine + Environment.NewLine +
            "Regole:" + Environment.NewLine +
            $"- Devono esserci esattamente {phases} fasi." + Environment.NewLine +
            "- Progressione logica e causale." + Environment.NewLine +
            "- Ogni fase deve introdurre un cambiamento reale." + Environment.NewLine +
            "- Non scrivere testo narrativo." + Environment.NewLine +
            "- Output solo JSON valido.";
    }

    private static string BuildMilitaryStrictPlannerSystemPrompt(string costSeverity, string combatIntensity, int requestedPhases)
    {
        var phases = Math.Max(1, requestedPhases);
        return
            "Sei un narrative planner specializzato in narrativa militare rigorosa." + Environment.NewLine + Environment.NewLine +
            "Genera una struttura narrativa in JSON conforme al response_format della richiesta." + Environment.NewLine + Environment.NewLine +
            $"CostSeverity={NormalizeCostSeverity(costSeverity)}." + Environment.NewLine + Environment.NewLine +
            $"CombatIntensity={NormalizeCombatIntensity(combatIntensity)}." + Environment.NewLine + Environment.NewLine +
            "REGOLE OBBLIGATORIE:" + Environment.NewLine +
            $"1) Devono esserci esattamente {phases} fasi." + Environment.NewLine +
            "2) Progressione obbligatoria con escalation operativa e causale." + Environment.NewLine +
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
        int dialogueTolerancePercentMinus,
        int writerMaxPromptChars,
        int writerMaxCanonStateChars,
        int writerMaxPreviousBlocksChars)
    {
        var tail = blocks
            .TakeLast(Math.Max(1, previousBlocksWindow))
            .Select(b => $"- {b.Text}")
            .ToList();
        var previousBlocks = tail.Count == 0 ? "(nessuno)" : string.Join(Environment.NewLine + Environment.NewLine, tail);
        var safePrompt = ClampText(prompt, Math.Max(500, writerMaxPromptChars), "prompt");
        var safeCanonState = ClampText(
            string.IsNullOrWhiteSpace(currentCanonStateJson) ? "{}" : currentCanonStateJson,
            Math.Max(500, writerMaxCanonStateChars),
            "canon_state");
        var safePreviousBlocks = ClampText(previousBlocks, Math.Max(500, writerMaxPreviousBlocksChars), "blocchi_precedenti");

        return
            $"Prompt iniziale:{Environment.NewLine}{safePrompt}{Environment.NewLine}{Environment.NewLine}" +
            $"Fase narrativa corrente:{Environment.NewLine}{phase.Objective}{Environment.NewLine}" +
            $"Conflitto:{Environment.NewLine}{phase.Conflict}{Environment.NewLine}{Environment.NewLine}" +
            $"Vincolo dialoghi:{Environment.NewLine}- target dialogo: {dialogueTargetPercent}%{Environment.NewLine}- tolleranza inferiore: -{dialogueTolerancePercentMinus}%{Environment.NewLine}- tolleranza superiore: +{dialogueTolerancePercentPlus}%{Environment.NewLine}- mantieni il blocco nel range [{Math.Max(0, dialogueTargetPercent - dialogueTolerancePercentMinus)}%, {Math.Min(100, dialogueTargetPercent + dialogueTolerancePercentPlus)}%] di testo dialogato.{Environment.NewLine}{Environment.NewLine}" +
            $"Canon state risorse corrente (JSON):{Environment.NewLine}{safeCanonState}{Environment.NewLine}{Environment.NewLine}" +
            $"Blocchi precedenti:{Environment.NewLine}{safePreviousBlocks}";
    }

    private static string BuildSinglePassWriterUserInput(
        string prompt,
        IReadOnlyList<NarrativePhase> phases,
        string? currentCanonStateJson,
        int dialogueTargetPercent,
        int dialogueTolerancePercentPlus,
        int dialogueTolerancePercentMinus,
        int writerMaxPromptChars,
        int writerMaxCanonStateChars,
        int writerMaxPlanChars)
    {
        var phasePlan = string.Join(
            Environment.NewLine,
            phases
                .Where(p => p != null)
                .OrderBy(p => p.Index <= 0 ? int.MaxValue : p.Index)
                .Select(p => $"- {p.Index}. {p.Name}: objective={p.Objective} | conflict={p.Conflict} | tension={p.TensionLevel}"));

        if (string.IsNullOrWhiteSpace(phasePlan))
        {
            phasePlan = "(nessuna fase)";
        }
        var safePrompt = ClampText(prompt, Math.Max(500, writerMaxPromptChars), "prompt");
        var safeCanonState = ClampText(
            string.IsNullOrWhiteSpace(currentCanonStateJson) ? "{}" : currentCanonStateJson,
            Math.Max(500, writerMaxCanonStateChars),
            "canon_state");
        var safePhasePlan = ClampText(phasePlan, Math.Max(500, writerMaxPlanChars), "piano");

        return
            $"Prompt iniziale:{Environment.NewLine}{safePrompt}{Environment.NewLine}{Environment.NewLine}" +
            $"Piano completo approvato (da rispettare integralmente):{Environment.NewLine}{safePhasePlan}{Environment.NewLine}{Environment.NewLine}" +
            $"Vincolo dialoghi (sull'intera storia):{Environment.NewLine}- target dialogo: {dialogueTargetPercent}%{Environment.NewLine}- tolleranza inferiore: -{dialogueTolerancePercentMinus}%{Environment.NewLine}- tolleranza superiore: +{dialogueTolerancePercentPlus}%{Environment.NewLine}- mantieni la storia nel range [{Math.Max(0, dialogueTargetPercent - dialogueTolerancePercentMinus)}%, {Math.Min(100, dialogueTargetPercent + dialogueTolerancePercentPlus)}%] di testo dialogato.{Environment.NewLine}{Environment.NewLine}" +
            $"Canon state risorse iniziale (JSON):{Environment.NewLine}{safeCanonState}{Environment.NewLine}{Environment.NewLine}" +
            "Scrivi ORA l'intera storia finale in un solo output, senza meta-commenti.";
    }

    private static string ClampText(string? text, int maxChars, string label)
    {
        var value = (text ?? string.Empty).Trim();
        var safeMax = Math.Max(1, maxChars);
        if (value.Length <= safeMax)
        {
            return value;
        }

        var keepHead = Math.Max(200, (int)Math.Round(safeMax * 0.85));
        if (keepHead >= safeMax)
        {
            keepHead = safeMax - 1;
        }
        var keepTail = Math.Max(0, safeMax - keepHead);
        var head = keepHead > 0 ? value[..keepHead].TrimEnd() : string.Empty;
        var tail = keepTail > 0 && value.Length > keepHead ? value[^keepTail..].TrimStart() : string.Empty;
        var marker = $"...[{label}_troncato len={value.Length} limit={safeMax}]...";

        if (string.IsNullOrEmpty(tail))
        {
            var budget = Math.Max(1, safeMax - marker.Length);
            return value[..Math.Min(value.Length, budget)].TrimEnd() + marker;
        }

        var combined = head + Environment.NewLine + marker + Environment.NewLine + tail;
        if (combined.Length <= safeMax)
        {
            return combined;
        }

        return (head + Environment.NewLine + marker).Length >= safeMax
            ? (head + Environment.NewLine + marker)[..safeMax]
            : combined[..safeMax];
    }

    private static string BuildSinglePassEvaluatorCheckerContextInput(
        string prompt,
        IReadOnlyList<NarrativePhase> phases,
        string? structureMode,
        string? costSeverity,
        string? combatIntensity)
    {
        var phasePlan = string.Join(
            Environment.NewLine,
            phases
                .Where(p => p != null)
                .OrderBy(p => p.Index <= 0 ? int.MaxValue : p.Index)
                .Select(p => $"- {p.Index}. {p.Name}: objective={p.Objective} | conflict={p.Conflict} | tension={p.TensionLevel}"));

        if (string.IsNullOrWhiteSpace(phasePlan))
        {
            phasePlan = "(nessuna fase)";
        }

        var normalizedStructureMode = NormalizeStructureMode(structureMode);
        var normalizedCostSeverity = NormalizeCostSeverity(costSeverity);
        var normalizedCombatIntensity = NormalizeCombatIntensity(combatIntensity);

        return
            $"Prompt iniziale:{Environment.NewLine}{prompt.Trim()}{Environment.NewLine}{Environment.NewLine}" +
            $"Piano completo approvato:{Environment.NewLine}{phasePlan}{Environment.NewLine}{Environment.NewLine}" +
            $"Vincoli globali:{Environment.NewLine}" +
            $"- structure_mode: {normalizedStructureMode}{Environment.NewLine}" +
            $"- cost_severity: {normalizedCostSeverity}{Environment.NewLine}" +
            $"- combat_intensity: {normalizedCombatIntensity}{Environment.NewLine}{Environment.NewLine}" +
            "Valuta se CandidateResponse rispetta l'intero piano approvato, la progressione causale tra fasi e i vincoli globali della modalita'." + Environment.NewLine +
            "CandidateResponse: verra' fornita dal CallCenter al checker.";
    }

    private static string BuildEvaluatorUserInput(string prompt, NarrativePhase phase, List<NarrativeBlock> blocks, string newBlock, int previousBlocksWindow)
    {
        // Limitiamo il contesto dell'evaluator per ridurre il rischio di overflow del model context.
        var evaluatorWindow = Math.Max(1, Math.Min(previousBlocksWindow, 1));
        var tail = blocks
            .TakeLast(evaluatorWindow)
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
        // Limitiamo il contesto dell'evaluator per ridurre il rischio di overflow del model context.
        var evaluatorWindow = Math.Max(1, Math.Min(previousBlocksWindow, 1));
        var tail = blocks
            .TakeLast(evaluatorWindow)
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

    private static string BuildPlannerEvaluatorInput(
        string prompt,
        int maxSteps,
        string? structureMode,
        string? costSeverity,
        string? combatIntensity,
        IReadOnlyList<NarrativePhase> candidatePhases)
    {
        var normalizedStructureMode = NormalizeStructureMode(structureMode);
        var normalizedCostSeverity = NormalizeCostSeverity(costSeverity);
        var normalizedCombatIntensity = NormalizeCombatIntensity(combatIntensity);
        var candidatePlanYaml = BuildPlannerPhasesYaml(candidatePhases);

        return
            $@"Prompt iniziale:
{prompt.Trim()}

Vincoli planning:
- max_steps richiesti: {maxSteps}
- structure_mode: {normalizedStructureMode}
- cost_severity: {normalizedCostSeverity}
- combat_intensity: {normalizedCombatIntensity}

Valuta se il piano del planner e' coerente con prompt e vincoli, ha progressione causale e fasi concrete.
Boccia se piano generico, incoerente, non progressivo o non allineato ai vincoli.
NON valutare la sintassi/struttura JSON del piano: e' gia' stata validata a monte.
Rispondi SOLO in JSON valido secondo il response_format configurato per nre_plan_evaluator.

CandidateResponse planner (YAML):
{candidatePlanYaml}";
    }

    private static string BuildPlannerPhasesYaml(IReadOnlyList<NarrativePhase> phases)
    {
        if (phases == null || phases.Count == 0)
        {
            return "phases: []";
        }

        static string Esc(string? value)
        {
            var raw = (value ?? string.Empty).Trim().Replace("\r\n", "\n").Replace("\r", "\n");
            var singleLine = raw.Replace("\n", " ");
            return singleLine.Replace("\"", "\\\"");
        }

        var ordered = phases
            .Where(p => p != null)
            .OrderBy(p => p.Index <= 0 ? int.MaxValue : p.Index)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("phases:");
        foreach (var p in ordered)
        {
            var index = p.Index <= 0 ? 1 : p.Index;
            var name = string.IsNullOrWhiteSpace(p.Name) ? $"Fase {index}" : p.Name;
            var objective = p.Objective ?? string.Empty;
            var conflict = p.Conflict ?? string.Empty;
            var tension = p.TensionLevel;

            sb.AppendLine($"  - index: {index}");
            sb.AppendLine($"    name: \"{Esc(name)}\"");
            sb.AppendLine($"    objective: \"{Esc(objective)}\"");
            sb.AppendLine($"    conflict: \"{Esc(conflict)}\"");
            sb.AppendLine($"    tension_level: {tension}");
        }

        return sb.ToString().TrimEnd();
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

    private static bool TryParsePlannerSummaryText(string? summary, out List<NarrativePhase> phases, out string? error)
    {
        phases = new List<NarrativePhase>();
        error = null;

        if (string.IsNullOrWhiteSpace(summary))
        {
            error = "summary vuoto";
            return false;
        }

        var lines = summary
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length == 0)
        {
            error = "summary senza righe";
            return false;
        }

        var pattern = new Regex(
            @"^(?<index>\d+)\.\s*(?<name>.*?)\s*\|\s*Obiettivo:\s*(?<objective>.*?)\s*\|\s*Conflitto:\s*(?<conflict>.*?)\s*\|\s*Tensione:\s*(?<tension>\d+)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            var match = pattern.Match(rawLine.Trim());
            if (!match.Success)
            {
                continue;
            }

            if (!int.TryParse(match.Groups["index"].Value, out var index))
            {
                continue;
            }

            if (!int.TryParse(match.Groups["tension"].Value, out var tensionLevel))
            {
                tensionLevel = 1;
            }

            var objective = match.Groups["objective"].Value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(objective) || objective == "-")
            {
                objective = "Obiettivo non specificato";
            }

            var conflict = match.Groups["conflict"].Value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(conflict) || conflict == "-")
            {
                conflict = "Conflitto non specificato";
            }

            phases.Add(new NarrativePhase
            {
                Index = index <= 0 ? 1 : index,
                Name = string.IsNullOrWhiteSpace(match.Groups["name"].Value) ? $"Fase {index}" : match.Groups["name"].Value.Trim(),
                Objective = objective,
                Conflict = conflict,
                TensionLevel = tensionLevel
            });
        }

        phases = phases
            .OrderBy(p => p.Index <= 0 ? int.MaxValue : p.Index)
            .ToList();

        if (phases.Count == 0)
        {
            error = "nessuna fase riconosciuta dal summary";
            return false;
        }

        return true;
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

    private static void MarkLatestAgentResponseAsFailed(
        EngineContext ctx,
        Agent? agent,
        string? modelUsed,
        string failReason)
    {
        if (ctx == null || ctx.StoryId <= 0 || agent == null || string.IsNullOrWhiteSpace(failReason))
        {
            return;
        }

        try
        {
            var database = ServiceLocator.Services?.GetService(typeof(DatabaseService)) as DatabaseService;
            if (database == null)
            {
                return;
            }

            var agentName = string.IsNullOrWhiteSpace(agent.Name) ? null : agent.Name.Trim();
            var modelName = string.IsNullOrWhiteSpace(modelUsed)
                ? ResolveAgentModelName(agent)
                : modelUsed.Trim();

            long? logId = null;
            if (ctx.ThreadId > 0)
            {
                logId = database.TryGetLatestModelResponseLogId(
                    ctx.ThreadId,
                    agentName: agentName,
                    modelName: modelName,
                    storyId: ctx.StoryId);
            }

            if (!logId.HasValue || logId.Value <= 0)
            {
                var storyLogs = database.GetLogsByStoryId(ctx.StoryId, limit: 300);
                logId = storyLogs
                    .Where(l =>
                        l?.Id.HasValue == true &&
                        (string.Equals(l.Category, "ModelResponse", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(l.Category, "ModelCompletion", StringComparison.OrdinalIgnoreCase)) &&
                        (string.IsNullOrWhiteSpace(agentName) ||
                         string.Equals((l.AgentName ?? string.Empty).Trim(), agentName, StringComparison.OrdinalIgnoreCase)) &&
                        (string.IsNullOrWhiteSpace(modelName) ||
                         string.Equals((l.ModelName ?? string.Empty).Trim(), modelName, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(l => l!.Id!.Value)
                    .Select(l => l!.Id)
                    .FirstOrDefault();
            }

            if (logId.HasValue && logId.Value > 0)
            {
                database.UpdateModelResponseResultById(
                    logId.Value,
                    "FAILED",
                    failReason.Trim(),
                    examined: true);
            }
        }
        catch
        {
            // best effort: non bloccare il flusso NRE per errori di marcatura log
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
