using System;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands;

public sealed class CommandTelemetry : ICommandTelemetry
{
    private readonly ICustomLogger? _logger;
    private readonly Action<CommandProgressEventArgs>? _progressSink;

    public CommandTelemetry(ICustomLogger? logger, Action<CommandProgressEventArgs>? progressSink = null)
    {
        _logger = logger;
        _progressSink = progressSink;
    }

    public void Start(string runId)
    {
        _logger?.Start(runId);
    }

    public void Append(string runId, string message, string level = "info")
    {
        _logger?.Append(runId, message, level);
    }

    public void MarkCompleted(string runId, string status)
    {
        _logger?.MarkCompleted(runId, status);
    }

    public void MarkLatestModelResponseResult(string result, string? reason)
    {
        _logger?.MarkLatestModelResponseResult(result, reason);
    }

    public void ReportProgress(int current, int max, string description)
    {
        try
        {
            _progressSink?.Invoke(new CommandProgressEventArgs(current, max, description));
        }
        catch
        {
            // best-effort
        }
    }
}

