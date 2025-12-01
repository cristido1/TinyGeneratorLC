using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TinyGenerator.Services
{
    public interface IOllamaMonitorService
    {
        void SetOllamaEndpoint(string? endpoint);
        void RecordPrompt(string model, string prompt);
        (string Prompt, DateTime Ts)? GetLastPrompt(string model);
        Task<List<OllamaModelInfo>> GetRunningModelsAsync();
        Task<List<OllamaModelInfo>> GetInstalledModelsAsync();
        Task<(bool Success, string Output)> DeleteInstalledModelAsync(string modelName);
        Task<(bool Success, string Output)> StopModelAsync(string modelName);
        Task<List<OllamaModelInfo>> GetRunningModelsFromHttpAsync();
        Task<List<OllamaLogEntry>> GetRecentLogsAsync(string? noteFilter = null);
        void RecordModelNote(string model, string note);
    }
}
