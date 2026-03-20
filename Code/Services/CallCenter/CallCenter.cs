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
    private readonly IOptionsMonitor<MonomodelModeOptions>? _monomodelModeOptions;
    private readonly IOptionsMonitor<MemoryEmbeddingOptions>? _memoryEmbeddingOptions;
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
        IOptionsMonitor<MonomodelModeOptions>? monomodelModeOptions,
        IOptionsMonitor<MemoryEmbeddingOptions>? memoryEmbeddingOptions,
        IHubContext<StoryLiveHub>? hubContext = null,
        ICustomLogger? logger = null)
    {
        _agentCallService = agentCallService;
        _database = database;
        _responseValidationOptions = responseValidationOptions;
        _monomodelModeOptions = monomodelModeOptions;
        _memoryEmbeddingOptions = memoryEmbeddingOptions;
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
            ServiceLocator.Services?.GetService<IOptionsMonitor<MonomodelModeOptions>>(),
            ServiceLocator.Services?.GetService<IOptionsMonitor<MemoryEmbeddingOptions>>(),
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
        var baselineHistory = CloneHistory(history);
        var updatedHistory = CloneHistory(baselineHistory);
        var systemPromptByModel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
            PublishRetryProgressToCommandPanel(options, attemptsCurrentAgent);
            currentAgent = ApplyMonomodelOverrideIfActive(currentAgent);
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
            var systemPromptForCurrentCall = ResolveSystemPromptForCurrentAgent(
                currentAgent,
                roleCode,
                systemPromptByModel);
            UpsertConversationSystemMessage(updatedHistory, systemPromptForCurrentCall);
            UpsertRequestContextUserMessage(updatedHistory, options.SystemPromptOverride);

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
            PublishRetryProgressToCommandPanel(options, attemptsCurrentAgent);
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
                    MarkCheckedRequestFailure(currentAgent, lastFailure);
                    AppendFailureForRetry(updatedHistory, lastFailure);
                    _logger?.Log(
                        "Warning",
                        "CallCenter",
                        $"operation={operationName}; status=FAILED; story_id={storyId}; thread_id={threadId}; reason={lastFailure}; phase=agent_checker",
                        state: "agent_checker",
                        result: "FAILED");

                    if (IsNonRetriableVllmPromptContextError(lastFailure))
                    {
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
                            $"operation={operationName}; fallback_type=model; agent={currentAgent.Name}; from_model={previousModel}; to_model={nextModel}; retries_reset_to={retriesPerModel}; conversation=reset_to_baseline",
                            result: "SUCCESS");
                        updatedHistory = CloneHistory(baselineHistory);
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
            AppendFailureForRetry(updatedHistory, lastFailure);
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

            if (IsNonRetriableVllmPromptContextError(lastFailure))
            {
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
                    $"operation={operationName}; fallback_type=model; agent={currentAgent.Name}; from_model={previousModel}; to_model={nextModel}; retries_reset_to={retriesPerModel}; conversation=reset_to_baseline",
                    result: "SUCCESS");
                updatedHistory = CloneHistory(baselineHistory);
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

    private string ResolveSystemPromptForCurrentAgent(
        Agent agent,
        string roleCode,
        Dictionary<string, string> systemPromptByModel)
    {
        var cacheKey = BuildSystemPromptCacheKey(agent, roleCode);
        if (systemPromptByModel.TryGetValue(cacheKey, out var cached) && !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var built = _systemPromptBuilder.Build(agent, roleCode);
        systemPromptByModel[cacheKey] = built;
        return built;
    }

    private string BuildSystemPromptCacheKey(Agent agent, string roleCode)
    {
        var safeRole = string.IsNullOrWhiteSpace(roleCode) ? "agent" : roleCode.Trim().ToLowerInvariant();
        var modelKey = agent.ModelId.HasValue && agent.ModelId.Value > 0
            ? $"id:{agent.ModelId.Value}"
            : $"name:{(ResolveModelName(agent) ?? "unknown").Trim().ToLowerInvariant()}";
        return $"{safeRole}|{modelKey}";
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
        if (HasStructuredResponseFormat(agent, options))
        {
            _logger?.Log(
                "Information",
                "CallCenter",
                $"operation=fail_explanation; status=SKIPPED; story_id={storyId}; thread_id={threadId}; agent={agent.Description}; reason=json_response_format_active",
                result: "SKIPPED");
            return;
        }

        try
        {
            var diagnosticHistory = CloneHistory(history);
            diagnosticHistory.AddUser(
                "[CALLCENTER_EXPLAIN_REQUEST] Spiega in massimo 6 righe il motivo del fallimento precedente e come correggerlo.");

            var req = new CommandModelExecutionService.Request
            {
                CommandKey = "fail_explanation",
                Agent = agent,
                RoleCode = string.IsNullOrWhiteSpace(agent.Role) ? "agent" : agent.Role,
                Prompt = string.Empty,
                ConversationMessages = diagnosticHistory.Messages
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
                _logger?.Log(
                    "Information",
                    "CallCenter",
                    $"operation=fail_explanation; status=SUCCESS; story_id={storyId}; thread_id={threadId}; agent={agent.Description}",
                    result: "SUCCESS");
            }
            else
            {
                _logger?.Log(
                    "Warning",
                    "CallCenter",
                    $"operation=fail_explanation; status=FAILED; story_id={storyId}; thread_id={threadId}; agent={agent.Description}; reason={explanation.Error ?? "empty"}",
                    result: "FAILED");
            }
        }
        catch (Exception ex)
        {
            _logger?.Log(
                "Warning",
                "CallCenter",
                $"operation=fail_explanation; status=FAILED; story_id={storyId}; thread_id={threadId}; agent={agent.Description}; reason={ex.Message}",
                result: "FAILED");
        }
    }

    private static bool HasStructuredResponseFormat(Agent agent, CallOptions options)
    {
        if (!string.IsNullOrWhiteSpace(agent?.JsonResponseFormat))
        {
            return true;
        }

        return options?.ResponseFormat != null;
    }

    private static ChatHistory CloneHistory(ChatHistory history)
    {
        var clone = new ChatHistory();
        if (history?.Messages == null)
        {
            return clone;
        }

        foreach (var message in history.Messages.Where(m => m != null && !string.IsNullOrWhiteSpace(m.Content)))
        {
            clone.Messages.Add(new ConversationMessage
            {
                Role = message.Role,
                Content = message.Content
            });
        }

        return clone;
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

    private Agent ApplyMonomodelOverrideIfActive(Agent agent)
    {
        if (agent == null)
        {
            throw new ArgumentNullException(nameof(agent));
        }

        var mono = _monomodelModeOptions?.CurrentValue;
        if (mono == null || !mono.Enabled)
        {
            return agent;
        }

        var fixedModelDescription = (mono.ModelDescription ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(fixedModelDescription))
        {
            return agent;
        }

        var currentModelName = ResolveModelName(agent) ?? agent.ModelName?.Trim() ?? string.Empty;
        var currentModelId = ResolveEffectiveModelId(agent.ModelId, currentModelName);

        var embeddingModelName = (_memoryEmbeddingOptions?.CurrentValue?.Model ?? string.Empty).Trim();
        var embeddingModelId = _database.GetModelIdByName(embeddingModelName);
        if (embeddingModelId.HasValue && embeddingModelId.Value > 0 &&
            currentModelId.HasValue && currentModelId.Value == embeddingModelId.Value)
        {
            return agent;
        }

        if (!string.IsNullOrWhiteSpace(embeddingModelName) &&
            string.Equals(currentModelName, embeddingModelName, StringComparison.OrdinalIgnoreCase))
        {
            return agent;
        }

        var fixedModelId = _database.GetModelIdByName(fixedModelDescription);
        var fixedModel = fixedModelId.HasValue && fixedModelId.Value > 0
            ? _database.GetModelInfoById(fixedModelId.Value)
            : _database.ListModels().FirstOrDefault(m =>
                string.Equals(m.Name, fixedModelDescription, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.CallName, fixedModelDescription, StringComparison.OrdinalIgnoreCase));
        var currentMatchesFixed = false;
        if (fixedModelId.HasValue && fixedModelId.Value > 0 &&
            currentModelId.HasValue && currentModelId.Value > 0)
        {
            currentMatchesFixed = fixedModelId.Value == currentModelId.Value;
        }
        else
        {
            currentMatchesFixed = string.Equals(currentModelName, fixedModelDescription, StringComparison.OrdinalIgnoreCase);
        }

        if (currentMatchesFixed)
        {
            return agent;
        }

        var overridden = CloneAgent(agent);
        if (fixedModelId.HasValue && fixedModelId.Value > 0)
        {
            overridden.ModelId = fixedModelId.Value;
        }
        else
        {
            overridden.ModelId = null;
        }

        overridden.ModelName = fixedModelDescription;
        if (mono.DisableThinking)
        {
            overridden.Thinking = false;
        }
        else if (fixedModel?.Thinking.HasValue == true)
        {
            overridden.Thinking = fixedModel.Thinking.Value;
        }
        _logger?.Log(
            "Information",
            "CallCenter",
            $"operation=monomodel_override; agent={agent.Name}; role={agent.Role}; from_model={currentModelName}; to_model={fixedModelDescription}",
            result: "SUCCESS");
        return overridden;
    }

    private static Agent CloneAgent(Agent source)
    {
        return new Agent
        {
            Id = source.Id,
            Name = source.Name,
            Role = source.Role,
            ModelId = source.ModelId,
            ModelName = source.ModelName,
            VoiceId = source.VoiceId,
            Skills = source.Skills,
            Config = source.Config,
            JsonResponseFormat = source.JsonResponseFormat,
            UserPrompt = source.UserPrompt,
            SystemPrompt = source.SystemPrompt,
            ExecutionPlan = source.ExecutionPlan,
            IsActive = source.IsActive,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            Notes = source.Notes,
            Temperature = source.Temperature,
            TopP = source.TopP,
            RepeatPenalty = source.RepeatPenalty,
            TopK = source.TopK,
            RepeatLastN = source.RepeatLastN,
            NumPredict = source.NumPredict,
            Thinking = source.Thinking,
            MultiStepTemplateId = source.MultiStepTemplateId,
            SortOrder = source.SortOrder,
            AllowedProfiles = source.AllowedProfiles
        };
    }

    private static void AppendFailureForRetry(ChatHistory history, string reason)
    {
        AppendFailureToHistory(history, reason);
    }

    private static void AppendFailureToHistory(ChatHistory history, string reason)
    {
        if (history == null) return;

        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "errore_non_specificato" : reason.Trim();
        history.AddUser($"[CALLCENTER_LAST_ERRORS] {normalizedReason}");
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

    private static void UpsertRequestContextUserMessage(ChatHistory history, string? requestContext)
    {
        if (history == null || string.IsNullOrWhiteSpace(requestContext))
        {
            return;
        }

        const string prefix = "[CALLCENTER_REQUEST_CONTEXT]";
        var payload = $"{prefix} {requestContext.Trim()}";

        history.Messages.RemoveAll(m =>
            string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(m.Content) &&
            m.Content.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        var insertIndex = 0;
        if (history.Messages.Count > 0 &&
            string.Equals(history.Messages[0].Role, "system", StringComparison.OrdinalIgnoreCase))
        {
            insertIndex = 1;
        }

        history.Messages.Insert(insertIndex, new ConversationMessage
        {
            Role = "user",
            Content = payload
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

    private void MarkCheckedRequestFailure(Agent checkedAgent, string? failureReason)
    {
        var reason = string.IsNullOrWhiteSpace(failureReason) ? "validation_failed" : failureReason.Trim();
        if (_logger is CustomLogger concreteLogger && !string.IsNullOrWhiteSpace(checkedAgent?.Description))
        {
            concreteLogger.MarkLatestModelResponseResultForAgent(
                checkedAgent.Description!,
                "FAILED",
                reason,
                examined: true);
            return;
        }

        _logger?.MarkLatestModelResponseResult("FAILED", reason, examined: true);
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
                : (!string.IsNullOrWhiteSpace(checker.Agent.Description) ? checker.Agent.Description!.Trim() : $"{operationName}:agent_checker");

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
                    // IMPORTANT:
                    // The checker agent validates the primary response. Running response_checker again
                    // on the checker output can mis-attribute failures to the checker log row itself.
                    // Keep this disabled here so checker failures are propagated to the primary call.
                    UseResponseChecker = false,
                    AskFailExplanation = false,
                    AllowFallback = true,
                    PublishRetryProgressToCommandPanel = false
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
                    CheckerAgentName = checker.Agent.Description ?? "agent_checker",
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
            _monomodelModeOptions,
            _memoryEmbeddingOptions,
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

    private static void PublishRetryProgressToCommandPanel(CallOptions? options, int attemptsCurrentAgent)
    {
        if (options?.PublishRetryProgressToCommandPanel != true)
        {
            return;
        }

        var runId = LogScope.CurrentRunId;
        if (string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        var maxRetry = Math.Max(0, options?.MaxRetries ?? 0);
        var retryCount = Math.Max(0, attemptsCurrentAgent - 1);

        try
        {
            if (ServiceLocator.Services?.GetService<ICommandDispatcher>() is CommandDispatcher dispatcher)
            {
                dispatcher.UpdateCallCenterRetry(runId, retryCount, maxRetry);
                return;
            }

            ServiceLocator.Services?.GetService<ICommandDispatcher>()?.UpdateRetry(runId, retryCount);
        }
        catch
        {
            // best-effort
        }
    }

    private static bool IsNonRetriableVllmPromptContextError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("vLLM prompt troppo lungo per il contesto disponibile", StringComparison.OrdinalIgnoreCase)
               || value.Contains("maximum context length", StringComparison.OrdinalIgnoreCase)
               || value.Contains("\"param\":\"input_tokens\"", StringComparison.OrdinalIgnoreCase)
               || value.Contains("max_model_len", StringComparison.OrdinalIgnoreCase);
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
