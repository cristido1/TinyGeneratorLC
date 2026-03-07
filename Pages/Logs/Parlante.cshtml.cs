using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.Logs;

public class ParlanteModel : PageModel
{
    private readonly DatabaseService _db;

    public ParlanteModel(DatabaseService db)
    {
        _db = db;
    }

    public List<StoryOption> Stories { get; private set; } = new();
    public List<SpokenLogLine> Lines { get; private set; } = new();
    public long? StoryId { get; private set; }
    public string StoryTitle { get; private set; } = string.Empty;

    public void OnGet(long? storyId = null)
    {
        Stories = LoadStoryOptions();
        var requested = storyId.GetValueOrDefault();
        StoryId = requested > 0 && Stories.Any(s => s.StoryId == requested)
            ? requested
            : Stories.FirstOrDefault()?.StoryId;
        if (!StoryId.HasValue || StoryId.Value <= 0)
        {
            return;
        }

        var story = _db.GetStoryById(StoryId.Value);
        StoryTitle = string.IsNullOrWhiteSpace(story?.Title) ? $"Storia {StoryId.Value}" : story!.Title!;
        var logs = _db.GetLogsByStoryId(StoryId.Value, 8000);
        Lines = BuildSpokenTimeline(logs, StoryTitle);
    }

    private List<StoryOption> LoadStoryOptions()
    {
        var ids = _db.GetRecentNreStoryIds(200);
        var result = new List<StoryOption>(ids.Count);
        foreach (var id in ids)
        {
            var story = _db.GetStoryById(id);
            if (story == null || story.Deleted)
            {
                continue;
            }

            var title = string.IsNullOrWhiteSpace(story?.Title) ? $"Storia {id}" : story!.Title!;
            result.Add(new StoryOption(id, $"{id} - {title}"));
        }

        return result
            .OrderByDescending(x => x.StoryId)
            .ToList();
    }

