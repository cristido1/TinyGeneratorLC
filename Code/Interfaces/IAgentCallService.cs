using System.Threading;
using System.Threading.Tasks;

namespace TinyGenerator.Services;

public interface IAgentCallService
{
    Task<CommandModelExecutionService.Result> ExecuteAsync(
        CommandModelExecutionService.Request request,
        CancellationToken ct = default);
}
