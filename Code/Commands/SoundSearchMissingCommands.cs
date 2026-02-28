using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands;

public sealed class SearchMissingSoundCommand : ICommand
{
    private readonly SoundSearchService _service;
    private readonly long _missingId;

    public SearchMissingSoundCommand(SoundSearchService service, long missingId)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _missingId = missingId;
    }

    public string CommandName => "search_missing_sound";
    public int Priority => 3;
    public event EventHandler<CommandProgressEventArgs>? Progress;

    public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default, string? runId = null)
    {
        if (_missingId <= 0) return new CommandResult(false, "missingId non valido");
        var customLogger = ServiceLocator.Services?.GetService(typeof(ICustomLogger)) as ICustomLogger;

        void Report(int current, int max, string desc)
            => Progress?.Invoke(this, new CommandProgressEventArgs(current, max, desc));

        Report(0, 1, $"Avvio ricerca sound missing #{_missingId}");
        if (!string.IsNullOrWhiteSpace(runId))
        {
            customLogger?.Append(runId!, $"[SoundSearchCommand] Enter ExecuteAsync missingId={_missingId}; runId={runId}");
        }
        var r = await _service.ProcessOneMissingSoundAsync(_missingId, ct, Report, runId).ConfigureAwait(false);
        var ok = r.Status is "found" or "skipped";
        var msg = $"missingId={_missingId} status={r.Status}; cercati={r.CandidatesSeen}; trovati={r.InsertedCount}; errori={r.Errors.Count}. {r.Message}";
        if (!string.IsNullOrWhiteSpace(runId))
        {
            customLogger?.Append(runId!, $"[SoundSearchCommand] Exit ExecuteAsync missingId={_missingId}; status={r.Status}; candidates={r.CandidatesSeen}; inserted={r.InsertedCount}");
        }
        Report(1, 1, msg);
        return new CommandResult(ok, msg);
    }

    public Task<CommandResult> Execute(CommandContext context)
        => ExecuteAsync(context.CancellationToken, context.RunId);
}

public sealed class SearchAllMissingSoundsCommand : ICommand
{
    private readonly SoundSearchService _service;
    private readonly ICommandDispatcher? _dispatcher;
    private readonly SoundScoringService? _soundScoring;
    private readonly int? _limit;

    public SearchAllMissingSoundsCommand(
        SoundSearchService service,
        ICommandDispatcher? dispatcher = null,
        SoundScoringService? soundScoring = null,
        int? limit = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dispatcher = dispatcher;
        _soundScoring = soundScoring;
        _limit = limit;
    }

    public string CommandName => "search_all_missing_sounds";
    public int Priority => 3;
    public bool Batch => true;
    public event EventHandler<CommandProgressEventArgs>? Progress;

    public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default, string? runId = null)
    {
        var missing = _service.ListOpenMissingSearchable(_limit).ToList();
        var customLogger = ServiceLocator.Services?.GetService(typeof(ICustomLogger)) as ICustomLogger;
        if (!string.IsNullOrWhiteSpace(runId))
        {
            customLogger?.Append(runId!, $"[SoundSearchBatch] Start batch items={missing.Count} limit={_limit?.ToString() ?? "<null>"}");
        }
        if (missing.Count == 0)
        {
            Progress?.Invoke(this, new CommandProgressEventArgs(1, 1, "Nessun sounds_missing (fx/amb) aperto da processare."));
            return new CommandResult(true, "Nessun sounds_missing aperto da processare");
        }

        int searched = 0, found = 0, notFound = 0, skipped = 0, errors = 0, inserted = 0;
        for (var i = 0; i < missing.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var row = missing[i];
            Progress?.Invoke(this, new CommandProgressEventArgs(
                i + 1, missing.Count,
                $"[{i + 1}/{missing.Count}] missingId={row.Id} type={row.Type} | cercati={searched} trovati={found} non_trovati={notFound}"));

            var r = await _service.ProcessOneMissingSoundAsync(row.Id, ct, runId: runId).ConfigureAwait(false);
            searched++;
            inserted += r.InsertedCount;
            if (r.Status == "found") found++;
            else if (r.Status == "not_found") notFound++;
            else if (r.Status == "skipped") skipped++;
            else errors++;
        }

        var summary = $"Completato: cercati={searched}, trovati={found}, non_trovati={notFound}, skipped={skipped}, errori={errors}, suoni_inseriti={inserted}";
        if (!string.IsNullOrWhiteSpace(runId))
        {
            customLogger?.Append(runId!, $"[SoundSearchBatch] {summary}");
        }

        // Follow-up batch commands: first durations (for new mp3/wav files), then missing scores.
        if (_dispatcher != null && _soundScoring != null)
        {
            try
            {
                var durationCmd = new BackfillMissingSoundDurationsCommand(_soundScoring);
                var durationHandle = _dispatcher.Enqueue(
                    durationCmd,
                    runId: $"sound_durations_missing_{DateTime.UtcNow:yyyyMMddHHmmssfff}",
                    threadScope: "sounds/duration",
                    metadata: new Dictionary<string, string>
                    {
                        ["scope"] = "missing_duration",
                        ["entity"] = "sounds",
                        ["trigger"] = "search_all_missing_sounds"
                    },
                    priority: 3);

                var scoreCmd = new RecalculateSoundScoresCommand(_soundScoring, onlyMissingFinal: true);
                var scoreHandle = _dispatcher.Enqueue(
                    scoreCmd,
                    runId: $"sound_scores_missing_{DateTime.UtcNow:yyyyMMddHHmmssfff}",
                    threadScope: "sounds/scoring",
                    metadata: new Dictionary<string, string>
                    {
                        ["scope"] = "missing",
                        ["entity"] = "sounds",
                        ["trigger"] = "search_all_missing_sounds"
                    },
                    priority: 3);

                if (!string.IsNullOrWhiteSpace(runId))
                {
                    customLogger?.Append(runId!,
                        $"[SoundSearchBatch] Follow-up accodati: durations(runId={durationHandle.RunId}), scores_missing(runId={scoreHandle.RunId})");
                }
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrWhiteSpace(runId))
                {
                    customLogger?.Append(runId!, $"[SoundSearchBatch][WARN] Errore accodando follow-up (durate/score): {ex.Message}", "warn");
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(runId))
        {
            customLogger?.Append(runId!, "[SoundSearchBatch][WARN] Follow-up durate/score non accodati: dispatcher o SoundScoringService non disponibili.", "warn");
        }

        Progress?.Invoke(this, new CommandProgressEventArgs(missing.Count, missing.Count, summary));
        return new CommandResult(errors == 0, summary);
    }

    public Task<CommandResult> Execute(CommandContext context)
        => ExecuteAsync(context.CancellationToken, context.RunId);
}

