using System;
using System.Collections.Generic;
using System.Linq;
using TinyGenerator.Models;

namespace TinyGenerator.Services;

public sealed partial class StoriesService
{
    public string? EnqueueNextStatusCommand(StoryRecord story, string trigger, int priority = 2)
    {
        if (story == null || _commandDispatcher == null)
            return null;

        var statuses = _database.ListAllStoryStatuses();
        var next = GetNextStatusForStory(story, statuses);
        if (next == null)
            return null;

        var command = CreateCommandForStatus(next);
        if (command == null)
            return null;

        var operationName = GetOperationNameForStatus(next) ?? next.FunctionName ?? next.Code ?? "status";
        var storyIdString = story.Id.ToString();
        var nextStatusId = next.Id.ToString();
        var nextStatusCode = next.Code ?? string.Empty;

        try
        {
            var existing = _commandDispatcher.GetActiveCommands().FirstOrDefault(snapshot =>
            {
                if (snapshot.Metadata == null)
                    return false;

                if (!snapshot.Metadata.TryGetValue("storyId", out var sid) ||
                    !string.Equals(sid, storyIdString, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var statusMatch = false;
                if (snapshot.Metadata.TryGetValue("statusId", out var statusId))
                {
                    statusMatch = string.Equals(statusId, nextStatusId, StringComparison.OrdinalIgnoreCase);
                }
                if (!statusMatch && !string.IsNullOrWhiteSpace(nextStatusCode) &&
                    snapshot.Metadata.TryGetValue("statusCode", out var statusCode))
                {
                    statusMatch = string.Equals(statusCode, nextStatusCode, StringComparison.OrdinalIgnoreCase);
                }

                if (!statusMatch &&
                    string.Equals(snapshot.OperationName, operationName, StringComparison.OrdinalIgnoreCase))
                {
                    statusMatch = true;
                }

                if (!statusMatch)
                    return false;

                return string.Equals(snapshot.Status, "queued", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(snapshot.Status, "running", StringComparison.OrdinalIgnoreCase);
            });
            if (existing != null && !string.IsNullOrWhiteSpace(existing.RunId))
            {
                return existing.RunId;
            }
        }
        catch
        {
            // best-effort: ignore de-dup errors
        }

        var operationPrefix = next.Code ?? next.Id.ToString();
        var runId = $"status_{operationPrefix}_{storyIdString}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        var metadata = new Dictionary<string, string>
        {
            ["storyId"] = storyIdString,
            ["operation"] = operationName,
            ["trigger"] = trigger,
            ["statusId"] = nextStatusId,
            ["statusCode"] = nextStatusCode
        };

        TryPopulateAgentMetadata(next, metadata);

        _commandDispatcher.Enqueue(
            operationName,
            async ctx =>
            {
                var latestStory = GetStoryById(story.Id);
                if (latestStory == null)
                    return new CommandResult(false, "Storia non trovata");

                using var runScope = BeginDispatcherRunScope(ctx.RunId, ctx.CancellationToken);
                var (success, message) = await ExecuteStoryCommandAsync(latestStory, command, next);
                if (success)
                {
                    _ = EnqueueNextStatusCommand(latestStory, trigger, priority);
                }
                return new CommandResult(success, message);
            },
            runId: runId,
            threadScope: $"story/status_next/{story.Id}",
            metadata: metadata,
            priority: Math.Max(1, priority));

        return runId;
    }
}
