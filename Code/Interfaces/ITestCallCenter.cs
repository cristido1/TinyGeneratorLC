using TinyGenerator.Models;

namespace TinyGenerator.Services;

public sealed class TestCallRequest
{
    public string Operation { get; set; } = "model_test";
    public string ModelName { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
    public int MaxRetries { get; set; }
    public bool UseResponseChecker { get; set; }
    public bool AskFailExplanation { get; set; }
    public bool AllowFallback { get; set; }
    public object? ResponseFormat { get; set; }
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? NumPredict { get; set; }
    public int ThreadId { get; set; }
    public long StoryId { get; set; }
    public ChatHistory? History { get; set; }
    public List<IDeterministicCheck> DeterministicChecks { get; } = new();
}

public sealed class TestCallResult
{
    public bool Success { get; init; }
    public string ResponseText { get; init; } = string.Empty;
    public string? FailureReason { get; init; }
    public TimeSpan Duration { get; init; }
    public int Attempts { get; init; }
    public string ModelUsed { get; init; } = string.Empty;
    public ChatHistory UpdatedHistory { get; init; } = new();
}

public interface ITestCallCenter
{
    Task<TestCallResult> CallAsync(TestCallRequest request, CancellationToken cancellationToken = default);
}

