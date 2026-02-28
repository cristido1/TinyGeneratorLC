using System.Text.Json;
using Microsoft.Extensions.Options;
using TinyGenerator.Models;

namespace TinyGenerator.Services;

public sealed class DefaultDeterministicValidator : IDeterministicValidator
{
    public DeterministicValidatorResult Validate(AgentExecutionContext context)
    {
        var output = context?.OutputText ?? string.Empty;
        var checks = context?.DeterministicChecks ?? Array.Empty<IDeterministicCheck>();
        var previousNormalizedResponse = context?.PreviousNormalizedResponse;

        var nonEmptyCheck = new NonEmptyResponseCheck();
        var nonEmptyResult = nonEmptyCheck.Execute(output);
        if (!nonEmptyResult.Successed)
        {
            var reason = BuildDeterministicFailureReason(nonEmptyCheck, nonEmptyResult);
            return new DeterministicValidatorResult
            {
                IsValid = false,
                FailureReason = reason,
                Violations = new List<string> { reason }
            };
        }

        var normalizedCurrent = NormalizeForComparison(output);
        if (!string.IsNullOrWhiteSpace(previousNormalizedResponse) &&
            string.Equals(previousNormalizedResponse, normalizedCurrent, StringComparison.Ordinal))
        {
            var reason = "CallCenterDuplicateResponseCheck: risposta identica al tentativo precedente";
            return new DeterministicValidatorResult
            {
                IsValid = false,
                FailureReason = reason,
                Violations = new List<string> { reason }
            };
        }

        foreach (var check in checks)
        {
            if (check == null) continue;
            var result = check.Execute(output);
            if (!result.Successed)
            {
                var reason = BuildDeterministicFailureReason(check, result);
                return new DeterministicValidatorResult
                {
                    IsValid = false,
                    FailureReason = reason,
                    Violations = new List<string> { reason }
                };
            }
        }

        return new DeterministicValidatorResult { IsValid = true };
    }

    private static string NormalizeForComparison(string? text) => (text ?? string.Empty).Trim();

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
}

public sealed class DefaultResponseValidator : IResponseValidator
{
    public ResponseValidatorResult Validate(AgentExecutionContext context)
    {
        // Il response checker resta eseguito nel CommandModelExecutionService (via request.UseResponseChecker),
        // qui manteniamo un adapter esplicito per il coordinamento del CallCenter senza alterare il comportamento.
        var checkerEnabled = context?.Options?.UseResponseChecker ?? true;
        var success = context?.ExecutionResult?.Success ?? false;
        return new ResponseValidatorResult
        {
            IsValid = true,
            Status = checkerEnabled ? (success ? "delegated_pass" : "delegated_fail_or_skipped") : "skipped"
        };
    }
}

public sealed class DefaultRetryPolicy : IRetryPolicy
{
    private readonly DatabaseService _database;
    private readonly IOptionsMonitor<ResponseValidationOptions>? _responseValidationOptions;

    public DefaultRetryPolicy(
        DatabaseService database,
        IOptionsMonitor<ResponseValidationOptions>? responseValidationOptions = null)
    {
        _database = database;
        _responseValidationOptions = responseValidationOptions;
    }