public sealed class ResetStoriesWithResolvedMissingSoundsCommand : ICommand
{
    private readonly DatabaseService _database;
    private readonly StoriesService _stories;

    public ResetStoriesWithResolvedMissingSoundsCommand(DatabaseService database, StoriesService stories)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _stories = stories ?? throw new ArgumentNullException(nameof(stories));
    }

    public string CommandName => "reset_stories_with_resolved_missing_sounds";
    public int Priority => 2;
    public bool Batch => true;
    public event EventHandler<CommandProgressEventArgs>? Progress;

    public Task<CommandResult> Execute(CommandContext context)
        => ExecuteAsync(context.CancellationToken, context.RunId);

    public Task<CommandResult> ExecuteAsync(CancellationToken ct = default, string? runId = null)
    {
        var resolved = _database.ListMissingSounds(status: "resolved")
            .Where(x => x.StoryId.HasValue && x.StoryId.Value > 0)
            .ToList();
        var openStoryIds = _database.ListMissingSounds(status: "open")
            .Where(x => x.StoryId.HasValue && x.StoryId.Value > 0)
            .Select(x => x.StoryId!.Value)
            .Distinct()
            .ToHashSet();

        var groups = resolved
            .GroupBy(x => x.StoryId!.Value)
            .Where(g => !openStoryIds.Contains(g.Key))
            .OrderBy(g => g.Key)
            .ToList();

        if (groups.Count == 0)
        {
            Progress?.Invoke(this, new CommandProgressEventArgs(1, 1, "Nessuna storia idonea (resolved senza open)."));
            return Task.FromResult(new CommandResult(true, "Nessuna storia idonea da resettare"));
        }

        int resetOk = 0, resetFail = 0, deletedResolved = 0;
        var errors = new List<string>();

        for (var i = 0; i < groups.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var g = groups[i];
            var storyId = g.Key;
            Progress?.Invoke(this, new CommandProgressEventArgs(i + 1, groups.Count,
                $"Story {storyId}: reset a tts_generated ({i + 1}/{groups.Count}) • ok={resetOk} fail={resetFail}"));

            var reset = _stories.ResetStoryAudioPipelineToTtsGenerated(storyId, runId);
            if (!reset.success)
            {
                resetFail++;
                errors.Add($"story {storyId}: {reset.message}");
            }
            else
            {
                resetOk++;
            }

            try
            {
                // Cancellazione robusta: elimina tutti i resolved correnti della storia.
                deletedResolved += _database.DeleteMissingSoundsByStoryAndStatus(storyId, "resolved");
            }
            catch (Exception ex)
            {
                errors.Add($"story {storyId}: delete resolved fallita: {ex.Message}");
            }
        }

        var msg = $"Reset completato: storie_ok={resetOk}, fail={resetFail}, resolved_cancellati={deletedResolved}.";
        if (errors.Count > 0)
        {
            msg += " " + string.Join(" | ", errors.Take(3)) + (errors.Count > 3 ? $" (+{errors.Count - 3} altri)" : string.Empty);
        }

        Progress?.Invoke(this, new CommandProgressEventArgs(groups.Count, groups.Count, msg));
        return Task.FromResult(new CommandResult(resetFail == 0, msg));
    }
}
