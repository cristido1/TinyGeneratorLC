using TinyGenerator.Models;

namespace TinyGenerator.Services;

public sealed class CallCenter : ICallCenter
{
    private readonly IAgentCallService _agentCallService;
    private readonly DatabaseService _database;
    private readonly ICustomLogger? _logger;

    public CallCenter(
        IAgentCallService agentCallService,
        DatabaseService database,
        ICustomLogger? logger = null)
    {
        _agentCallService = agentCallService;
        _database = database;
        _logger = logger;
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

        var checksConfigError = ValidateDeterministicChecksConfiguration(options);
        if (!string.IsNullOrWhiteSpace(checksConfigError))
        {
            _logger?.Log(
                "Warning",
                "CallCenter",
                $"operation={options.Operation}; status=FAILED; story_id={storyId}; thread_id={threadId}; agent={agent.Name}; reason={checksConfigError}",
                state: "deterministic_configuration",
                result: "FAILED");

            return new CallCenterResult
            {
                Success = false,
                UpdatedHistory = new ChatHistory(history.Messages),
                Attempts = 0,
                ModelUsed = ResolveModelName(agent) ?? string.Empty,
                Duration = TimeSpan.Zero,
                FailureReason = checksConfigError
            };
        }

        var started = DateTime.UtcNow;
        var updatedHistory = new ChatHistory(history.Messages);
        var usedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var attempts = 0;
        string? lastFailure = null;
        Agent currentAgent = agent;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var modelName = ResolveModelName(currentAgent);
            if (!string.IsNullOrWhiteSpace(modelName))
            {
                usedModels.Add(modelName);
            }

            using var scope = LogScope.Push(
                string.IsNullOrWhiteSpace(options.Operation) ? "call_center" : options.Operation.Trim(),
                operationId: null,
                stepNumber: null,
                maxStep: null,
                agentName: currentAgent.Name,
                agentRole: currentAgent.Role,
                threadId: threadId > 0 ? threadId : null,
                storyId: storyId > 0 ? storyId : null);

            var result = await ExecuteWithCurrentAgentAsync(
                storyId,
                threadId,
                currentAgent,
                updatedHistory,
                options,
                cancellationToken).ConfigureAwait(false);

            attempts += Math.Max(1, result.AttemptsUsed);
            if (result.Success && !string.IsNullOrWhiteSpace(result.Text))
            {
                updatedHistory.AddAssistant(result.Text.Trim());
                _logger?.Log(
                    "Information",
                    "CallCenter",
                    $"operation={options.Operation}; status=SUCCESS; story_id={storyId}; thread_id={threadId}; agent={currentAgent.Name}; model={result.ModelName ?? modelName}; attempts={attempts}",
                    result: "SUCCESS");

                return new CallCenterResult
                {
                    Success = true,
                    ResponseText = result.Text.Trim(),
                    UpdatedHistory = updatedHistory,
                    Attempts = attempts,
                    ModelUsed = result.ModelName ?? modelName ?? string.Empty,
                    Duration = DateTime.UtcNow - started
                };
            }

            lastFailure = string.IsNullOrWhiteSpace(result.Error) ? "agent_call_failed" : result.Error;
            AppendFailureToHistory(updatedHistory, lastFailure);
            _logger?.Log(
                "Warning",
                "CallCenter",
                $"operation={options.Operation}; status=FAILED; story_id={storyId}; thread_id={threadId}; agent={currentAgent.Name}; model={result.ModelName ?? modelName}; reason={lastFailure}",
                state: result.DeterministicFailure ? "deterministic_validation" : "agent_call",
                result: "FAILED");

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

            if (!options.AllowFallback)
            {
                return new CallCenterResult
                {
                    Success = false,
                    UpdatedHistory = updatedHistory,
                    Attempts = attempts,
                    ModelUsed = result.ModelName ?? modelName ?? string.Empty,
                    Duration = DateTime.UtcNow - started,
                    FailureReason = lastFailure
                };
            }

            var fallbackAgent = SelectNextFallbackAgent(currentAgent, usedModels);
            if (fallbackAgent == null)
            {
                return new CallCenterResult
                {
                    Success = false,
                    UpdatedHistory = updatedHistory,
                    Attempts = attempts,
                    ModelUsed = result.ModelName ?? modelName ?? string.Empty,
                    Duration = DateTime.UtcNow - started,
                    FailureReason = lastFailure
                };
            }

            currentAgent = fallbackAgent;
        }
    }

    private async Task<CommandModelExecutionService.Result> ExecuteWithCurrentAgentAsync(
        long storyId,
        int threadId,
        Agent agent,
        ChatHistory history,
        CallOptions options,
        CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, options.MaxRetries + 1);
        var timeoutSec = Math.Max(1, (int)Math.Ceiling(options.Timeout.TotalSeconds));

        var request = new CommandModelExecutionService.Request
        {
            CommandKey = string.IsNullOrWhiteSpace(options.Operation) ? "call_center" : options.Operation.Trim(),
            Agent = agent,
            RoleCode = string.IsNullOrWhiteSpace(agent.Role) ? "agent" : agent.Role,
            Prompt = BuildPromptFromHistory(history),
            SystemPrompt = string.IsNullOrWhiteSpace(options.SystemPromptOverride)
                ? BuildSystemPrompt(agent)
                : options.SystemPromptOverride,
            MaxAttempts = maxAttempts,
            StepTimeoutSec = timeoutSec,
            UseResponseChecker = options.UseResponseChecker,
            EnableFallback = false,
            DiagnoseOnFinalFailure = options.AskFailExplanation,
            ExplainAfterAttempt = options.AskFailExplanation ? Math.Max(1, maxAttempts - 1) : 0,
            RunId = $"callcenter_{storyId}_{threadId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}",
            EnableDeterministicValidation = true,
            DeterministicValidator = output => ExecuteDeterministicChecks(output, options)
        };

        return await _agentCallService.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static CommandModelExecutionService.DeterministicValidationResult ExecuteDeterministicChecks(string output, CallOptions options)
    {
        var checks = options.DeterministicChecks;

        foreach (var check in checks)
        {
            check.TextToCheck = output ?? string.Empty;
            var result = check.Execute();
            if (!result.Successed)
            {
                var reason = string.IsNullOrWhiteSpace(result.Message)
                    ? $"Deterministic check failed: {check.Rule}"
                    : result.Message;
                return new CommandModelExecutionService.DeterministicValidationResult(false, reason);
            }
        }

        return new CommandModelExecutionService.DeterministicValidationResult(true, null);
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
                RunId = $"fail_explanation_{storyId}_{threadId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}",
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

    private Agent? SelectNextFallbackAgent(Agent currentAgent, HashSet<string> usedModels)
    {
        var candidates = _database.ListAgents()
            .Where(a =>
                a.IsActive &&
                a.Id != currentAgent.Id &&
                string.Equals(a.Role, currentAgent.Role, StringComparison.OrdinalIgnoreCase))
            .Select(a => new { Agent = a, Model = ResolveModelName(a) ?? string.Empty })
            .Where(x => !string.IsNullOrWhiteSpace(x.Model) && !usedModels.Contains(x.Model))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        var stats = _database.GetModelStats()
            .GroupBy(s => s.ModelName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
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

        return candidates
            .OrderByDescending(c => stats.TryGetValue(c.Model, out var m) ? m.successRate : 0.0)
            .ThenByDescending(c => stats.TryGetValue(c.Model, out var m) ? m.tokensPerSec : 0.0)
            .Select(c => c.Agent)
            .FirstOrDefault();
    }

    private static string BuildPromptFromHistory(ChatHistory history)
    {
        var messages = history.Messages
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .ToList();

        if (messages.Count == 0)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var message in messages)
        {
            var role = string.IsNullOrWhiteSpace(message.Role) ? "user" : message.Role.Trim().ToLowerInvariant();
            sb.Append('[').Append(role).Append("] ").AppendLine(message.Content!.Trim());
        }

        return sb.ToString().Trim();
    }

    private static string BuildSystemPrompt(Agent agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.Instructions))
        {
            return agent.Instructions.Trim();
        }

        if (!string.IsNullOrWhiteSpace(agent.Prompt))
        {
            return agent.Prompt.Trim();
        }

        return "Rispondi in modo utile e coerente con la richiesta.";
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
        history.AddUser($"[CALLCENTER_LAST_FAILURE] Tentativo fallito: {reason}");
    }

    private static string? ValidateDeterministicChecksConfiguration(CallOptions options)
    {
        if (options.DeterministicChecks.Count == 0)
        {
            return "Configurazione non valida: passare almeno un deterministic check al CallCenter.";
        }

        var hasMandatoryEmptyCheck = options.DeterministicChecks.Any(c =>
            c is CheckEmpty ||
            c is NonEmptyResponseCheck);

        if (!hasMandatoryEmptyCheck)
        {
            return "Configurazione non valida: manca il check obbligatorio di risposta non vuota (CheckEmpty).";
        }

        return null;
    }
}
