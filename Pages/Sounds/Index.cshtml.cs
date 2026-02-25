using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using TinyGenerator.Models;
using TinyGenerator.Services;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Pages.Sounds;

public class IndexModel : PageModel
{
    private const string TypeFilterCookieName = "sounds_type_filter";
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
        var hasTypeFilterQuery = Request.Query.ContainsKey("typeFilter");
        var requestedType = hasTypeFilterQuery
            ? TypeFilter
            : Request.Cookies[TypeFilterCookieName];

        var type = NormalizeTypeOrNull(requestedType);
        // Server-side DataTables paging via API: do not preload the whole sounds table.
        Items = Array.Empty<Sound>();
        TypeFilter = type;

        if (hasTypeFilterQuery)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                Response.Cookies.Delete(TypeFilterCookieName);
            }
            else
            {
                Response.Cookies.Append(
                    TypeFilterCookieName,
                    type,
                    new CookieOptions
                    {
                        HttpOnly = false,
                        IsEssential = true,
                        Expires = DateTimeOffset.UtcNow.AddDays(90)
                    });
            }
        }
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

    public IActionResult OnPostEnqueueRecalculateScores(bool onlyMissing = false)
    {
        if (_dispatcher == null)
        {
            TempData["Error"] = "CommandDispatcher non disponibile.";
            return RedirectToPage(new { typeFilter = TypeFilter });
        }

        try
        {
            var runId = onlyMissing
                ? $"sound_scores_missing_{DateTime.UtcNow:yyyyMMddHHmmssfff}"
                : $"sound_scores_all_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            var cmd = new RecalculateSoundScoresCommand(_soundScoring, onlyMissingFinal: onlyMissing);
            var handle = _dispatcher.Enqueue(
                cmd,
                runId: runId,
                threadScope: "sounds/scoring",
                metadata: new Dictionary<string, string>
                {
                    ["scope"] = onlyMissing ? "missing" : "all",
                    ["entity"] = "sounds"
                },
                priority: 3);

            TempData["Success"] = onlyMissing
                ? $"Ricalcolo score mancanti accodato (runId={handle.RunId})."
                : $"Ricalcolo score (tutti) accodato (runId={handle.RunId}).";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Errore accodando ricalcolo score suoni (onlyMissing={OnlyMissing})", onlyMissing);
            TempData["Error"] = ex.Message;
        }

        return RedirectToPage(new { typeFilter = TypeFilter });
    }

    public IActionResult OnPostEnqueueRecalculateMissingScores()
    {
        return OnPostEnqueueRecalculateScores(onlyMissing: true);
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

            var ext = Path.GetExtension(full);
            var contentType = ext.ToLowerInvariant() switch
            {
                ".wav" => "audio/wav",
                ".mp3" => "audio/mpeg",
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(contentType))
            {
                return BadRequest("Solo file .wav e .mp3 supportati.");
            }

            return PhysicalFile(full, contentType);
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
