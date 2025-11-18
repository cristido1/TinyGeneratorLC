using System.Collections.Generic;
using System.Threading.Tasks;

namespace TinyGenerator.Services
{
    public interface IOllamaManagementService
    {
        Task<List<object>> PurgeDisabledModelsAsync();
        Task<int> RefreshRunningContextsAsync();
    }
}
