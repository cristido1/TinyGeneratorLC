using TinyGenerator.Models;

namespace TinyGenerator.Services;

public interface IAgentExecutor
{
    Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken ct);
}

public interface IDeterministicValidator
{
    DeterministicValidatorResult Validate(AgentExecutionContext context);
}

public interface IResponseValidator
{
    ResponseValidatorResult Validate(AgentExecutionContext context);
}

public interface IRetryPolicy
{
    RetryDecision Evaluate(RetryContext context);
}

public sealed class AgentExecutionRequest
{
    public long StoryId { get; init; }
    public int ThreadId { get; init; }
    public Agent Agent { get; init; } = new();
    public ChatHistory History { get; init; } = new();
    public string SystemPrompt { get; init; } = string.Empty;
    public CallOptions Options { get; init; } = new();
    public bool EnableStoryLiveStream { get; init; }
    public string? StoryLiveGroup { get; init; }
    public object? ResponseFormat { get; init; }
    public Func<string, CommandModelExecutionService.DeterministicValidationResult>? DeterministicValidatorCallback { get; init; }
    public Func<string, Task>? StreamChunkCallback { get; init; }
}

public sealed class AgentExecutionResult
{
    public bool Success { get; init; }
    public string? Text { get; init; }
    public string? Error { get; init; }
    public string? ModelName { get; init; }
    public bool UsedFallback { get; init; }
    public bool DeterministicFailure { get; init; }
    public int AttemptsUsed { get; init; }
}

public sealed class AgentExecutionContext
{
    public string Operation { get; init; } = "call_center";
    public Agent? Agent { get; init; }
    public CallOptions? Options { get; init; }
    public AgentExecutionResult? ExecutionResult { get; init; }
    public string OutputText { get; init; } = string.Empty;
    public string? PreviousNormalizedResponse { get; init; }
    public IReadOnlyList<IDeterministicCheck> DeterministicChecks { get; init; } = Array.Empty<IDeterministicCheck>();
}

public sealed class DeterministicValidatorResult
{
    public bool IsValid { get; init; }
    public string? FailureReason { get; init; }
    public string? CorrectedText { get; init; }
    public List<string> Violations { get; init; } = new();
}

public sealed class ResponseValidatorResult
{
    public bool IsValid { get; init; } = true;
    public string Status { get; init; } = "skipped";
    public string? FailureReason { get; init; }
}

public enum RetryDecisionKind
{
    RetrySameAgent,
    FallbackAgent,
    Stop
}

public sealed class RetryContext
{
    public Agent CurrentAgent { get; init; } = new();
    public CallOptions Options { get; init; } = new();
    public int AttemptsCurrentAgent { get; init; }
    public int AttemptsTotal { get; init; }
    public HashSet<string> UsedModels { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, (double successRate, double tokensPerSec)> FallbackStats { get; init; } =
        new Dictionary<string, (double successRate, double tokensPerSec)>(StringComparer.OrdinalIgnoreCase);
}

public sealed class RetryDecision
{
    public RetryDecisionKind Kind { get; init; }
    public Agent? FallbackAgent { get; init; }
    public bool ShouldAskFailureExplanation { get; init; }
}
