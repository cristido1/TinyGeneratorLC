using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands;

public interface INextStatusEnqueuer
{
    bool TryAdvanceAndEnqueueVoice(
        StoryRecord story,
        string runId,
        long commandStoryId,
        bool autolaunchNextCommand);

    bool TryAdvanceAndEnqueueAmbient(
        StoryRecord story,
        string runId,
        long commandStoryId,
        bool autolaunchNextCommand);

    bool TryAdvanceAndEnqueueFx(
        StoryRecord story,
        string runId,
        long commandStoryId,
        bool autolaunchNextCommand);

    bool TryAdvanceAndEnqueueMusic(
        StoryRecord story,
        string runId,
        long commandStoryId,
        bool autolaunchNextCommand);
}

