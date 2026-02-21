using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TinyGenerator.Models;

namespace TinyGenerator.Services;

public sealed class CommandModelExecutionService : IAgentCallService
{
    public sealed record DeterministicValidationResult(bool IsValid, string? Reason = null);

    public sealed class Request
    {
        public string CommandKey { get; set; } = string.Empty;
        public Agent Agent { get; set; } = new();
        public string RoleCode { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string? SystemPrompt { get; set; }
        public int? MaxAttempts { get; set; }
        public int? RetryDelaySeconds { get; set; }
        public int? StepTimeoutSec { get; set; }
        public bool? UseResponseChecker { get; set; }
        public bool? EnableFallback { get; set; }
        public bool? DiagnoseOnFinalFailure { get; set; }
        public int? ExplainAfterAttempt { get; set; }
        public int? CheckerTimeoutSec { get; set; }
        public bool? EnableDeterministicValidation { get; set; }
        public string? RunId { get; set; }
        public object? ResponseFormat { get; set; }
        public Func<string, DeterministicValidationResult>? DeterministicValidator { get; set; }
        public Func<string, string, string>? RetryPromptFactory { get; set; }
        public Func<AttemptFailure, CancellationToken, Task>? AttemptFailureCallback { get; set; }
        public bool EnableStreamingOutput { get; set; }
        public Func<string, Task>? StreamChunkCallback { get; set; }
    }

    public sealed class AttemptFailure
    {
        public string RoleCode { get; init; } = string.Empty;
        public string ModelName { get; init; } = string.Empty;
        public int Attempt { get; init; }
        public int MaxAttempts { get; init; }
        public string Reason { get; init; } = string.Empty;
        public bool IsDeterministic { get; init; }
        public bool IsChecker { get; init; }
        public string PromptSent { get; init; } = string.Empty;
        public string ResponseText { get; init; } = string.Empty;
        public List<int>? ViolatedRules { get; init; }
    }

    public sealed class Result
    {
        public bool Success { get; init; }
        public string? Text { get; init; }
        public string? Error { get; init; }
        public string? ModelName { get; init; }
        public bool UsedFallback { get; init; }
        public bool DeterministicFailure { get; init; }
        public int AttemptsUsed { get; init; }
    }

    private readonly ILangChainKernelFactory _kernelFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DatabaseService _database;
    private readonly ICustomLogger? _logger;
    private readonly IOptions<ResponseValidationOptions>? _responseValidationOptions;
    private readonly IOptions<CommandPoliciesOptions>? _commandPoliciesOptions;
    private readonly IOptionsMonitor<RepetitionDetectionOptions>? _repetitionDetectionOptions;
    private readonly IOptionsMonitor<EmbeddingRepetitionOptions>? _embeddingRepetitionOptions;
    private readonly Queue<List<string>> _recentSentenceNgrams = new();
    private readonly object _recentSentenceNgramsLock = new();
    private static readonly HttpClient EmbeddingHttpClient = new HttpClient();

    private sealed record ExecutionSettings(
        int MaxAttempts,
        int RetryDelaySeconds,
        int StepTimeoutSec,
        bool UseResponseChecker,
        bool EnableFallback,
        bool DiagnoseOnFinalFailure,
        int ExplainAfterAttempt,
        int CheckerTimeoutSec,
        bool EnableDeterministicValidation,
        bool EnableSimilarSentenceRepetitionCheck,
        bool EnableEmbeddingSemanticRepetitionCheck,
        int SimilarSentenceRepeatLimit,
        int NGramSize,
        int LocalWindow,
        int RecentMemorySize,
        int ChunkSizeSentences,
        double LocalThreshold,
        double MemoryThreshold,
        double ChunkThreshold,
        double HardFailThreshold,
        double PenaltyMedium,
        double PenaltyLow,
        bool RemoveStopWords);

    public sealed record RepetitionAnalysisResult(
        double LocalJaccard,
        double MemoryJaccard,
        double ChunkJaccard,
        double RepetitionScore,
        string Source,
        int LocalHits,
        int MemoryHits,
        int ChunkHits);

    public sealed class RepetitionResult
    {
        public double MaxScore { get; set; }
        public string Source { get; set; } = "local";
        public bool HardFail { get; set; }
        public int SentencesCount { get; set; }
    }

    public CommandModelExecutionService(
        ILangChainKernelFactory kernelFactory,
        IServiceScopeFactory scopeFactory,
        DatabaseService database,
        IOptions<CommandPoliciesOptions>? commandPoliciesOptions = null,
        IOptions<ResponseValidationOptions>? responseValidationOptions = null,
        IOptionsMonitor<RepetitionDetectionOptions>? repetitionDetectionOptions = null,
        IOptionsMonitor<EmbeddingRepetitionOptions>? embeddingRepetitionOptions = null,
        ICustomLogger? logger = null)
    {
        _kernelFactory = kernelFactory;
        _scopeFactory = scopeFactory;
        _database = database;
        _commandPoliciesOptions = commandPoliciesOptions;
        _responseValidationOptions = responseValidationOptions;
        _repetitionDetectionOptions = repetitionDetectionOptions;
        _embeddingRepetitionOptions = embeddingRepetitionOptions;
        _logger = logger;
    }

    public async Task<Result> ExecuteAsync(Request request, CancellationToken ct = default)
    {
        if (request.Agent == null) throw new ArgumentNullException(nameof(request.Agent));
        var modelName = ResolveModelName(request.Agent);
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return new Result { Success = false, Error = $"Agente {request.Agent.Name} senza modello configurato", DeterministicFailure = false, AttemptsUsed = 0 };
        }

        var settings = ResolveSettings(request);
        var primary = await ExecuteOnModelAsync(request, settings, modelName, request.RoleCode, ct, request.Agent.ModelId).ConfigureAwait(false);
        if (primary.Success)
        {
            return new Result
            {
                Success = true,
                Text = primary.Text,
                Error = primary.Error,
                ModelName = modelName,
                UsedFallback = false,
                DeterministicFailure = false,
                AttemptsUsed = primary.AttemptsUsed
            };
        }

        if (settings.EnableFallback)
        {
            var fallback = await TryFallbackAsync(request, settings, ct).ConfigureAwait(false);
            if (fallback.Success)
            {
                return fallback;
            }
        }

        return new Result
        {
            Success = false,
            Text = primary.Text,
            Error = primary.Error,
            ModelName = modelName,
            UsedFallback = primary.UsedFallback,
            DeterministicFailure = primary.DeterministicFailure,
            AttemptsUsed = primary.AttemptsUsed
        };
    }

