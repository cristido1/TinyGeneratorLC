using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TinyGenerator.Services;
using TinyGenerator.Models;
using TinyGenerator.Services.Commands;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace TinyGenerator.Pages.Stories
{
    public class IndexModel : PageModel
    {
        private readonly StoriesService _stories;
        private readonly DatabaseService _database;
        private readonly ILangChainKernelFactory _kernelFactory;
        private readonly IServiceScopeFactory? _scopeFactory;
        private readonly ICustomLogger? _customLogger;
        private readonly ILogger<IndexModel>? _logger;
        private readonly ICommandDispatcher _commandDispatcher;
        private readonly CommandTuningOptions _tuning;
        private readonly IOptionsMonitor<AutomaticOperationsOptions>? _idleAutoOptions;

        public IndexModel(
            StoriesService stories,
            DatabaseService database,
            ILangChainKernelFactory kernelFactory,
            IOptions<CommandTuningOptions> tuningOptions,
            IServiceScopeFactory? scopeFactory = null,
            ICustomLogger? customLogger = null,
            ICommandDispatcher? commandDispatcher = null,
            IOptionsMonitor<AutomaticOperationsOptions>? idleAutoOptions = null,
            ILogger<IndexModel>? logger = null)
        {
            _stories = stories;
            _database = database;
            _kernelFactory = kernelFactory;
            _scopeFactory = scopeFactory;
            _tuning = tuningOptions.Value ?? new CommandTuningOptions();
            _customLogger = customLogger;
            _logger = logger;
            _commandDispatcher = commandDispatcher ?? throw new ArgumentNullException(nameof(commandDispatcher));
            _idleAutoOptions = idleAutoOptions;
        }

        public IEnumerable<StoryRecord> Stories { get; set; } = new List<StoryRecord>();
        // Paging/search properties (server-side)
        public int PageIndex { get; set; } = 1;
        public int PageSize { get; set; } = 10000;
        public int TotalCount { get; set; }
        public string? Search { get; set; }
        public string? OrderBy { get; set; }
        public List<Agent> Evaluators { get; set; } = new List<Agent>();
        public List<Agent> ActionEvaluators { get; set; } = new List<Agent>();
        public List<StoryStatus> Statuses { get; set; } = new List<StoryStatus>();
        public IReadOnlyList<CommandSnapshot> ActiveCommands { get; set; } = Array.Empty<CommandSnapshot>();

        public void OnGet()
        {
            // read querystring for paging/search
            if (int.TryParse(Request.Query["page"], out var p) && p > 0) PageIndex = p;
            if (int.TryParse(Request.Query["pageSize"], out var ps) && ps > 0) PageSize = ps;
            Search = string.IsNullOrWhiteSpace(Request.Query["search"]) ? null : Request.Query["search"].ToString();
            OrderBy = string.IsNullOrWhiteSpace(Request.Query["orderBy"]) ? null : Request.Query["orderBy"].ToString();
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

        public IActionResult OnPostDeleteAllEvaluations()
        {
            try
            {
                _database.DeleteAllEvaluations();
                TempData["StatusMessage"] = "Tutte le valutazioni eliminate (inclusa coerenza).";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Errore eliminazione valutazioni: " + ex.Message;
            }

            return RedirectToPage();
        }

        public IActionResult OnPostRealignCreatorModelIds()
        {
            try
            {
                var changed = _database.RealignStoriesCreatorModelIds();
                TempData["StatusMessage"] = $"Allineamento completato: aggiornate {changed} storie (model_id ← agent.model_id).";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Errore allineamento model_id: " + ex.Message;
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

        public IActionResult OnPostRevise(long id)
        {
            try
            {
                var runId = _stories.EnqueueReviseStoryCommand(id, trigger: "stories_index", priority: 2, force: true);
                TempData["StatusMessage"] = string.IsNullOrWhiteSpace(runId)
                    ? "Revisione non accodata (forse già in coda o dispatcher non disponibile)."
                    : $"Revisione accodata (run {runId}).";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Errore accodando revisione: " + ex.Message;
            }

            return RedirectToPage();
        }

        public IActionResult OnPostEnsureTagStatusConsistency()
        {
            var evaluatedStatus = _database.GetStoryStatusByCode("evaluated");
            var insertedStatus = _database.GetStoryStatusByCode("inserted");
            if (evaluatedStatus == null || insertedStatus == null)
            {
                TempData["ErrorMessage"] = "Impossibile determinare i codici di stato necessari (inserted/evaluated).";
                return RedirectToPage();
            }

            var stories = _stories.GetAllStories()
                .Where(s => (s.StatusStep ?? 0) > evaluatedStatus.Step)
                .ToList();

            var updated = new List<long>();
            foreach (var story in stories)
            {
                if (!string.IsNullOrWhiteSpace(story.StoryTags))
                {
                    continue;
                }

                var hasEvaluations = (story.Evaluations?.Count ?? 0) > 0;
                var targetStatusId = hasEvaluations ? evaluatedStatus.Id : insertedStatus.Id;
                if (targetStatusId == story.StatusId)
                {
                    continue;
                }

                _database.UpdateStoryById(story.Id, statusId: targetStatusId, updateStatus: true);
                updated.Add(story.Id);
            }

            if (updated.Count == 0)
            {
                TempData["StatusMessage"] = "Nessuna storia aggiornata: tutti i record già coerenti o non mancanti di story_tags.";
            }
            else
            {
                TempData["StatusMessage"] = $"Aggiornati {updated.Count} storie (mancavano story_tags e tornate a step inserito/evaluated).";
            }

            return RedirectToPage();
        }

    public IActionResult OnPostCloneFromRevised(long id)
    {
        var runId = QueueStoryCommand(
            id,
            "clone_revised_story",
            ctx =>
            {
                var result = _stories.TryCloneStoryFromRevised(id);
                return Task.FromResult(new CommandResult(result.Success, result.Message));
            },
            "Clonazione da revisione accodata.",
            threadScopeOverride: $"story/clone_revised/{id}",
            metadata: new Dictionary<string, string>
            {
                ["operation"] = "clone_revised_story",
                ["storyId"] = id.ToString()
            });

        TempData["StatusMessage"] = $"Clonazione accodata (run {runId}).";
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

        /// <summary>
        /// Delete stories that have at least two evaluations and an average score < 50/100.
        /// Also deletes associated folders on disk and evaluations.
        /// </summary>
        public IActionResult OnPostDeleteLowRated()
        {
            try
            {
                var all = _stories.GetAllStories();
                var toDelete = new List<StoryRecord>();
                var opts = _idleAutoOptions?.CurrentValue ?? new AutomaticOperationsOptions();
                var minAvg = opts.AutoDeleteLowRated.MinAverageScore;
                var minEvals = Math.Max(1, opts.AutoDeleteLowRated.MinEvaluations);
                foreach (var s in all)
                {
                    if (!string.Equals(s.Status, "evaluated", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var evals = _database.GetStoryEvaluations(s.Id) ?? new List<StoryEvaluation>();
                    if (evals.Count < minEvals) continue;
                    var avgTotal = evals.Average(e => e.TotalScore);
                    var pct = DatabaseService.NormalizeEvaluationScoreTo100(avgTotal);
                    if (pct < minAvg)
                    {
                        toDelete.Add(s);
                    }
                }

                int deleted = 0;
                foreach (var s in toDelete)
                {
                    try
                    {
                        // delete folder
                        if (!string.IsNullOrWhiteSpace(s.Folder))
                        {
                            var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", s.Folder);
                            if (Directory.Exists(folderPath))
                            {
                                try { Directory.Delete(folderPath, true); } catch (Exception ex) { _logger?.LogWarning(ex, "Unable to delete folder {Path}", folderPath); }
                            }
                        }

                        // delete evaluations
                        _database.DeleteEvaluationsForStory(s.Id);

                        // delete story record(s)
                        _stories.Delete(s.Id);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to delete story {Id}", s.Id);
                    }
                }

                TempData["StatusMessage"] = $"Eliminate {deleted} storie con valutazione media <{minAvg} e almeno {minEvals} valutazioni (solo evaluated).";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Errore durante eliminazione storie: " + ex.Message;
            }
            return RedirectToPage();
        }

        public IActionResult OnPostBatchSummarize()
        {
            try
            {
                var runId = Guid.NewGuid().ToString();
                var kernelFactory = HttpContext.RequestServices.GetService<ILangChainKernelFactory>();
                
                if (kernelFactory == null)
                {
                    TempData["ErrorMessage"] = "Kernel factory non disponibile.";
                    return RedirectToPage();
                }

                var cmd = new BatchSummarizeStoriesCommand(
                    _database,
                    kernelFactory,
                    _commandDispatcher,
                    _customLogger!,
                    minScore: 60);

                _commandDispatcher.Enqueue(
                    "BatchSummarizeStories",
                    async ctx => await cmd.ExecuteAsync(ctx.CancellationToken),
                    runId: runId,
                    metadata: new Dictionary<string, string>
                    {
                        ["minScore"] = "60",
                        ["agentName"] = "batch_orchestrator",
                        ["operation"] = "batch_summarize",
                        ["triggeredBy"] = "manual_ui"
                    },
                    priority: 2);

                TempData["StatusMessage"] = $"Batch summarization avviato (run {runId}). I riassunti verranno generati in background.";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Errore durante avvio batch summarization: " + ex.Message;
            }
            return RedirectToPage();
        }

        public IActionResult OnPostGenerateTts(long id, string folderName)
        {
            // Unified path: single dispatcher command, storyId only.
            var result = _stories.TryEnqueueGenerateTtsAudioCommand(id, trigger: "manual_tts_audio", priority: 3);
            TempData["StatusMessage"] = result.Enqueued
                ? $"Generazione audio TTS avviata (run {result.RunId})."
                : result.Message;
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

        public IActionResult OnPostGenerateMusic(long id, string folderName)
        {
            var runId = QueueStoryCommand(
                id,
                "generate_music",
                async ctx =>
                {
                    var (success, error) = await _stories.GenerateMusicForStoryAsync(id, folderName, ctx.RunId);
                    var message = success ? "Generazione musica completata." : error;
                    return new CommandResult(success, message);
                },
                "Generazione musica avviata in background.");

            TempData["StatusMessage"] = $"Generazione musica avviata (run {runId}).";
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

        public IActionResult OnPostPrepareTtsSchema(long id)
        {
            var runId = QueueStoryCommand(
                id,
                "prepare_tts_schema",
                async ctx =>
                {
                    var sb = new System.Text.StringBuilder();
                    var overallSuccess = true;

                    // 1) Generate TTS schema JSON
                    try
                    {
                        var (ttsOk, ttsMsg) = await _stories.GenerateTtsSchemaJsonAsync(id);
                        sb.AppendLine($"GenerateTtsSchema: {ttsMsg}");
                        if (!ttsOk) overallSuccess = false;
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine("GenerateTtsSchema: exception " + ex.Message);
                        overallSuccess = false;
                    }

                    // 2) Normalize character names (best-effort)
                    try
                    {
                        var (normCharOk, normCharMsg) = await _stories.NormalizeCharacterNamesAsync(id);
                        sb.AppendLine($"NormalizeCharacterNames: {normCharMsg}");
                        if (!normCharOk) overallSuccess = false;
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine("NormalizeCharacterNames: exception " + ex.Message);
                        overallSuccess = false;
                    }

                    // 3) Assign voices
                    try
                    {
                        var (assignOk, assignMsg) = await _stories.AssignVoicesAsync(id);
                        sb.AppendLine($"AssignVoices: {assignMsg}");
                        if (!assignOk) overallSuccess = false;
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine("AssignVoices: exception " + ex.Message);
                        overallSuccess = false;
                    }

                    // 4) Normalize sentiments
                    try
                    {
                        var (normSentOk, normSentMsg) = await _stories.NormalizeSentimentsAsync(id);
                        sb.AppendLine($"NormalizeSentiments: {normSentMsg}");
                        if (!normSentOk) overallSuccess = false;
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine("NormalizeSentiments: exception " + ex.Message);
                        overallSuccess = false;
                    }

                    var message = sb.ToString();
                    return new CommandResult(overallSuccess, message);
                },
                "Preparazione TTS schema avviata in background.");

            TempData["StatusMessage"] = $"Preparazione TTS schema avviata (run {runId}).";
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
                var r = new PhysicalFileResult(mp3Path, "audio/mpeg") { FileDownloadName = "final_mix.mp3", EnableRangeProcessing = true };
                return r;
            }
            if (System.IO.File.Exists(wavPath))
            {
                var r = new PhysicalFileResult(wavPath, "audio/wav") { FileDownloadName = "final_mix.wav", EnableRangeProcessing = true };
                return r;
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

            var pres = new PhysicalFileResult(filePath, contentType) { EnableRangeProcessing = true };
            return pres;
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

        public IActionResult OnPostDeleteFinalMix(long id)
        {
            var runId = QueueStoryCommand(
                id,
                "delete_final_mix",
                async ctx =>
                {
                    var (success, message) = await _stories.DeleteFinalMixAsync(id);
                    return new CommandResult(success, message ?? (success ? "Mix finale cancellato" : "Cancellazione mix fallita"));
                },
                "Cancellazione mix finale avviata in background.");
            TempData["StatusMessage"] = $"Cancellazione mix finale avviata (run {runId}).";
            return RedirectToPage();
        }

        public IActionResult OnPostDeleteMusic(long id)
        {
            var runId = QueueStoryCommand(
                id,
                "delete_music",
                async ctx =>
                {
                    var (success, message) = await _stories.DeleteMusicAsync(id);
                    return new CommandResult(success, message ?? (success ? "Musica cancellata" : "Cancellazione musica fallita"));
                },
                "Cancellazione musica avviata in background.");
            TempData["StatusMessage"] = $"Cancellazione musica avviata (run {runId}).";
            return RedirectToPage();
        }

        public IActionResult OnPostDeleteFx(long id)
        {
            var runId = QueueStoryCommand(
                id,
                "delete_fx",
                async ctx =>
                {
                    var (success, message) = await _stories.DeleteFxAsync(id);
                    return new CommandResult(success, message ?? (success ? "Effetti cancellati" : "Cancellazione effetti fallita"));
                },
                "Cancellazione effetti sonori avviata in background.");
            TempData["StatusMessage"] = $"Cancellazione effetti avviata (run {runId}).";
            return RedirectToPage();
        }

        public IActionResult OnPostDeleteAmbience(long id)
        {
            var runId = QueueStoryCommand(
                id,
                "delete_ambience",
                async ctx =>
                {
                    var (success, message) = await _stories.DeleteAmbienceAsync(id);
                    return new CommandResult(success, message ?? (success ? "Rumori ambientali cancellati" : "Cancellazione ambient fallita"));
                },
                "Cancellazione rumori ambientali avviata in background.");
            TempData["StatusMessage"] = $"Cancellazione rumori ambientali avviata (run {runId}).";
            return RedirectToPage();
        }

        public IActionResult OnPostDeleteTts(long id)
        {
            var runId = QueueStoryCommand(
                id,
                "delete_tts",
                async ctx =>
                {
                    var (success, message) = await _stories.DeleteTtsAsync(id);
                    return new CommandResult(success, message ?? (success ? "TTS cancellato" : "Cancellazione TTS fallita"));
                },
                "Cancellazione TTS avviata in background.");
            TempData["StatusMessage"] = $"Cancellazione TTS avviata (run {runId}).";
            return RedirectToPage();
        }

        public IActionResult OnPostDeleteStoryTagged(long id)
        {
            var runId = QueueStoryCommand(
                id,
                "delete_story_tagged",
                async ctx =>
                {
                    var (success, message) = await _stories.DeleteStoryTaggedAsync(id);
                    return new CommandResult(success, message ?? (success ? "Story tagged cancellato" : "Cancellazione story tagged fallita"));
                },
                "Cancellazione story tagged avviata in background.");
            TempData["StatusMessage"] = $"Cancellazione story tagged avviata (run {runId}).";
            return RedirectToPage();
        }

        public IActionResult OnPostAddTags(long id)
        {
            var runId = QueueStoryCommand(
                id,
                "add_voice_tags_to_story",
                async ctx =>
                {
                    var cmd = new AddVoiceTagsToStoryCommand(
                        id,
                        _database,
                        _kernelFactory,
                        _stories,
                        _customLogger,
                        _commandDispatcher,
                        _tuning,
                        _scopeFactory);
                    return await cmd.ExecuteAsync(ctx.CancellationToken, ctx.RunId);
                },
                "Aggiunta tag avviata in background.");

            TempData["StatusMessage"] = $"Aggiunta tag avviata (run {runId}).";
            return RedirectToPage();
        }

        public IActionResult OnPostRegenAmbientTags(long id)
        {
            var runId = QueueStoryCommand(
                id,
                "regen_ambient_tags",
                async ctx =>
                {
                    var story = _database.GetStoryById(id);
                    if (story == null)
                    {
                        return new CommandResult(false, $"Story {id} non trovata");
                    }

                    if (string.IsNullOrWhiteSpace(story.StoryTagged))
                    {
                        return new CommandResult(false, "story_tagged vuoto: impossibile rigenerare RUMORI");
                    }

                    // Migration-friendly cleanup: remove both legacy [RUMORE] and canonical [RUMORI] blocks.
                    var (cleaned, removedA) = RemoveTagBlocks(story.StoryTagged, "RUMORI");
                    var (cleaned2, removedB) = RemoveTagBlocks(cleaned, "RUMORE");
                    var cleanedFinal = cleaned2;
                    var removed = removedA + removedB;
                    var nextVersion = (story.StoryTaggedVersion ?? 0) + 1;
                    var saved = _database.UpdateStoryTagged(
                        id,
                        cleanedFinal,
                        story.FormatterModelId,
                        story.FormatterPromptHash,
                        nextVersion);

                    if (!saved)
                    {
                        return new CommandResult(false, "Impossibile salvare story_tagged ripulito");
                    }

                    var alreadyQueued = IsExpertAlreadyQueued("add_ambient_tags_to_story", id);
                    if (alreadyQueued)
                    {
                        return new CommandResult(true, $"Ripuliti {removed} blocchi RUMORI (v{nextVersion}). add_ambient_tags_to_story già in coda/in esecuzione");
                    }

                    var expertRunId = $"add_ambient_tags_to_story_{id}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                    _commandDispatcher.Enqueue(
                        "add_ambient_tags_to_story",
                        async inner =>
                        {
                            var cmd = new AddAmbientTagsToStoryCommand(id, _database, _kernelFactory, _stories, _customLogger, _commandDispatcher, _tuning, _scopeFactory);
                            return await cmd.ExecuteAsync(inner.CancellationToken, expertRunId);
                        },
                        runId: expertRunId,
                        threadScope: "story/add_ambient_tags_to_story",
                        metadata: new Dictionary<string, string>
                        {
                            ["storyId"] = id.ToString(),
                            ["operation"] = "add_ambient_tags_to_story",
                            ["trigger"] = "stories_index_regen",
                            ["cleaned"] = "RUMORI",
                            ["taggedVersion"] = nextVersion.ToString()
                        },
                        priority: 2);

                    return new CommandResult(true, $"Ripuliti {removed} blocchi RUMORI (v{nextVersion}) e avviato add_ambient_tags_to_story (run {expertRunId})");
                },
                "Ripulizia RUMORI + rilancio add_ambient_tags_to_story avviati in background.");

            TempData["StatusMessage"] = $"Rigenerazione RUMORI avviata (run {runId}).";
            return RedirectToPage();
        }

        public IActionResult OnPostRegenFxTags(long id)
        {
            var runId = QueueStoryCommand(
                id,
                "regen_fx_tags",
                async ctx =>
                {
                    var story = _database.GetStoryById(id);
                    if (story == null)
                    {
                        return new CommandResult(false, $"Story {id} non trovata");
                    }

                    if (string.IsNullOrWhiteSpace(story.StoryTagged))
                    {
                        return new CommandResult(false, "story_tagged vuoto: impossibile rigenerare FX");
                    }

                    var (cleaned, removed) = RemoveTagBlocks(story.StoryTagged, "FX");
                    var nextVersion = (story.StoryTaggedVersion ?? 0) + 1;
                    var saved = _database.UpdateStoryTagged(
                        id,
                        cleaned,
                        story.FormatterModelId,
                        story.FormatterPromptHash,
                        nextVersion);

                    if (!saved)
                    {
                        return new CommandResult(false, "Impossibile salvare story_tagged ripulito");
                    }

                    var alreadyQueued = IsExpertAlreadyQueued("add_fx_tags_to_story", id);
                    if (alreadyQueued)
                    {
                        return new CommandResult(true, $"Ripuliti {removed} blocchi FX (v{nextVersion}). add_fx_tags_to_story già in coda/in esecuzione");
                    }

                    var expertRunId = $"add_fx_tags_to_story_{id}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                    _commandDispatcher.Enqueue(
                        "add_fx_tags_to_story",
                        async inner =>
                        {
                            var cmd = new AddFxTagsToStoryCommand(id, _database, _kernelFactory, _stories, _customLogger, _commandDispatcher, _tuning, _scopeFactory);
                            return await cmd.ExecuteAsync(inner.CancellationToken, expertRunId);
                        },
                        runId: expertRunId,
                        threadScope: "story/add_fx_tags_to_story",
                        metadata: new Dictionary<string, string>
                        {
                            ["storyId"] = id.ToString(),
                            ["operation"] = "add_fx_tags_to_story",
                            ["trigger"] = "stories_index_regen",
                            ["cleaned"] = "FX",
                            ["taggedVersion"] = nextVersion.ToString()
                        },
                        priority: 2);

                    return new CommandResult(true, $"Ripuliti {removed} blocchi FX (v{nextVersion}) e avviato add_fx_tags_to_story (run {expertRunId})");
                },
                "Ripulizia FX + rilancio add_fx_tags_to_story avviati in background.");

            TempData["StatusMessage"] = $"Rigenerazione FX avviata (run {runId}).";
            return RedirectToPage();
        }

        public IActionResult OnPostRegenMusicTags(long id)
        {
            var runId = QueueStoryCommand(
                id,
                "regen_music_tags",
                async ctx =>
                {
                    var story = _database.GetStoryById(id);
                    if (story == null)
                    {
                        return new CommandResult(false, $"Story {id} non trovata");
                    }

                    if (string.IsNullOrWhiteSpace(story.StoryTagged))
                    {
                        return new CommandResult(false, "story_tagged vuoto: impossibile rigenerare MUSICA");
                    }

                    var (cleaned, removed) = RemoveTagBlocks(story.StoryTagged, "MUSICA");
                    var nextVersion = (story.StoryTaggedVersion ?? 0) + 1;
                    var saved = _database.UpdateStoryTagged(
                        id,
                        cleaned,
                        story.FormatterModelId,
                        story.FormatterPromptHash,
                        nextVersion);

                    if (!saved)
                    {
                        return new CommandResult(false, "Impossibile salvare story_tagged ripulito");
                    }

                    var alreadyQueued = IsExpertAlreadyQueued("add_music_tags_to_story", id);
                    if (alreadyQueued)
                    {
                        return new CommandResult(true, $"Ripuliti {removed} blocchi MUSICA (v{nextVersion}). add_music_tags_to_story già in coda/in esecuzione");
                    }

                    var expertRunId = $"add_music_tags_to_story_{id}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                    _commandDispatcher.Enqueue(
                        "add_music_tags_to_story",
                        async inner =>
                        {
                            var cmd = new AddMusicTagsToStoryCommand(id, _database, _kernelFactory, _stories, _customLogger, _commandDispatcher, _tuning, _scopeFactory);
                            return await cmd.ExecuteAsync(inner.CancellationToken, expertRunId);
                        },
                        runId: expertRunId,
                        threadScope: "story/add_music_tags_to_story",
                        metadata: new Dictionary<string, string>
                        {
                            ["storyId"] = id.ToString(),
                            ["operation"] = "add_music_tags_to_story",
                            ["trigger"] = "stories_index_regen",
                            ["cleaned"] = "MUSICA",
                            ["taggedVersion"] = nextVersion.ToString()
                        },
                        priority: 2);

                    return new CommandResult(true, $"Ripuliti {removed} blocchi MUSICA (v{nextVersion}) e avviato add_music_tags_to_story (run {expertRunId})");
                },
                "Ripulizia MUSICA + rilancio add_music_tags_to_story avviati in background.");

            TempData["StatusMessage"] = $"Rigenerazione MUSICA avviata (run {runId}).";
            return RedirectToPage();
        }

        private bool IsExpertAlreadyQueued(string operationName, long storyId)
        {
            try
            {
                // NOTE: CommandDispatcher.GetActiveCommands() includes also recently completed commands (for ~5 minutes)
                // with status 'completed'/'failed'/'cancelled'. We only want to block re-enqueue when the expert is
                // actually in-flight.
                return _commandDispatcher.GetActiveCommands().Any(s =>
                    string.Equals(s.OperationName, operationName, StringComparison.OrdinalIgnoreCase) &&
                    s.Metadata != null &&
                    s.Metadata.TryGetValue("storyId", out var sid) &&
                    string.Equals(sid, storyId.ToString(), StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(s.Status, "queued", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(s.Status, "running", StringComparison.OrdinalIgnoreCase)));
            }
            catch
            {
                return false;
            }
        }

        private static (string Cleaned, int RemovedCount) RemoveTagBlocks(string input, string tagName)
        {
            if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(tagName))
            {
                return (input ?? string.Empty, 0);
            }

            var normalized = input.Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = normalized.Split('\n').ToList();
            var removed = 0;

            var tagLineRegex = new Regex($@"^\s*\[(?:{Regex.Escape(tagName)})\b[^\]]*\]\s*(?<tail>.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var looksLikeTagStartRegex = new Regex(@"^\s*\[", RegexOptions.Compiled);

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i] ?? string.Empty;
                var match = tagLineRegex.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                // Remove the whole line containing the tag (and inline description, if any)
                lines[i] = string.Empty;
                removed++;

                // If the tag line has no trailing text, the description might be on the next line.
                // Heuristic: remove the immediate next non-empty, non-tag line if it looks like a short description.
                var tail = (match.Groups["tail"].Value ?? string.Empty).Trim();
                if (tail.Length == 0 && i + 1 < lines.Count)
                {
                    var next = lines[i + 1] ?? string.Empty;
                    var nextTrim = next.Trim();
                    if (!string.IsNullOrWhiteSpace(nextTrim) &&
                        !looksLikeTagStartRegex.IsMatch(nextTrim) &&
                        nextTrim.Length <= 200 &&
                        !Regex.IsMatch(nextTrim, @"[.!?]"))
                    {
                        lines[i + 1] = string.Empty;
                        removed++;
                        i++;
                    }
                }
            }

            var rejoined = string.Join("\n", lines);
            rejoined = Regex.Replace(rejoined, @"\n{3,}", "\n\n");
            return (rejoined.Trim(), removed);
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
                    var (success, message) = await _stories.ExecuteNextStatusOperationAsync(id, ctx.RunId);
                    return new CommandResult(success, message ?? (success ? "Operazione completata" : "Operazione fallita"));
                },
                "Operazione di avanzamento avviata in background.");
            TempData["StatusMessage"] = $"Operazione di avanzamento avviata (run {runId}).";
            return RedirectToPage();
        }

        public IActionResult OnPostStartStatusChain(long id)
        {
            var chainId = _stories.EnqueueStatusChain(id);
            TempData["StatusMessage"] = string.IsNullOrWhiteSpace(chainId)
                ? "Catena stati non avviata."
                : $"Catena stati avviata (id {chainId}).";
            return RedirectToPage();
        }

        private void LoadData()
        {
            var allStories = _stories.GetAllStories().ToList();

            // server-side search (basic, in-memory fallback)
            if (!string.IsNullOrWhiteSpace(Search))
            {
                var s = Search!.ToLowerInvariant();
                allStories = allStories.Where(st => (st.Prompt ?? string.Empty).ToLowerInvariant().Contains(s)
                    || (st.Model ?? string.Empty).ToLowerInvariant().Contains(s)
                    || (st.Folder ?? string.Empty).ToLowerInvariant().Contains(s)).ToList();
            }

            // ordering (basic)
            if (!string.IsNullOrWhiteSpace(OrderBy))
            {
                if (OrderBy == "id") allStories = allStories.OrderBy(st => st.Id).ToList();
                else if (OrderBy == "chars") allStories = allStories.OrderByDescending(st => st.CharCount).ToList();
                // fallback: leave as is
            }

            TotalCount = allStories.Count;

            // paging (server-side)
            var skip = (PageIndex - 1) * PageSize;
            var pageItems = allStories.Skip(skip).Take(PageSize).ToList();

            // Load evaluations and flags for each page item
            foreach (var story in pageItems)
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

            var seriesMap = _database.ListAllSeries()
                .ToDictionary(s => s.Id, s => s.Titolo);
            foreach (var story in pageItems)
            {
                if (story.SerieId.HasValue && seriesMap.TryGetValue(story.SerieId.Value, out var serieTitle))
                {
                    story.SerieName = serieTitle;
                }
            }

            Stories = pageItems;
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

        // JSON data endpoint used by client-side JS to fetch stories and allowed actions
        public IActionResult OnGetData()
        {
            LoadData();

            var storiesDto = Stories.Select(s =>
            {
                var evals = s.Evaluations ?? new List<StoryEvaluation>();
                return new
                {
                s.Id,
                Timestamp = DateTime.TryParse(s.Timestamp, out var dt) ? dt.ToString("dd/MM/yyyy HH:mm") : s.Timestamp,
                Prompt = s.Prompt?.Length > 200 ? s.Prompt.Substring(0, 200) + "..." : s.Prompt,
                FullPrompt = s.Prompt,
                s.StatusId,
                s.Status,
                StatusDescription = !string.IsNullOrWhiteSpace(s.StatusDescription) ? s.StatusDescription : (!string.IsNullOrWhiteSpace(s.Status) ? s.Status : "N/D"),
                StatusColor = string.IsNullOrWhiteSpace(s.StatusColor) ? "#6c757d" : s.StatusColor,
                s.Model,
                Folder = s.Folder ?? "-",
                GeneratedTtsJson = s.GeneratedTtsJson,
                GeneratedTts = s.GeneratedTts,
                GeneratedAmbient = s.GeneratedAmbient,
                GeneratedEffects = s.GeneratedEffects,
                GeneratedMusic = s.GeneratedMusic,
                GeneratedMixedAudio = s.GeneratedMixedAudio,
                CharCount = s.CharCount,
                s.TestRunId,
                s.TestStepId,
                s.Score,
                EvalScore = evals.Any() ? ((evals.Average(e => e.TotalScore) * 100.0 / 40.0).ToString("F1") + "/100") : "-",
                Approved = s.Approved,
                s.HasFinalMix,
                s.HasVoiceSource,
                s.Characters,
                Evaluations = evals.Select(e => new {
                    e.Id, e.Model, e.AgentName, e.AgentModel, e.Timestamp, e.TotalScore,
                    e.NarrativeCoherenceScore, e.NarrativeCoherenceDefects,
                    e.OriginalityScore, e.OriginalityDefects,
                    e.EmotionalImpactScore, e.EmotionalImpactDefects,
                    e.ActionScore, e.ActionDefects
                }),
                NextStatus = GetNextStatus(s) is StoryStatus ns ? new { ns.Id, ns.CaptionToExecute, ns.OperationType } : null,
                Actions = GetActionsForStory(s)
                };
            }).ToList();

            var evaluatorsDto = Evaluators.Select(e => new { e.Id, e.Name, e.Role }).ToList();
            var actionEvaluatorsDto = ActionEvaluators.Select(e => new { e.Id, e.Name, e.Role }).ToList();

            return new JsonResult(new { stories = storiesDto, evaluators = evaluatorsDto, actionEvaluators = actionEvaluatorsDto });
        }

        public List<object> GetActionsForStory(StoryRecord s)
        {
            var actions = new List<object>();

            // Advance status (POST)
            var next = GetNextStatus(s);
            if (next != null
                && !string.IsNullOrWhiteSpace(next.OperationType)
                && !next.OperationType.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                actions.Add(new { id = "advance", title = next.CaptionToExecute ?? "Avanza", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "AdvanceStatus", id = s.Id }, Request.Scheme) });
            }

            actions.Add(new { id = "start_chain", title = "Avvia catena stati", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "StartStatusChain", id = s.Id }, Request.Scheme) });

            // Details / Edit (GET)
            actions.Add(new { id = "details", title = "Dettagli", method = "GET", url = Url.Page("/Stories/Details", new { id = s.Id }) });
            actions.Add(new { id = "edit", title = "Modifica", method = "GET", url = Url.Page("/Stories/Edit", new { id = s.Id }) });
            actions.Add(new { id = "delete", title = "Elimina", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "Delete", id = s.Id }, Request.Scheme), confirm = true });
            
            // Revision (POST)
            actions.Add(new { id = "revise", title = "Revisione", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "Revise", id = s.Id }, Request.Scheme) });

            var evalCount = s.Evaluations?.Count ?? 0;
            if (!string.IsNullOrWhiteSpace(s.StoryRevised) && evalCount >= 2)
            {
                actions.Add(new
                {
                    id = "clone_revised",
                    title = "Crea versione migliorata",
                    method = "POST",
                    url = Url.Page("/Stories/Index", null, new { handler = "CloneFromRevised", id = s.Id }, Request.Scheme),
                    confirm = true
                });
            }

            if (!string.IsNullOrWhiteSpace(s.Folder))
            {
                actions.Add(new { id = "add_tags", title = "Rigerera TAG voci", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "AddTags", id = s.Id }, Request.Scheme) });
                actions.Add(new { id = "regen_ambient_tags", title = "Rigenera TAG RUMORI (add_ambient_tags_to_story)", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "RegenAmbientTags", id = s.Id }, Request.Scheme), confirm = true });
                actions.Add(new { id = "regen_fx_tags", title = "Rigenera TAG FX (add_fx_tags_to_story)", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "RegenFxTags", id = s.Id }, Request.Scheme), confirm = true });
                actions.Add(new { id = "regen_music_tags", title = "Rigenera TAG MUSICA (add_music_tags_to_story)", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "RegenMusicTags", id = s.Id }, Request.Scheme), confirm = true });
                // Combined operation to prepare TTS schema: generate schema, normalize characters, assign voices, normalize sentiments
                actions.Add(new { id = "prepare_tts_schema", title = "Prepara TTS schema da TAGs", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "PrepareTtsSchema", id = s.Id }, Request.Scheme) });

                actions.Add(new { id = "gen_tts", title = "Genera WAV TTS", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "GenerateTts", id = s.Id, folderName = s.Folder }, Request.Scheme) });
                actions.Add(new { id = "gen_ambience", title = "Genera WAV Audio Ambientale", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "GenerateAmbience", id = s.Id, folderName = s.Folder }, Request.Scheme) });
                actions.Add(new { id = "gen_fx", title = "Genera WAV Effetti Sonori", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "GenerateFx", id = s.Id, folderName = s.Folder }, Request.Scheme) });
                actions.Add(new { id = "gen_music", title = "Genera WAV Musica", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "GenerateMusic", id = s.Id, folderName = s.Folder }, Request.Scheme) });
                actions.Add(new { id = "mix_final", title = "Mix Audio Finale WAV", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "MixFinalAudio", id = s.Id, folderName = s.Folder }, Request.Scheme) });
                actions.Add(new { id = "final_mix_play", title = "Ascolta Mix Finale", method = "GET", url = Url.Page("/Stories/Index", null, new { handler = "FinalMixAudio", id = s.Id }, Request.Scheme) });
                actions.Add(new { id = "tts_playlist", title = "Ascolta sequenza TTS", method = "GET", url = Url.Page("/Stories/Index", null, new { handler = "TtsPlaylist", id = s.Id }, Request.Scheme) });
            }

