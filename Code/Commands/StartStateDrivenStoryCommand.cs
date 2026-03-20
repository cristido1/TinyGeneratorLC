using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands;

public sealed class StartStateDrivenStoryCommand : ICommand
{
    private readonly DatabaseService _database;
    private readonly ICallCenter? _callCenter;
    private readonly ICustomLogger? _logger;

    public StartStateDrivenStoryCommand(
        DatabaseService database,
        ICallCenter? callCenter = null,
        ICustomLogger? logger = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _callCenter = callCenter;
        _logger = logger;
    }

    public async Task<(bool success, long storyId, string? error)> ExecuteAsync(
        string theme,
        string title,
        int narrativeProfileId,
        int? serieId,
        int? serieEpisode,
        string? plannerMode,
        string? resourceHints = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(theme))
        {
            return (false, 0, "Theme is required");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return (false, 0, "Title is required");
        }

        if (narrativeProfileId <= 0)
        {
            return (false, 0, "NarrativeProfileId is required");
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

            if (storyId > 0)
            {
                _ = await TryInitializeResourceManagerAsync(
                    storyId,
                    title,
                    theme,
                    serieId,
                    serieEpisode,
                    resourceHints,
                    ct).ConfigureAwait(false);
            }

            return (storyId > 0, storyId, storyId > 0 ? null : "Failed to create story");
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    private async Task<bool> TryInitializeResourceManagerAsync(
        long storyId,
        string title,
        string theme,
        int? serieId,
        int? serieEpisode,
        string? resourceHints,
        CancellationToken ct)
    {
        if (_callCenter == null)
        {
            return false;
        }

        var resourceManager = _database.GetAgentByRole("resource_manager");
        if (resourceManager == null || !resourceManager.IsActive)
        {
            return false;
        }

        var systemPrompt = resourceManager.SystemPrompt ?? resourceManager.UserPrompt ?? "Genera lo stato iniziale risorse in JSON valido.";
        var history = new ChatHistory();
        history.AddSystem(systemPrompt);
        history.AddUser(BuildResourceManagerInitPrompt(storyId, title, theme, serieId, serieEpisode, resourceHints));

        var options = new CallOptions
        {
            Operation = "state_driven_resource_manager_init",
            Timeout = TimeSpan.FromSeconds(120),
            MaxRetries = 1,
            UseResponseChecker = true,
            AllowFallback = true,
            AskFailExplanation = true
        };
        options.DeterministicChecks.Add(new CheckEmpty
        {
            Options = Microsoft.Extensions.Options.Options.Create<object>(new Dictionary<string, object>
            {
                ["ErrorMessage"] = "resource_manager init: risposta vuota"
            })
        });

        var call = await _callCenter.CallAgentAsync(
            storyId: storyId,
            threadId: ("resource_init:" + storyId).GetHashCode(StringComparison.Ordinal),
            agent: resourceManager,
            history: history,
            options: options,
            cancellationToken: ct).ConfigureAwait(false);

        if (!call.Success || string.IsNullOrWhiteSpace(call.ResponseText))
        {
            return false;
        }

        var canonStateJson = ExtractCanonStateJson(call.ResponseText);
        if (string.IsNullOrWhiteSpace(canonStateJson))
        {
            return false;
        }

        _database.ReplaceInitialStoryResourceState(
            storyId: storyId,
            seriesId: serieId,
            episodeNumber: serieEpisode,
            canonicalStateJson: canonStateJson,
            sourceEngine: "state_driven");
        _logger?.Append(string.Empty, $"[story {storyId}] ResourceManager INIT completato.");
        return true;
    }

    private static string BuildResourceManagerInitPrompt(
        long storyId,
        string title,
        string theme,
        int? serieId,
        int? serieEpisode,
        string? resourceHints)
    {
        return
            "Genera lo stato iniziale delle risorse narrative e restituisci SOLO JSON valido." + Environment.NewLine +
            "Mode=INIT." + Environment.NewLine +
            "Puoi dedurre risorse dal prompt; puoi includere personaggi, navi, mezzi, energia, munizioni, oggetti." + Environment.NewLine +
            "Se mancano indicazioni psicologiche esplicite, deducile in modo plausibile dal contesto." + Environment.NewLine + Environment.NewLine +
            "{\n" +
            $"  \"mode\": \"INIT\",\n" +
            $"  \"story_id\": {storyId},\n" +
            $"  \"series_id\": {(serieId.HasValue ? serieId.Value.ToString() : "null")},\n" +
            $"  \"episode_number\": {(serieEpisode.HasValue ? serieEpisode.Value.ToString() : "null")},\n" +
            $"  \"story_title\": \"{EscapeJson(title)}\",\n" +
            $"  \"story_outline\": \"{EscapeJson(theme)}\",\n" +
            $"  \"resource_hints\": \"{EscapeJson(resourceHints ?? string.Empty)}\"\n" +
            "}\n\n" +
            "Output atteso: { \"canon_state\": { ... } }";
    }

    private static string ExtractCanonStateJson(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            if (doc.RootElement.TryGetProperty("canon_state", out var canonState) &&
                canonState.ValueKind == JsonValueKind.Object)
            {
                return canonState.GetRawText();
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("resources", out _))
            {
                return doc.RootElement.GetRawText();
            }
        }
        catch
        {
            // best effort
        }

        return string.Empty;
    }

    private static string EscapeJson(string text)
    {
        return (text ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }
}
