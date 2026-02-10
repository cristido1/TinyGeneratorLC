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
}

