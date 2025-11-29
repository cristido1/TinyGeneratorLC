using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.Stories
{
    public class IndexModel : PageModel
    {
        private readonly StoriesService _stories;
        private readonly DatabaseService _database;
        private readonly ProgressService? _progress;
        private readonly NotificationService? _notifications;
        private readonly ILogger<IndexModel>? _logger;
        private readonly ICommandDispatcher _commandDispatcher;

        public IndexModel(
            StoriesService stories,
            DatabaseService database,
            ProgressService? progress = null,
            NotificationService? notifications = null,
            ICommandDispatcher? commandDispatcher = null,
            ILogger<IndexModel>? logger = null)
        {
            _stories = stories;
            _database = database;
            _progress = progress;
            _notifications = notifications;
            _logger = logger;
            _commandDispatcher = commandDispatcher ?? throw new ArgumentNullException(nameof(commandDispatcher));
        }

        public IEnumerable<StoryRecord> Stories { get; set; } = new List<StoryRecord>();
        public List<Agent> Evaluators { get; set; } = new List<Agent>();
        public List<StoryStatus> Statuses { get; set; } = new List<StoryStatus>();

        public void OnGet()
        {
            LoadData();
        }

        public void OnPostDelete(long id)
        {
            _stories.Delete(id);
            LoadData();
        }

        public IActionResult OnPostEvaluate(long id, int agentId)
        {
            var runId = QueueStoryOperation(id, $"evaluate_{agentId}", async ct =>
            {
                var (success, score, error) = await _stories.EvaluateStoryWithAgentAsync(id, agentId);
                var message = success
                    ? $"Valutazione completata. Score medio: {score:F1}"
                    : error;
                return (success, message);
            }, "Valutazione avviata in background.");

            TempData["StatusMessage"] = $"Valutazione avviata in background (run {runId}).";
            return RedirectToPage();
        }

        // Allow manual evaluation input (for stories created without an associated model/agent)
        public IActionResult OnPostManualEvaluate(long id, double score, string overall)
        {
            // Build minimal raw JSON representation
            var raw = System.Text.Json.JsonSerializer.Serialize(new { overall_evaluation = overall });
            // Persist evaluation without model/agent association
            _database.AddStoryEvaluation(id, raw, score, null, null);
            return RedirectToPage();
        }

        public IActionResult OnPostGenerateTts(long id, string folderName)
        {
            var runId = QueueStoryOperation(id, "generate_tts_audio", async ct =>
            {
                var (success, error) = await _stories.GenerateTtsForStoryAsync(id, folderName);
                var message = success ? "Generazione audio TTS avviata." : error;
                return (success, message);
            }, "Generazione audio TTS avviata in background.");

            TempData["StatusMessage"] = $"Generazione audio TTS avviata (run {runId}).";
            return RedirectToPage();
        }

        public IActionResult OnPostGenerateTtsJson(long id)
        {
            var runId = QueueStoryOperation(id, "generate_tts_schema", _ => _stories.GenerateTtsSchemaJsonAsync(id), "Generazione JSON TTS avviata in background.");
            TempData["StatusMessage"] = $"Generazione JSON TTS avviata (run {runId}).";
            return RedirectToPage();
        }

        public IActionResult OnPostAssignVoices(long id)
        {
            var runId = QueueStoryOperation(id, "assign_voices", _ => _stories.AssignVoicesAsync(id), "Assegnazione voci avviata in background.");
            TempData["StatusMessage"] = $"Assegnazione voci avviata (run {runId}).";
            return RedirectToPage();
        }

        public StoryStatus? GetNextStatus(StoryRecord story)
        {
            return _stories.GetNextStatusForStory(story, Statuses);
        }

        public IActionResult OnPostAdvanceStatus(long id)
        {
            var runId = QueueStoryOperation(id, "advance_status", _ => _stories.ExecuteNextStatusOperationAsync(id), "Operazione di avanzamento avviata in background.");
            TempData["StatusMessage"] = $"Operazione di avanzamento avviata (run {runId}).";
            return RedirectToPage();
        }

        private void LoadData()
        {
            var allStories = _stories.GetAllStories().ToList();
            
            // Load evaluations for each story
            foreach (var story in allStories)
            {
                story.Evaluations = _stories.GetEvaluationsForStory(story.Id);
                if (!string.IsNullOrWhiteSpace(story.Folder))
                {
                    try
                    {
                        var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", story.Folder);
                        story.HasVoiceSource = System.IO.File.Exists(Path.Combine(folderPath, "tts_schema.json"));
                    }
                    catch { story.HasVoiceSource = false; }
                }
            }
            
            Stories = allStories;
            Statuses = _stories.GetAllStoryStatuses();

            // Load active evaluator agents
            Evaluators = _database.ListAgents()
                .Where(a => a.Role?.Equals("story_evaluator", System.StringComparison.OrdinalIgnoreCase) == true && a.IsActive)
                .ToList();
        }

        private string QueueStoryOperation(long storyId, string operationCode, Func<CancellationToken, Task<(bool success, string? message)>> operation, string? startMessage = null)
        {
            var safeCode = string.IsNullOrWhiteSpace(operationCode)
                ? "operation"
                : new string(operationCode.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            var runId = $"{safeCode}_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}";

            try
            {
                _progress?.Start(runId);
                var startLog = startMessage ?? $"[{storyId}] Avvio operazione {operationCode}";
                _progress?.Append(runId, startLog);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Impossibile inizializzare il progresso per run {RunId}", runId);
            }

            _commandDispatcher.Enqueue(
                $"story_{safeCode}",
                async ctx =>
                {
                    try
                    {
                        var (success, message) = await operation(ctx.CancellationToken);
                        var finalMessage = message ?? (success ? "Operazione completata" : "Operazione non riuscita");
                        _progress?.Append(runId, $"[{storyId}] {finalMessage}");
                        await (_progress?.MarkCompletedAsync(runId, finalMessage) ?? Task.CompletedTask);
                        try
                        {
                            var level = success ? "success" : "error";
                            await (_notifications?.NotifyAllAsync(
                                success ? "Operazione completata" : "Operazione fallita",
                                $"[{storyId}] {operationCode}: {finalMessage}",
                                level) ?? Task.CompletedTask);
                        }
                        catch (Exception notifyEx)
                        {
                            _logger?.LogWarning(notifyEx, "Notifica operazione {RunId} non riuscita", runId);
                        }

                        return new CommandResult(success, finalMessage);
                    }
                    catch (Exception ex)
                    {
                        var errorMessage = $"Errore inatteso: {ex.Message}";
                        _logger?.LogError(ex, "Operazione {OperationCode} per storia {StoryId} fallita", operationCode, storyId);
                        _progress?.Append(runId, $"[{storyId}] {errorMessage}");
                        await (_progress?.MarkCompletedAsync(runId, errorMessage) ?? Task.CompletedTask);
                        try
                        {
                            await (_notifications?.NotifyAllAsync("Operazione fallita", $"[{storyId}] {operationCode}: {errorMessage}", "error") ?? Task.CompletedTask);
                        }
                        catch (Exception notifyEx)
                        {
                            _logger?.LogWarning(notifyEx, "Notifica errore operazione {RunId} non riuscita", runId);
                        }

                        return new CommandResult(false, errorMessage);
                    }
                },
                runId: runId,
                threadScope: $"story/{safeCode}",
                metadata: new Dictionary<string, string>
                {
                    ["storyId"] = storyId.ToString(),
                    ["operation"] = operationCode
                });

            return runId;
        }
    }
}