    public RetryDecision Evaluate(RetryContext context)
    {
        var maxAttemptsPerAgent = Math.Max(1, (context?.Options?.MaxRetries ?? 0) + 1);
        if (context != null && context.AttemptsCurrentAgent < maxAttemptsPerAgent)
        {
            return new RetryDecision
            {
                Kind = RetryDecisionKind.RetrySameAgent
            };
        }

        if (context != null &&
            context.Options?.AllowFallback == true &&
            IsAgentFallbackEnabledForOperation(context.Options.Operation))
        {
            var fallbackAgent = SelectNextFallbackAgent(
                context.CurrentAgent,
                context.UsedModels,
                context.FallbackStats);
            if (fallbackAgent != null)
            {
                return new RetryDecision
                {
                    Kind = RetryDecisionKind.FallbackAgent,
                    FallbackAgent = fallbackAgent
                };
            }
        }

        return new RetryDecision
        {
            Kind = RetryDecisionKind.Stop,
            ShouldAskFailureExplanation = context?.Options?.AskFailExplanation ?? false
        };
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

    private bool IsAgentFallbackEnabledForOperation(string? operation)
    {
        try
        {
            var op = string.IsNullOrWhiteSpace(operation) ? "call_center" : operation.Trim();
            var policies = _responseValidationOptions?.CurrentValue?.CommandPolicies;
            if (policies == null || policies.Count == 0)
            {
                return true;
            }

            if (TryGetResponseValidationPolicyForOperation(policies, op, out var policy) &&
                policy?.EnableAgentFallback.HasValue == true)
            {
                return policy.EnableAgentFallback.Value;
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
}

public sealed class DefaultAgentExecutor : IAgentExecutor
{
    private readonly IAgentCallService _agentCallService;
    private readonly DatabaseService _database;
    private readonly ICustomLogger? _logger;
    private readonly IOptionsMonitor<ResponseValidationOptions>? _responseValidationOptions;

    public DefaultAgentExecutor(
        IAgentCallService agentCallService,
        DatabaseService database,
        IOptionsMonitor<ResponseValidationOptions>? responseValidationOptions = null,
        ICustomLogger? logger = null)
    {
        _agentCallService = agentCallService;
        _database = database;
        _responseValidationOptions = responseValidationOptions;
        _logger = logger;
    }

    public async Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken ct)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (request.Agent == null) throw new ArgumentNullException(nameof(request.Agent));

        var timeoutSec = Math.Max(1, (int)Math.Ceiling((request.Options?.Timeout ?? TimeSpan.FromSeconds(180)).TotalSeconds));
        var roleCode = string.IsNullOrWhiteSpace(request.Agent.Role) ? "agent" : request.Agent.Role;

        var innerRequest = new CommandModelExecutionService.Request
        {
            CommandKey = string.IsNullOrWhiteSpace(request.Options?.Operation) ? "call_center" : request.Options.Operation.Trim(),
            Agent = request.Agent,
            RoleCode = roleCode,
            Prompt = BuildPromptFromHistory(request.History),
            SystemPrompt = request.SystemPrompt,
            MaxAttempts = 1,
            StepTimeoutSec = timeoutSec,
            UseResponseChecker = request.Options?.UseResponseChecker ?? true,
            EnableFallback = (request.Options?.AllowFallback ?? true) && IsModelFallbackEnabledByConfig(),
            DiagnoseOnFinalFailure = false,
            ExplainAfterAttempt = 0,
            RunId = $"callcenter_{Guid.NewGuid():N}",
            EnableDeterministicValidation = true,
            ResponseFormat = request.ResponseFormat,
            DeterministicValidator = request.DeterministicValidatorCallback,
            EnableStreamingOutput = request.EnableStoryLiveStream && !string.IsNullOrWhiteSpace(request.StoryLiveGroup),
            StreamChunkCallback = request.EnableStoryLiveStream && !string.IsNullOrWhiteSpace(request.StoryLiveGroup)
                ? request.StreamChunkCallback
                : null,
            AttemptFailureCallback = async (failure, token) =>
            {
                var role = string.IsNullOrWhiteSpace(request.Agent.Role) ? failure.RoleCode : request.Agent.Role!;
                if (!IsCancellationReason(failure.Reason))
                {
                    var effectiveFailureModelId = ResolveEffectiveModelId(request.Agent.ModelId, failure.ModelName);
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
            $"story_live request setup: story_id={request.StoryId}; role={innerRequest.RoleCode}; enable_stream={innerRequest.EnableStreamingOutput}; group={request.StoryLiveGroup ?? "(none)"}; agent={request.Agent.Name}; model={ResolveModelName(request.Agent) ?? "unknown"}",
            result: "SUCCESS");

        var exec = await _agentCallService.ExecuteAsync(innerRequest, ct).ConfigureAwait(false);
        return new AgentExecutionResult
        {
            Success = exec.Success,
            Text = exec.Text,
            Error = exec.Error,
            ModelName = exec.ModelName,
            UsedFallback = exec.UsedFallback,
            DeterministicFailure = exec.DeterministicFailure,
            AttemptsUsed = exec.AttemptsUsed
        };
    }

    private bool IsModelFallbackEnabledByConfig()
    {
        try
        {
            return _responseValidationOptions?.CurrentValue?.EnableFallback ?? true;
        }
        catch
        {
            return true;
        }
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
            if (!match.Success || match.Groups.Count < 3) continue;
            if (!int.TryParse(match.Groups[1].Value, out var ruleId) || ruleId <= 0) continue;
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
}