    private async Task<Result> ExecuteOnModelAsync(
        Request request,
        ExecutionSettings settings,
        string modelName,
        string roleCode,
        CancellationToken ct,
        int? modelIdOverride = null)
    {
        var maxAttempts = settings.MaxAttempts;
        var delayMs = Math.Max(0, settings.RetryDelaySeconds) * 1000;
        var currentPrompt = request.Prompt ?? string.Empty;
        var lastError = "Risposta non valida";
        var lastOutput = string.Empty;
        var hadDeterministicFailure = false;
        var explained = false;
        var responseValidation = _responseValidationOptions?.Value;
        var (_, responsePolicy) = ResolveResponsePolicy(request.CommandKey, responseValidation);
        var rules = ResolveRules(responseValidation, responsePolicy);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _logger?.Append(request.RunId ?? string.Empty, $"[{roleCode}] Tentativo {attempt}/{maxAttempts} (model={modelName})");

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (settings.StepTimeoutSec > 0)
            {
                attemptCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, settings.StepTimeoutSec)));
            }
            var attemptToken = attemptCts.Token;

            string text;
            try
            {
                var systemPrompt = BuildSystemPromptWithDynamic(request, modelName, roleCode, modelIdOverride);
                text = await CallModelTextAsync(
                    modelName,
                    request.Agent,
                    roleCode,
                    systemPrompt,
                    currentPrompt,
                    request.CommandKey,
                    request.ResponseFormat,
                    skipResponseChecker: true,
                    enableStreaming: request.EnableStreamingOutput,
                    streamChunkCallback: request.StreamChunkCallback,
                    attemptToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && settings.StepTimeoutSec > 0 && attemptCts.IsCancellationRequested)
            {
                lastError = $"Timeout fase dopo {settings.StepTimeoutSec}s";
                _logger?.Append(request.RunId ?? string.Empty, $"[{roleCode}] Tentativo {attempt} fallito: {lastError}");
                MarkLatestAttemptResult(modelName, request.Agent!, "FAILED", lastError, examined: true);
                await NotifyAttemptFailureAsync(
                    request,
                    modelName,
                    roleCode,
                    attempt,
                    maxAttempts,
                    lastError,
                    deterministic: false,
                    checker: false,
                    currentPrompt,
                    lastOutput,
                    null,
                    CancellationToken.None).ConfigureAwait(false);
                if (!explained && settings.ExplainAfterAttempt > 0 && attempt >= settings.ExplainAfterAttempt)
                {
                    await DiagnoseAsync(modelName, request, lastError, lastOutput, CancellationToken.None).ConfigureAwait(false);
                    explained = true;
                }
                if (attempt < maxAttempts && delayMs > 0) await Task.Delay(delayMs, ct).ConfigureAwait(false);
                continue;
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested)
                {
                    await TryStopLlamaCppOnCancellationAsync(modelName).ConfigureAwait(false);
                }
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                _logger?.Append(request.RunId ?? string.Empty, $"[{roleCode}] Tentativo {attempt} eccezione: {lastError}");
                MarkLatestAttemptResult(modelName, request.Agent, "FAILED", lastError, examined: true);
                await NotifyAttemptFailureAsync(
                    request,
                    modelName,
                    roleCode,
                    attempt,
                    maxAttempts,
                    lastError,
                    deterministic: false,
                    checker: false,
                    currentPrompt,
                    lastOutput,
                    null,
                    CancellationToken.None).ConfigureAwait(false);
                if (!explained && settings.ExplainAfterAttempt > 0 && attempt >= settings.ExplainAfterAttempt)
                {
                    await DiagnoseAsync(modelName, request, lastError, lastOutput, CancellationToken.None).ConfigureAwait(false);
                    explained = true;
                }
                if (attempt < maxAttempts && delayMs > 0) await Task.Delay(delayMs, ct).ConfigureAwait(false);
                continue;
            }

            lastOutput = text;
            var deterministic = await ValidateGlobalDeterministicChecksAsync(
                text,
                settings,
                request.Agent?.Name,
                modelName,
                roleCode,
                request.RunId ?? string.Empty,
                ct).ConfigureAwait(false);
            if (deterministic.IsValid && settings.EnableDeterministicValidation)
            {
                deterministic = request.DeterministicValidator?.Invoke(text) ?? new DeterministicValidationResult(true, null);
            }

            if (!deterministic.IsValid)
            {
                lastError = string.IsNullOrWhiteSpace(deterministic.Reason) ? "Check deterministico fallito" : deterministic.Reason!;
                hadDeterministicFailure = true;
                _logger?.Append(request.RunId ?? string.Empty, $"[{roleCode}] Tentativo {attempt} fallito (deterministico): {lastError}");
                _logger?.Log(
                    "Warning",
                    "DeterministicValidation",
                    $"operation={request.CommandKey}; role={roleCode}; agent={request.Agent?.Name ?? "(unknown)"}; model={modelName}; attempt={attempt}; reason={lastError}",
                    state: "deterministic_validation",
                    result: "FAILED");
                LogValidationFailureContext(
                    request,
                    roleCode,
                    modelName,
                    validationType: "deterministico",
                    attempt: attempt,
                    reason: lastError,
                    promptSent: currentPrompt,
                    responseText: lastOutput);
                MarkLatestAttemptResult(modelName, request.Agent, "FAILED", lastError, examined: true);
                await NotifyAttemptFailureAsync(
                    request,
                    modelName,
                    roleCode,
                    attempt,
                    maxAttempts,
                    lastError,
                    deterministic: true,
                    checker: false,
                    currentPrompt,
                    lastOutput,
                    null,
                    CancellationToken.None).ConfigureAwait(false);
                if (!explained && settings.ExplainAfterAttempt > 0 && attempt >= settings.ExplainAfterAttempt)
                {
                    await DiagnoseAsync(modelName, request, lastError, lastOutput, CancellationToken.None).ConfigureAwait(false);
                    explained = true;
                }
                if (attempt < maxAttempts)
                {
                    currentPrompt = request.RetryPromptFactory?.Invoke(request.Prompt ?? string.Empty, lastError)
                                    ?? BuildDefaultRetryPrompt(request.Prompt ?? string.Empty, lastError);
                    if (delayMs > 0) await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
                continue;
            }

            if (settings.UseResponseChecker)
            {
                using var checkerCts = settings.CheckerTimeoutSec > 0
                    ? CancellationTokenSource.CreateLinkedTokenSource(attemptToken)
                    : null;
                if (checkerCts != null)
                {
                    checkerCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, settings.CheckerTimeoutSec)));
                }

                var checkerToken = checkerCts?.Token ?? attemptToken;
                LogCheckerRequestContext(
                    request,
                    roleCode,
                    modelName,
                    attempt,
                    currentPrompt,
                    text);
                var checkerResult = await ValidateWithCheckerAsync(request, text, rules, checkerToken).ConfigureAwait(false);
                if (!checkerResult.IsValid)
                {
                    lastError = string.IsNullOrWhiteSpace(checkerResult.Reason) ? "response_checker invalid" : checkerResult.Reason!;
                    var checkerTrackingReason = BuildCheckerTrackingReason(checkerResult);
                    _logger?.Append(request.RunId ?? string.Empty, $"[{roleCode}] Tentativo {attempt} fallito (checker): {lastError}");
                    LogValidationFailureContext(
                        request,
                        roleCode,
                        modelName,
                        validationType: "checker",
                        attempt: attempt,
                        reason: lastError,
                        promptSent: currentPrompt,
                        responseText: lastOutput);
                    MarkLatestAttemptResult(modelName, request.Agent!, "FAILED", lastError, examined: true);
                    await NotifyAttemptFailureAsync(
                        request,
                        modelName,
                        roleCode,
                        attempt,
                        maxAttempts,
                        checkerTrackingReason,
                        deterministic: false,
                        checker: true,
                        currentPrompt,
                        lastOutput,
                        checkerResult.ViolatedRules,
                        CancellationToken.None).ConfigureAwait(false);
                    if (!explained && settings.ExplainAfterAttempt > 0 && attempt >= settings.ExplainAfterAttempt)
                    {
                        await DiagnoseAsync(modelName, request, lastError, lastOutput, CancellationToken.None).ConfigureAwait(false);
                        explained = true;
                    }
                    if (attempt < maxAttempts)
                    {
                        currentPrompt = request.RetryPromptFactory?.Invoke(request.Prompt ?? string.Empty, lastError)
                                        ?? BuildDefaultRetryPrompt(request.Prompt ?? string.Empty, lastError);
                        if (delayMs > 0) await Task.Delay(delayMs, ct).ConfigureAwait(false);
                    }
                    continue;
                }
            }

            _logger?.Append(request.RunId ?? string.Empty, $"[{roleCode}] Tentativo {attempt} riuscito");
            MarkLatestAttemptResult(modelName, request.Agent!, "SUCCESS", null, examined: true);
            return new Result
            {
                Success = true,
                Text = text,
                ModelName = modelName,
                DeterministicFailure = false
                ,
                AttemptsUsed = attempt
            };
        }

        if (settings.DiagnoseOnFinalFailure && !explained)
        {
            await DiagnoseAsync(modelName, request, lastError, lastOutput, CancellationToken.None).ConfigureAwait(false);
        }

        _logger?.Append(request.RunId ?? string.Empty, $"[{roleCode}] Fallito dopo {maxAttempts} tentativi. Motivo finale: {lastError}");
        return new Result
        {
            Success = false,
            Error = lastError,
            Text = lastOutput,
            ModelName = modelName,
            DeterministicFailure = hadDeterministicFailure,
            AttemptsUsed = maxAttempts
        };
    }

    private async Task<Result> TryFallbackAsync(Request request, ExecutionSettings settings, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var fallbackService = scope.ServiceProvider.GetService<ModelFallbackService>();
        if (fallbackService == null)
        {
            return new Result { Success = false, Error = "ModelFallbackService non disponibile", DeterministicFailure = false, AttemptsUsed = 0 };
        }

        foreach (var role in BuildFallbackRoleCandidates(request.RoleCode, request.Agent.Role))
        {
            var fallbackAttemptsUsed = 0;
            var (text, successfulModelRole) = await fallbackService.ExecuteWithFallbackAsync<string>(
                role,
                request.Agent.ModelId,
                async modelRole =>
                {
                    var fallbackName = modelRole.Model?.Name;
                    if (string.IsNullOrWhiteSpace(fallbackName))
                    {
                        throw new InvalidOperationException("Fallback ModelRole has no Model.Name");
                    }

                    var req = new Request
                    {
                        CommandKey = request.CommandKey,
                        Agent = request.Agent,
                        RoleCode = role,
                        Prompt = request.Prompt,
                        SystemPrompt = !string.IsNullOrWhiteSpace(modelRole.Instructions) ? modelRole.Instructions : request.SystemPrompt,
                        MaxAttempts = settings.MaxAttempts,
                        RetryDelaySeconds = settings.RetryDelaySeconds,
                        StepTimeoutSec = settings.StepTimeoutSec,
                        UseResponseChecker = settings.UseResponseChecker,
                        EnableFallback = false,
                        DiagnoseOnFinalFailure = false,
                        ExplainAfterAttempt = settings.ExplainAfterAttempt,
                        CheckerTimeoutSec = settings.CheckerTimeoutSec,
                        EnableDeterministicValidation = settings.EnableDeterministicValidation,
                        RunId = request.RunId,
                        DeterministicValidator = request.DeterministicValidator,
                        RetryPromptFactory = request.RetryPromptFactory,
                        AttemptFailureCallback = request.AttemptFailureCallback
                    };

                    var result = await ExecuteOnModelAsync(req, settings, fallbackName, role, ct, modelRole.ModelId).ConfigureAwait(false);
                    fallbackAttemptsUsed = result.AttemptsUsed;
                    if (!result.Success || string.IsNullOrWhiteSpace(result.Text))
                    {
                        throw new InvalidOperationException(result.Error ?? "Fallback result invalid");
                    }

                    return result.Text;
                },
                validateResult: s => !string.IsNullOrWhiteSpace(s));

            if (!string.IsNullOrWhiteSpace(text) && successfulModelRole?.Model?.Name != null)
            {
                _logger?.Append(request.RunId ?? string.Empty, $"[{request.RoleCode}] Fallback model succeeded: {successfulModelRole.Model.Name}");
                return new Result
                {
                    Success = true,
                    Text = text,
                    ModelName = successfulModelRole.Model.Name,
                    UsedFallback = true,
                    DeterministicFailure = false,
                    AttemptsUsed = fallbackAttemptsUsed
                };
            }
        }

        return new Result { Success = false, Error = "Fallback fallito", DeterministicFailure = false, AttemptsUsed = 0 };
    }

    private async Task<string> CallModelTextAsync(
        string modelName,
        Agent agent,
        string roleCode,
        string systemPrompt,
        string prompt,
        string? operationScope,
        object? responseFormat,
        bool skipResponseChecker,
        bool enableStreaming,
        Func<string, Task>? streamChunkCallback,
        CancellationToken ct)
    {
        var bridge = _kernelFactory.CreateChatBridge(
            modelName,
            agent.Temperature,
            agent.TopP,
            agent.RepeatPenalty,
            agent.TopK,
            agent.RepeatLastN,
            agent.NumPredict,
            useMaxTokens: false,
            numCtx: ResolveNumCtxForAgent(agent, modelName));
        bridge.ResponseFormat = responseFormat;
        bridge.EnableStreaming = enableStreaming && streamChunkCallback != null;
        bridge.StreamChunkCallbackAsync = streamChunkCallback;
        try
        {
            Console.WriteLine(
                $"[StoryLive TRACE] CallModelTextAsync role={roleCode} model={modelName} " +
                $"enableStreamingRequested={enableStreaming} callback_present={streamChunkCallback != null} " +
                $"bridge_enable_streaming={bridge.EnableStreaming}");
        }
        catch
        {
            // No-op: tracing must not break command execution.
        }

        var messages = new List<ConversationMessage>
        {
            new ConversationMessage { Role = "system", Content = systemPrompt },
            new ConversationMessage { Role = "user", Content = prompt }
        };

        var effectiveScope = string.IsNullOrWhiteSpace(operationScope)
            ? (LogScope.Current ?? requestScope(roleCode))
            : operationScope.Trim();
        using var scope = LogScope.Push(
            effectiveScope,
            operationId: null,
            stepNumber: null,
            maxStep: null,
            agentName: agent.Name ?? roleCode,
            agentRole: roleCode);

        var responseJson = await bridge.CallModelWithToolsAsync(
            messages,
            new List<Dictionary<string, object>>(),
            ct,
            skipResponseChecker: skipResponseChecker).ConfigureAwait(false);
        try
        {
            Console.WriteLine(
                $"[StoryLive TRACE] CallModelTextAsync response_received role={roleCode} model={modelName} " +
                $"response_json_len={(responseJson?.Length ?? 0)}");
        }
        catch
        {
            // No-op: tracing must not break command execution.
        }

        var (text, _) = LangChainChatBridge.ParseChatResponse(responseJson);
        try
        {
            Console.WriteLine(
                $"[StoryLive TRACE] CallModelTextAsync parsed_text role={roleCode} model={modelName} " +
                $"text_len={(text?.Length ?? 0)}");
        }
        catch
        {
            // No-op: tracing must not break command execution.
        }
        return text ?? string.Empty;
    }

    private async Task<ValidationResult> ValidateWithCheckerAsync(
        Request request,
        string outputText,
        IReadOnlyList<ResponseValidationRule> rules,
        CancellationToken ct)
    {
        if (IsResponseCheckerRole(request.RoleCode, request.Agent?.Role))
        {
            // Never let response_checker recursively validate itself.
            return new ValidationResult { IsValid = true, NeedsRetry = false, Reason = "response_checker self-check disabled" };
        }

        using var scope = _scopeFactory.CreateScope();
        var checker = scope.ServiceProvider.GetService<ResponseCheckerService>();
        if (checker == null)
        {
            return new ValidationResult { IsValid = true, NeedsRetry = false, Reason = "checker non disponibile" };
        }

        var instruction = BuildInstructionForChecker(request);
        return await checker.ValidateGenericResponseAsync(
            instruction,
            outputText,
            rules,
            agentName: request.Agent?.Name,
            modelName: request.Agent != null ? ResolveModelName(request.Agent) : null,
            ct: ct).ConfigureAwait(false);
    }

    private async Task DiagnoseAsync(string modelName, Request request, string reason, string? lastOutput, CancellationToken ct)
    {
        try
        {
            var bridge = _kernelFactory.CreateChatBridge(
                modelName,
                request.Agent.Temperature,
                request.Agent.TopP,
                request.Agent.RepeatPenalty,
                request.Agent.TopK,
                request.Agent.RepeatLastN,
                request.Agent.NumPredict,
                useMaxTokens: false,
                numCtx: ResolveNumCtxForAgent(request.Agent, modelName));

            var sb = new StringBuilder();
            sb.AppendLine("Spiega in 3-6 righe perche la tua risposta precedente ha fallito e cosa farai per correggerla.");
            sb.AppendLine($"Errore: {reason}");
            if (!string.IsNullOrWhiteSpace(lastOutput))
            {
                sb.AppendLine();
                sb.AppendLine("ULTIMO OUTPUT:");
                sb.AppendLine(lastOutput);
            }

            var messages = new List<ConversationMessage>
            {
                new ConversationMessage { Role = "system", Content = "Sei in modalita diagnostica. Niente JSON." },
                new ConversationMessage { Role = "user", Content = sb.ToString() }
            };

            using var scope = LogScope.Push(
                LogScope.Current ?? requestScope(request.RoleCode),
                operationId: null,
                stepNumber: null,
                maxStep: null,
                agentName: request.Agent.Name ?? request.RoleCode,
                agentRole: request.RoleCode);
            var response = await bridge.CallModelWithToolsAsync(messages, new List<Dictionary<string, object>>(), ct, skipResponseChecker: true).ConfigureAwait(false);
            var (text, _) = LangChainChatBridge.ParseChatResponse(response);
            if (!string.IsNullOrWhiteSpace(text))
            {
                _logger?.Append(request.RunId ?? string.Empty, $"[{request.RoleCode}] Diagnosi: {text}");
            }
        }
        catch
        {
            // best-effort
        }
    }

    private void LogCheckerRequestContext(
        Request request,
        string roleCode,
        string modelName,
        int attempt,
        string? promptSent,
        string? candidateResponse)
    {
        if (_logger == null)
        {
            return;
        }

        var runId = request.RunId ?? string.Empty;
        var agentName = string.IsNullOrWhiteSpace(request.Agent?.Name) ? "(agent n/a)" : request.Agent.Name;
        var instruction = BuildInstructionForChecker(request);

        var sb = new StringBuilder();
        sb.AppendLine($"[CHECKER_REQUEST] attempt={attempt}");
        sb.AppendLine($"role={roleCode}; agent={agentName}; model={modelName}");
        sb.AppendLine("PROMPT_INVIATO_AL_MODELLO:");
        sb.AppendLine(promptSent ?? request.Prompt ?? string.Empty);
        sb.AppendLine("ISTRUZIONE_INVIATA_AL_CHECKER:");
        sb.AppendLine(instruction);
        sb.AppendLine("RISPOSTA_CANDIDATA_DA_VALIDARE:");
        sb.AppendLine(candidateResponse ?? string.Empty);

        _logger.Append(runId, sb.ToString(), "info");
    }

    private void LogValidationFailureContext(
        Request request,
        string roleCode,
        string modelName,
        string validationType,
        int attempt,
        string reason,
        string? promptSent,
        string? responseText)
    {
        if (_logger == null)
        {
            return;
        }

        var runId = request.RunId ?? string.Empty;
        var agentName = string.IsNullOrWhiteSpace(request.Agent?.Name) ? "(agent n/a)" : request.Agent.Name;
        var prompt = promptSent ?? request.Prompt ?? string.Empty;
        var responsePreview = responseText ?? string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"[VALIDATION_FAIL_CONTEXT] type={validationType}; attempt={attempt}");
        sb.AppendLine($"role={roleCode}; agent={agentName}; model={modelName}");
        sb.AppendLine($"reason={reason}");
        sb.AppendLine("PROMPT_INVIATO:");
        sb.AppendLine(prompt);
        if (!string.IsNullOrWhiteSpace(responsePreview))
        {
            sb.AppendLine("RISPOSTA_MODELLO:");
            sb.AppendLine(responsePreview);
        }

        _logger.Append(runId, sb.ToString(), "warning");
    }

    private string BuildInstructionForChecker(Request request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== SYSTEM INSTRUCTIONS ===");
        sb.AppendLine(request.SystemPrompt ?? BuildSystemPrompt(request.Agent));
        sb.AppendLine();
        sb.AppendLine("=== USER INPUT ===");
        sb.AppendLine(request.Prompt);
        return sb.ToString();
    }

    private string BuildDefaultRetryPrompt(string originalPrompt, string reason)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ATTENZIONE: output non valido.");
        sb.AppendLine("Motivo: " + reason);
        sb.AppendLine("Rigenera la risposta completa rispettando i vincoli.");
        sb.AppendLine();
        sb.AppendLine("PROMPT ORIGINALE:");
        sb.AppendLine(originalPrompt);
        return sb.ToString();
    }

    private async Task<DeterministicValidationResult> ValidateGlobalDeterministicChecksAsync(
        string text,
        ExecutionSettings settings,
        string? agentName,
        string? modelName,
        string roleCode,
        string runId,
        CancellationToken ct)
    {
        var output = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(output))
        {
            return new DeterministicValidationResult(false, "Output vuoto");
        }

        RepetitionAnalysisResult? jaccard = null;
        RepetitionResult? embedding = null;
        var checkSw = Stopwatch.StartNew();

        if (settings.EnableSimilarSentenceRepetitionCheck)
        {
            jaccard = AnalyzeRepetition(
                output,
                settings.NGramSize,
                settings.LocalWindow,
                settings.RecentMemorySize,
                settings.ChunkSizeSentences,
                settings.LocalThreshold,
                settings.MemoryThreshold,
                settings.ChunkThreshold,
                settings.RemoveStopWords);
        }

        if (settings.EnableEmbeddingSemanticRepetitionCheck)
        {
            var embeddingOptions = _embeddingRepetitionOptions?.CurrentValue ?? new EmbeddingRepetitionOptions();
            embedding = await DetectEmbeddingRepetitionsAsync(output, embeddingOptions, ct).ConfigureAwait(false);
        }

        checkSw.Stop();
        var durationSecs = Math.Max(1, (int)Math.Ceiling(checkSw.Elapsed.TotalSeconds));
        if (_logger != null && (settings.EnableSimilarSentenceRepetitionCheck || settings.EnableEmbeddingSemanticRepetitionCheck))
        {
            var maxScore = Math.Max(jaccard?.RepetitionScore ?? 0, embedding?.MaxScore ?? 0);
            var source = (embedding != null && embedding.MaxScore >= (jaccard?.RepetitionScore ?? 0))
                ? $"embedding:{embedding.Source}"
                : $"jaccard:{jaccard?.Source ?? "none"}";
            var success = true;

            if (jaccard != null)
            {
                var hasLocalFail = jaccard.LocalJaccard >= settings.LocalThreshold && jaccard.LocalHits > settings.SimilarSentenceRepeatLimit;
                var hasMemoryFail = jaccard.MemoryJaccard >= settings.MemoryThreshold && jaccard.MemoryHits > settings.SimilarSentenceRepeatLimit;
                var hasChunkFail = jaccard.ChunkJaccard >= settings.ChunkThreshold && jaccard.ChunkHits > settings.SimilarSentenceRepeatLimit;
                if (hasLocalFail || hasMemoryFail || hasChunkFail) success = false;
            }
            if (embedding != null && embedding.HardFail) success = false;

            var statusWord = success ? "SUCCESSED" : "FAILED";
            _logger.Log(
                success ? "Information" : "Warning",
                "RepetitionValidation",
                $"agent={agentName ?? roleCode}; model={modelName ?? "(unknown)"}; score={maxScore:0.000}; source={source}; status={statusWord}; durationSecs={durationSecs}",
                result: success ? "SUCCESS" : "FAILED",
                durationSecs: durationSecs);
        }

        if (jaccard != null)
        {
            var hasLocalFail = jaccard.LocalJaccard >= settings.LocalThreshold && jaccard.LocalHits > settings.SimilarSentenceRepeatLimit;
            var hasMemoryFail = jaccard.MemoryJaccard >= settings.MemoryThreshold && jaccard.MemoryHits > settings.SimilarSentenceRepeatLimit;
            var hasChunkFail = jaccard.ChunkJaccard >= settings.ChunkThreshold && jaccard.ChunkHits > settings.SimilarSentenceRepeatLimit;
            if (hasLocalFail || hasMemoryFail || hasChunkFail)
            {
                return new DeterministicValidationResult(
                    false,
                    $"Ripetizioni rilevate (Jaccard) score={jaccard.RepetitionScore:0.00} source={jaccard.Source} (limite={settings.SimilarSentenceRepeatLimit})");
            }
        }

        if (embedding != null && embedding.HardFail)
        {
            return new DeterministicValidationResult(
                false,
                $"Ripetizioni semantiche (STS) score={embedding.MaxScore:0.00} source={embedding.Source}");
        }

        return new DeterministicValidationResult(true, null);
    }

    private RepetitionAnalysisResult AnalyzeRepetition(
        string text,
        int nGramSize,
        int localWindow,
        int recentMemorySize,
        int chunkSizeSentences,
        double localThreshold,
        double memoryThreshold,
        double chunkThreshold,
        bool removeStopWords)
    {
        var sentences = ExtractSentences(text);
        if (sentences.Count == 0)
        {
            return new RepetitionAnalysisResult(0, 0, 0, 0, "none", 0, 0, 0);
        }

        var localBest = 0.0;
        var localHits = 0;
        for (var i = 0; i < sentences.Count; i++)
        {
            var maxOffset = Math.Min(localWindow, sentences.Count - i - 1);
            for (var offset = 1; offset <= maxOffset; offset++)
            {
                var score = ComputeSentenceSimilarity(sentences[i], sentences[i + offset], nGramSize, removeStopWords);
                if (score > localBest) localBest = score;
                if (score >= localThreshold) localHits++;
            }
        }

        var memoryBest = 0.0;
        var memoryHits = 0;
        var memorySnapshot = GetRecentMemorySnapshot();
        foreach (var sentence in sentences)
        {
            var tokens = NormalizeAndTokenize(sentence, removeStopWords);
            var currentNgrams = BuildNGrams(tokens, nGramSize).ToList();
            if (currentNgrams.Count == 0) continue;
            var currentSet = new HashSet<string>(currentNgrams, StringComparer.Ordinal);

            foreach (var past in memorySnapshot)
            {
                if (past == null || past.Count == 0) continue;
                var score = ComputeJaccard(currentSet, new HashSet<string>(past, StringComparer.Ordinal));
                if (score > memoryBest) memoryBest = score;
                if (score >= memoryThreshold) memoryHits++;
            }

            EnqueueRecentSentenceNgrams(currentNgrams, recentMemorySize);
        }

        var chunkBest = 0.0;
        var chunkHits = 0;
        var chunks = BuildChunks(sentences, chunkSizeSentences);
        var chunkSets = chunks
            .Select(chunk => BuildChunkNGramSet(chunk, nGramSize, removeStopWords))
            .ToList();
        for (var i = 0; i < chunkSets.Count; i++)
        {
            for (var j = 0; j < i; j++)
            {
                var score = ComputeJaccard(chunkSets[i], chunkSets[j]);
                if (score > chunkBest) chunkBest = score;
                if (score >= chunkThreshold) chunkHits++;
            }
        }

        var repetitionScore = Math.Max(localBest, Math.Max(memoryBest, chunkBest));
        var source = "local";
        if (memoryBest >= localBest && memoryBest >= chunkBest) source = "memory";
        if (chunkBest >= localBest && chunkBest >= memoryBest) source = "chunk";
        if (repetitionScore <= 0) source = "none";

        return new RepetitionAnalysisResult(
            LocalJaccard: localBest,
            MemoryJaccard: memoryBest,
            ChunkJaccard: chunkBest,
            RepetitionScore: repetitionScore,
            Source: source,
            LocalHits: localHits,
            MemoryHits: memoryHits,
            ChunkHits: chunkHits);
    }

    public static RepetitionAnalysisResult AnalyzeTextRepetition(string text, RepetitionDetectionOptions? options = null)
    {
        var cfg = options ?? new RepetitionDetectionOptions();
        var sentences = ExtractSentences(text);
        if (sentences.Count == 0)
        {
            return new RepetitionAnalysisResult(0, 0, 0, 0, "none", 0, 0, 0);
        }

        var nGramSize = Math.Max(1, cfg.NGramSize);
        var localWindow = Math.Max(1, cfg.LocalWindow);
        var chunkSizeSentences = Math.Max(1, cfg.ChunkSizeSentences);
        var localThreshold = Math.Clamp(cfg.LocalThreshold, 0.0, 1.0);
        var chunkThreshold = Math.Clamp(cfg.ChunkThreshold, 0.0, 1.0);

        var localBest = 0.0;
        var localHits = 0;
        for (var i = 0; i < sentences.Count; i++)
        {
            var maxOffset = Math.Min(localWindow, sentences.Count - i - 1);
            for (var offset = 1; offset <= maxOffset; offset++)
            {
                var score = ComputeSentenceSimilarity(sentences[i], sentences[i + offset], nGramSize, cfg.RemoveStopWords);
                if (score > localBest) localBest = score;
                if (score >= localThreshold) localHits++;
            }
        }

        var chunkBest = 0.0;
        var chunkHits = 0;
        var chunks = BuildChunks(sentences, chunkSizeSentences);
        var chunkSets = chunks
            .Select(chunk => BuildChunkNGramSet(chunk, nGramSize, cfg.RemoveStopWords))
            .ToList();
        for (var i = 0; i < chunkSets.Count; i++)
        {
            for (var j = 0; j < i; j++)
            {
                var score = ComputeJaccard(chunkSets[i], chunkSets[j]);
                if (score > chunkBest) chunkBest = score;
                if (score >= chunkThreshold) chunkHits++;
            }
        }

        var repetitionScore = Math.Max(localBest, chunkBest);
        var source = repetitionScore <= 0 ? "none" : (chunkBest >= localBest ? "chunk" : "local");
        return new RepetitionAnalysisResult(localBest, 0, chunkBest, repetitionScore, source, localHits, 0, chunkHits);
    }

    public async Task<RepetitionResult> DetectEmbeddingRepetitionsAsync(string text, EmbeddingRepetitionOptions opt, CancellationToken ct = default)
    {
        var options = opt ?? new EmbeddingRepetitionOptions();
        var sentences = ExtractSentences(text);
        if (sentences.Count == 0)
        {
            return new RepetitionResult { MaxScore = 0, Source = "local", HardFail = false, SentencesCount = 0 };
        }

        var embeddings = await BatchGetEmbeddings(sentences, options, ct).ConfigureAwait(false);
        var recentMemory = new Queue<float[]>();

        var maxLocal = 0.0;
        var maxMemory = 0.0;

        for (var i = 0; i < embeddings.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var embI = embeddings[i];
            if (embI.Length == 0) continue;

            var maxJ = Math.Min(embeddings.Count - 1, i + Math.Max(1, options.SentenceWindow));
            for (var j = i + 1; j <= maxJ; j++)
            {
                var sim = Cosine(embI, embeddings[j]);
                if (sim > maxLocal) maxLocal = sim;
            }

            foreach (var embPrev in recentMemory)
            {
                var sim = Cosine(embI, embPrev);
                if (sim > maxMemory) maxMemory = sim;
            }

            recentMemory.Enqueue(embI);
            while (recentMemory.Count > Math.Max(1, options.MemorySize))
            {
                recentMemory.Dequeue();
            }
        }

        var maxChunk = 0.0;
        var chunkSize = Math.Max(1, options.ChunkSize);
        var chunkEmbeddings = new List<float[]>();
        for (var i = 0; i < embeddings.Count; i += chunkSize)
        {
            var slice = embeddings.Skip(i).Take(chunkSize).Where(v => v.Length > 0).ToList();
            if (slice.Count == 0) continue;
            chunkEmbeddings.Add(AverageEmbedding(slice));
        }

        for (var i = 0; i < chunkEmbeddings.Count; i++)
        {
            for (var j = 0; j < i; j++)
            {
                var sim = Cosine(chunkEmbeddings[i], chunkEmbeddings[j]);
                if (sim > maxChunk) maxChunk = sim;
            }
        }

        var maxScore = Math.Max(maxLocal, Math.Max(maxMemory, maxChunk));
        var source = "local";
        if (maxChunk >= maxMemory && maxChunk >= maxLocal) source = "chunk";
        else if (maxMemory >= maxLocal) source = "memory";

        return new RepetitionResult
        {
            MaxScore = maxScore,
            Source = source,
            HardFail = maxScore >= options.HardFail,
            SentencesCount = sentences.Count
        };
    }

    private async Task<List<float[]>> BatchGetEmbeddings(List<string> sentences, EmbeddingRepetitionOptions options, CancellationToken ct)
    {
        var maxParallel = Math.Max(1, Math.Min(4, options.MaxParallelRequests));
        var cache = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var results = new float[sentences.Count][];
        var sem = new SemaphoreSlim(maxParallel, maxParallel);
        var tasks = new List<Task>(sentences.Count);

        for (var i = 0; i < sentences.Count; i++)
        {
            var idx = i;
            var sentence = sentences[i];
            tasks.Add(Task.Run(async () =>
            {
                ct.ThrowIfCancellationRequested();
                if (cache.TryGetValue(sentence, out var cached))
                {
                    results[idx] = cached;
                    return;
                }

                await sem.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (cache.TryGetValue(sentence, out var cachedAgain))
                    {
                        results[idx] = cachedAgain;
                        return;
                    }

                    var emb = await GetEmbeddingAsync(sentence, options, ct).ConfigureAwait(false);
                    lock (cache)
                    {
                        if (!cache.ContainsKey(sentence))
                        {
                            cache[sentence] = emb;
                        }
                        results[idx] = cache[sentence];
                    }
                }
                finally
                {
                    sem.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Select(r => r ?? Array.Empty<float>()).ToList();
    }

    private static async Task<float[]> GetEmbeddingAsync(string text, EmbeddingRepetitionOptions options, CancellationToken ct)
    {
        var endpoint = (options.Endpoint ?? "http://localhost:11434").TrimEnd('/');
        var url = $"{endpoint}/api/embeddings";
        var payload = JsonSerializer.Serialize(new
        {
            model = string.IsNullOrWhiteSpace(options.Model) ? "nomic-embed-text" : options.Model,
            prompt = text ?? string.Empty
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await EmbeddingHttpClient.PostAsync(url, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (doc.RootElement.TryGetProperty("embedding", out var embProp) && embProp.ValueKind == JsonValueKind.Array)
        {
            var vals = new List<float>();
            foreach (var n in embProp.EnumerateArray())
            {
                if (n.ValueKind == JsonValueKind.Number && n.TryGetSingle(out var f))
                {
                    vals.Add(f);
                }
                else if (n.ValueKind == JsonValueKind.Number)
                {
                    vals.Add((float)n.GetDouble());
                }
            }

            return vals.ToArray();
        }

        return Array.Empty<float>();
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a == null || b == null || a.Length == 0 || b.Length == 0) return 0;
        var len = Math.Min(a.Length, b.Length);
        if (len == 0) return 0;

        double dot = 0;
        double normA = 0;
        double normB = 0;
        for (var i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA <= 0 || normB <= 0) return 0;
        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    private static float[] AverageEmbedding(List<float[]> vectors)
    {
        if (vectors == null || vectors.Count == 0) return Array.Empty<float>();
        var dim = vectors.Min(v => v?.Length ?? 0);
        if (dim <= 0) return Array.Empty<float>();

        var avg = new float[dim];
        foreach (var vector in vectors)
        {
            if (vector == null || vector.Length < dim) continue;
            for (var i = 0; i < dim; i++)
            {
                avg[i] += vector[i];
            }
        }

        for (var i = 0; i < dim; i++)
        {
            avg[i] /= vectors.Count;
        }

        return avg;
    }

    private List<List<string>> GetRecentMemorySnapshot()
    {
        lock (_recentSentenceNgramsLock)
        {
            return _recentSentenceNgrams.Select(x => x.ToList()).ToList();
        }
    }

    private void EnqueueRecentSentenceNgrams(List<string> ngrams, int recentMemorySize)
    {
        lock (_recentSentenceNgramsLock)
        {
            _recentSentenceNgrams.Enqueue(ngrams);
            while (_recentSentenceNgrams.Count > recentMemorySize)
            {
                _recentSentenceNgrams.Dequeue();
            }
        }
    }

    private static List<string> ExtractSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        var sentences = Regex.Split(text.Trim(), @"(?<=[\.!?])\s+");
        return sentences
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static List<List<string>> BuildChunks(List<string> sentences, int chunkSizeSentences)
    {
        var size = Math.Max(1, chunkSizeSentences);
        var chunks = new List<List<string>>();
        for (var i = 0; i < sentences.Count; i += size)
        {
            chunks.Add(sentences.Skip(i).Take(size).ToList());
        }

        return chunks;
    }

    private static HashSet<string> BuildChunkNGramSet(List<string> chunk, int nGramSize, bool removeStopWords)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sentence in chunk)
        {
            var tokens = NormalizeAndTokenize(sentence, removeStopWords);
            foreach (var ng in BuildNGrams(tokens, nGramSize))
            {
                set.Add(ng);
            }
        }

        return set;
    }

    private static readonly HashSet<string> ItalianStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "il","lo","la","i","gli","le","un","uno","una","di","a","da","in","con","su","per","tra","fra",
        "e","o","ma","che","chi","cui","non","si","mi","ti","ci","vi","ne","del","della","dello","dei","degli","delle"
    };

    private static List<string> NormalizeAndTokenize(string sentence, bool removeStopWords = false)
    {
        var tokens = Regex.Matches((sentence ?? string.Empty).ToLowerInvariant(), @"\p{L}+")
            .Select(m => m.Value)
            .Where(x => x.Length > 0);

        if (removeStopWords)
        {
            tokens = tokens.Where(t => !ItalianStopWords.Contains(t));
        }

        return tokens.ToList();
    }

    private static IEnumerable<string> BuildNGrams(List<string> tokens, int n)
    {
        var gramSize = Math.Max(1, n);
        var items = tokens ?? new List<string>();
        if (items.Count == 0) return Enumerable.Empty<string>();
        if (items.Count < gramSize) return new[] { string.Join(" ", items) };

        var ngrams = new List<string>(items.Count - gramSize + 1);
        for (var i = 0; i <= items.Count - gramSize; i++)
        {
            ngrams.Add(string.Join(" ", items.Skip(i).Take(gramSize)));
        }

        return ngrams;
    }

    private static HashSet<string> ToNGramSet(string sentence, int nGramSize, bool removeStopWords)
    {
        var tokens = NormalizeAndTokenize(sentence, removeStopWords);
        var ngrams = BuildNGrams(tokens, nGramSize).ToList();
        return new HashSet<string>(ngrams, StringComparer.Ordinal);
    }

    private static double ComputeSentenceSimilarity(string s1, string s2)
        => ComputeSentenceSimilarity(s1, s2, 3, true);

    private static double ComputeSentenceSimilarity(string s1, string s2, int nGramSize, bool removeStopWords)
    {
        var a = ToNGramSet(s1, nGramSize, removeStopWords);
        var b = ToNGramSet(s2, nGramSize, removeStopWords);
        return ComputeJaccard(a, b);
    }

    private static double ComputeChunkSimilarity(List<string> chunkA, List<string> chunkB)
    {
        var a = BuildChunkNGramSet(chunkA, 3, true);
        var b = BuildChunkNGramSet(chunkB, 3, true);
        return ComputeJaccard(a, b);
    }

    private static double ComputeJaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersection = a.Intersect(b).Count();
        var union = a.Union(b).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    private ExecutionSettings ResolveSettings(Request request)
    {
        var commandPolicy = _commandPoliciesOptions?.Value?.Resolve(request.CommandKey, null) ?? new CommandExecutionPolicy();
        var responseValidation = _responseValidationOptions?.Value ?? new ResponseValidationOptions();
        var (_, responsePolicy) = ResolveResponsePolicy(request.CommandKey, responseValidation);

        var maxAttempts = request.MaxAttempts ?? Math.Max(1, commandPolicy.MaxAttempts);
        var retryDelaySeconds = request.RetryDelaySeconds ?? Math.Max(0, commandPolicy.RetryDelayBaseSeconds);
        var stepTimeoutSec = request.StepTimeoutSec ?? Math.Max(0, commandPolicy.TimeoutSec);
        var useResponseChecker = request.UseResponseChecker
            ?? responsePolicy?.EnableChecker
            ?? responseValidation.EnableCheckerByDefault;
        if (IsResponseCheckerRole(request.RoleCode, request.Agent?.Role))
        {
            useResponseChecker = false;
        }
        var enableFallback = request.EnableFallback ?? responseValidation.EnableFallback;
        var diagnoseOnFinalFailure = request.DiagnoseOnFinalFailure ?? responseValidation.AskFailureReasonOnFinalFailure;
        var explainAfterAttempt = request.ExplainAfterAttempt
            ?? responsePolicy?.ExplainAfterAttempt
            ?? responseValidation.ExplainAfterAttempt;
        var checkerTimeoutSec = request.CheckerTimeoutSec
            ?? responsePolicy?.CheckerTimeoutSec
            ?? responseValidation.CheckerTimeoutSec;
        var enableDeterministicValidation = request.EnableDeterministicValidation
            ?? responsePolicy?.EnableDeterministicValidation
            ?? responseValidation.EnableDeterministicValidation;
        var enableSimilarSentenceRepetitionCheck = responsePolicy?.EnableSimilarSentenceRepetitionCheck
            ?? responseValidation.EnableSimilarSentenceRepetitionCheck;
        var enableEmbeddingSemanticRepetitionCheck = responsePolicy?.EnableEmbeddingSemanticRepetitionCheck
            ?? responseValidation.EnableEmbeddingSemanticRepetitionCheck;
        var similarSentenceRepeatLimit = responsePolicy?.SimilarSentenceRepeatLimit
            ?? responseValidation.SimilarSentenceRepeatLimit;
        var repetitionOptions = _repetitionDetectionOptions?.CurrentValue ?? new RepetitionDetectionOptions();
        var nGramSize = Math.Max(1, repetitionOptions.NGramSize);
        var localWindow = Math.Max(1, repetitionOptions.LocalWindow);
        var recentMemorySize = Math.Max(1, repetitionOptions.RecentMemorySize);
        var chunkSizeSentences = Math.Max(1, repetitionOptions.ChunkSizeSentences);
        var localThreshold = Math.Clamp(repetitionOptions.LocalThreshold, 0.0, 1.0);
        var memoryThreshold = Math.Clamp(repetitionOptions.MemoryThreshold, 0.0, 1.0);
        var chunkThreshold = Math.Clamp(repetitionOptions.ChunkThreshold, 0.0, 1.0);
        var hardFailThreshold = Math.Clamp(repetitionOptions.HardFailThreshold, 0.0, 1.0);
        var penaltyMedium = Math.Clamp(repetitionOptions.PenaltyMedium, 0.0, 1.0);
        var penaltyLow = Math.Clamp(repetitionOptions.PenaltyLow, 0.0, 1.0);
        var removeStopWords = repetitionOptions.RemoveStopWords;

        return new ExecutionSettings(
            MaxAttempts: Math.Max(1, maxAttempts),
            RetryDelaySeconds: Math.Max(0, retryDelaySeconds),
            StepTimeoutSec: Math.Max(0, stepTimeoutSec),
            UseResponseChecker: useResponseChecker,
            EnableFallback: enableFallback,
            DiagnoseOnFinalFailure: diagnoseOnFinalFailure,
            ExplainAfterAttempt: Math.Max(0, explainAfterAttempt),
            CheckerTimeoutSec: Math.Max(0, checkerTimeoutSec),
            EnableDeterministicValidation: enableDeterministicValidation,
            EnableSimilarSentenceRepetitionCheck: enableSimilarSentenceRepetitionCheck,
            EnableEmbeddingSemanticRepetitionCheck: enableEmbeddingSemanticRepetitionCheck,
            SimilarSentenceRepeatLimit: Math.Max(1, similarSentenceRepeatLimit),
            NGramSize: nGramSize,
            LocalWindow: localWindow,
            RecentMemorySize: recentMemorySize,
            ChunkSizeSentences: chunkSizeSentences,
            LocalThreshold: localThreshold,
            MemoryThreshold: memoryThreshold,
            ChunkThreshold: chunkThreshold,
            HardFailThreshold: hardFailThreshold,
            PenaltyMedium: penaltyMedium,
            PenaltyLow: penaltyLow,
            RemoveStopWords: removeStopWords);
    }

    private static (string? MatchedKey, ResponseValidationCommandPolicy? Policy) ResolveResponsePolicy(
        string? commandKey,
        ResponseValidationOptions? options)
    {
        if (options?.CommandPolicies == null || options.CommandPolicies.Count == 0)
        {
            return (null, null);
        }

        foreach (var key in CommandOperationNameResolver.GetLookupKeys(commandKey))
        {
            if (options.CommandPolicies.TryGetValue(key, out var policy) && policy != null)
            {
                return (key, policy);
            }
        }

        return (null, null);
    }

    private static IReadOnlyList<ResponseValidationRule> ResolveRules(
        ResponseValidationOptions? options,
        ResponseValidationCommandPolicy? policy)
    {
        var allRules = (options?.Rules ?? new List<ResponseValidationRule>()).ToList();
        if (policy?.RuleIds == null || policy.RuleIds.Count == 0)
        {
            return allRules;
        }

        var allowed = new HashSet<int>(policy.RuleIds);
        return allRules.Where(r => allowed.Contains(r.Id)).ToList();
    }

    private static string BuildSystemPrompt(Agent agent)
        => !string.IsNullOrWhiteSpace(agent.Instructions) ? agent.Instructions! :
           !string.IsNullOrWhiteSpace(agent.Prompt) ? agent.Prompt! : "Sei un assistente esperto.";

    private string BuildSystemPromptWithDynamic(Request request, string modelName, string roleCode, int? modelIdOverride)
    {
        var basePrompt = (request.SystemPrompt ?? BuildSystemPrompt(request.Agent)).Trim();
        var modelId = ResolveModelId(modelIdOverride, request.Agent!, modelName);
        if (!modelId.HasValue || modelId.Value <= 0)
        {
            return basePrompt;
        }

        var frequentErrors = _database.ListTopModelRoleErrors(modelId.Value, null, roleCode, 10);
        if (frequentErrors.Count == 0)
        {
            return basePrompt;
        }

        var sb = new StringBuilder();
        sb.AppendLine(basePrompt);
        sb.AppendLine();
        sb.AppendLine("NON RIPETERE QUESTI ERRORI:");
        foreach (var err in frequentErrors)
        {
            sb.AppendLine($"- ({err.ErrorCount}) {err.ErrorText}");
        }

        return sb.ToString().TrimEnd();
    }

    private int? ResolveModelId(int? modelIdOverride, Agent agent, string? modelName)
    {
        if (modelIdOverride.HasValue && modelIdOverride.Value > 0)
        {
            return modelIdOverride.Value;
        }

        if (agent.ModelId.HasValue && agent.ModelId.Value > 0)
        {
            return agent.ModelId.Value;
        }

        if (!string.IsNullOrWhiteSpace(modelName))
        {
            return _database.GetModelIdByName(modelName);
        }

        return null;
    }

    private static async Task NotifyAttemptFailureAsync(
        Request request,
        string modelName,
        string roleCode,
        int attempt,
        int maxAttempts,
        string reason,
        bool deterministic,
        bool checker,
        string promptSent,
        string responseText,
        List<int>? violatedRules,
        CancellationToken ct)
    {
        var callback = request.AttemptFailureCallback;
        if (callback == null)
        {
            return;
        }

        try
        {
            await callback(
                new AttemptFailure
                {
                    RoleCode = roleCode,
                    ModelName = modelName,
                    Attempt = attempt,
                    MaxAttempts = maxAttempts,
                    Reason = reason ?? string.Empty,
                    IsDeterministic = deterministic,
                    IsChecker = checker,
                    PromptSent = promptSent ?? string.Empty,
                    ResponseText = responseText ?? string.Empty,
                    ViolatedRules = violatedRules
                },
                ct).ConfigureAwait(false);
        }
        catch
        {
            // best-effort callback
        }
    }

    private string? ResolveModelName(Agent agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.ModelName)) return agent.ModelName;
        if (!agent.ModelId.HasValue) return null;
        return _database.GetModelInfoById(agent.ModelId.Value)?.Name;
    }

    private static string requestScope(string roleCode) => $"command/{roleCode}";

    private static string BuildCheckerTrackingReason(ValidationResult checkerResult)
    {
        if (checkerResult?.ViolatedRules != null && checkerResult.ViolatedRules.Count > 0)
        {
            var rules = checkerResult.ViolatedRules
                .Where(r => r > 0)
                .Distinct()
                .OrderBy(r => r)
                .ToList();
            if (rules.Count > 0)
            {
                return $"rules:{string.Join(",", rules)}";
            }
        }

        return "rules:unknown";
    }

    private int? ResolveNumCtxForAgent(Agent? agent, string? modelName)
    {
        try
        {
            if (agent?.ModelId is > 0)
            {
                var model = _database.GetModelInfoById(agent.ModelId.Value);
                if (model?.ContextToUse is > 0)
                {
                    return model.ContextToUse;
                }
            }

            if (!string.IsNullOrWhiteSpace(modelName))
            {
                var model = _database.ListModels()
                    .FirstOrDefault(m => string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase));
                if (model?.ContextToUse is > 0)
                {
                    return model.ContextToUse;
                }
            }
        }
        catch
        {
            // best-effort
        }

        return null;
    }

    private static List<string> BuildFallbackRoleCandidates(string roleCode, string? agentRole)
    {
        var candidates = new List<string>();
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var t = value.Trim();
            if (!candidates.Contains(t, StringComparer.OrdinalIgnoreCase)) candidates.Add(t);
        }

        static string? TrimAgentSuffix(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var t = value.Trim();
            return t.EndsWith("_agent", StringComparison.OrdinalIgnoreCase) ? t[..^6] : null;
        }

        Add(roleCode);
        Add(agentRole);
        Add(TrimAgentSuffix(roleCode));
        Add(TrimAgentSuffix(agentRole));
        return candidates;
    }

    private static bool IsResponseCheckerRole(string? roleCode, string? agentRole)
    {
        static bool Match(string? value)
            => !string.IsNullOrWhiteSpace(value)
               && value.Trim().Equals("response_checker", StringComparison.OrdinalIgnoreCase);

        return Match(roleCode) || Match(agentRole);
    }

    private async Task TryStopLlamaCppOnCancellationAsync(string modelName)
    {
        try
        {
            var info = _database.ListModels().FirstOrDefault(m =>
                string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase));
            if (info == null || !string.Equals(info.Provider?.Trim(), "llama.cpp", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var llama = scope.ServiceProvider.GetService<LlamaService>();
            if (llama == null)
            {
                return;
            }

            await Task.Run(() => llama.StopServer()).ConfigureAwait(false);
            _logger?.Log("Information", "CommandModelExecution", $"Cancellation received: llama.cpp server stopped for model={modelName}");
        }
        catch (Exception ex)
        {
            _logger?.Log("Warning", "CommandModelExecution", $"Cancellation received but llama.cpp stop failed for model={modelName}: {ex.Message}");
        }
    }

    private void MarkLatestAttemptResult(string modelName, Agent agent, string result, string? failReason, bool examined)
    {
        try
        {
            var threadId = LogScope.CurrentThreadId;
            var agentName = string.IsNullOrWhiteSpace(agent.Name) ? null : agent.Name;
            long? logId = null;
            if (threadId.HasValue && threadId.Value > 0)
            {
                logId = _database.TryGetLatestModelResponseLogId(threadId.Value, agentName: agentName, modelName: modelName)
                    ?? _database.TryGetLatestModelResponseLogId(threadId.Value, agentName: null, modelName: modelName);
            }

            if (!logId.HasValue || logId.Value <= 0)
            {
                var scope = LogScope.Current;
                if (!string.IsNullOrWhiteSpace(scope))
                {
                    logId = _database.TryGetLatestModelResponseLogIdByScope(scope, agentName: agentName, modelName: modelName)
                        ?? _database.TryGetLatestModelResponseLogIdByScope(scope, agentName: null, modelName: modelName);
                }
            }

            if (!logId.HasValue || logId.Value <= 0)
            {
                return;
            }

            _database.UpdateModelResponseResultById(logId.Value, result, failReason, examined);
        }
        catch
        {
            // best-effort
        }
    }
}
