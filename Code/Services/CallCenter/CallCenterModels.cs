using TinyGenerator.Models;

namespace TinyGenerator.Services;

public sealed class ChatHistory
{
    public List<ConversationMessage> Messages { get; } = new();

    public ChatHistory()
    {
    }

    public ChatHistory(IEnumerable<ConversationMessage>? messages)
    {
        if (messages == null) return;
        Messages.AddRange(messages.Select(m => new ConversationMessage
        {
            Role = m.Role,
            Content = m.Content,
            ToolCallId = m.ToolCallId,
            ToolCalls = m.ToolCalls
        }));
    }

    public void AddSystem(string text) => Messages.Add(new ConversationMessage { Role = "system", Content = text ?? string.Empty });
    public void AddUser(string text) => Messages.Add(new ConversationMessage { Role = "user", Content = text ?? string.Empty });
    public void AddAssistant(string text) => Messages.Add(new ConversationMessage { Role = "assistant", Content = text ?? string.Empty });
}

public sealed class CallOptions
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(180);
    public int MaxRetries { get; set; } = 2;
    public bool UseResponseChecker { get; set; } = true;
    public bool AskFailExplanation { get; set; } = true;
    public bool AllowFallback { get; set; } = true;

    // Extra metadata for policy routing/logging in the centralized executor.
    public string Operation { get; set; } = "call_center";
    public string? SystemPromptOverride { get; set; }
    public object? ResponseFormat { get; set; }
    public string? CheckerContextText { get; set; }

    // Additional deterministic checks executed after the mandatory NonEmpty check.
    public List<IDeterministicCheck> DeterministicChecks { get; } = new();
    public List<IAgentChecker> AgentCheckers { get; } = new();
}

public sealed class CallCenterResult
{
    public bool Success { get; set; }
    public string ResponseText { get; set; } = string.Empty;
    public ChatHistory UpdatedHistory { get; set; } = new();
    public int Attempts { get; set; }
    public string ModelUsed { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public string? FailureReason { get; set; }
    public List<AgentCheckerOutcome> CheckerOutcomes { get; set; } = new();
}

public sealed class AgentCheckerOutcome
{
    public string CheckerAgentName { get; set; } = string.Empty;
    public string? ModelUsed { get; set; }
    public bool Passed { get; set; }
    public int? Score { get; set; }
    public bool? NeedsRetry { get; set; }
    public List<string> Issues { get; set; } = new();
    public string? FailureReason { get; set; }
    public string? RawResponse { get; set; }
}

public sealed class DeterministicResult : IDeterministicResult
{
    public bool Successed { get; init; }
    public string Message { get; init; } = string.Empty;
    public long CheckDurationMs { get; init; }
}

public sealed class NonEmptyResponseCheck : IDeterministicCheck
{
    public string Rule => "La risposta NON puo essere null, vuota o whitespace.";
    public string GenericErrorDescription => "Risposta vuota";
    public Microsoft.Extensions.Options.IOptions<object>? Options { get; set; }

    public IDeterministicResult Execute(string textToCheck)
    {
        var started = DateTime.UtcNow;
        var ok = !string.IsNullOrWhiteSpace(textToCheck);
        return new DeterministicResult
        {
            Successed = ok,
            Message = ok ? "ok" : "deterministic_empty: risposta vuota",
            CheckDurationMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds)
        };
    }
}
