using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands;

public sealed class StartStateDrivenStoryCommand : ICommand
{
    private readonly DatabaseService _database;

    public StartStateDrivenStoryCommand(DatabaseService database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public Task<(bool success, long storyId, string? error)> ExecuteAsync(
        string theme,
        string title,
        int narrativeProfileId,
        int? serieId,
        int? serieEpisode,
        string? plannerMode,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(theme))
        {
            return Task.FromResult<(bool, long, string?)>((false, 0, "Theme is required"));
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return Task.FromResult<(bool, long, string?)>((false, 0, "Title is required"));
        }

        if (narrativeProfileId <= 0)
        {
            return Task.FromResult<(bool, long, string?)>((false, 0, "NarrativeProfileId is required"));
        }

        try
        {
            var storyId = _database.StartStateDrivenStory(
                prompt: theme,
                title: title,
                narrativeProfileId: narrativeProfileId,
                serieId: serieId,
                serieEpisode: serieEpisode,
                plannerMode: string.IsNullOrWhiteSpace(plannerMode) ? null : plannerMode.Trim());

            return Task.FromResult<(bool, long, string?)>((storyId > 0, storyId, storyId > 0 ? null : "Failed to create story"));
        }
        catch (Exception ex)
        {
            return Task.FromResult<(bool, long, string?)>((false, 0, ex.Message));
        }
    }
}
