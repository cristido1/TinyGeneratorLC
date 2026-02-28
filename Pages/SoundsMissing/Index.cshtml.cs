using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Pages.SoundsMissing;

public class IndexModel : PageModel
{
    private static readonly string[] AllowedTypes = new[] { "fx", "amb", "music" };
    private static readonly string[] AllowedStatuses = new[] { "open", "resolved" };
    private readonly DatabaseService _database;
    private readonly ICommandDispatcher? _dispatcher;
    private readonly SoundSearchService? _soundSearch;
    private readonly StoriesService? _stories;
    private readonly SoundScoringService? _soundScoring;

    public IndexModel(
        DatabaseService database,
        ICommandDispatcher? dispatcher = null,
        SoundSearchService? soundSearch = null,
        StoriesService? stories = null,
        SoundScoringService? soundScoring = null)
    {
        _database = database;
        _dispatcher = dispatcher;
        _soundSearch = soundSearch;
        _stories = stories;
        _soundScoring = soundScoring;
    }

    public IReadOnlyList<SoundMissing> Items { get; private set; } = Array.Empty<SoundMissing>();
    public string? LoadError { get; private set; }
    [TempData] public string? ActionMessage { get; set; }
    [TempData] public string? ActionMessageType { get; set; }

    public string? TypeFilter { get; set; }
    public string? StatusFilter { get; set; }

    public void OnGet(string? typeFilter, string? statusFilter)
    {
        TypeFilter = NormalizeOrNull(typeFilter, AllowedTypes);
        StatusFilter = NormalizeOrNull(statusFilter, AllowedStatuses);

        try
        {
            Items = _database.ListMissingSounds(status: StatusFilter, type: TypeFilter);
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
            Items = Array.Empty<SoundMissing>();
        }
    }

    public IActionResult OnPostRecheckAgainstSounds(string? typeFilter, string? statusFilter)
    {
        TypeFilter = NormalizeOrNull(typeFilter, AllowedTypes);
        StatusFilter = NormalizeOrNull(statusFilter, AllowedStatuses);

        try
        {
            var missing = _database.ListMissingSounds(status: "open", type: TypeFilter);
            var sounds = _database.ListSounds(type: TypeFilter)
                .Where(s => s.Enabled)
                .ToList();

            var soundTagsByType = sounds
                .GroupBy(s => NormalizeType(s.Type))
                .ToDictionary(
                    g => g.Key,
                    g => g.SelectMany(s => ParseTagTokens(s.Tags))
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToHashSet(StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);

            var toDelete = new List<long>();
            int skippedNoTags = 0;

            foreach (var row in missing)
            {
                var type = NormalizeType(row.Type);
                if (string.IsNullOrWhiteSpace(type)) continue;

                var missingTags = ParseTagTokens(row.Tags).ToList();
                if (missingTags.Count == 0)
                {
                    skippedNoTags++;
                    continue;
                }

                if (!soundTagsByType.TryGetValue(type, out var availableTags) || availableTags.Count == 0)
                {
                    continue;
                }

                if (missingTags.Any(t => availableTags.Contains(t)))
                {
                    toDelete.Add(row.Id);
                }
            }

            var deleted = _database.DeleteMissingSoundsByIds(toDelete);
            ActionMessageType = deleted > 0 ? "success" : "info";
            ActionMessage = deleted > 0
                ? $"Verifica completata: rimossi {deleted} record da sounds_missing (match per tag trovato)."
                : $"Verifica completata: nessun record da rimuovere. {(skippedNoTags > 0 ? $"Record senza tag: {skippedNoTags}." : string.Empty)}";
        }
        catch (Exception ex)
        {
            ActionMessageType = "danger";
            ActionMessage = $"Errore durante la verifica dei sounds mancanti: {ex.Message}";
        }

        return RedirectToPage("/SoundsMissing/Index", new { typeFilter = TypeFilter, statusFilter = StatusFilter });
    }

    public IActionResult OnPostEnqueueSearchOne(long id, string? typeFilter, string? statusFilter)
    {
        TypeFilter = NormalizeOrNull(typeFilter, AllowedTypes);
        StatusFilter = NormalizeOrNull(statusFilter, AllowedStatuses);

        if (id <= 0)
        {
            ActionMessageType = "danger";
            ActionMessage = "Id sounds_missing non valido.";
            return RedirectToPage("/SoundsMissing/Index", new { typeFilter = TypeFilter, statusFilter = StatusFilter });
        }

        if (_dispatcher == null || _soundSearch == null)
        {
            ActionMessageType = "danger";
            ActionMessage = "Dispatcher o SoundSearchService non disponibile.";
            return RedirectToPage("/SoundsMissing/Index", new { typeFilter = TypeFilter, statusFilter = StatusFilter });
        }

        try
        {
            var cmd = new SearchMissingSoundCommand(_soundSearch, id);
            var runId = $"search_missing_sound_{id}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            var handle = _dispatcher.Enqueue(
                cmd,
                runId: runId,
                threadScope: $"sounds_missing/{id}",
                metadata: new Dictionary<string, string>
                {
                    ["entity"] = "sounds_missing",
                    ["scope"] = "single",
                    ["missingId"] = id.ToString()
                },
                priority: 3);
            ActionMessageType = "success";
            ActionMessage = $"Ricerca online accodata per missing #{id} (runId={handle.RunId}).";
        }
        catch (Exception ex)
        {
            ActionMessageType = "danger";
            ActionMessage = $"Errore accodando la ricerca: {ex.Message}";
        }

        return RedirectToPage("/SoundsMissing/Index", new { typeFilter = TypeFilter, statusFilter = StatusFilter });
    }

