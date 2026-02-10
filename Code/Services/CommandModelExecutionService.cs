using System.Text;
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
        public Func<string, DeterministicValidationResult>? DeterministicValidator { get; set; }
        public Func<string, string, string>? RetryPromptFactory { get; set; }
    }

    public sealed class Result
    {
        public bool Success { get; init; }
        public string? Text { get; init; }
        public string? Error { get; init; }
        public string? ModelName { get; init; }
        public bool UsedFallback { get; init; }
    }

    private readonly ILangChainKernelFactory _kernelFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DatabaseService _database;
    private readonly ICustomLogger? _logger;
    private readonly IOptions<ResponseValidationOptions>? _responseValidationOptions;
    private readonly IOptions<CommandPoliciesOptions>? _commandPoliciesOptions;

    private sealed record ExecutionSettings(
        int MaxAttempts,
        int RetryDelaySeconds,
        int StepTimeoutSec,
        bool UseResponseChecker,
        bool EnableFallback,
        bool DiagnoseOnFinalFailure,
        int ExplainAfterAttempt,
        int CheckerTimeoutSec,
        bool EnableDeterministicValidation);

    public CommandModelExecutionService(
        ILangChainKernelFactory kernelFactory,
        IServiceScopeFactory scopeFactory,
        DatabaseService database,
        IOptions<CommandPoliciesOptions>? commandPoliciesOptions = null,
        IOptions<ResponseValidationOptions>? responseValidationOptions = null,
        ICustomLogger? logger = null)
    {
        _kernelFactory = kernelFactory;
        _scopeFactory = scopeFactory;
        _database = database;
        _commandPoliciesOptions = commandPoliciesOptions;
        _responseValidationOptions = responseValidationOptions;
        _logger = logger;
    }

    public async Task<Result> ExecuteAsync(Request request, CancellationToken ct = default)
    {
        if (request.Agent == null) throw new ArgumentNullException(nameof(request.Agent));
        var modelName = ResolveModelName(request.Agent);
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return new Result { Success = false, Error = $"Agente {request.Agent.Name} senza modello configurato" };
        }

        var settings = ResolveSettings(request);
        var primary = await ExecuteOnModelAsync(request, settings, modelName, request.RoleCode, ct).ConfigureAwait(false);
        if (primary.Success)
        {
            return new Result
            {
                Success = true,
                Text = primary.Text,
                Error = primary.Error,
                ModelName = modelName,
                UsedFallback = false
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
            UsedFallback = primary.UsedFallback
        };
    }

    private async Task<Result> ExecuteOnModelAsync(Request request, ExecutionSettings settings, string modelName, string roleCode, CancellationToken ct)
    {
        var maxAttempts = settings.MaxAttempts;
        var delayMs = Math.Max(0, settings.RetryDelaySeconds) * 1000;
        var currentPrompt = request.Prompt ?? string.Empty;
        var lastError = "Risposta non valida";
        var lastOutput = string.Empty;
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
                text = await CallModelTextAsync(
                    modelName,
                    request.Agent,
                    roleCode,
                    request.SystemPrompt ?? BuildSystemPrompt(request.Agent),
                    currentPrompt,
                    skipResponseChecker: true,
                    attemptToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && settings.StepTimeoutSec > 0 && attemptCts.IsCancellationRequested)
            {
                lastError = $"Timeout fase dopo {settings.StepTimeoutSec}s";
                _logger?.Append(request.RunId ?? string.Empty, $"[{roleCode}] Tentativo {attempt} fallito: {lastError}");
                MarkLatestAttemptResult(modelName, request.Agent, "FAILED", lastError, examined: true);
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
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                _logger?.Append(request.RunId ?? string.Empty, $"[{roleCode}] Tentativo {attempt} eccezione: {lastError}");
                MarkLatestAttemptResult(modelName, request.Agent, "FAILED", lastError, examined: true);
                if (!explained && settings.ExplainAfterAttempt > 0 && attempt >= settings.ExplainAfterAttempt)
                {
                    await DiagnoseAsync(modelName, request, lastError, lastOutput, CancellationToken.None).ConfigureAwait(false);
                    explained = true;
                }
                if (attempt < maxAttempts && delayMs > 0) await Task.Delay(delayMs, ct).ConfigureAwait(false);
                continue;
            }

            lastOutput = text;
            var deterministic = settings.EnableDeterministicValidation
                ? request.DeterministicValidator?.Invoke(text) ?? new DeterministicValidationResult(true, null)
                : new DeterministicValidationResult(true, null);

            if (!deterministic.IsValid)
            {
                lastError = string.IsNullOrWhiteSpace(deterministic.Reason) ? "Check deterministico fallito" : deterministic.Reason!;
                _logger?.Append(request.RunId ?? string.Empty, $"[{roleCode}] Tentativo {attempt} fallito (deterministico): {lastError}");
                MarkLatestAttemptResult(modelName, request.Agent, "FAILED", lastError, examined: true);
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
                var checkerResult = await ValidateWithCheckerAsync(request, text, rules, checkerToken).ConfigureAwait(false);
                if (!checkerResult.IsValid)
                {
                    lastError = string.IsNullOrWhiteSpace(checkerResult.Reason) ? "response_checker invalid" : checkerResult.Reason!;
                    _logger?.Append(request.RunId ?? string.Empty, $"[{roleCode}] Tentativo {attempt} fallito (checker): {lastError}");
                    MarkLatestAttemptResult(modelName, request.Agent, "FAILED", lastError, examined: true);
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
            MarkLatestAttemptResult(modelName, request.Agent, "SUCCESS", null, examined: true);
            return new Result
            {
                Success = true,
                Text = text,
                ModelName = modelName
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
            ModelName = modelName
        };
    }

    private async Task<Result> TryFallbackAsync(Request request, ExecutionSettings settings, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var fallbackService = scope.ServiceProvider.GetService<ModelFallbackService>();
        if (fallbackService == null)
        {
            return new Result { Success = false, Error = "ModelFallbackService non disponibile" };
        }

        foreach (var role in BuildFallbackRoleCandidates(request.RoleCode, request.Agent.Role))
        {
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
                        RetryPromptFactory = request.RetryPromptFactory
                    };

                    var result = await ExecuteOnModelAsync(req, settings, fallbackName, role, ct).ConfigureAwait(false);
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
                    UsedFallback = true
                };
            }
        }

        return new Result { Success = false, Error = "Fallback fallito" };
    }

    private async Task<string> CallModelTextAsync(
        string modelName,
        Agent agent,
        string roleCode,
        string systemPrompt,
        string prompt,
        bool skipResponseChecker,
        CancellationToken ct)
    {
        var bridge = _kernelFactory.CreateChatBridge(
            modelName,
            agent.Temperature,
            agent.TopP,
            agent.RepeatPenalty,
            agent.TopK,
            agent.RepeatLastN,
            agent.NumPredict);

        var messages = new List<ConversationMessage>
        {
            new ConversationMessage { Role = "system", Content = systemPrompt },
            new ConversationMessage { Role = "user", Content = prompt }
        };

        using var scope = LogScope.Push(
            LogScope.Current ?? requestScope(roleCode),
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

        var (text, _) = LangChainChatBridge.ParseChatResponse(responseJson);
        return text ?? string.Empty;
    }

    private async Task<ValidationResult> ValidateWithCheckerAsync(
        Request request,
        string outputText,
        IReadOnlyList<ResponseValidationRule> rules,
        CancellationToken ct)
    {
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
            agentName: request.Agent.Name,
            modelName: ResolveModelName(request.Agent),
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
                request.Agent.NumPredict);

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

        return new ExecutionSettings(
            MaxAttempts: Math.Max(1, maxAttempts),
            RetryDelaySeconds: Math.Max(0, retryDelaySeconds),
            StepTimeoutSec: Math.Max(0, stepTimeoutSec),
            UseResponseChecker: useResponseChecker,
            EnableFallback: enableFallback,
            DiagnoseOnFinalFailure: diagnoseOnFinalFailure,
            ExplainAfterAttempt: Math.Max(0, explainAfterAttempt),
            CheckerTimeoutSec: Math.Max(0, checkerTimeoutSec),
            EnableDeterministicValidation: enableDeterministicValidation);
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

    private string? ResolveModelName(Agent agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.ModelName)) return agent.ModelName;
        if (!agent.ModelId.HasValue) return null;
        return _database.GetModelInfoById(agent.ModelId.Value)?.Name;
    }

    private static string requestScope(string roleCode) => $"command/{roleCode}";

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
