using System.Threading.Tasks;

namespace TinyGenerator.Services
{
    public interface ICustomLogger
    {
        // Enqueue a log entry. Timestamp is captured at the time of call.
        void Log(string level, string category, string message, string? exception = null, string? state = null);

        // Force flush of any buffered logs to the database.
        Task FlushAsync();

        // Log a model prompt (question to the AI model)
        void LogPrompt(string modelName, string prompt);

        // Log a model response (answer from the AI model)
        void LogResponse(string modelName, string response);
    }
}
