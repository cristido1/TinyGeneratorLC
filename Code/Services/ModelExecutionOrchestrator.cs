using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TinyGenerator.Models;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed class ModelExecutionOrchestrator
{
    private readonly ILangChainKernelFactory _kernelFactory;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ICustomLogger? _logger;

    private sealed record AttemptFailure(string ModelName, string Reason, string? LastOutput);
    private sealed record ModelAttemptResult(bool Success, string? OutputText, AttemptFailure? LastFailure);

    public ModelExecutionOrchestrator(
        ILangChainKernelFactory kernelFactory,
        IServiceScopeFactory? scopeFactory = null,
        ICustomLogger? logger = null)
    {
        _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<ModelExecutionResult> ExecuteAsync(ModelExecutionRequest request, CancellationToken ct = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (request.Agent == null) throw new ArgumentNullException(nameof(request.Agent));
        if (string.IsNullOrWhiteSpace(request.RoleCode)) throw new ArgumentException("RoleCode is required", nameof(request));
        if (string.IsNullOrWhiteSpace(request.InitialModelName)) throw new ArgumentException("InitialModelName is required", nameof(request));
        if (request.WorkAsync == null) throw new ArgumentException("WorkAsync delegate is required", nameof(request));

        var (options, timeoutSource) = ResolveOptions(request.Options);
        if (options.RequestTimeoutSeconds > 0)
        {
            var sourceText = string.Equals(timeoutSource, "command_policy", StringComparison.Ordinal)
                ? $"CommandPolicies:{(CommandExecutionRuntime.CurrentOperationName ?? "command")}:TimeoutSec"
                : "ModelExecutionOptions.RequestTimeoutSeconds";
            _logger?.Append(
                request.RunId,
                $"[chunk {request.ChunkIndex}/{request.ChunkCount}] Timeout richiesta per-call={options.RequestTimeoutSeconds}s (source={sourceText})");
        }
        request.TriedModelNames.Add(request.InitialModelName);

        var primary = await ExecuteOnModelAsync(
            request,
            request.InitialModelName,
            options,
            ct).ConfigureAwait(false);

        if (primary.Success && !string.IsNullOrWhiteSpace(primary.OutputText))
        {
            return new ModelExecutionResult(
                primary.OutputText!,
                request.InitialModelId,
                request.InitialModelName,
                UsedFallback: false);
        }

        var lastFailure = primary.LastFailure;
        if (options.EnableFallback)
        {
            var (fallbackResult, fallbackFailure) = await TryFallbackAsync(
                request,
                options,
                ct).ConfigureAwait(false);

            if (fallbackResult != null)
            {
                return fallbackResult;
            }

            if (fallbackFailure != null)
            {
                lastFailure = fallbackFailure;
            }
        }

        if (options.EnableDiagnosis && lastFailure != null)
        {
            await TryDiagnoseAsync(request, lastFailure, ct).ConfigureAwait(false);
        }

        var finalReason = lastFailure?.Reason ?? "Errore sconosciuto";
        throw new InvalidOperationException(
            $"Failed to execute {request.RoleCode} for chunk {request.ChunkIndex}/{request.ChunkCount} after exhausting retries/fallback. Last error: {finalReason}");
    }

    private async Task<ModelAttemptResult> ExecuteOnModelAsync(
        ModelExecutionRequest request,
        string modelName,
        ModelExecutionOptions options,
        CancellationToken ct)
    {
        var bridge = CreateBridge(modelName, request.Agent);
        var maxAttempts = Math.Max(1, options.MaxAttemptsPerModel);
        var retryDelayBaseSeconds = Math.Max(0, options.RetryDelayBaseSeconds);
        var workLabel = string.IsNullOrWhiteSpace(request.WorkLabel) ? request.RoleCode : request.WorkLabel!;

        AttemptFailure? lastFailure = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _logger?.Append(
                request.RunId,
                $"[chunk {request.ChunkIndex}/{request.ChunkCount}] {workLabel} attempt {attempt}/{maxAttempts} (model={modelName})");

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (options.RequestTimeoutSeconds > 0)
            {
                attemptCts.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
            }

            try
            {
                var workResult = await request.WorkAsync(bridge, attemptCts.Token).ConfigureAwait(false);
                if (workResult.Success && !string.IsNullOrWhiteSpace(workResult.OutputText))
                {
                    _logger?.MarkLatestModelResponseResult("SUCCESS", null);
                    return new ModelAttemptResult(true, workResult.OutputText, null);
                }

                var reason = string.IsNullOrWhiteSpace(workResult.FailureReason)
                    ? "Risposta non valida"
                    : workResult.FailureReason!;
                lastFailure = new AttemptFailure(modelName, reason, workResult.OutputText);
                _logger?.MarkLatestModelResponseResult("FAILED", reason);
                _logger?.Append(
                    request.RunId,
                    $"[chunk {request.ChunkIndex}/{request.ChunkCount}] Validation failed on attempt {attempt}: {reason}",
                    "warn");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && options.RequestTimeoutSeconds > 0 && attemptCts.IsCancellationRequested)
            {
                var reason = $"Timeout richiesta modello dopo {options.RequestTimeoutSeconds}s";
                lastFailure = new AttemptFailure(modelName, reason, null);
                _logger?.MarkLatestModelResponseResult("FAILED", reason);
                _logger?.Append(
                    request.RunId,
                    $"[chunk {request.ChunkIndex}/{request.ChunkCount}] {reason}",
                    "warn");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var reason = $"Errore durante l'elaborazione: {ex.Message}";
                lastFailure = new AttemptFailure(modelName, reason, null);
                _logger?.MarkLatestModelResponseResult("FAILED", reason);
                _logger?.Append(
                    request.RunId,
                    $"[chunk {request.ChunkIndex}/{request.ChunkCount}] Error on attempt {attempt}: {ex.Message}",
                    "error");
            }

            if (attempt < maxAttempts && retryDelayBaseSeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(retryDelayBaseSeconds * attempt), ct).ConfigureAwait(false);
            }
        }

        return new ModelAttemptResult(false, null, lastFailure);
    }

    private async Task<(ModelExecutionResult? Result, AttemptFailure? LastFailure)> TryFallbackAsync(
        ModelExecutionRequest request,
        ModelExecutionOptions options,
        CancellationToken ct)
    {
        if (_scopeFactory == null)
        {
            _logger?.Append(request.RunId, "ScopeFactory non disponibile: fallback disattivato.", "warn");
            return (null, null);
        }

        using var scope = _scopeFactory.CreateScope();
        var fallbackService = scope.ServiceProvider.GetService<ModelFallbackService>();
        if (fallbackService == null)
        {
            _logger?.Append(request.RunId, "ModelFallbackService non disponibile in DI scope; fallback non eseguibile.", "warn");
            return (null, null);
        }

        AttemptFailure? lastFailure = null;

        var (result, successfulModelRole) = await fallbackService.ExecuteWithFallbackAsync(
            request.RoleCode,
            request.InitialModelId,
            async modelRole =>
            {
                var fallbackModelName = modelRole.Model?.Name;
                if (string.IsNullOrWhiteSpace(fallbackModelName))
                {
                    throw new InvalidOperationException("Fallback ModelRole has no Model.Name");
                }

                _logger?.Append(
                    request.RunId,
                    $"[chunk {request.ChunkIndex}/{request.ChunkCount}] Trying fallback model '{fallbackModelName}' for role '{request.RoleCode}'",
                    "warn");

                var fallbackAttempt = await ExecuteOnModelAsync(
                    request,
                    fallbackModelName,
                    options,
                    ct).ConfigureAwait(false);

                if (!fallbackAttempt.Success || string.IsNullOrWhiteSpace(fallbackAttempt.OutputText))
                {
                    lastFailure = fallbackAttempt.LastFailure ?? new AttemptFailure(fallbackModelName, "Fallback model failed", null);
                    throw new InvalidOperationException(lastFailure.Reason);
                }

                return new ModelExecutionResult(
                    fallbackAttempt.OutputText!,
                    modelRole.ModelId,
                    fallbackModelName,
                    UsedFallback: true);
            },
            validateResult: r => r != null && !string.IsNullOrWhiteSpace(r.OutputText),
            shouldTryModelRole: mr =>
            {
                var name = mr.Model?.Name;
                return !string.IsNullOrWhiteSpace(name) && request.TriedModelNames.Add(name);
            }).ConfigureAwait(false);

        if (result != null && successfulModelRole?.Model != null && !string.IsNullOrWhiteSpace(successfulModelRole.Model.Name))
        {
            return (result, null);
        }

        _logger?.Append(
            request.RunId,
            $"[chunk {request.ChunkIndex}/{request.ChunkCount}] Fallback models exhausted for role '{request.RoleCode}'.",
            "error");

        return (null, lastFailure);
    }

    private async Task TryDiagnoseAsync(ModelExecutionRequest request, AttemptFailure failure, CancellationToken ct)
    {
        try
        {
            var bridge = CreateBridge(failure.ModelName, request.Agent);

            var auditSystem =
                "Sei un assistente diagnostico. Spiega in italiano in modo conciso perche' l'output ha fallito " +
                "e come correggere il comportamento al prossimo tentativo. Non riscrivere il testo originale.";

            var sb = new StringBuilder();
            sb.AppendLine($"DIAGNOSI {request.RoleCode} - chunk {request.ChunkIndex}/{request.ChunkCount}");
            sb.AppendLine();
            sb.AppendLine("=== MOTIVO FALLIMENTO ===");
            sb.AppendLine(failure.Reason);
            sb.AppendLine();
            sb.AppendLine("=== INPUT ===");
            sb.AppendLine(ClipForPrompt(request.WorkInput, 2500));
            if (!string.IsNullOrWhiteSpace(failure.LastOutput))
            {
                sb.AppendLine();
                sb.AppendLine("=== ULTIMO OUTPUT ===");
                sb.AppendLine(ClipForPrompt(failure.LastOutput, 2500));
            }

            var messages = new List<ConversationMessage>
            {
                new ConversationMessage { Role = "system", Content = auditSystem },
                new ConversationMessage { Role = "user", Content = sb.ToString() }
            };

            var responseJson = await bridge.CallModelWithToolsAsync(
                messages,
                new List<Dictionary<string, object>>(),
                ct,
                skipResponseChecker: true).ConfigureAwait(false);

            var (text, _) = LangChainChatBridge.ParseChatResponse(responseJson);
            if (!string.IsNullOrWhiteSpace(text))
            {
                _logger?.Append(
                    request.RunId,
                    $"[chunk {request.ChunkIndex}/{request.ChunkCount}] {request.RoleCode} self-diagnosis: {text.Trim()}",
                    "warn");
            }
        }
        catch (Exception ex)
        {
            _logger?.Append(
                request.RunId,
                $"[chunk {request.ChunkIndex}/{request.ChunkCount}] Failed to collect {request.RoleCode} self-diagnosis: {ex.Message}",
                "warn");
        }
    }

    private LangChainChatBridge CreateBridge(string modelName, Agent agent)
    {
        return _kernelFactory.CreateChatBridge(
            modelName,
            agent.Temperature,
            agent.TopP,
            agent.RepeatPenalty,
            agent.TopK,
            agent.RepeatLastN,
            agent.NumPredict);
    }

    private static (ModelExecutionOptions Options, string? TimeoutSource) ResolveOptions(ModelExecutionOptions? options)
    {
        options ??= new ModelExecutionOptions();
        var explicitRequestTimeout = Math.Max(0, options.RequestTimeoutSeconds);
        var policyTimeout = Math.Max(0, CommandExecutionRuntime.CurrentTimeoutSec);
        var effectiveRequestTimeout = explicitRequestTimeout > 0 ? explicitRequestTimeout : policyTimeout;
        var timeoutSource = explicitRequestTimeout > 0
            ? "request_options"
            : policyTimeout > 0
                ? "command_policy"
                : null;

        return (new ModelExecutionOptions
        {
            MaxAttemptsPerModel = Math.Max(1, options.MaxAttemptsPerModel),
            RetryDelayBaseSeconds = Math.Max(0, options.RetryDelayBaseSeconds),
            EnableFallback = options.EnableFallback,
            EnableDiagnosis = options.EnableDiagnosis,
            RequestTimeoutSeconds = effectiveRequestTimeout
        }, timeoutSource);
    }

    private static string ClipForPrompt(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        if (maxChars <= 0) return string.Empty;
        if (text.Length <= maxChars) return text;
        return text.Substring(0, maxChars) + "...";
    }
}
