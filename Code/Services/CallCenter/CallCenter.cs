using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using TinyGenerator.Hubs;
using TinyGenerator.Models;

namespace TinyGenerator.Services;

public sealed class CallCenter : ICallCenter
{
    private readonly bool _storyLiveEnabled = false;
    private readonly IAgentCallService _agentCallService;
    private readonly DatabaseService _database;
    private readonly IHubContext<StoryLiveHub>? _hubContext;
    private readonly ICustomLogger? _logger;
    private readonly IOptionsMonitor<ResponseValidationOptions>? _responseValidationOptions;
    private readonly IAgentExecutor _agentExecutor;
    private readonly IDeterministicValidator _deterministicValidator;
    private readonly IResponseValidator _responseValidator;
    private readonly IRetryPolicy _retryPolicy;
    private readonly ISystemPromptBuilder _systemPromptBuilder;

    public CallCenter(
        IAgentCallService agentCallService,
        DatabaseService database,
        IOptionsMonitor<ResponseValidationOptions>? responseValidationOptions,
        IAgentExecutor? agentExecutor,
        IDeterministicValidator? deterministicValidator,
        IResponseValidator? responseValidator,
        IRetryPolicy? retryPolicy,
        ISystemPromptBuilder? systemPromptBuilder,
        IHubContext<StoryLiveHub>? hubContext = null,
        ICustomLogger? logger = null)
    {
        _agentCallService = agentCallService;
        _database = database;
        _responseValidationOptions = responseValidationOptions;
        _agentExecutor = agentExecutor ?? new DefaultAgentExecutor(agentCallService, database, responseValidationOptions, logger);
        _deterministicValidator = deterministicValidator ?? new DefaultDeterministicValidator();
        _responseValidator = responseValidator ?? new DefaultResponseValidator();
        _retryPolicy = retryPolicy ?? new DefaultRetryPolicy(database, responseValidationOptions);
        _systemPromptBuilder = systemPromptBuilder ?? new DefaultSystemPromptBuilder(database);
        _hubContext = hubContext;
        _logger = logger;
    }

    public CallCenter(
        IAgentCallService agentCallService,
        DatabaseService database,
        ICustomLogger? logger)
        : this(
            agentCallService,
            database,
            ServiceLocator.Services?.GetService<IOptionsMonitor<ResponseValidationOptions>>(),
            ServiceLocator.Services?.GetService<IAgentExecutor>(),
            ServiceLocator.Services?.GetService<IDeterministicValidator>(),
            ServiceLocator.Services?.GetService<IResponseValidator>(),
            ServiceLocator.Services?.GetService<IRetryPolicy>(),
            ServiceLocator.Services?.GetService<ISystemPromptBuilder>(),
            ServiceLocator.Services?.GetService<IHubContext<StoryLiveHub>>(),
            logger)
    {
    }

