using System;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands;

public sealed class NextStatusEnqueuer : INextStatusEnqueuer
{
    private readonly StoriesService? _storiesService;
    private readonly ICustomLogger? _logger;

    public NextStatusEnqueuer(StoriesService? storiesService, ICustomLogger? logger)
    {
        _storiesService = storiesService;
        _logger = logger;
    }

    public bool TryAdvanceAndEnqueueVoice(
        StoryRecord story,
        string runId,
        long commandStoryId,
        bool autolaunchNextCommand)
    {
        try
        {
            var allowNext = _storiesService?.ApplyStatusTransitionWithCleanup(story, CommandStatusCodes.TaggedVoice, runId) ?? true;
            if (!allowNext)
            {
                _logger?.Append(runId, $"[story {commandStoryId}] next status enqueue skipped: delete_next_items attivo", "info");
                return false;
            }

            if (_storiesService != null && !_storiesService.IsTaggedVoiceAutoLaunchEnabled())
            {
                _logger?.Append(runId, $"[story {commandStoryId}] next status enqueue skipped: StoryTaggingPipeline voice autolaunch disabled", "info");
                return false;
            }

            if (_storiesService != null && !_storiesService.TryValidateTaggedVoice(story, out var voiceReason))
            {
                _logger?.Append(runId, $"[story {commandStoryId}] next status enqueue skipped: voice validation failed ({voiceReason})", "warn");
                return false;
            }

            if (!autolaunchNextCommand)
            {
                _logger?.Append(runId, $"[story {commandStoryId}] next status enqueue skipped: AutolaunchNextCommand disabled", "info");
                return false;
            }

            if (_storiesService == null)
            {
                _logger?.Append(runId, $"[story {commandStoryId}] next status enqueue skipped: stories service missing", "warn");
                return false;
            }

            var refreshedStory = _storiesService.GetStoryById(story.Id) ?? story;
            var nextRunId = _storiesService.EnqueueNextStatusCommand(refreshedStory, CommandTriggerCodes.VoiceTagsCompleted, priority: 2);
            if (!string.IsNullOrWhiteSpace(nextRunId))
            {
                _logger?.Append(runId, $"[story {commandStoryId}] Enqueued next status (runId={nextRunId})", "info");
                return true;
            }

            _logger?.Append(runId, $"[story {commandStoryId}] Next status enqueue skipped: no next status available", "info");
            return false;
        }
        catch (Exception ex)
        {
            _logger?.Append(runId, $"[story {commandStoryId}] Failed to enqueue next status: {ex.Message}", "warn");
            return false;
        }
    }

    public bool TryAdvanceAndEnqueueAmbient(
        StoryRecord story,
        string runId,
        long commandStoryId,
        bool autolaunchNextCommand)
    {
        try
        {
            var allowNext = _storiesService?.ApplyStatusTransitionWithCleanup(story, CommandStatusCodes.TaggedAmbient, runId) ?? true;
            if (!allowNext)
            {
                _logger?.Append(runId, $"[story {commandStoryId}] next status enqueue skipped: delete_next_items attivo", "info");
                return false;
            }

            if (_storiesService != null && !_storiesService.IsTaggedAmbientAutoLaunchEnabled())
            {
                _logger?.Append(runId, $"[story {commandStoryId}] next status enqueue skipped: StoryTaggingPipeline ambient autolaunch disabled", "info");
                return false;
            }

            if (_storiesService != null && !_storiesService.TryValidateTaggedAmbient(story, out var ambientReason))
            {
                _logger?.Append(runId, $"[story {commandStoryId}] next status enqueue skipped: ambient validation failed ({ambientReason})", "warn");
                return false;
            }

            if (!autolaunchNextCommand)
            {
                _logger?.Append(runId, $"[story {commandStoryId}] next status enqueue skipped: AutolaunchNextCommand disabled", "info");
                return false;
            }

            if (_storiesService == null)
            {
                _logger?.Append(runId, $"[story {commandStoryId}] next status enqueue skipped: stories service missing", "warn");
                return false;
            }

            var refreshedStory = _storiesService.GetStoryById(story.Id) ?? story;
            var nextRunId = _storiesService.EnqueueNextStatusCommand(refreshedStory, CommandTriggerCodes.AmbientTagsCompleted, priority: 2);
            if (!string.IsNullOrWhiteSpace(nextRunId))
            {
                _logger?.Append(runId, $"[story {commandStoryId}] Enqueued next status (runId={nextRunId})", "info");
                return true;
            }

            _logger?.Append(runId, $"[story {commandStoryId}] Next status enqueue skipped: no next status available", "info");
            return false;
        }
        catch (Exception ex)
        {
            _logger?.Append(runId, $"[story {commandStoryId}] Failed to enqueue next status: {ex.Message}", "warn");
            return false;
        }
    }

