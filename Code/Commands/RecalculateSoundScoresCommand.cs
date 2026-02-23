using Microsoft.Extensions.Logging;

namespace TinyGenerator.Services.Commands;

public sealed class RecalculateSoundScoresCommand : ICommand
{
    private readonly SoundScoringService _soundScoringService;
    private readonly ILogger<RecalculateSoundScoresCommand>? _logger;
    private readonly int? _soundId;
    private readonly bool _onlyMissingFinal;
    private readonly int? _limit;

    public RecalculateSoundScoresCommand(
        SoundScoringService soundScoringService,
        int? soundId = null,
        bool onlyMissingFinal = false,
        int? limit = null,
        ILogger<RecalculateSoundScoresCommand>? logger = null)
    {
        _soundScoringService = soundScoringService ?? throw new ArgumentNullException(nameof(soundScoringService));
        _soundId = soundId;
        _onlyMissingFinal = onlyMissingFinal;
        _limit = limit;
        _logger = logger;
    }

    public string CommandName => _soundId.HasValue ? "recalculate_sound_score" : "recalculate_sound_scores";
    public bool Batch => !_soundId.HasValue;
    public int Priority => _soundId.HasValue ? 2 : 3;

    public CommandResult ExecuteAsync(CancellationToken ct = default, string? runId = null)
    {
        ct.ThrowIfCancellationRequested();

        if (_soundId.HasValue)
        {
            _soundScoringService.RecalculateScoreForSound(_soundId.Value);
            var message = $"Score ricalcolato per soundId={_soundId.Value}.";
            _logger?.LogInformation("Sound score recalculated for soundId={SoundId} (runId={RunId})", _soundId.Value, runId);
            return new CommandResult(true, message);
        }

        var result = _soundScoringService.RecalculateScores(onlyMissingFinal: _onlyMissingFinal, limit: _limit);
        var scopeText = _onlyMissingFinal ? "mancanti" : "tutti";
        var msg = $"Ricalcolo score {scopeText} completato. Processati={result.Processed}, aggiornati={result.Updated}, errori={result.Failed}.";
        if (result.Errors.Count > 0)
        {
            msg += " " + string.Join(" | ", result.Errors.Take(3)) + (result.Errors.Count > 3 ? $" (+{result.Errors.Count - 3} altri)" : string.Empty);
        }

        _logger?.LogInformation(
            "Batch sound scoring completed mode={Mode} processed={Processed} updated={Updated} failed={Failed} runId={RunId}",
            scopeText, result.Processed, result.Updated, result.Failed, runId);

        return new CommandResult(result.Failed == 0, msg);
    }
}

public sealed class BackfillMissingSoundDurationsCommand : ICommand
{
    private readonly SoundScoringService _soundScoringService;
    private readonly ILogger<BackfillMissingSoundDurationsCommand>? _logger;
    private readonly int? _limit;

    public BackfillMissingSoundDurationsCommand(
        SoundScoringService soundScoringService,
        int? limit = null,
        ILogger<BackfillMissingSoundDurationsCommand>? logger = null)
    {
        _soundScoringService = soundScoringService ?? throw new ArgumentNullException(nameof(soundScoringService));
        _limit = limit;
        _logger = logger;
    }

    public string CommandName => "backfill_missing_sound_durations";
    public bool Batch => true;
    public int Priority => 3;

    public CommandResult ExecuteAsync(CancellationToken ct = default, string? runId = null)
    {
        ct.ThrowIfCancellationRequested();

        var result = _soundScoringService.BackfillMissingDurations(_limit);
        var msg = $"Backfill durate suoni completato. Processati={result.Processed}, aggiornati={result.Updated}, errori={result.Failed}.";
        if (result.Errors.Count > 0)
        {
            msg += " " + string.Join(" | ", result.Errors.Take(3)) + (result.Errors.Count > 3 ? $" (+{result.Errors.Count - 3} altri)" : string.Empty);
        }

        _logger?.LogInformation(
            "Backfill missing sound durations completed processed={Processed} updated={Updated} failed={Failed} runId={RunId}",
            result.Processed, result.Updated, result.Failed, runId);

        return new CommandResult(result.Failed == 0, msg);
    }
}
