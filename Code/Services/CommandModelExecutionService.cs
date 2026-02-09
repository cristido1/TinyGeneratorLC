using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TinyGenerator.Models;

namespace TinyGenerator.Services;

public sealed class CommandModelExecutionService
{
    public sealed record DeterministicValidationResult(bool IsValid, string? Reason = null);

    public sealed class Request
    {
        public string CommandKey { get; set; } = string.Empty;
        public Agent Agent { get; set; } = new();
        public string RoleCode { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string? SystemPrompt { get; set; }
        public int MaxAttempts { get; set; } = 3;
        public int RetryDelaySeconds { get; set; } = 2;
        public int StepTimeoutSec { get; set; } = 0;
        public bool UseResponseChecker { get; set; }
        public bool EnableFallback { get; set; } = true;
        public bool DiagnoseOnFinalFailure { get; set; } = true;
        public int ExplainAfterAttempt { get; set; } = 0;
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

    public CommandModelExecutionService(
        ILangChainKernelFactory kernelFactory,
        IServiceScopeFactory scopeFactory,
        DatabaseService database,
        IOptions<ResponseValidationOptions>? responseValidationOptions = null,
        ICustomLogger? logger = null)
    {
        _kernelFactory = kernelFactory;
        _scopeFactory = scopeFactory;
        _database = database;
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

        var primary = await ExecuteOnModelAsync(request, modelName, request.RoleCode, ct).ConfigureAwait(false);
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

        if (request.EnableFallback)
        {
            var fallback = await TryFallbackAsync(request, ct).ConfigureAwait(false);
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

    private async Task<Result> ExecuteOnModelAsync(Request request, string modelName, string roleCode, CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, request.MaxAttempts);
        var delayMs = Math.Max(0, request.RetryDelaySeconds) * 1000;
        var currentPrompt = request.Prompt ?? string.Empty;
        var lastError = "Risposta non valida";
        var lastOutput = string.Empty;
        var explained = false;
        var rules = (_responseValidationOptions?.Value?.Rules ?? new List<ResponseValidationRule>()).ToList();

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _logger?.Append(request.RunId ?? string.Empty, $"[{roleCode}] Tentativo {attempt}/{maxAttempts} (model={modelName})");

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (request.StepTimeoutSec > 0)
            {
                attemptCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, request.StepTimeoutSec)));
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
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && request.StepTimeoutSec > 0 && attemptCts.IsCancellationRequested)
            {
                lastError = $"Timeout fase dopo {request.StepTimeoutSec}s";
                MarkLatestAttemptResult(modelName, request.Agent, "FAILED", lastError, examined: true);
                if (!explained && request.ExplainAfterAttempt > 0 && attempt >= request.ExplainAfterAttempt)
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
                MarkLatestAttemptResult(modelName, request.Agent, "FAILED", lastError, examined: true);
                if (!explained && request.ExplainAfterAttempt > 0 && attempt >= request.ExplainAfterAttempt)
                {
                    await DiagnoseAsync(modelName, request, lastError, lastOutput, CancellationToken.None).ConfigureAwait(false);
                    explained = true;
                }
                if (attempt < maxAttempts && delayMs > 0) await Task.Delay(delayMs, ct).ConfigureAwait(false);
                continue;
            }

            lastOutput = text;
            var deterministic = request.DeterministicValidator?.Invoke(text) ?? new DeterministicValidationResult(true, null);
            if (!deterministic.IsValid)
            {
                lastError = string.IsNullOrWhiteSpace(deterministic.Reason) ? "Check deterministico fallito" : deterministic.Reason!;
                MarkLatestAttemptResult(modelName, request.Agent, "FAILED", lastError, examined: true);
                if (!explained && request.ExplainAfterAttempt > 0 && attempt >= request.ExplainAfterAttempt)
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

            if (request.UseResponseChecker)
            {
                var checkerResult = await ValidateWithCheckerAsync(request, text, rules, attemptToken).ConfigureAwait(false);
                if (!checkerResult.IsValid)
                {
                    lastError = string.IsNullOrWhiteSpace(checkerResult.Reason) ? "response_checker invalid" : checkerResult.Reason!;
                    MarkLatestAttemptResult(modelName, request.Agent, "FAILED", lastError, examined: true);
                    if (!explained && request.ExplainAfterAttempt > 0 && attempt >= request.ExplainAfterAttempt)
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

            MarkLatestAttemptResult(modelName, request.Agent, "SUCCESS", null, examined: true);
            return new Result
            {
                Success = true,
                Text = text,
                ModelName = modelName
            };
        }

        if (request.DiagnoseOnFinalFailure && !explained)
        {
            await DiagnoseAsync(modelName, request, lastError, lastOutput, CancellationToken.None).ConfigureAwait(false);
        }

        return new Result
        {
            Success = false,
            Error = lastError,
            Text = lastOutput,
            ModelName = modelName
        };
    }

    private async Task<Result> TryFallbackAsync(Request request, CancellationToken ct)
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
                        MaxAttempts = request.MaxAttempts,
                        RetryDelaySeconds = request.RetryDelaySeconds,
                        StepTimeoutSec = request.StepTimeoutSec,
                        UseResponseChecker = request.UseResponseChecker,
                        EnableFallback = false,
                        DiagnoseOnFinalFailure = false,
                        ExplainAfterAttempt = request.ExplainAfterAttempt,
                        RunId = request.RunId,
                        DeterministicValidator = request.DeterministicValidator,
                        RetryPromptFactory = request.RetryPromptFactory
                    };

                    var result = await ExecuteOnModelAsync(req, fallbackName, role, ct).ConfigureAwait(false);
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
