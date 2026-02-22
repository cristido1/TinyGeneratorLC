using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
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

    public CallCenter(
        IAgentCallService agentCallService,
        DatabaseService database,
        IHubContext<StoryLiveHub>? hubContext = null,
        ICustomLogger? logger = null)
    {
        _agentCallService = agentCallService;
        _database = database;
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
        var maxAttemptsPerAgent = Math.Max(1, options.MaxRetries + 1);
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
                ? BuildSystemPrompt(currentAgent, roleCode)
                : options.SystemPromptOverride!;
            UpsertConversationSystemMessage(updatedHistory, systemPromptForCurrentCall);

            var result = await ExecuteWithCurrentAgentAsync(
                storyId,
                threadId,
                currentAgent,
                updatedHistory,
                systemPromptForCurrentCall,
                options,
                shouldStreamLive,
                liveGroup,
                cancellationToken).ConfigureAwait(false);

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
            if (attemptsCurrentAgent < maxAttemptsPerAgent)
            {
                continue;
            }

            if (options.AllowFallback)
            {
                var fallbackAgent = SelectNextFallbackAgent(currentAgent, usedModels, fallbackStats);
                if (fallbackAgent != null)
                {
                    // Requested behavior: when switching to fallback agent, do not append fail-explanation
                    // requests tied to the previous model failure.
                    currentAgent = fallbackAgent;
                    attemptsCurrentAgent = 0;
                    continue;
                }
            }

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

    private async Task<CommandModelExecutionService.Result> ExecuteWithCurrentAgentAsync(
        long storyId,
        int threadId,
        Agent agent,
        ChatHistory history,
        string systemPrompt,
        CallOptions options,
        bool enableStoryLiveStream,
        string? storyLiveGroup,
        CancellationToken cancellationToken)
    {
        var timeoutSec = Math.Max(1, (int)Math.Ceiling(options.Timeout.TotalSeconds));
        string? previousNormalizedResponse = null;
        var roleCode = string.IsNullOrWhiteSpace(agent.Role) ? "agent" : agent.Role;
        var (resolvedResponseFormat, jsonFormatCheck) = ResolveResponseFormatArtifacts(agent);
        var effectiveChecks = BuildEffectiveDeterministicChecks(options, jsonFormatCheck);

        var request = new CommandModelExecutionService.Request
        {
            CommandKey = string.IsNullOrWhiteSpace(options.Operation) ? "call_center" : options.Operation.Trim(),
            Agent = agent,
            RoleCode = roleCode,
            Prompt = BuildPromptFromHistory(history),
            SystemPrompt = systemPrompt,
            MaxAttempts = 1,
            StepTimeoutSec = timeoutSec,
            UseResponseChecker = options.UseResponseChecker,
            EnableFallback = false,
            DiagnoseOnFinalFailure = false,
            ExplainAfterAttempt = 0,
            RunId = $"callcenter_{Guid.NewGuid():N}",
            EnableDeterministicValidation = true,
            ResponseFormat = resolvedResponseFormat ?? options.ResponseFormat,
            DeterministicValidator = output =>
            {
                var validation = ExecuteDeterministicChecks(output, effectiveChecks, previousNormalizedResponse);
                if (validation.IsValid)
                {
                    previousNormalizedResponse = NormalizeForComparison(output);
                }

                return validation;
            },
            EnableStreamingOutput = enableStoryLiveStream && !string.IsNullOrWhiteSpace(storyLiveGroup),
            StreamChunkCallback = enableStoryLiveStream && !string.IsNullOrWhiteSpace(storyLiveGroup)
                ? (chunk => PublishStoryLiveChunkAsync(storyLiveGroup!, storyId, chunk))
                : null,
            AttemptFailureCallback = async (failure, token) =>
            {
                var role = string.IsNullOrWhiteSpace(agent.Role) ? failure.RoleCode : agent.Role!;
                if (!IsCancellationReason(failure.Reason))
                {
                    var effectiveFailureModelId = ResolveEffectiveModelId(agent.ModelId, failure.ModelName);
                    _database.RecordModelRoleUsage(role, effectiveFailureModelId, failure.ModelName, success: false);

                    var errorTexts = BuildTrackedErrorTexts(failure);
                    var errorType = ResolveErrorType(failure);
                    var modelRoleId = _database.ResolveOrCreateModelRoleId(effectiveFailureModelId, failure.ModelName, role);
                    if (modelRoleId.HasValue && modelRoleId.Value > 0)
                    {
                        foreach (var errorText in errorTexts)
                        {
                            _database.UpsertModelRoleError(modelRoleId.Value, errorText, errorType);
                        }
                    }
                }
                await Task.CompletedTask;
            }
        };

        _logger?.Log(
            "Information",
            "StoryLive",
            $"story_live request setup: story_id={storyId}; role={request.RoleCode}; enable_stream={request.EnableStreamingOutput}; group={storyLiveGroup ?? "(none)"}; agent={agent.Name}; model={ResolveModelName(agent) ?? "unknown"}",
            result: "SUCCESS");
        var exec = await _agentCallService.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

        return exec;
    }

    private static CommandModelExecutionService.DeterministicValidationResult ExecuteDeterministicChecks(
        string output,
        IReadOnlyList<IDeterministicCheck> checks,
        string? previousNormalizedResponse)
    {
        var text = output ?? string.Empty;

        var nonEmptyCheck = new NonEmptyResponseCheck();
        var nonEmptyResult = nonEmptyCheck.Execute(text);
        if (!nonEmptyResult.Successed)
        {
            var reason = BuildDeterministicFailureReason(nonEmptyCheck, nonEmptyResult);
            return new CommandModelExecutionService.DeterministicValidationResult(false, reason);
        }

        var normalizedCurrent = NormalizeForComparison(text);
        if (!string.IsNullOrWhiteSpace(previousNormalizedResponse) &&
            string.Equals(previousNormalizedResponse, normalizedCurrent, StringComparison.Ordinal))
        {
            return new CommandModelExecutionService.DeterministicValidationResult(
                false,
                "CallCenterDuplicateResponseCheck: risposta identica al tentativo precedente");
        }

        foreach (var check in checks)
        {
            if (check == null)
            {
                continue;
            }

            var result = check.Execute(text);
            if (!result.Successed)
            {
                var reason = BuildDeterministicFailureReason(check, result);
                return new CommandModelExecutionService.DeterministicValidationResult(false, reason);
            }
        }

        return new CommandModelExecutionService.DeterministicValidationResult(true, null);
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

    private static bool IsCancellationReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return false;
        var r = reason.Trim();
        return r.Contains("annull", StringComparison.OrdinalIgnoreCase)
               || r.Contains("cancel", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveErrorType(CommandModelExecutionService.AttemptFailure failure)
    {
        if (failure == null) return "exception";
        if (failure.IsChecker) return "checker";
        if (failure.IsDeterministic) return "deterministic";
        if (!string.IsNullOrWhiteSpace(failure.Reason) &&
            failure.Reason.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "timeout";
        }

        return "exception";
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

    private Agent? SelectNextFallbackAgent(
        Agent currentAgent,
        HashSet<string> usedModels,
        IReadOnlyDictionary<string, (double successRate, double tokensPerSec)> stats)
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

        return candidates
            .OrderByDescending(c => stats.TryGetValue(c.Model, out var m) ? m.successRate : 0.0)
            .ThenByDescending(c => stats.TryGetValue(c.Model, out var m) ? m.tokensPerSec : 0.0)
            .Select(c => c.Agent)
            .FirstOrDefault();
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

    private string BuildSystemPrompt(Agent agent, string? roleCode)
    {
        var basePrompt = !string.IsNullOrWhiteSpace(agent.Instructions)
            ? agent.Instructions.Trim()
            : !string.IsNullOrWhiteSpace(agent.Prompt)
                ? agent.Prompt.Trim()
                : "Rispondi in modo utile e coerente con la richiesta.";

        var modelName = ResolveModelName(agent);
        var errors = _database.ListTopModelRoleErrors(agent.ModelId, modelName, roleCode, 10);
        if (errors.Count == 0)
        {
            return basePrompt;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(basePrompt);
        sb.AppendLine();
        sb.AppendLine("IN PASSATO HAI COMMESSO QUESTI ERRORI, NON RIPETERLI:");
        foreach (var err in errors)
        {
            var count = Math.Max(0, err.ErrorCount);
            var emphasis = count > 0 ? new string('!', count / 5) : string.Empty;
            sb.AppendLine($"- {err.ErrorText} (l'hai commesso {count} volte{emphasis})");
        }

        return sb.ToString().TrimEnd();
    }

    private static List<string> BuildTrackedErrorTexts(CommandModelExecutionService.AttemptFailure failure)
    {
        if (failure.IsChecker)
        {
            var ruleRows = new List<string>();

            if (failure.ViolatedRuleDetails != null && failure.ViolatedRuleDetails.Count > 0)
            {
                foreach (var ruleDetail in failure.ViolatedRuleDetails)
                {
                    if (!string.IsNullOrWhiteSpace(ruleDetail))
                    {
                        ruleRows.Add(ruleDetail.Trim());
                    }
                }
            }

            var rulesByIdFromSystem = ExtractRuleRowsFromSystemMessage(failure.SystemPromptSent);
            var ruleIds = failure.ViolatedRules != null && failure.ViolatedRules.Count > 0
                ? failure.ViolatedRules
                    .Where(r => r > 0)
                    .Distinct()
                    .OrderBy(r => r)
                    .ToList()
                : ExtractRuleIdsFromReason(failure.Reason);

            foreach (var ruleId in ruleIds)
            {
                if (rulesByIdFromSystem.TryGetValue(ruleId, out var row) && !string.IsNullOrWhiteSpace(row))
                {
                    ruleRows.Add(row.Trim());
                }
                else
                {
                    ruleRows.Add($"rules:{ruleId}");
                }
            }

            var normalized = ruleRows
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (normalized.Count > 0)
            {
                return normalized;
            }

            return new List<string> { "rules:unknown" };
        }

        return new List<string> { BuildTrackedErrorTextSingle(failure) };
    }

    private static string BuildTrackedErrorTextSingle(CommandModelExecutionService.AttemptFailure failure)
    {
        if (failure.IsDeterministic)
        {
            var generic = ExtractGenericDeterministicDescription(failure.Reason);
            if (!string.IsNullOrWhiteSpace(generic))
            {
                return generic;
            }
        }

        return string.IsNullOrWhiteSpace(failure.Reason) ? "unknown_error" : failure.Reason.Trim();
    }

    private static Dictionary<int, string> ExtractRuleRowsFromSystemMessage(string? systemMessage)
    {
        var result = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(systemMessage))
        {
            return result;
        }

        var matches = System.Text.RegularExpressions.Regex.Matches(
            systemMessage,
            @"REGOLA\s+(\d+)\s*:\s*(.+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (!match.Success || match.Groups.Count < 3)
            {
                continue;
            }

            if (!int.TryParse(match.Groups[1].Value, out var ruleId) || ruleId <= 0)
            {
                continue;
            }

            var row = $"REGOLA {ruleId}: {match.Groups[2].Value.Trim()}";
            if (!result.ContainsKey(ruleId))
            {
                result[ruleId] = row;
            }
        }

        return result;
    }

    private static List<int> ExtractRuleIdsFromReason(string? reason)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(reason))
        {
            return result;
        }

        var matches = System.Text.RegularExpressions.Regex.Matches(reason, @"\d+");
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (int.TryParse(match.Value, out var n) && n > 0 && !result.Contains(n))
            {
                result.Add(n);
            }
        }

        result.Sort();
        return result;
    }

    private static string? ExtractGenericDeterministicDescription(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        const string marker = "GENERIC_ERROR:";
        var idx = reason.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var tail = reason[(idx + marker.Length)..].Trim();
        var detailIdx = tail.IndexOf("|", StringComparison.Ordinal);
        if (detailIdx >= 0)
        {
            tail = tail[..detailIdx].Trim();
        }

        return string.IsNullOrWhiteSpace(tail) ? null : tail;
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

    private sealed class JsonSchemaResponseFormatCheck : IDeterministicCheck
    {
        private readonly string _schemaJson;
        private readonly string _schemaName;

        public JsonSchemaResponseFormatCheck(string schemaJson, string schemaName)
        {
            _schemaJson = schemaJson ?? string.Empty;
            _schemaName = string.IsNullOrWhiteSpace(schemaName) ? "schema.json" : schemaName;
        }

        public string Rule => $"La risposta deve rispettare il JSON schema '{_schemaName}'.";
        public string GenericErrorDescription => "Formato di risposta JSON non rispettato";
        public Microsoft.Extensions.Options.IOptions<object>? Options { get; set; }

        public IDeterministicResult Execute(string textToCheck)
        {
            var started = DateTime.UtcNow;
            try
            {
                var raw = (textToCheck ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return Fail("Risposta vuota: atteso JSON conforme allo schema.");
                }

                using var schemaDoc = JsonDocument.Parse(_schemaJson);
                using var responseDoc = JsonDocument.Parse(raw);

                var errors = new List<string>();
                ValidateAgainstSchema(
                    responseDoc.RootElement,
                    schemaDoc.RootElement,
                    "$",
                    errors,
                    depth: 0);

                if (errors.Count == 0)
                {
                    return new DeterministicResult
                    {
                        Successed = true,
                        Message = "ok",
                        CheckDurationMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds)
                    };
                }

                return Fail(string.Join(" | ", errors));
            }
            catch (JsonException ex)
            {
                return Fail($"Risposta non JSON valida: {ex.Message}");
            }
            catch (Exception ex)
            {
                return Fail($"Errore validazione JSON schema: {ex.Message}");
            }

            DeterministicResult Fail(string message) => new()
            {
                Successed = false,
                Message = $"json_response_format_check: non ha rispettato il formato di risposta JSON richiesto ({_schemaName}): {message}",
                CheckDurationMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds)
            };
        }

        private static void ValidateAgainstSchema(
            JsonElement data,
            JsonElement schema,
            string path,
            List<string> errors,
            int depth)
        {
            if (depth > 64)
            {
                errors.Add($"{path}: profondita schema eccessiva");
                return;
            }

            var schemaType = GetSchemaType(schema);
            if (!IsTypeCompatible(data, schemaType))
            {
                errors.Add($"{path}: tipo atteso '{schemaType}', ricevuto '{data.ValueKind}'");
                return;
            }

            if (string.Equals(schemaType, "object", StringComparison.OrdinalIgnoreCase))
            {
                ValidateObject(data, schema, path, errors, depth + 1);
                return;
            }

            if (string.Equals(schemaType, "array", StringComparison.OrdinalIgnoreCase))
            {
                ValidateArray(data, schema, path, errors, depth + 1);
            }
        }

        private static void ValidateObject(
            JsonElement data,
            JsonElement schema,
            string path,
            List<string> errors,
            int depth)
        {
            if (TryGetPropertyIgnoreCase(schema, "required", out var required) &&
                required.ValueKind == JsonValueKind.Array)
            {
                foreach (var req in required.EnumerateArray())
                {
                    if (req.ValueKind != JsonValueKind.String) continue;
                    var propName = req.GetString();
                    if (string.IsNullOrWhiteSpace(propName)) continue;
                    if (!TryGetPropertyIgnoreCase(data, propName!, out _))
                    {
                        errors.Add($"{path}: manca campo obbligatorio '{propName}'");
                    }
                }
            }

            if (!TryGetPropertyIgnoreCase(schema, "properties", out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var propSchema in properties.EnumerateObject())
            {
                if (!TryGetPropertyIgnoreCase(data, propSchema.Name, out var child))
                {
                    continue;
                }

                ValidateAgainstSchema(
                    child,
                    propSchema.Value,
                    $"{path}.{propSchema.Name}",
                    errors,
                    depth + 1);
            }
        }

        private static void ValidateArray(
            JsonElement data,
            JsonElement schema,
            string path,
            List<string> errors,
            int depth)
        {
            if (!TryGetPropertyIgnoreCase(schema, "items", out var itemsSchema))
            {
                return;
            }

            var index = 0;
            foreach (var item in data.EnumerateArray())
            {
                ValidateAgainstSchema(item, itemsSchema, $"{path}[{index}]", errors, depth + 1);
                index++;
            }
        }

        private static string GetSchemaType(JsonElement schema)
        {
            if (TryGetPropertyIgnoreCase(schema, "type", out var t))
            {
                if (t.ValueKind == JsonValueKind.String)
                {
                    return t.GetString() ?? string.Empty;
                }

                if (t.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in t.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            var candidate = item.GetString();
                            if (!string.IsNullOrWhiteSpace(candidate) &&
                                !string.Equals(candidate, "null", StringComparison.OrdinalIgnoreCase))
                            {
                                return candidate!;
                            }
                        }
                    }
                }
            }

            if (TryGetPropertyIgnoreCase(schema, "properties", out _))
            {
                return "object";
            }

            if (TryGetPropertyIgnoreCase(schema, "items", out _))
            {
                return "array";
            }

            return string.Empty;
        }

        private static bool IsTypeCompatible(JsonElement data, string schemaType)
        {
            if (string.IsNullOrWhiteSpace(schemaType))
            {
                return true;
            }

            return schemaType.ToLowerInvariant() switch
            {
                "object" => data.ValueKind == JsonValueKind.Object,
                "array" => data.ValueKind == JsonValueKind.Array,
                "string" => data.ValueKind == JsonValueKind.String,
                "number" => data.ValueKind == JsonValueKind.Number,
                "integer" => data.ValueKind == JsonValueKind.Number && data.TryGetInt64(out _),
                "boolean" => data.ValueKind == JsonValueKind.True || data.ValueKind == JsonValueKind.False,
                "null" => data.ValueKind == JsonValueKind.Null,
                _ => true
            };
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty(propertyName, out value))
                {
                    return true;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
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