    private static List<SpokenLogLine> BuildSpokenTimeline(List<LogEntry> logs, string storyTitle)
    {
        var items = new List<SpokenLogLine>();
        var ordered = (logs ?? new List<LogEntry>())
            .OrderBy(l => l.Id ?? 0L)
            .ToList();

        if (ordered.Count == 0)
        {
            items.Add(new SpokenLogLine(DateTime.UtcNow, "info", "Nessun log disponibile per questa storia."));
            return items;
        }

        var narrations = new List<ScopeNarration>
        {
            new(
                Scope: "nre_planner",
                StartTemplate: $"Nuova storia \"{storyTitle}\", il planner {{0}} sta scrivendo lo schema della trama.",
                SuccessText: "SUCCESSO, abbiamo la trama.",
                FailureFormat: "Sembra che ci sia stato un ERRORE nella generazione del piano: {0}"),
            new(
                Scope: "nre_plan_evaluator",
                StartTemplate: "L'agente {0} sta valutando il piano della storia.",
                SuccessText: "SUCCESSO: il piano ha superato la valutazione.",
                FailureFormat: "Sembra che ci sia stato un ERRORE nella valutazione del piano: {0}"),
            new(
                Scope: "nre_resource_initializer_init",
                StartTemplate: "Adesso che abbiamo la trama, l'agente {0} sta mettendo le risorse iniziali della storia.",
                SuccessText: "SUCCESSO: abbiamo anche le risorse iniziali della storia.",
                FailureFormat: "Sembra che ci sia stato un ERRORE nella generazione delle risorse iniziali: {0}"),
            new(
                Scope: "nre_writer",
                StartTemplate: "L'agente {0} sta scrivendo la storia.",
                SuccessText: "SUCCESSO: la scrittura della storia è completata.",
                FailureFormat: "Sembra che ci sia stato un ERRORE durante la scrittura della storia: {0}",
                IncludeSuccessCount: true,
                SuccessCountFormat: "SUCCESSO: la storia è stata scritta in {0} blocchi."),
            new(
                Scope: "nre_resource_manager_update",
                StartTemplate: "L'agente {0} sta aggiornando lo stato delle risorse narrative.",
                SuccessText: "SUCCESSO: stato risorse aggiornato.",
                FailureFormat: "Sembra che ci sia stato un ERRORE nell'aggiornamento delle risorse: {0}",
                IncludeSuccessCount: true,
                SuccessCountFormat: "SUCCESSO: stato risorse aggiornato in {0} passaggi.")
        };

        // Add generic narration for any additional NRE scope not explicitly mapped.
        var knownScopes = new HashSet<string>(narrations.Select(n => n.Scope), StringComparer.OrdinalIgnoreCase);
        var additionalScopes = ordered
            .Where(l => IsNreScope(l) && IsModelRequest(l))
            .Select(l => NormalizeScope(l.ThreadScope))
            .Where(s => !string.IsNullOrWhiteSpace(s) && !knownScopes.Contains(s!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => ordered.First(l => IsScope(l, s!)).Id ?? 0L)
            .ToList();

        foreach (var scope in additionalScopes)
        {
            narrations.Add(new ScopeNarration(
                Scope: scope!,
                StartTemplate: $"Operazione {scope}: il modello {{0}} sta eseguendo il task.",
                SuccessText: $"SUCCESSO: operazione {scope} completata.",
                FailureFormat: $"Sembra che ci sia stato un ERRORE nell'operazione {scope}: {{0}}"));
        }

        foreach (var narration in narrations)
        {
            AppendScopeNarration(items, ordered, narration);
        }

        if (items.Count == 0)
        {
            items.Add(new SpokenLogLine(DateTime.UtcNow, "info", "Nessun evento NRE riconosciuto nel log della storia."));
        }

        return items.OrderBy(i => i.Timestamp).ToList();
    }

    private static void AppendScopeNarration(
        List<SpokenLogLine> items,
        List<LogEntry> ordered,
        ScopeNarration narration)
    {
        var firstReq = ordered.FirstOrDefault(l => IsScope(l, narration.Scope) && IsModelRequest(l));
        if (firstReq != null)
        {
            items.Add(new SpokenLogLine(
                firstReq.Timestamp,
                "info",
                string.Format(narration.StartTemplate, SafeModel(firstReq.ModelName))));
        }

        AppendFallbackLines(items, ordered, narration.Scope);
        AppendOutcomeLine(
            items,
            ordered,
            narration.Scope,
            narration.SuccessText,
            narration.FailureFormat,
            narration.IncludeSuccessCount,
            narration.SuccessCountFormat);
    }

    private static void AppendFallbackLines(List<SpokenLogLine> lines, List<LogEntry> ordered, string scope)
    {
        var opLogs = ordered.Where(l => IsScope(l, scope)).OrderBy(l => l.Id ?? 0L).ToList();
        if (opLogs.Count == 0) return;

        string? currentModel = null;
        var failedAttemptsOnCurrentModel = 0;

        foreach (var log in opLogs)
        {
            if (IsModelRequest(log))
            {
                var reqModel = SafeModel(log.ModelName);
                if (currentModel == null)
                {
                    currentModel = reqModel;
                    continue;
                }

                if (!string.Equals(currentModel, reqModel, StringComparison.OrdinalIgnoreCase))
                {
                    if (failedAttemptsOnCurrentModel > 0)
                    {
                        lines.Add(new SpokenLogLine(
                            log.Timestamp,
                            "warn",
                            $"{currentModel} ha FALLITO le sue {failedAttemptsOnCurrentModel} possibilita, adesso tocca al modello {reqModel} provare."));
                    }

                    currentModel = reqModel;
                    failedAttemptsOnCurrentModel = 0;
                }

                continue;
            }

            if (!IsModelResponse(log))
            {
                continue;
            }

            if (IsFailed(log))
            {
                failedAttemptsOnCurrentModel++;
            }
            else if (IsSuccess(log))
            {
                failedAttemptsOnCurrentModel = 0;
            }
        }
    }

    private static void AppendOutcomeLine(
        List<SpokenLogLine> lines,
        List<LogEntry> ordered,
        string scope,
        string successText,
        string failureFormat,
        bool includeSuccessCount = false,
        string? successCountFormat = null)
    {
        var responses = ordered
            .Where(l => IsScope(l, scope) && IsModelResponse(l))
            .OrderBy(l => l.Id ?? 0L)
            .ToList();
        if (responses.Count == 0) return;

        var successResponses = responses.Where(IsSuccess).ToList();
        var lastSuccess = successResponses.LastOrDefault();
        if (lastSuccess != null)
        {
            var successMessage = successText;
            if (includeSuccessCount && successResponses.Count > 1)
            {
                successMessage = string.Format(
                    successCountFormat ?? successText,
                    successResponses.Count);
            }

            lines.Add(new SpokenLogLine(lastSuccess.Timestamp, "success", successMessage));
            return;
        }

        var lastFailure = responses.LastOrDefault(IsFailed);
        if (lastFailure != null)
        {
            var err = CompactError(lastFailure.ResultFailReason);
            lines.Add(new SpokenLogLine(lastFailure.Timestamp, "error", string.Format(failureFormat, err)));
        }
    }

    private static string SafeModel(string? modelName) =>
        string.IsNullOrWhiteSpace(modelName) ? "modello_sconosciuto" : modelName.Trim();

    private static string CompactError(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "errore non specificato";
        var normalized = Regex.Replace(text.Trim(), @"\s+", " ");
        return normalized.Length <= 240 ? normalized : normalized[..240] + "...";
    }

    private static bool IsScope(LogEntry log, string scope) =>
        !string.IsNullOrWhiteSpace(log.ThreadScope) &&
        log.ThreadScope.StartsWith(scope, StringComparison.OrdinalIgnoreCase);

    private static bool IsNreScope(LogEntry log) =>
        !string.IsNullOrWhiteSpace(log.ThreadScope) &&
        log.ThreadScope.StartsWith("nre_", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeScope(string? scope) =>
        string.IsNullOrWhiteSpace(scope) ? null : scope.Trim();

    private static bool IsModelRequest(LogEntry log) =>
        string.Equals(log.Category, "ModelRequest", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(log.Category, "ModelPrompt", StringComparison.OrdinalIgnoreCase);

    private static bool IsModelResponse(LogEntry log) =>
        string.Equals(log.Category, "ModelResponse", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(log.Category, "ModelCompletion", StringComparison.OrdinalIgnoreCase);

    private static bool IsSuccess(LogEntry log) =>
        string.Equals(log.Result, "SUCCESS", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailed(LogEntry log) =>
        string.Equals(log.Result, "FAILED", StringComparison.OrdinalIgnoreCase);

    public sealed record StoryOption(long StoryId, string Label);
    public sealed record SpokenLogLine(DateTime Timestamp, string Kind, string Text);
    private sealed record ScopeNarration(
        string Scope,
        string StartTemplate,
        string SuccessText,
        string FailureFormat,
        bool IncludeSuccessCount = false,
        string? SuccessCountFormat = null);
}