/*             var addTagsAction = new { id = "add_tags", title = "Aggiungi TAG", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "AddTags", id = s.Id }, Request.Scheme) };
            var insertAt = actions.FindIndex(a =>
            {
                var id = a.GetType().GetProperty("id")?.GetValue(a)?.ToString() ?? string.Empty;
                var title = a.GetType().GetProperty("title")?.GetValue(a)?.ToString() ?? string.Empty;
                return id.Contains("evaluate", StringComparison.OrdinalIgnoreCase)
                    || title.Contains("Valuta", StringComparison.OrdinalIgnoreCase)
                    || title.Contains("Evaluate", StringComparison.OrdinalIgnoreCase);
            });
            if (insertAt >= 0)
            {
                actions.Insert(insertAt + 1, addTagsAction);
            }
            else
            {
                actions.Add(addTagsAction);
            } */

            // Delete
            

            // Remove any delete-like actions except the main story delete, and any actions with titles
            // that contain "Cancella"/"Cancellazione"/"Elimina" (defensive), then ensure
            // 'revise' and 'add_tags' are the first actions after the pinned group.
            var toRemove = new List<object>();
            foreach (var a in actions)
            {
                var id = a.GetType().GetProperty("id")?.GetValue(a)?.ToString() ?? string.Empty;
                var title = a.GetType().GetProperty("title")?.GetValue(a)?.ToString() ?? string.Empty;
                if (id == "delete") continue; // keep main story delete
                if (id.StartsWith("delete_") || title.IndexOf("cancell", StringComparison.OrdinalIgnoreCase) >= 0 || title.IndexOf("elimin", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    toRemove.Add(a);
                }
            }

            foreach (var r in toRemove) actions.Remove(r);

            // Ensure revise -> add_tags ordering immediately after pinned actions (details/edit/delete)
            var pinnedIds = new[] { "details", "edit", "delete" };
            var insertAfterIndex = actions.FindIndex(a => {
                var id = a.GetType().GetProperty("id")?.GetValue(a)?.ToString() ?? string.Empty;
                return id == "delete";
            });

            if (insertAfterIndex < 0)
            {
                // fallback: place after any pinned action if delete not found
                insertAfterIndex = actions.FindLastIndex(a => {
                    var id = a.GetType().GetProperty("id")?.GetValue(a)?.ToString() ?? string.Empty;
                    return pinnedIds.Contains(id);
                });
            }

            var ensureOrder = new[] { "revise", "add_tags", "regen_ambient_tags", "regen_fx_tags", "regen_music_tags" };
            var currentIndex = insertAfterIndex >= 0 ? insertAfterIndex + 1 : 0;
            foreach (var desiredId in ensureOrder)
            {
                var existing = actions.FirstOrDefault(a => (a.GetType().GetProperty("id")?.GetValue(a)?.ToString() ?? string.Empty) == desiredId);
                if (existing != null)
                {
                    actions.Remove(existing);
                    if (currentIndex >= 0 && currentIndex <= actions.Count)
                    {
                        actions.Insert(currentIndex, existing);
                        currentIndex++;
                    }
                    else
                    {
                        actions.Add(existing);
                    }
                }
            }

            // Ensure TTS-related actions appear immediately after the tag-related actions (add_tags + regen*) when present
            var anchorIndex = actions.FindLastIndex(a =>
            {
                var id = a.GetType().GetProperty("id")?.GetValue(a)?.ToString() ?? string.Empty;
                return id == "regen_music_tags" || id == "regen_fx_tags" || id == "regen_ambient_tags" || id == "add_tags";
            });

            if (anchorIndex >= 0)
            {
                // prefer to place prepare_tts_schema first, then gen_tts (if present)
                var prepare = actions.FirstOrDefault(a => (a.GetType().GetProperty("id")?.GetValue(a)?.ToString() ?? string.Empty) == "prepare_tts_schema");
                if (prepare != null)
                {
                    actions.Remove(prepare);
                    var insertPos = anchorIndex + 1;
                    if (insertPos >= 0 && insertPos <= actions.Count) actions.Insert(insertPos, prepare);
                    else actions.Add(prepare);
                    anchorIndex = insertPos; // update position so next insertion goes after it
                }

                var genTts = actions.FirstOrDefault(a => (a.GetType().GetProperty("id")?.GetValue(a)?.ToString() ?? string.Empty) == "gen_tts");
                if (genTts != null)
                {
                    actions.Remove(genTts);
                    var insertPos = anchorIndex + 1;
                    if (insertPos >= 0 && insertPos <= actions.Count) actions.Insert(insertPos, genTts);
                    else actions.Add(genTts);
                }
            }

            // Evaluators (client will render evaluator-specific actions)
            return actions;
        }
    }
}