    public bool TryAdvanceAndEnqueueFx(
        StoryRecord story,
        string runId,
        long commandStoryId,
        bool autolaunchNextCommand)
    {
        try
        {
            var allowNext = _storiesService?.ApplyStatusTransitionWithCleanup(story, CommandStatusCodes.TaggedFx, runId) ?? true;
            if (!allowNext)
            {
                _logger?.Append(runId, $"[story {commandStoryId}] next status enqueue skipped: delete_next_items attivo", "info");
                return false;
            }

            if (_storiesService != null && !_storiesService.IsTaggedFxAutoLaunchEnabled())
            {
                _logger?.Append(runId, $"[story {commandStoryId}] next status enqueue skipped: StoryTaggingPipeline fx autolaunch disabled", "info");
                return false;
            }

            if (_storiesService != null && !_storiesService.TryValidateTaggedFx(story, out var fxReason))
            {
                _logger?.Append(runId, $"[story {commandStoryId}] next status enqueue skipped: fx validation failed ({fxReason})", "warn");
                return false;
            }

            if (!autolaunchNextCommand)
            {
                _logger?.Append(runId, $"[story {commandStoryId}] next status enqueue skipped: AutolaunchNextCommand disabled", "info");
                return false;
            }

            if (_storiesService == null)
            {
                _logger?.Append(runId, $"[story {commandStoryId}] next status enqueue skipped: stories service missing", "warn");
                return false;
            }

            var refreshedStory = _storiesService.GetStoryById(story.Id) ?? story;
            var nextRunId = _storiesService.EnqueueNextStatusCommand(refreshedStory, CommandTriggerCodes.FxTagsCompleted, priority: 2);
            if (!string.IsNullOrWhiteSpace(nextRunId))
            {
                _logger?.Append(runId, $"[story {commandStoryId}] Enqueued next status (runId={nextRunId})", "info");
                return true;
            }

            _logger?.Append(runId, $"[story {commandStoryId}] Next status enqueue skipped: no next status available", "info");
            return false;
        }
        catch (Exception ex)
        {
            _logger?.Append(runId, $"[story {commandStoryId}] Failed to enqueue next status: {ex.Message}", "warn");
            return false;
        }
    }

    public bool TryAdvanceAndEnqueueMusic(
        StoryRecord story,
        string runId,
        long commandStoryId,
        bool autolaunchNextCommand)
    {
        try
        {
            var allowNext = _storiesService?.ApplyStatusTransitionWithCleanup(story, CommandStatusCodes.Tagged, runId) ?? true;
            if (!allowNext)
            {
                _logger?.Append(runId, $"[story {commandStoryId}] next status enqueue skipped: delete_next_items attivo", "info");
                return false;
            }

            if (_storiesService != null && !_storiesService.IsTaggedFinalAutoLaunchEnabled())
            {
                _logger?.Append(runId, $"[story {commandStoryId}] next status enqueue skipped: StoryTaggingPipeline final autolaunch disabled", "info");
                return false;
            }

            if (_storiesService != null && !_storiesService.TryValidateTaggedMusic(story, out var musicReason))
            {
                _logger?.Append(runId, $"[story {commandStoryId}] next status enqueue skipped: music validation failed ({musicReason})", "warn");
                return false;
            }

            if (!autolaunchNextCommand)
            {
                _logger?.Append(runId, $"[story {commandStoryId}] next status enqueue skipped: AutolaunchNextCommand disabled", "info");
                return false;
            }

            if (_storiesService == null)
            {
                _logger?.Append(runId, $"[story {commandStoryId}] next status enqueue skipped: stories service missing", "warn");
                return false;
            }

            var refreshedStory = _storiesService.GetStoryById(story.Id) ?? story;
            var nextRunId = _storiesService.EnqueueNextStatusCommand(refreshedStory, CommandTriggerCodes.MusicTagsCompleted, priority: 2);
            if (!string.IsNullOrWhiteSpace(nextRunId))
            {
                _logger?.Append(runId, $"[story {commandStoryId}] Enqueued next status (runId={nextRunId})", "info");
                return true;
            }

            _logger?.Append(runId, $"[story {commandStoryId}] Next status enqueue skipped: no next status available", "info");
            return false;
        }
        catch (Exception ex)
        {
            _logger?.Append(runId, $"[story {commandStoryId}] Failed to enqueue next status: {ex.Message}", "warn");
            return false;
        }
    }
}

