using System;
using System.Collections.Generic;

namespace TinyGenerator.Services;

/// <summary>
/// Centralized per-command execution policies for CommandDispatcher.
/// Keys are matched case-insensitively against operationName first,
/// then metadata["operation"] as a fallback.
/// </summary>
public sealed class CommandPoliciesOptions
{
    public CommandExecutionPolicy Default { get; set; } = new();

    /// <summary>
    /// Per-operation overrides.
    /// Key examples: "TransformStoryRawToTagged", "add_ambient_tags_to_story".
    /// </summary>
    public Dictionary<string, CommandExecutionPolicy> Commands { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public CommandExecutionPolicy Resolve(string? operationName, IReadOnlyDictionary<string, string>? metadata)
    {
        if (!string.IsNullOrWhiteSpace(operationName) && Commands.TryGetValue(operationName.Trim(), out var byName) && byName != null)
        {
            return byName;
        }

        if (metadata != null && metadata.TryGetValue("operation", out var op) && !string.IsNullOrWhiteSpace(op))
        {
            if (Commands.TryGetValue(op.Trim(), out var byOp) && byOp != null)
            {
                return byOp;
            }
        }

        return Default ?? new CommandExecutionPolicy();
    }
}

public sealed class CommandExecutionPolicy
{
    /// <summary>
    /// Total attempts including the first. Use 1 to disable retries.
    /// </summary>
    public int MaxAttempts { get; set; } = 1;

    /// <summary>
    /// Base delay before a retry. Effective delay may grow with attempt number.
    /// </summary>
    public int RetryDelayBaseSeconds { get; set; } = 2;

    public int RetryDelayMaxSeconds { get; set; } = 30;

    public bool ExponentialBackoff { get; set; } = true;

    /// <summary>
    /// If true, a CommandResult with Success=false is considered retryable.
    /// </summary>
    public bool RetryOnFailureResult { get; set; } = false;

    /// <summary>
    /// If true, exceptions are considered retryable.
    /// </summary>
    public bool RetryOnException { get; set; } = true;
}
