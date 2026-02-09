using System.Collections.Generic;
using System.Threading.Tasks;

namespace TinyGenerator.Services
{
    public interface IOllamaManagementService
    {
        Task<List<object>> PurgeDisabledModelsAsync();
        Task<int> RefreshRunningContextsAsync();
        /// <summary>
        /// Load a model into memory without executing any conversation.
        /// Useful for warmup before tests to ensure model is ready.
        /// </summary>
        Task<bool> WarmupModelAsync(string model, int timeoutSeconds = 60);
    }
}
