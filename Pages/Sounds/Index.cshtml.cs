using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using TinyGenerator.Models;
using TinyGenerator.Services;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Pages.Sounds;

public class IndexModel : PageModel
{
    private readonly DatabaseService _database;
    private readonly SoundScoringService _soundScoring;
    private readonly ICommandDispatcher? _dispatcher;
    private readonly ILogger<IndexModel>? _logger;
    private static readonly string[] AllowedTypes = new[] { "fx", "music", "amb" };
    private static readonly string SoundsRoot = Path.GetFullPath(@"C:\Users\User\Documents\ai\sounds_library");

    public IndexModel(
        DatabaseService database,
        SoundScoringService soundScoring,
        ICommandDispatcher? dispatcher = null,
        ILogger<IndexModel>? logger = null)
    {
        _database = database;
        _soundScoring = soundScoring;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public IReadOnlyList<Sound> Items { get; set; } = Array.Empty<Sound>();

    [BindProperty(SupportsGet = true)]
    public string? TypeFilter { get; set; }

    public void OnGet()
    {
        var type = NormalizeTypeOrNull(TypeFilter);
        Items = _database.ListSounds(type: type);
        TypeFilter = type;
    }

    public IActionResult OnPostDelete(int id)
    {
        try
        {
            _database.DeleteSoundById(id);
            TempData["Success"] = "Suono eliminato.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToPage(new { typeFilter = TypeFilter });
    }

    public IActionResult OnPostRecalculateScores(bool onlyMissing = false)
    {
        try
        {
            var result = _soundScoring.RecalculateScores(onlyMissingFinal: onlyMissing);
            TempData["Success"] = $"Score suoni ricalcolati. Processati={result.Processed}, aggiornati={result.Updated}, errori={result.Failed}.";
            if (result.Errors.Count > 0)
            {
                TempData["Error"] = string.Join(" | ", result.Errors.Take(3)) + (result.Errors.Count > 3 ? $" (+{result.Errors.Count - 3} altri)" : string.Empty);
            }
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToPage(new { typeFilter = TypeFilter });
    }

    public IActionResult OnPostEnqueueRecalculateMissingScores()
    {
        if (_dispatcher == null)
        {
            TempData["Error"] = "CommandDispatcher non disponibile.";
            return RedirectToPage(new { typeFilter = TypeFilter });
        }

        try
        {
            var runId = $"sound_scores_missing_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            var cmd = new RecalculateSoundScoresCommand(_soundScoring, onlyMissingFinal: true);
            var handle = _dispatcher.Enqueue(
                cmd,
                runId: runId,
                threadScope: "sounds/scoring",
                metadata: new Dictionary<string, string>
                {
                    ["scope"] = "missing",
                    ["entity"] = "sounds"
                },
                priority: 3);

            TempData["Success"] = $"Ricalcolo score mancanti accodato (runId={handle.RunId}).";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Errore accodando ricalcolo score mancanti suoni");
            TempData["Error"] = ex.Message;
        }

        return RedirectToPage(new { typeFilter = TypeFilter });
    }

    public IActionResult OnPostEnqueueRecalculateRowScore(int id)
    {
        if (id <= 0)
        {
            TempData["Error"] = "Id suono non valido.";
            return RedirectToPage(new { typeFilter = TypeFilter });
        }

        if (_dispatcher == null)
        {
            TempData["Error"] = "CommandDispatcher non disponibile.";
            return RedirectToPage(new { typeFilter = TypeFilter });
        }

        try
        {
            var runId = $"sound_score_{id}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            var cmd = new RecalculateSoundScoresCommand(_soundScoring, soundId: id);
            var handle = _dispatcher.Enqueue(
                cmd,
                runId: runId,
                threadScope: $"sounds/{id}",
                metadata: new Dictionary<string, string>
                {
                    ["soundId"] = id.ToString(),
                    ["scope"] = "single",
                    ["entity"] = "sounds"
                },
                priority: 2);

            TempData["Success"] = $"Ricalcolo score accodato per suono #{id} (runId={handle.RunId}).";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Errore accodando ricalcolo score per soundId={SoundId}", id);
            TempData["Error"] = ex.Message;
        }

        return RedirectToPage(new { typeFilter = TypeFilter });
    }

    public IActionResult OnPostEnqueueBackfillMissingDurations()
    {
        if (_dispatcher == null)
        {
            TempData["Error"] = "CommandDispatcher non disponibile.";
            return RedirectToPage(new { typeFilter = TypeFilter });
        }

        try
        {
            var runId = $"sound_durations_missing_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            var cmd = new BackfillMissingSoundDurationsCommand(_soundScoring);
            var handle = _dispatcher.Enqueue(
                cmd,
                runId: runId,
                threadScope: "sounds/duration",
                metadata: new Dictionary<string, string>
                {
                    ["scope"] = "missing_duration",
                    ["entity"] = "sounds"
                },
                priority: 3);

            TempData["Success"] = $"Backfill durate mancanti accodato (runId={handle.RunId}).";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Errore accodando backfill durate mancanti suoni");
            TempData["Error"] = ex.Message;
        }

        return RedirectToPage(new { typeFilter = TypeFilter });
    }

    public IActionResult OnGetAudio(int id)
    {
        var item = _database.GetSoundById(id);
        if (item == null || string.IsNullOrWhiteSpace(item.FilePath))
        {
            return NotFound();
        }

        try
        {
            var full = Path.GetFullPath(item.FilePath);
            if (!full.StartsWith(SoundsRoot, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("Percorso non consentito.");
            }

            if (!System.IO.File.Exists(full))
            {
                return NotFound();
            }

            if (!string.Equals(Path.GetExtension(full), ".wav", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("Solo file .wav supportati.");
            }

            return PhysicalFile(full, "audio/wav");
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }

    public static string NormalizeTypeOrNull(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        var t = type.Trim().ToLowerInvariant();
        return AllowedTypes.Contains(t) ? t : null;
    }
}
