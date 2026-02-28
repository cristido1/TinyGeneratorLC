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
                    previousNormalizedResponse = NormalizeForComparison(output);
                }

                return new CommandModelExecutionService.DeterministicValidationResult(validation.IsValid, validation.FailureReason);
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
                var effectiveSuccessModelId = ResolveEffectiveModelId(currentAgent.ModelId, result.ModelName);
                _database.RecordModelRoleUsage(currentAgent.Role, effectiveSuccessModelId, result.ModelName, success: true);
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
                    Duration = stopwatch.Elapsed
                };
            }

            lastFailure = string.IsNullOrWhiteSpace(result.Error) ? "agent_call_failed" : result.Error;
            AppendFailureToHistory(updatedHistory, lastFailure);
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
                // Requested behavior: when switching to fallback agent, do not append fail-explanation
                // requests tied to the previous model failure.
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

        if (jsonFormatCheck != null)
        {
            checks.Add(jsonFormatCheck);
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
                Prompt = BuildPromptFromHistory(history),
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
        if (!string.IsNullOrWhiteSpace(agent.ModelName))
        {
            return agent.ModelName.Trim();
        }

        if (agent.ModelId.HasValue && agent.ModelId.Value > 0)
        {
            var byId = _database.GetModelInfoById(agent.ModelId.Value)?.Name;
            if (!string.IsNullOrWhiteSpace(byId))
            {
                return byId.Trim();
            }
        }

        return null;
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