    public IActionResult OnPostEnqueueSearchAll(string? typeFilter, string? statusFilter)
    {
        TypeFilter = NormalizeOrNull(typeFilter, AllowedTypes);
        StatusFilter = NormalizeOrNull(statusFilter, AllowedStatuses);

        if (_dispatcher == null || _soundSearch == null)
        {
            ActionMessageType = "danger";
            ActionMessage = "Dispatcher o SoundSearchService non disponibile.";
            return RedirectToPage("/SoundsMissing/Index", new { typeFilter = TypeFilter, statusFilter = StatusFilter });
        }

        try
        {
            var cmd = new SearchAllMissingSoundsCommand(_soundSearch, _dispatcher, _soundScoring);
            var runId = $"search_all_missing_sounds_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            var handle = _dispatcher.Enqueue(
                cmd,
                runId: runId,
                threadScope: "sounds_missing/batch",
                metadata: new Dictionary<string, string>
                {
                    ["entity"] = "sounds_missing",
                    ["scope"] = "batch"
                },
                priority: 3);
            ActionMessageType = "success";
            ActionMessage = $"Ricerca batch accodata (runId={handle.RunId}).";
        }
        catch (Exception ex)
        {
            ActionMessageType = "danger";
            ActionMessage = $"Errore accodando la ricerca batch: {ex.Message}";
        }

