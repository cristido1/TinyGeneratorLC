using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands;

public sealed class ModelExecutionOptions
{
    public int MaxAttemptsPerModel { get; set; } = 1;
    public int RetryDelayBaseSeconds { get; set; } = 0;
    public bool EnableFallback { get; set; } = true;
    public bool EnableDiagnosis { get; set; } = false;
    public int RequestTimeoutSeconds { get; set; } = 0;
}

public sealed class ModelExecutionRequest
{
    public string RoleCode { get; init; } = string.Empty;
    public Agent Agent { get; init; } = new();
    public int InitialModelId { get; init; }
    public string InitialModelName { get; init; } = string.Empty;
    public HashSet<string> TriedModelNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? SystemPrompt { get; init; }
    public string WorkInput { get; init; } = string.Empty;
    public string RunId { get; init; } = string.Empty;
    public int ChunkIndex { get; init; }
    public int ChunkCount { get; init; }
    public string? WorkLabel { get; init; }
    public ModelExecutionOptions Options { get; init; } = new();
    public Func<LangChainChatBridge, CancellationToken, Task<ModelWorkResult>> WorkAsync { get; init; } =
        (_, _) => Task.FromResult(ModelWorkResult.Fail("WorkAsync delegate non configurato"));
}

public sealed record ModelWorkResult(
    bool Success,
    string? OutputText,
    string? FailureReason = null)
{
    public static ModelWorkResult Ok(string outputText) => new(true, outputText, null);
    public static ModelWorkResult Fail(string reason, string? outputText = null) => new(false, outputText, reason);
}

public sealed record ModelExecutionResult(
    string OutputText,
    int ModelId,
    string ModelName,
    bool UsedFallback);
