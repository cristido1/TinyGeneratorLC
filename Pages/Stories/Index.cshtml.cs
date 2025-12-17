using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        private readonly ICustomLogger? _customLogger;
        private readonly ILogger<IndexModel>? _logger;
        private readonly ICommandDispatcher _commandDispatcher;

        public IndexModel(
            StoriesService stories,
            DatabaseService database,
            ICustomLogger? customLogger = null,
            ICommandDispatcher? commandDispatcher = null,
            ILogger<IndexModel>? logger = null)
        {
            _stories = stories;
            _database = database;
            _customLogger = customLogger;
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
            _customLogger?.Start(runId);
            _customLogger?.Append(runId, $"Avvio valutazione batch di {pending.Count} storie con {agent.Name} ({agent.Role})");

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
                    _customLogger?.Append(runId, msg);
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

        public IActionResult OnPostTestTtsSchema(long storyId = 32)
        {
            var runId = QueueStoryCommand(
                storyId,
                "test_tts_schema",
                async ctx =>
                {
                    var (ok, msg) = await _stories.TestTtsSchemaAllModelsAsync(storyId, ctx.CancellationToken);
                    return new CommandResult(ok, msg);
                },
                $"Avvio test tts_schema su story {storyId} per tutti i modelli.",
                threadScopeOverride: $"story/test_tts_schema/{storyId}",
                metadata: new Dictionary<string, string>
                {
                    ["storyId"] = storyId.ToString(),
                    ["operation"] = "test_tts_schema_all_models"
                });

            TempData["StatusMessage"] = $"Test tts_schema avviato (run {runId}).";
            return RedirectToPage();
        }

        public IActionResult OnPostGenerateTts(long id, string folderName)
        {
            var runId = QueueStoryCommand(
                id,
                "generate_tts_audio",
                async ctx =>
                {
                    // Pass dispatcher runId to enable progress reporting
                    var (success, error) = await _stories.GenerateTtsForStoryAsync(id, folderName, ctx.RunId);
                    var message = success ? "Generazione audio TTS completata." : error;
                    return new CommandResult(success, message);
                },
                "Generazione audio TTS avviata in background.");

            TempData["StatusMessage"] = $"Generazione audio TTS avviata (run {runId}).";
            return RedirectToPage();
        }

        public IActionResult OnPostGenerateAmbience(long id, string folderName)
        {
            var runId = QueueStoryCommand(
                id,
                "generate_ambience_audio",
                async ctx =>
                {
                    var (success, error) = await _stories.GenerateAmbienceForStoryAsync(id, folderName, ctx.RunId);
                    var message = success ? "Generazione audio ambientale completata." : error;
                    return new CommandResult(success, message);
                },
                "Generazione audio ambientale avviata in background.");

            TempData["StatusMessage"] = $"Generazione audio ambientale avviata (run {runId}).";
            return RedirectToPage();
        }

        public IActionResult OnPostGenerateFx(long id, string folderName)
        {
            var runId = QueueStoryCommand(
                id,
                "generate_fx_audio",
                async ctx =>
                {
                    var (success, error) = await _stories.GenerateFxForStoryAsync(id, folderName, ctx.RunId);
                    var message = success ? "Generazione effetti sonori completata." : error;
                    return new CommandResult(success, message);
                },
                "Generazione effetti sonori avviata in background.");

            TempData["StatusMessage"] = $"Generazione effetti sonori avviata (run {runId}).";
            return RedirectToPage();
        }

        public IActionResult OnPostMixFinalAudio(long id, string folderName)
        {
            var runId = QueueStoryCommand(
                id,
                "mix_final_audio",
                async ctx =>
                {
                    var (success, error) = await _stories.MixFinalAudioForStoryAsync(id, folderName, ctx.RunId);
                    var message = success ? "Mixaggio audio completato." : error;
                    return new CommandResult(success, message);
                },
                "Mixaggio audio finale avviato in background.");

            TempData["StatusMessage"] = $"Mixaggio audio finale avviato (run {runId}).";
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

        public IActionResult OnGetFinalMixAudio(long id)
        {
            var story = _stories.GetStoryById(id);
            if (story == null || string.IsNullOrWhiteSpace(story.Folder)) return NotFound();

            var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", story.Folder);
            
            // Prefer MP3, fallback to WAV
            var mp3Path = Path.Combine(folderPath, "final_mix.mp3");
            var wavPath = Path.Combine(folderPath, "final_mix.wav");
            
            if (System.IO.File.Exists(mp3Path))
            {
                return PhysicalFile(mp3Path, "audio/mpeg", "final_mix.mp3");
            }
            if (System.IO.File.Exists(wavPath))
            {
                return PhysicalFile(wavPath, "audio/wav", "final_mix.wav");
            }
            
            return NotFound("File audio finale non trovato");
        }

        public IActionResult OnGetTtsAudio(long id, string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return NotFound();
            var story = _stories.GetStoryById(id);
            if (story == null || string.IsNullOrWhiteSpace(story.Folder)) return NotFound();

            var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", story.Folder);
            var filePath = Path.Combine(folderPath, file);
            if (!System.IO.File.Exists(filePath)) return NotFound();

            var ext = Path.GetExtension(file)?.ToLowerInvariant();
            var contentType = ext switch
            {
                ".wav" => "audio/wav",
                ".mp3" => "audio/mpeg",
                ".ogg" => "audio/ogg",
                _ => "application/octet-stream"
            };

            return PhysicalFile(filePath, contentType);
        }

        public IActionResult OnGetTtsPlaylist(long id)
        {
            var story = _stories.GetStoryById(id);
            if (story == null || string.IsNullOrWhiteSpace(story.Folder)) return NotFound();

            var schemaPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", story.Folder, "tts_schema.json");
            if (!System.IO.File.Exists(schemaPath)) return NotFound("tts_schema.json mancante");

            try
            {
                var json = System.IO.File.ReadAllText(schemaPath);
                var root = JsonNode.Parse(json) as JsonObject;
                if (root == null || root["Timeline"] is not JsonArray timeline) return NotFound("Timeline mancante nello schema");

                var items = new List<object>();

                foreach (var node in timeline.OfType<JsonObject>())
                {
                    if (!node.TryGetPropertyValue("FileName", out var fNode)) continue;
                    var fileName = fNode?.GetValue<string>() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(fileName)) continue;

                    var character = node.TryGetPropertyValue("Character", out var cNode) ? cNode?.ToString() ?? "" : "";
                    var text = node.TryGetPropertyValue("Text", out var tNode) ? tNode?.ToString() ?? "" : "";
                    var durationMs = node.TryGetPropertyValue("DurationMs", out var dNode) ? dNode?.GetValue<int?>() : null;

                    var url = Url.Page("/Stories/Index", null, new { handler = "TtsAudio", id = story.Id, file = fileName }, Request.Scheme);
                    items.Add(new
                    {
                        url,
                        character,
                        text,
                        durationMs
                    });
                }

                return new JsonResult(new { items, gapMs = 2000 });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Impossibile creare la playlist TTS per la storia {StoryId}", id);
                return StatusCode(500, ex.Message);
            }
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

        public IActionResult OnPostNormalizeCharacterNames(long id)
        {
            var runId = QueueStoryCommand(
                id,
                "normalize_characters",
                async ctx =>
                {
                    var (success, message) = await _stories.NormalizeCharacterNamesAsync(id);
                    return new CommandResult(success, message ?? (success ? "Nomi normalizzati" : "Normalizzazione fallita"));
                },
                "Normalizzazione nomi personaggi avviata in background.");
            TempData["StatusMessage"] = $"Normalizzazione nomi avviata (run {runId}).";
            return RedirectToPage();
        }

        public IActionResult OnPostNormalizeSentiments(long id)
        {
            var runId = QueueStoryCommand(
                id,
                "normalize_sentiments",
                async ctx =>
                {
                    var (success, message) = await _stories.NormalizeSentimentsAsync(id);
                    return new CommandResult(success, message ?? (success ? "Sentimenti normalizzati" : "Normalizzazione fallita"));
                },
                "Normalizzazione sentimenti avviata in background.");
            TempData["StatusMessage"] = $"Normalizzazione sentimenti avviata (run {runId}).";
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
                        story.HasFinalMix = System.IO.File.Exists(Path.Combine(folderPath, "final_mix.mp3")) 
                            || System.IO.File.Exists(Path.Combine(folderPath, "final_mix.wav"));
                    }
                    catch { story.HasVoiceSource = false; story.HasFinalMix = false; }
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
                _customLogger?.Start(runId);
                var startLog = startMessage ?? $"[{storyId}] Avvio operazione {operationCode}";
                _customLogger?.Append(runId, startLog);
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
                        _customLogger?.Append(runId, $"[{storyId}] {finalMessage}");
                        await (_customLogger?.MarkCompletedAsync(runId, finalMessage) ?? Task.CompletedTask);
                        try
                        {
                            var level = result.Success ? "success" : "error";
                            await (_customLogger?.NotifyAllAsync(
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
                        _customLogger?.Append(runId, $"[{storyId}] {errorMessage}");
                        await (_customLogger?.MarkCompletedAsync(runId, errorMessage) ?? Task.CompletedTask);
                        try
                        {
                        await (_customLogger?.NotifyAllAsync("Operazione fallita", $"[{storyId}] {operationCode}: {errorMessage}", "error") ?? Task.CompletedTask);
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
