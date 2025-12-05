using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TinyGenerator.Services;
using TinyGenerator.Models;
using TinyGenerator.Services.Commands;

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
        public List<Agent> ActionEvaluators { get; set; } = new List<Agent>();
        public List<StoryStatus> Statuses { get; set; } = new List<StoryStatus>();
        public IReadOnlyList<CommandSnapshot> ActiveCommands { get; set; } = Array.Empty<CommandSnapshot>();

        public void OnGet()
        {
            LoadData();
        }

        public void OnPostDelete(long id)
        {
            _stories.Delete(id);
            LoadData();
        }

        public IActionResult OnPostDeleteEvaluation(long id, long storyId)
        {
            try
            {
                _database.DeleteStoryEvaluationById(id);
                TempData["StatusMessage"] = "Valutazione eliminata.";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Errore eliminazione valutazione: " + ex.Message;
            }
            return RedirectToPage();
        }

        public IActionResult OnPostEvaluate(long id, int agentId)
        {
            var agent = _database.GetAgentById(agentId);
            // Use agentId in threadScope to allow parallel evaluation by different agents
            var isCoherence = agent?.Role?.Equals("coherence_evaluator", StringComparison.OrdinalIgnoreCase) == true;
            var opName = isCoherence ? "evaluate_coherence" : "evaluate_story";

            var runId = QueueStoryCommand(
                id,
                opName,
                async ctx =>
                {
                    IStoryCommand cmd = isCoherence
                        ? new EvaluateCoherenceCommand(_stories, id, agentId)
                        : new EvaluateStoryCommand(_stories, id, agentId);
                    return await cmd.ExecuteAsync(ctx.CancellationToken);
                },
                "Valutazione avviata in background.",
                threadScopeOverride: $"story/{opName}/agent_{agentId}",
                metadata: BuildMetadata(id, opName, agent));

            TempData["StatusMessage"] = $"Valutazione avviata in background (run {runId}).";
            return RedirectToPage();
        }

        public IActionResult OnPostEvaluateAll(int agentId)
        {
            var agent = _database.GetAgentById(agentId);
            if (agent == null || !agent.IsActive)
            {
                TempData["ErrorMessage"] = "Agente valutatore non trovato o inattivo.";
                return RedirectToPage();
            }

            var isCoherence = agent.Role?.Equals("coherence_evaluator", StringComparison.OrdinalIgnoreCase) == true;
            var allStories = _stories.GetAllStories().ToList();
            var pending = allStories.Where(s => (s.Evaluations ?? new List<StoryEvaluation>()).All(ev => ev.AgentId != agentId)).ToList();

            if (pending.Count == 0)
            {
                TempData["StatusMessage"] = $"Nessuna storia da valutare per l'agente {agent.Name}.";
                return RedirectToPage();
            }

            var runId = $"bulk_eval_{agentId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
            _progress?.Start(runId);
            _progress?.Append(runId, $"Avvio valutazione batch di {pending.Count} storie con {agent.Name} ({agent.Role})");

            _commandDispatcher.Enqueue(
                "bulk_evaluate",
                async ctx =>
                {
                    int ok = 0, ko = 0;
                    foreach (var story in pending)
                    {
                        try
                        {
                            IStoryCommand cmd = isCoherence
                                ? new EvaluateCoherenceCommand(_stories, story.Id, agentId)
                                : new EvaluateStoryCommand(_stories, story.Id, agentId);
                            var res = await cmd.ExecuteAsync(ctx.CancellationToken);
                            if (res.Success) ok++; else ko++;
                        }
                        catch
                        {
                            ko++;
                        }
                    }

                    var msg = $"Completate {ok} valutazioni, fallite {ko}.";
                    _progress?.Append(runId, msg);
                    return new CommandResult(ko == 0, msg);
                },
                runId: runId,
                threadScope: $"story/bulk_evaluate/agent_{agentId}",
                metadata: new Dictionary<string, string>
                {
                    ["agentId"] = agentId.ToString(),
                    ["agentName"] = agent.Name ?? string.Empty,
                    ["agentRole"] = agent.Role ?? string.Empty,
                    ["operation"] = isCoherence ? "bulk_evaluate_coherence" : "bulk_evaluate_story"
                });

            TempData["StatusMessage"] = $"Valutazione batch avviata (run {runId}) per {pending.Count} storie.";
            return RedirectToPage();
        }

        public IActionResult OnPostEvaluateAction(long id, int agentId)
        {
            var agent = _database.GetAgentById(agentId);
            var runId = QueueStoryCommand(
                id,
                "evaluate_action_pacing",
                async ctx =>
                {
                    var cmd = new EvaluateActionPacingCommand(_stories, id, agentId);
                    return await cmd.ExecuteAsync(ctx.CancellationToken);
                },
                "Valutazione azione/ritmo avviata in background.",
                threadScopeOverride: $"story/evaluate_action/agent_{agentId}",
                metadata: BuildMetadata(id, "evaluate_action_pacing", agent));

            TempData["StatusMessage"] = $"Valutazione azione/ritmo avviata (run {runId}).";
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
            var runId = QueueStoryCommand(
                id,
                "generate_tts_audio",
                async ctx =>
                {
                    var (success, error) = await _stories.GenerateTtsForStoryAsync(id, folderName);
                    var message = success ? "Generazione audio TTS avviata." : error;
                    return new CommandResult(success, message);
                },
                "Generazione audio TTS avviata in background.");

            TempData["StatusMessage"] = $"Generazione audio TTS avviata (run {runId}).";
            return RedirectToPage();
        }

        public IActionResult OnPostGenerateTtsJson(long id)
        {
            var runId = QueueStoryCommand(
                id,
                "generate_tts_schema",
                async ctx =>
                {
                    var cmd = new GenerateTtsSchemaCommand(_stories, id);
                    return await cmd.ExecuteAsync(ctx.CancellationToken);
                },
                "Generazione JSON TTS avviata in background.");
            TempData["StatusMessage"] = $"Generazione JSON TTS avviata (run {runId}).";
            return RedirectToPage();
        }

        public IActionResult OnPostAssignVoices(long id)
        {
            var runId = QueueStoryCommand(
                id,
                "assign_voices",
                async ctx =>
                {
                    var cmd = new GenerateTtsVoiceCommand(_stories, id);
                    return await cmd.ExecuteAsync(ctx.CancellationToken);
                },
                "Assegnazione voci avviata in background.");
            TempData["StatusMessage"] = $"Assegnazione voci avviata (run {runId}).";
            return RedirectToPage();
        }

        public StoryStatus? GetNextStatus(StoryRecord story)
        {
            return _stories.GetNextStatusForStory(story, Statuses);
        }

        public IActionResult OnPostAdvanceStatus(long id)
        {
            var runId = QueueStoryCommand(
                id,
                "advance_status",
                async ctx =>
                {
                    var (success, message) = await _stories.ExecuteNextStatusOperationAsync(id);
                    return new CommandResult(success, message ?? (success ? "Operazione completata" : "Operazione fallita"));
                },
                "Operazione di avanzamento avviata in background.");
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

            // Load active evaluator agents (both story_evaluator and coherence_evaluator)
            Evaluators = _database.ListAgents()
                .Where(a => a.IsActive && 
                    (a.Role?.Equals("story_evaluator", System.StringComparison.OrdinalIgnoreCase) == true ||
                     a.Role?.Equals("coherence_evaluator", System.StringComparison.OrdinalIgnoreCase) == true))
                .ToList();

            ActionEvaluators = _database.ListAgents()
                .Where(a => a.IsActive &&
                    a.Role?.Equals("action_evaluator", System.StringComparison.OrdinalIgnoreCase) == true)
                .ToList();

            // Snapshot of queued/running commands
            ActiveCommands = _commandDispatcher.GetActiveCommands();
        }

        private string QueueStoryCommand(long storyId, string operationCode, Func<CommandContext, Task<CommandResult>> operation, string? startMessage = null, string? threadScopeOverride = null, IReadOnlyDictionary<string, string>? metadata = null)
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

            var threadScope = threadScopeOverride ?? $"story/{safeCode}";

            _commandDispatcher.Enqueue(
                $"story_{safeCode}",
                async ctx =>
                {
                    try
                    {
                        var result = await operation(ctx);
                        var finalMessage = result.Message ?? (result.Success ? "Operazione completata" : "Operazione non riuscita");
                        _progress?.Append(runId, $"[{storyId}] {finalMessage}");
                        await (_progress?.MarkCompletedAsync(runId, finalMessage) ?? Task.CompletedTask);
                        try
                        {
                            var level = result.Success ? "success" : "error";
                            await (_notifications?.NotifyAllAsync(
                                result.Success ? "Operazione completata" : "Operazione fallita",
                                $"[{storyId}] {operationCode}: {finalMessage}",
                                level) ?? Task.CompletedTask);
                        }
                        catch (Exception notifyEx)
                        {
                            _logger?.LogWarning(notifyEx, "Notifica operazione {RunId} non riuscita", runId);
                        }

                        return new CommandResult(result.Success, finalMessage);
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
                threadScope: threadScope,
                metadata: MergeMetadata(storyId, operationCode, metadata));

            return runId;
        }

        private IReadOnlyDictionary<string, string> MergeMetadata(long storyId, string operationCode, IReadOnlyDictionary<string, string>? extra)
        {
            var dict = new Dictionary<string, string>
            {
                ["storyId"] = storyId.ToString(),
                ["operation"] = operationCode
            };

            if (extra != null)
            {
                foreach (var kvp in extra)
                {
                    if (!dict.ContainsKey(kvp.Key))
                        dict[kvp.Key] = kvp.Value;
                }
            }

            return dict;
        }

        private IReadOnlyDictionary<string, string>? BuildMetadata(long storyId, string operationCode, Agent? agent)
        {
            if (agent == null) return null;
            return new Dictionary<string, string>
            {
                ["agentId"] = agent.Id.ToString(),
                ["agentName"] = agent.Name ?? string.Empty,
                ["agentRole"] = agent.Role ?? string.Empty,
                ["storyId"] = storyId.ToString(),
                ["operation"] = operationCode
            };
        }
    }
}
