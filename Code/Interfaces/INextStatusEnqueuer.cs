using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands;

public interface INextStatusEnqueuer
{
    bool TryAdvanceAndEnqueueAmbient(
        StoryRecord story,
        string runId,
        long commandStoryId,
        bool autolaunchNextCommand);
}

