using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands;

internal sealed class SeriesAgentCaller
{
    private readonly ICallCenter? _callCenter;
    private readonly SeriesValidationRules _validationRules;
    private readonly ICustomLogger? _logger;

    public SeriesAgentCaller(
        ICallCenter? callCenter,
        SeriesValidationRules validationRules,
        ICustomLogger? logger = null)
    {
        _callCenter = callCenter;
        _validationRules = validationRules ?? throw new ArgumentNullException(nameof(validationRules));
        _logger = logger;
    }

    public async Task<SeriesAgentCallResponse> CallRoleWithRetriesAsync(SeriesAgentCallRequest request, CancellationToken ct)
    {
        if (_callCenter == null)
        {
            return SeriesAgentCallResponse.Fail("CallCenter non disponibile");
        }

        var hasDeterministicChecks = request.ValidationFunc != null || request.RequiredTags.Count > 0;
        var effectiveUseResponseChecker = request.Options.UseResponseChecker && !hasDeterministicChecks;
        var history = new ChatHistory();
        history.AddSystem(BuildSystemPrompt(request.Agent));
        history.AddUser(request.Prompt);

        var options = new CallOptions
        {
            Operation = "generate_new_serie",
            Timeout = TimeSpan.FromSeconds(Math.Max(1, request.Options.TimeoutSec)),
            MaxRetries = Math.Max(0, request.Options.MaxAttempts - 1),
            UseResponseChecker = effectiveUseResponseChecker,
            AllowFallback = true,
            AskFailExplanation = request.Options.DiagnoseOnFinalFailure,
            SystemPromptOverride = BuildSystemPrompt(request.Agent)
        };
        options.DeterministicChecks.Add(new CheckEmpty
        {
            Options = Options.Create<object>(new Dictionary<string, object>
            {
                ["ErrorMessage"] = $"Output vuoto per {request.RoleCode}"
            })
        });
        options.DeterministicChecks.Add(new CheckSeriesOutputValidity
        {
            Options = Options.Create<object>(new Dictionary<string, object>
            {
                ["RoleCode"] = request.RoleCode,
                ["ValidationRules"] = _validationRules,
                ["RequiredTags"] = request.RequiredTags,
                ["ValidationFunc"] = request.ValidationFunc as object
            })
        });

        var execution = await _callCenter.CallAgentAsync(
            storyId: 0,
            threadId: $"{request.RunId}:{request.RoleCode}".GetHashCode(StringComparison.Ordinal),
            agent: request.Agent,
            history: history,
            options: options,
            cancellationToken: ct).ConfigureAwait(false);

        if (execution.Success && !string.IsNullOrWhiteSpace(execution.ResponseText))
        {
            return SeriesAgentCallResponse.Ok(execution.ResponseText);
        }

        var errorMessage = execution.FailureReason ?? $"Operazione {request.RoleCode} fallita";
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