        return RedirectToPage("/SoundsMissing/Index", new { typeFilter = TypeFilter, statusFilter = StatusFilter });
    }

    public IActionResult OnPostEnqueueResetResolvedStories(string? typeFilter, string? statusFilter)
    {
        TypeFilter = NormalizeOrNull(typeFilter, AllowedTypes);
        StatusFilter = NormalizeOrNull(statusFilter, AllowedStatuses);

        if (_dispatcher == null || _stories == null)
        {
            ActionMessageType = "danger";
            ActionMessage = "Dispatcher o StoriesService non disponibile.";
            return RedirectToPage("/SoundsMissing/Index", new { typeFilter = TypeFilter, statusFilter = StatusFilter });
        }

        try
        {
            var cmd = new ResetStoriesWithResolvedMissingSoundsCommand(_database, _stories);
            var runId = $"reset_stories_resolved_missing_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            var handle = _dispatcher.Enqueue(
                cmd,
                runId: runId,
                threadScope: "sounds_missing/reset_stories",
                metadata: new Dictionary<string, string>
                {
                    ["entity"] = "sounds_missing",
                    ["scope"] = "reset_stories_from_resolved"
                },
                priority: 2);

            ActionMessageType = "success";
            ActionMessage = $"Reset storie da sounds_missing resolved accodato (runId={handle.RunId}).";
        }
        catch (Exception ex)
        {
            ActionMessageType = "danger";
            ActionMessage = $"Errore accodando il reset storie: {ex.Message}";
        }

        return RedirectToPage("/SoundsMissing/Index", new { typeFilter = TypeFilter, statusFilter = StatusFilter });
    }

    public IActionResult OnPostDeleteResolved(string? typeFilter, string? statusFilter)
    {
        TypeFilter = NormalizeOrNull(typeFilter, AllowedTypes);
        StatusFilter = NormalizeOrNull(statusFilter, AllowedStatuses);

        try
        {
            var resolved = _database.ListMissingSounds(status: "resolved", type: TypeFilter);
            var ids = resolved.Select(r => r.Id).Where(id => id > 0).Distinct().ToList();
            var deleted = _database.DeleteMissingSoundsByIds(ids);

            ActionMessageType = deleted > 0 ? "success" : "info";
            ActionMessage = deleted > 0
                ? $"Cancellati {deleted} record sounds_missing con stato resolved."
                : "Nessun record resolved da cancellare.";
        }
        catch (Exception ex)
        {
            ActionMessageType = "danger";
            ActionMessage = $"Errore cancellando i record resolved: {ex.Message}";
        }

        return RedirectToPage("/SoundsMissing/Index", new { typeFilter = TypeFilter, statusFilter = StatusFilter });
    }

    public IActionResult OnPostToggleStatus(long id, string? currentStatus)
    {
        if (id <= 0)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return new JsonResult(new { success = false, message = "Id sounds_missing non valido." });
        }

        try
        {
            var row = _database.GetMissingSoundById(id);
            if (row == null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return new JsonResult(new { success = false, message = $"Record sounds_missing #{id} non trovato." });
            }

            var normalizedCurrent =
                NormalizeOrNull(currentStatus, AllowedStatuses)
                ?? NormalizeOrNull(row.Status, AllowedStatuses)
                ?? "open";

            var nextStatus = string.Equals(normalizedCurrent, "open", StringComparison.OrdinalIgnoreCase)
                ? "resolved"
                : "open";

            var updated = _database.UpdateMissingSoundStatus(id, nextStatus);
            if (updated <= 0)
            {
                Response.StatusCode = StatusCodes.Status500InternalServerError;
                return new JsonResult(new { success = false, message = "Nessun aggiornamento applicato." });
            }

            return new JsonResult(new { success = true, id, status = nextStatus });
        }
        catch (Exception ex)
        {
            Response.StatusCode = StatusCodes.Status500InternalServerError;
            return new JsonResult(new { success = false, message = ex.Message });
        }
    }

    private static string? NormalizeOrNull(string? value, string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToLowerInvariant();
        return allowed.Contains(normalized) ? normalized : null;
    }

    private static string NormalizeType(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static IEnumerable<string> ParseTagTokens(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            yield break;

        foreach (var chunk in Regex.Split(raw, @"[,;|\r\n]+"))
        {
            var token = NormalizeTagToken(chunk);
            if (!string.IsNullOrWhiteSpace(token))
            {
                yield return token;
            }
        }
    }

    private static string NormalizeTagToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return string.Empty;
        var normalized = token.Trim().Trim('[', ']', '"', '\'').ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"\s+", "_");
        normalized = Regex.Replace(normalized, @"[^a-z0-9_]+", string.Empty);
        normalized = Regex.Replace(normalized, @"_+", "_").Trim('_');
        return normalized;
    }
}
