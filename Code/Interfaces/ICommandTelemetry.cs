namespace TinyGenerator.Services.Commands;

public interface ICommandTelemetry
{
    void Start(string runId);
    void Append(string runId, string message, string level = "info");
    void MarkCompleted(string runId, string status);
    void MarkLatestModelResponseResult(string result, string? reason);
    void ReportProgress(int current, int max, string description);
}

