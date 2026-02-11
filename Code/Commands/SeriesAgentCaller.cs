using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands;

internal sealed class SeriesAgentCaller
{
    private readonly IAgentCallService? _modelExecution;
    private readonly SeriesValidationRules _validationRules;
    private readonly ICustomLogger? _logger;

    public SeriesAgentCaller(
        IAgentCallService? modelExecution,
        SeriesValidationRules validationRules,
        ICustomLogger? logger = null)
    {
        _modelExecution = modelExecution;
        _validationRules = validationRules ?? throw new ArgumentNullException(nameof(validationRules));
        _logger = logger;
    }

    public async Task<SeriesAgentCallResponse> CallRoleWithRetriesAsync(SeriesAgentCallRequest request, CancellationToken ct)
    {
        if (_modelExecution == null)
        {
            return SeriesAgentCallResponse.Fail("CommandModelExecutionService non disponibile");
        }

        var hasDeterministicChecks = request.ValidationFunc != null || request.RequiredTags.Count > 0;
        var effectiveUseResponseChecker = request.Options.UseResponseChecker && !hasDeterministicChecks;

        var execution = await _modelExecution.ExecuteAsync(
            new CommandModelExecutionService.Request
            {
                CommandKey = "generate_new_serie",
                Agent = request.Agent,
                RoleCode = request.RoleCode,
                Prompt = request.Prompt,
                SystemPrompt = BuildSystemPrompt(request.Agent),
                MaxAttempts = Math.Max(1, request.Options.MaxAttempts),
                RetryDelaySeconds = Math.Max(0, request.Options.RetryDelaySeconds),
                StepTimeoutSec = Math.Max(1, request.Options.TimeoutSec),
                UseResponseChecker = effectiveUseResponseChecker,
                EnableFallback = true,
                DiagnoseOnFinalFailure = request.Options.DiagnoseOnFinalFailure,
                ExplainAfterAttempt = Math.Max(0, request.Options.ExplainAfterAttempt),
                RunId = request.RunId,
                DeterministicValidator = output =>
                {
                    if (!_validationRules.HasRequiredTags(output ?? string.Empty, request.RequiredTags, out var missingTags))
                    {
                        var missingText = missingTags.Count > 0 ? string.Join(", ", missingTags) : "tag richiesti";
                        return new CommandModelExecutionService.DeterministicValidationResult(
                            false,
                            $"Output privo di tag richiesti per {request.RoleCode}: {missingText}");
                    }

                    if (request.ValidationFunc != null)
                    {
                        var error = request.ValidationFunc(output ?? string.Empty);
                        if (!string.IsNullOrWhiteSpace(error))
                        {
                            return new CommandModelExecutionService.DeterministicValidationResult(false, error);
                        }
                    }

                    return new CommandModelExecutionService.DeterministicValidationResult(true, null);
                },
                RetryPromptFactory = (originalPrompt, reason) => BuildRetryPrompt(originalPrompt, reason)
            },
            ct).ConfigureAwait(false);

        if (execution.Success && !string.IsNullOrWhiteSpace(execution.Text))
        {
            return SeriesAgentCallResponse.Ok(execution.Text);
        }

        var errorMessage = execution.Error ?? $"Operazione {request.RoleCode} fallita";
        _logger?.Append(request.RunId, $"[{request.RoleCode}] Errore: {errorMessage}", "error");
        return SeriesAgentCallResponse.Fail(errorMessage);
    }

    private static string BuildSystemPrompt(Agent agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.Instructions))
        {
            return agent.Instructions!;
        }

        return "Sei un assistente esperto.";
    }

    private static string BuildRetryPrompt(string originalPrompt, string reason)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ATTENZIONE: il tuo output precedente NON era valido.");
        sb.AppendLine("Motivo: " + reason);
        sb.AppendLine("Rigenera la risposta COMPLETA rispettando tutti i tag richiesti.");
        sb.AppendLine();
        sb.AppendLine("PROMPT ORIGINALE:");
        sb.AppendLine(originalPrompt.Trim());
        return sb.ToString();
    }
}

internal sealed record SeriesAgentCallRequest(
    Agent Agent,
    string RoleCode,
    string Prompt,
    IReadOnlyCollection<string> RequiredTags,
    SeriesGenerationOptions.SeriesRoleOptions Options,
    string RunId,
    Func<string, string?>? ValidationFunc = null);

internal sealed record SeriesAgentCallResponse(
    bool Success,
    string? Text,
    string? Error)
{
    public static SeriesAgentCallResponse Ok(string text) => new(true, text, null);
    public static SeriesAgentCallResponse Fail(string error) => new(false, null, error);
}