    public async Task<CallCenterResult> CallAgentAsync(
        long storyId,
        int threadId,
        Agent agent,
        ChatHistory history,
        CallOptions options,
        CancellationToken cancellationToken = default)
    {
        if (agent == null) throw new ArgumentNullException(nameof(agent));
        if (history == null) throw new ArgumentNullException(nameof(history));
        options ??= new CallOptions();

        var stopwatch = Stopwatch.StartNew();
        var operationName = string.IsNullOrWhiteSpace(options.Operation) ? "call_center" : options.Operation.Trim();
        var updatedHistory = new ChatHistory(history.Messages);
        var usedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var attemptsTotal = 0;
        var attemptsCurrentAgent = 0;
        string? lastFailure = null;
        Agent currentAgent = agent;
        var fallbackStats = LoadFallbackStats();

        using var callScope = LogScope.Push(
            operationName,
            operationId: null,
            stepNumber: null,
            maxStep: null,
            agentName: currentAgent.Name,
            agentRole: currentAgent.Role,
            threadId: threadId > 0 ? threadId : null,
            storyId: storyId > 0 ? storyId : null);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var modelName = ResolveModelName(currentAgent) ?? "unknown";
            if (!string.IsNullOrWhiteSpace(modelName))
            {
                usedModels.Add(modelName);
            }

            using var attemptScope = LogScope.Push(
                operationName,
                operationId: null,
                stepNumber: null,
                maxStep: null,
                agentName: currentAgent.Name,
                agentRole: currentAgent.Role,
                threadId: threadId > 0 ? threadId : null,
                storyId: storyId > 0 ? storyId : null);

            var shouldStreamLive = ShouldUseStoryLiveStreaming(currentAgent.Role);
            var liveGroup = shouldStreamLive && storyId > 0 ? BuildStoryLiveGroup(storyId) : null;
            _logger?.Log(
                "Information",
                "StoryLive",
                $"story_live decision: story_id={storyId}; thread_id={threadId}; role={currentAgent.Role}; should_stream={shouldStreamLive}; group={liveGroup ?? "(none)"}; hub_available={_hubContext != null}",
                result: "SUCCESS");
            if (!string.IsNullOrWhiteSpace(liveGroup))
            {
                await PublishStoryLiveStartAsync(
                    liveGroup!,
                    storyId,
                    currentAgent.Name,
                    modelName,
                    threadId).ConfigureAwait(false);
            }

            var roleCode = string.IsNullOrWhiteSpace(currentAgent.Role) ? "agent" : currentAgent.Role;
            var systemPromptForCurrentCall = string.IsNullOrWhiteSpace(options.SystemPromptOverride)
                ? _systemPromptBuilder.Build(currentAgent, roleCode)
                : options.SystemPromptOverride!;
            UpsertConversationSystemMessage(updatedHistory, systemPromptForCurrentCall);

            string? previousNormalizedResponse = null;
            var (resolvedResponseFormat, jsonFormatCheck) = ResolveResponseFormatArtifacts(currentAgent);
            var effectiveChecks = BuildEffectiveDeterministicChecks(options, jsonFormatCheck);
            CommandModelExecutionService.DeterministicValidationResult DeterministicCallback(string output)
            {
                var validation = _deterministicValidator.Validate(new AgentExecutionContext
                {
                    Operation = operationName,
                    Agent = currentAgent,
                    Options = options,
                    OutputText = output ?? string.Empty,
                    PreviousNormalizedResponse = previousNormalizedResponse,
                    DeterministicChecks = effectiveChecks
                });

                if (validation.IsValid)
                {
                    var normalizedSource = validation.CorrectedText ?? output;
                    previousNormalizedResponse = NormalizeForComparison(normalizedSource);
                }

                return new CommandModelExecutionService.DeterministicValidationResult(
                    validation.IsValid,
                    validation.FailureReason,
                    validation.CorrectedText);
            }

            var result = await _agentExecutor.ExecuteAsync(
                new AgentExecutionRequest
                {
                    StoryId = storyId,
                    ThreadId = threadId,
                    Agent = currentAgent,
                    History = updatedHistory,
                    SystemPrompt = systemPromptForCurrentCall,
                    Options = options,
                    EnableStoryLiveStream = shouldStreamLive,
                    StoryLiveGroup = liveGroup,
                    ResponseFormat = resolvedResponseFormat ?? options.ResponseFormat,
                    DeterministicValidatorCallback = DeterministicCallback,
                    StreamChunkCallback = !string.IsNullOrWhiteSpace(liveGroup)
                        ? (chunk => PublishStoryLiveChunkAsync(liveGroup!, storyId, chunk))
                        : null
                },
                cancellationToken).ConfigureAwait(false);

            _ = _responseValidator.Validate(new AgentExecutionContext
            {
                Operation = operationName,
                Agent = currentAgent,
                Options = options,
                ExecutionResult = result,
                OutputText = result.Text ?? string.Empty
            });

            attemptsCurrentAgent += Math.Max(1, result.AttemptsUsed);
            attemptsTotal += Math.Max(1, result.AttemptsUsed);
            if (result.Success && !string.IsNullOrWhiteSpace(result.Text))
            {
                var normalizedResponse = result.Text.Trim();
                var checkerOutcomes = await RunAgentCheckersAsync(
                    storyId,
                    threadId,
                    operationName,
                    normalizedResponse,
                    options,
                    cancellationToken).ConfigureAwait(false);

                var failedChecker = checkerOutcomes.FirstOrDefault(o => !o.Passed);
                if (failedChecker != null)
                {
                    lastFailure = failedChecker.FailureReason ?? $"agent_checker_failed:{failedChecker.CheckerAgentName}";
                    AppendFailureForRetry(updatedHistory, operationName, lastFailure, normalizedResponse);
                    _logger?.Log(
                        "Warning",
                        "CallCenter",
                        $"operation={operationName}; status=FAILED; story_id={storyId}; thread_id={threadId}; reason={lastFailure}; phase=agent_checker",
                        state: "agent_checker",
                        result: "FAILED");

                    var retryDecisionAfterChecker = _retryPolicy.Evaluate(new RetryContext
                    {
                        CurrentAgent = currentAgent,
                        Options = options,
                        AttemptsCurrentAgent = attemptsCurrentAgent,
                        AttemptsTotal = attemptsTotal,
                        UsedModels = usedModels,
                        FallbackStats = fallbackStats
                    });

                    if (retryDecisionAfterChecker.Kind == RetryDecisionKind.RetrySameAgent)
                    {
                        continue;
                    }

                    if (retryDecisionAfterChecker.Kind == RetryDecisionKind.FallbackAgent && retryDecisionAfterChecker.FallbackAgent != null)
                    {
                        if (options.AskFailExplanation)
                        {
                            await TryAskFailureExplanationAsync(
                                storyId,
                                threadId,
                                currentAgent,
                                updatedHistory,
                                options,
                                lastFailure,
                                cancellationToken).ConfigureAwait(false);
                        }

                        var previousModel = ResolveModelName(currentAgent) ?? "unknown";
                        var nextModel = ResolveModelName(retryDecisionAfterChecker.FallbackAgent) ?? "unknown";
                        var retriesPerModel = Math.Max(1, options.MaxRetries + 1);
                        _logger?.Log(
                            "Information",
                            "CallCenter",
                            $"operation={operationName}; fallback_type=model; agent={currentAgent.Name}; from_model={previousModel}; to_model={nextModel}; retries_reset_to={retriesPerModel}; conversation=preserved",
                            result: "SUCCESS");
                        currentAgent = retryDecisionAfterChecker.FallbackAgent;
                        attemptsCurrentAgent = 0;
                        continue;
                    }

                    return new CallCenterResult
                    {
                        Success = false,
                        UpdatedHistory = updatedHistory,
                        Attempts = attemptsTotal,
                        ModelUsed = NormalizeModelName(result.ModelName, modelName),
                        Duration = stopwatch.Elapsed,
                        FailureReason = lastFailure,
                        CheckerOutcomes = checkerOutcomes
                    };
                }

                var effectiveSuccessModelId = ResolveEffectiveModelId(currentAgent.ModelId, result.ModelName);
                _database.RecordModelRoleUsage(currentAgent.Role, effectiveSuccessModelId, result.ModelName, success: true, agentId: currentAgent.Id);
                updatedHistory.AddAssistant(normalizedResponse);
                _logger?.Log(
                    "Information",
                    "CallCenter",
                    $"operation={operationName}; status=SUCCESS; story_id={storyId}; thread_id={threadId}; agent={currentAgent.Name}; model={NormalizeModelName(result.ModelName, modelName)}; attempts_total={attemptsTotal}; attempts_agent={attemptsCurrentAgent}",
                    result: "SUCCESS");
                if (!string.IsNullOrWhiteSpace(liveGroup))
                {
                    await PublishStoryLiveCompletedAsync(liveGroup!, storyId).ConfigureAwait(false);
                }

                return new CallCenterResult
                {
                    Success = true,
                    ResponseText = normalizedResponse,
                    UpdatedHistory = updatedHistory,
                    Attempts = attemptsTotal,
                    ModelUsed = NormalizeModelName(result.ModelName, modelName),
                    Duration = stopwatch.Elapsed,
                    CheckerOutcomes = checkerOutcomes
                };
            }

            lastFailure = string.IsNullOrWhiteSpace(result.Error) ? "agent_call_failed" : result.Error;
            AppendFailureForRetry(updatedHistory, operationName, lastFailure, result.Text);
            if (!string.IsNullOrWhiteSpace(liveGroup))
            {
                await PublishStoryLiveFailedAsync(liveGroup!, storyId, lastFailure).ConfigureAwait(false);
            }
            _logger?.Log(
                "Warning",
                "CallCenter",
                $"operation={operationName}; status=FAILED; story_id={storyId}; thread_id={threadId}; agent={currentAgent.Name}; model={NormalizeModelName(result.ModelName, modelName)}; reason={lastFailure}; attempts_total={attemptsTotal}; attempts_agent={attemptsCurrentAgent}",
                state: result.DeterministicFailure ? "deterministic_validation" : "agent_call",
                result: "FAILED");

            // Retry with the same agent first, preserving full conversation history.
            var retryDecision = _retryPolicy.Evaluate(new RetryContext
            {
                CurrentAgent = currentAgent,
                Options = options,
                AttemptsCurrentAgent = attemptsCurrentAgent,
                AttemptsTotal = attemptsTotal,
                UsedModels = usedModels,
                FallbackStats = fallbackStats
            });

            if (retryDecision.Kind == RetryDecisionKind.RetrySameAgent)
            {
                continue;
            }

            if (retryDecision.Kind == RetryDecisionKind.FallbackAgent && retryDecision.FallbackAgent != null)
            {
                if (options.AskFailExplanation)
                {
                    await TryAskFailureExplanationAsync(
                        storyId,
                        threadId,
                        currentAgent,
                        updatedHistory,
                        options,
                        lastFailure,
                        cancellationToken).ConfigureAwait(false);
                }

                var previousModel = ResolveModelName(currentAgent) ?? "unknown";
                var nextModel = ResolveModelName(retryDecision.FallbackAgent) ?? "unknown";
                var retriesPerModel = Math.Max(1, options.MaxRetries + 1);
                _logger?.Log(
                    "Information",
                    "CallCenter",
                    $"operation={operationName}; fallback_type=model; agent={currentAgent.Name}; from_model={previousModel}; to_model={nextModel}; retries_reset_to={retriesPerModel}; conversation=preserved",
                    result: "SUCCESS");
                currentAgent = retryDecision.FallbackAgent;
                attemptsCurrentAgent = 0;
                continue;
            }

            if (retryDecision.ShouldAskFailureExplanation)
            {
                await TryAskFailureExplanationAsync(
                    storyId,
                    threadId,
                    currentAgent,
                    updatedHistory,
                    options,
                    lastFailure,
                    cancellationToken).ConfigureAwait(false);
            }

            return new CallCenterResult
            {
                Success = false,
                UpdatedHistory = updatedHistory,
                Attempts = attemptsTotal,
                ModelUsed = NormalizeModelName(result.ModelName, modelName),
                Duration = stopwatch.Elapsed,
                FailureReason = lastFailure
            };
        }
    }

    private static List<IDeterministicCheck> BuildEffectiveDeterministicChecks(
        CallOptions options,
        IDeterministicCheck? jsonFormatCheck)
    {
        var checks = new List<IDeterministicCheck>();
        if (jsonFormatCheck != null)
        {
            // 1) JSON format validity must be checked first.
            checks.Add(jsonFormatCheck);
        }

        if (options?.DeterministicChecks != null)
        {
            foreach (var check in options.DeterministicChecks)
            {
                if (check != null)
                {
                    checks.Add(check);
                }
            }
        }

        return checks;
    }

    private static (object? responseFormat, IDeterministicCheck? schemaCheck) ResolveResponseFormatArtifacts(Agent agent)
    {
        var formatName = agent?.JsonResponseFormat?.Trim();
        if (string.IsNullOrWhiteSpace(formatName))
        {
            return (null, null);
        }

        var safeFileName = Path.GetFileName(formatName);
        if (!string.Equals(safeFileName, formatName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"json_response_format non valido: '{formatName}'");
        }

        var formatPath = Path.Combine(Directory.GetCurrentDirectory(), "response_formats", safeFileName);
        if (!File.Exists(formatPath))
        {
            throw new InvalidOperationException($"File response format non trovato: {safeFileName}");
        }

        string schemaJson;
        try
        {
            schemaJson = File.ReadAllText(formatPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Impossibile leggere response format '{safeFileName}': {ex.Message}");
        }

        object? responseFormat;
        try
        {
            responseFormat = JsonSerializer.Deserialize<object>(schemaJson);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"JSON schema non valido in '{safeFileName}': {ex.Message}");
        }

        return (responseFormat, new JsonSchemaResponseFormatCheck(schemaJson, safeFileName));
    }

    private int? ResolveEffectiveModelId(int? fallbackModelId, string? modelName)
    {
        if (!string.IsNullOrWhiteSpace(modelName))
        {
            var resolved = _database.GetModelIdByName(modelName);
            if (resolved.HasValue && resolved.Value > 0)
            {
                return resolved.Value;
            }
        }

        return fallbackModelId;
    }

    private async Task TryAskFailureExplanationAsync(
        long storyId,
        int threadId,
        Agent agent,
        ChatHistory history,
        CallOptions options,
        string failureReason,
        CancellationToken cancellationToken)
    {
        try
        {
            history.AddUser(
                "[CALLCENTER_EXPLAIN_REQUEST] Spiega in massimo 6 righe il motivo del fallimento precedente e come correggerlo.");

            var req = new CommandModelExecutionService.Request
            {
                CommandKey = "fail_explanation",
                Agent = agent,
                RoleCode = string.IsNullOrWhiteSpace(agent.Role) ? "agent" : agent.Role,
                Prompt = string.Empty,
                ConversationMessages = history.Messages
                    .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Content))
                    .Select(m => new ConversationMessage
                    {
                        Role = string.IsNullOrWhiteSpace(m.Role) ? "user" : m.Role!.Trim().ToLowerInvariant(),
                        Content = m.Content!.Trim()
                    })
                    .ToList(),
                SystemPrompt = "Modalita diagnostica. Rispondi in testo semplice, niente JSON.",
                MaxAttempts = 1,
                StepTimeoutSec = Math.Max(1, (int)Math.Ceiling(options.Timeout.TotalSeconds)),
                UseResponseChecker = false,
                EnableFallback = false,
                DiagnoseOnFinalFailure = false,
                EnableDeterministicValidation = true,
                RunId = $"fail_explanation_{Guid.NewGuid():N}",
                DeterministicValidator = text =>
                    string.IsNullOrWhiteSpace(text)
                        ? new CommandModelExecutionService.DeterministicValidationResult(false, "spiegazione vuota")
                        : new CommandModelExecutionService.DeterministicValidationResult(true)
            };

            var explanation = await _agentCallService.ExecuteAsync(req, cancellationToken).ConfigureAwait(false);
            if (explanation.Success && !string.IsNullOrWhiteSpace(explanation.Text))
            {
                history.AddAssistant($"[FAIL_EXPLANATION] {explanation.Text.Trim()}");
                _logger?.Log(
                    "Information",
                    "CallCenter",
                    $"operation=fail_explanation; status=SUCCESS; story_id={storyId}; thread_id={threadId}; agent={agent.Name}",
                    result: "SUCCESS");
            }
            else
            {
                _logger?.Log(
                    "Warning",
                    "CallCenter",
                    $"operation=fail_explanation; status=FAILED; story_id={storyId}; thread_id={threadId}; agent={agent.Name}; reason={explanation.Error ?? "empty"}",
                    result: "FAILED");
            }
        }
        catch (Exception ex)
        {
            _logger?.Log(
                "Warning",
                "CallCenter",
                $"operation=fail_explanation; status=FAILED; story_id={storyId}; thread_id={threadId}; agent={agent.Name}; reason={ex.Message}",
                result: "FAILED");
        }
    }

    private static string BuildPromptFromHistory(ChatHistory history)
    {
        var messages = history.Messages
            .Where(m =>
                !string.IsNullOrWhiteSpace(m.Content) &&
                !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (messages.Count == 0)
        {
            return string.Empty;
        }

        // Fast path for the common single-turn case: keep plain user prompt without role prefix.
        if (messages.Count == 1 &&
            string.Equals(messages[0].Role, "user", StringComparison.OrdinalIgnoreCase))
        {
            return messages[0].Content!.Trim();
        }

        var sb = new System.Text.StringBuilder();
        foreach (var message in messages)
        {
            var role = string.IsNullOrWhiteSpace(message.Role) ? "user" : message.Role.Trim().ToLowerInvariant();
            sb.Append('[').Append(role).Append("] ").AppendLine(message.Content!.Trim());
        }

        return sb.ToString().Trim();
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

    private void AppendFailureForRetry(ChatHistory history, string operationName, string reason, string? failedResponse)
    {
        if (IsConversationalFailureContextEnabled(operationName))
        {
            AppendConversationalFailureContext(history, reason, failedResponse);
            return;
        }

        AppendFailureToHistory(history, reason);
    }

    private bool IsConversationalFailureContextEnabled(string? operationName)
    {
        try
        {
            var op = string.IsNullOrWhiteSpace(operationName) ? "call_center" : operationName.Trim();
            var policies = _responseValidationOptions?.CurrentValue?.CommandPolicies;
            if (policies == null || policies.Count == 0)
            {
                return true;
            }

            if (TryGetResponseValidationPolicyForOperation(policies, op, out var policy) &&
                policy?.UseConversationalFailureContext.HasValue == true)
            {
                return policy.UseConversationalFailureContext.Value;
            }

            return true;
        }
        catch
        {
            return true;
        }
    }

    private static bool TryGetResponseValidationPolicyForOperation(
        Dictionary<string, ResponseValidationCommandPolicy> policies,
        string operation,
        out ResponseValidationCommandPolicy? policy)
    {
        policy = null;
        if (policies.TryGetValue(operation, out var exact) && exact != null)
        {
            policy = exact;
            return true;
        }

        var key = operation;
        while (!string.IsNullOrWhiteSpace(key))
        {
            var slash = key.LastIndexOf('/');
            if (slash <= 0) break;
            key = key[..slash];
            if (policies.TryGetValue(key, out var pref) && pref != null)
            {
                policy = pref;
                return true;
            }
        }

        return false;
    }

    private static void AppendConversationalFailureContext(ChatHistory history, string reason, string? failedResponse)
    {
        if (history == null) return;

        history.AddUser(
            "[CALLCENTER_LAST_FAILURE_REASON] " + reason + "\n" +
            "[CALLCENTER_RETRY_REQUEST] Rispondi di nuovo rispettando tutti i vincoli della richiesta e correggendo gli errori evidenziati.");
    }

    private static void AppendFailureToHistory(ChatHistory history, string reason)
    {
        if (history == null) return;

        var failureMessage = $"[CALLCENTER_LAST_FAILURE] Tentativo fallito: {reason}";
        var lastNonSystem = history.Messages
            .LastOrDefault(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase));

        // Keep user/assistant alternation in history.
        if (lastNonSystem != null &&
            string.Equals(lastNonSystem.Role, "user", StringComparison.OrdinalIgnoreCase))
        {
            history.AddAssistant(failureMessage);
            return;
        }

        history.AddUser(failureMessage);
    }

    private static void UpsertConversationSystemMessage(ChatHistory history, string systemPrompt)
    {
        if (history == null) return;

        history.Messages.RemoveAll(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase));
        history.Messages.Insert(0, new ConversationMessage
        {
            Role = "system",
            Content = systemPrompt ?? string.Empty
        });
    }

    private static string NormalizeForComparison(string? text)
    {
        return (text ?? string.Empty).Trim();
    }

    private static string BuildDeterministicFailureReason(IDeterministicCheck check, IDeterministicResult result)
    {
        var className = check.GetType().Name;
        var rule = check.Rule;
        var generic = string.IsNullOrWhiteSpace(check.GenericErrorDescription)
            ? rule
            : check.GenericErrorDescription;
        var message = string.IsNullOrWhiteSpace(result.Message) ? "failed" : result.Message;
        return $"{className}: {rule} | GENERIC_ERROR: {generic} | DETAIL: {message}";
    }

    private async Task<List<AgentCheckerOutcome>> RunAgentCheckersAsync(
        long storyId,
        int threadId,
        string operationName,
        string primaryResponse,
        CallOptions options,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<AgentCheckerOutcome>();
        if (options?.AgentCheckers == null || options.AgentCheckers.Count == 0)
        {
            return outcomes;
        }

        foreach (var checker in options.AgentCheckers)
        {
            if (checker?.Agent == null) continue;
            var nestedCallCenter = CreateNestedCallCenter();

            var checkerHistory = new ChatHistory();
            checkerHistory.AddUser(BuildAgentCheckerInput(options.CheckerContextText, primaryResponse, checker.MinimalScore));
            var checkerOperation = !string.IsNullOrWhiteSpace(checker.Agent.Role)
                ? checker.Agent.Role!.Trim()
                : (!string.IsNullOrWhiteSpace(checker.Agent.Name) ? checker.Agent.Name!.Trim() : $"{operationName}:agent_checker");

            const int defaultCheckerRetries = 3;
            var remainingRetries = defaultCheckerRetries;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var checkerOptions = new CallOptions
                {
                    // Treat agent-based non-deterministic checks as regular agent calls.
                    Operation = checkerOperation,
                    Timeout = options.Timeout,
                    // Nested CallCenter handles single attempt; retry loop is managed here to preserve conversation.
                    MaxRetries = 0,
                    UseResponseChecker = true,
                    AskFailExplanation = true,
                    AllowFallback = true
                };

                var checkerResult = await nestedCallCenter.CallAgentAsync(
                    storyId: storyId,
                    threadId: threadId,
                    agent: checker.Agent,
                    history: checkerHistory,
                    options: checkerOptions,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var outcome = new AgentCheckerOutcome
                {
                    CheckerAgentName = checker.Agent.Name ?? "agent_checker",
                    ModelUsed = checkerResult.ModelUsed,
                    RawResponse = checkerResult.ResponseText
                };

                if (!checkerResult.Success || string.IsNullOrWhiteSpace(checkerResult.ResponseText))
                {
                    if (remainingRetries > 0)
                    {
                        remainingRetries--;
                        AppendFailureToHistory(
                            checkerHistory,
                            $"Chiamata checker fallita ({checkerResult.FailureReason ?? "empty_response"}). Riprova in JSON valido. Retries rimanenti: {remainingRetries}.");
                        continue;
                    }

                    outcome.Passed = false;
                    outcome.FailureReason = $"agent_checker_call_failed:{outcome.CheckerAgentName}:{checkerResult.FailureReason ?? "empty_response"}";
                    outcomes.Add(outcome);
                    break;
                }

                if (!TryParseAgentCheckerResult(checkerResult.ResponseText, out var score, out var needsRetry, out var issues, out var parseError))
                {
                    if (remainingRetries > 0)
                    {
                        remainingRetries--;
                        AppendFailureToHistory(
                            checkerHistory,
                            $"JSON checker non parseabile ({parseError ?? "unknown"}). Rispondi SOLO con JSON valido: {{\"score\":int,\"needs_retry\":bool,\"issues\":[string]}}. Retries rimanenti: {remainingRetries}.");
                        continue;
                    }

                    outcome.Passed = false;
                    outcome.FailureReason = $"agent_checker_parse_failed:{outcome.CheckerAgentName}:{parseError}";
                    outcomes.Add(outcome);
                    break;
                }

                var minimalScore = Math.Clamp(checker.MinimalScore, 1, 100);
                var effectiveNeedsRetry = needsRetry ?? (score < minimalScore);
                outcome.Score = score;
                outcome.NeedsRetry = effectiveNeedsRetry;
                outcome.Issues = issues;

                var checkerFailed = effectiveNeedsRetry;
                if (checkerFailed)
                {
                    outcome.Passed = false;
                    outcome.FailureReason = $"agent_checker_failed:{outcome.CheckerAgentName}:score={score};min={minimalScore};needs_retry={effectiveNeedsRetry}";
                    outcomes.Add(outcome);
                    break;
                }

                outcome.Passed = true;
                outcomes.Add(outcome);
                break;
            }

            if (outcomes.Count > 0 && outcomes[^1].Passed == false)
            {
                break;
            }
        }

        return outcomes;
    }

    private ICallCenter CreateNestedCallCenter()
    {
        // Every agent-based non-deterministic checker must run through a nested CallCenter call.
        return new CallCenter(
            _agentCallService,
            _database,
            _responseValidationOptions,
            _agentExecutor,
            _deterministicValidator,
            _responseValidator,
            _retryPolicy,
            _systemPromptBuilder,
            _hubContext,
            _logger);
    }

    private static string BuildAgentCheckerInput(string? contextText, string candidateResponse, int minimalScore)
    {
        var ctx = string.IsNullOrWhiteSpace(contextText) ? "(nessun contesto aggiuntivo)" : contextText.Trim();
        var candidate = string.IsNullOrWhiteSpace(candidateResponse) ? "(vuoto)" : candidateResponse.Trim();
        _ = minimalScore;

        return
            "Valuta la seguente risposta candidata." + Environment.NewLine +
            "Context:" + Environment.NewLine + ctx + Environment.NewLine + Environment.NewLine +
            "CandidateResponse:" + Environment.NewLine + candidate;
    }

    private static bool TryParseAgentCheckerResult(
        string? json,
        out int score,
        out bool? needsRetry,
        out List<string> issues,
        out string? error)
    {
        score = 0;
        needsRetry = null;
        issues = new List<string>();
        error = null;

        try
        {
            using var doc = JsonDocument.Parse((json ?? string.Empty).Trim());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "root non object";
                return false;
            }

            if (!root.TryGetProperty("score", out var scoreEl) || !scoreEl.TryGetInt32(out score))
            {
                error = "score mancante/non intero";
                return false;
            }

            if (root.TryGetProperty("needs_retry", out var retryEl))
            {
                if (retryEl.ValueKind != JsonValueKind.True && retryEl.ValueKind != JsonValueKind.False)
                {
                    error = "needs_retry non bool";
                    return false;
                }
                needsRetry = retryEl.GetBoolean();
            }

            if (root.TryGetProperty("issues", out var issuesEl) && issuesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in issuesEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var text = item.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            issues.Add(text.Trim());
                        }
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private IReadOnlyDictionary<string, (double successRate, double tokensPerSec)> LoadFallbackStats()
    {
        return _database.GetModelStats()
            .GroupBy(s => s.ModelName ?? "unknown", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var used = g.Sum(x => x.CountUsed ?? 0);
                    var succ = g.Sum(x => x.CountSuccessed ?? 0);
                    var evalCount = g.Sum(x => x.EvalCountTotal ?? 0);
                    var evalDur = g.Sum(x => x.EvalDurationTotal ?? 0.0);
                    var successRate = used > 0 ? (double)succ / used : 0.0;
                    var tokensPerSec = evalDur > 0 ? evalCount / evalDur : 0.0;
                    return (successRate, tokensPerSec);
                },
                StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeModelName(string? preferred, string? fallback)
    {
        var value = !string.IsNullOrWhiteSpace(preferred) ? preferred : fallback;
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        return value.Trim();
    }

    private static bool ShouldUseStoryLiveStreaming(string? roleCode)
    {
        // StoryLive temporarily disabled by product choice.
        _ = roleCode;
        return false;
    }

    private static string BuildStoryLiveGroup(long storyId) => $"story_live_{storyId}";

    private async Task PublishStoryLiveStartAsync(string group, long storyId, string? agentName, string? modelName, int threadId)
    {
        if (!_storyLiveEnabled) return;

        if (_hubContext == null)
        {
            _logger?.Log("Warning", "StoryLive", $"story_live start skipped: hub null; story_id={storyId}; group={group}", result: "FAILED");
            return;
        }

        var title = _database.GetStoryById(storyId)?.Title;
        var payload = new
        {
            storyId,
            title = string.IsNullOrWhiteSpace(title) ? $"Story {storyId}" : title,
            agentName = string.IsNullOrWhiteSpace(agentName) ? "N/A" : agentName,
            modelName = string.IsNullOrWhiteSpace(modelName) ? "N/A" : modelName,
            threadId,
            ts = DateTime.UtcNow.ToString("o")
        };
        await _hubContext.Clients.Group(group).SendAsync("StoryLiveStarted", payload).ConfigureAwait(false);
        await _hubContext.Clients.All.SendAsync("StoryLiveStarted", payload).ConfigureAwait(false);
        try
        {
            Console.WriteLine($"[StoryLive START] story_id={storyId} thread_id={threadId} agent={payload.agentName} model={payload.modelName}");
        }
        catch
        {
            // Never fail generation because terminal output failed.
        }
        _logger?.Log("Information", "StoryLive", $"story_live started event published: story_id={storyId}; group={group}; agent={payload.agentName}; model={payload.modelName}", result: "SUCCESS");
    }

    private async Task PublishStoryLiveChunkAsync(string group, long storyId, string chunk)
    {
        if (!_storyLiveEnabled) return;

        if (_hubContext == null || string.IsNullOrEmpty(chunk))
        {
            if (_hubContext == null)
            {
                _logger?.Log("Warning", "StoryLive", $"story_live chunk skipped: hub null; story_id={storyId}; group={group}", result: "FAILED");
            }
            return;
        }

        var payload = new { storyId, chunk };
        await _hubContext.Clients.Group(group).SendAsync("StoryLiveChunk", payload).ConfigureAwait(false);
        await _hubContext.Clients.All.SendAsync("StoryLiveChunk", payload).ConfigureAwait(false);
        try
        {
            Console.Write(chunk);
        }
        catch
        {
            // Never fail generation because terminal output failed.
        }
        _logger?.Log("Information", "StoryLive", $"story_live chunk published: story_id={storyId}; group={group}; chars={chunk.Length}", result: "SUCCESS");
    }

    private async Task PublishStoryLiveCompletedAsync(string group, long storyId)
    {
        if (!_storyLiveEnabled) return;

        if (_hubContext == null)
        {
            _logger?.Log("Warning", "StoryLive", $"story_live completed skipped: hub null; story_id={storyId}; group={group}", result: "FAILED");
            return;
        }

        var payload = new
        {
            storyId,
            ts = DateTime.UtcNow.ToString("o")
        };
        await _hubContext.Clients.Group(group).SendAsync("StoryLiveCompleted", payload).ConfigureAwait(false);
        await _hubContext.Clients.All.SendAsync("StoryLiveCompleted", payload).ConfigureAwait(false);
        try
        {
            Console.WriteLine();
            Console.WriteLine($"[StoryLive COMPLETED] story_id={storyId}");
        }
        catch
        {
            // Never fail generation because terminal output failed.
        }
        _logger?.Log("Information", "StoryLive", $"story_live completed event published: story_id={storyId}; group={group}", result: "SUCCESS");
    }

    private async Task PublishStoryLiveFailedAsync(string group, long storyId, string? error)
    {
        if (!_storyLiveEnabled) return;

        if (_hubContext == null)
        {
            _logger?.Log("Warning", "StoryLive", $"story_live failed skipped: hub null; story_id={storyId}; group={group}; error={error}", result: "FAILED");
            return;
        }

        var payload = new
        {
            storyId,
            error = string.IsNullOrWhiteSpace(error) ? "Errore durante la generazione." : error,
            ts = DateTime.UtcNow.ToString("o")
        };
        await _hubContext.Clients.Group(group).SendAsync("StoryLiveFailed", payload).ConfigureAwait(false);
        await _hubContext.Clients.All.SendAsync("StoryLiveFailed", payload).ConfigureAwait(false);
        try
        {
            Console.WriteLine();
            Console.WriteLine($"[StoryLive FAILED] story_id={storyId} error={payload.error}");
        }
        catch
        {
            // Never fail generation because terminal output failed.
        }
        _logger?.Log("Warning", "StoryLive", $"story_live failed event published: story_id={storyId}; group={group}; error={payload.error}", result: "FAILED");
    }
}
