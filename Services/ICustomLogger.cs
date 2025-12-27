using System.Collections.Generic;
using System.Threading.Tasks;
using TinyGenerator.Models;

namespace TinyGenerator.Services
{
    public interface ICustomLogger
    {
        // Enqueue a log entry. Timestamp is captured at the time of call.
    void Log(string level, string category, string message, string? exception = null, string? state = null, string? result = null);

        // Force flush of any buffered logs to the database.
        Task FlushAsync();

        // Log a model prompt (question to the AI model)
        void LogPrompt(string modelName, string prompt);

        // Log a model response (answer from the AI model)
        void LogResponse(string modelName, string response);

        // Log raw request JSON
        void LogRequestJson(string modelName, string requestJson, int? threadId = null);

        // Log raw response JSON
        void LogResponseJson(string modelName, string responseJson, int? threadId = null);

        void Start(string runId);
        Task AppendAsync(string runId, string message, string? extraClass = null);
        void Append(string runId, string message, string? extraClass = null);
        Task MarkCompletedAsync(string runId, string? finalResult = null);
        void MarkCompleted(string runId, string? finalResult = null);
        List<string> Get(string runId);
        bool IsCompleted(string runId);
        string? GetResult(string runId);
        void Clear(string runId);

        Task ShowAgentActivityAsync(string agentName, string status, string? agentId = null, string testType = "question");
        void ShowAgentActivity(string agentName, string status, string? agentId = null, string testType = "question");
        Task HideAgentActivityAsync(string agentId);
        void HideAgentActivity(string agentId);

        Task BroadcastLogsAsync(IEnumerable<LogEntry> entries);

        Task ModelRequestStartedAsync(string modelName);
        void ModelRequestStarted(string modelName);
        Task ModelRequestFinishedAsync(string modelName);
        void ModelRequestFinished(string modelName);
        IReadOnlyList<string> GetBusyModelsSnapshot();

        Task NotifyAllAsync(string title, string message, string level = "info");
        Task NotifyGroupAsync(string group, string title, string message, string level = "info");

        Task BroadcastStepProgress(Guid generationId, int current, int max, string stepDescription);
        Task BroadcastStepRetry(Guid generationId, int retryCount, string reason);
        Task BroadcastStepComplete(Guid generationId, int stepNumber);
        Task BroadcastTaskComplete(Guid generationId, string status);

        Task PublishEventAsync(string eventType, string title, string message, string level = "information", string? group = null);
    }
}
