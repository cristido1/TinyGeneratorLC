using TinyGenerator.Models;

namespace TinyGenerator.Services;

public interface ICallCenter
{
    Task<CallCenterResult> CallAgentAsync(
        long storyId,
        int threadId,
        Agent agent,
        ChatHistory history,
        CallOptions options,
        CancellationToken cancellationToken = default);
}

