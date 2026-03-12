using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Mail;
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
        private readonly IAgentCallService _modelExecution;
        private readonly CinoOptions _cinoOptions;
        private readonly RepetitionDetectionOptions _repetitionOptions;
        private readonly EmbeddingRepetitionOptions _embeddingRepetitionOptions;
        private readonly StoryEvaluationOptions _storyEvaluationOptions;

        public IndexModel(
            StoriesService stories,
            DatabaseService database,
            ILangChainKernelFactory kernelFactory,
            IAgentCallService modelExecution,
            IOptions<CinoOptions> cinoOptions,
            IOptions<RepetitionDetectionOptions> repetitionOptions,
            IOptions<EmbeddingRepetitionOptions> embeddingRepetitionOptions,
            IOptions<StoryEvaluationOptions> storyEvaluationOptions,
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
            _modelExecution = modelExecution ?? throw new ArgumentNullException(nameof(modelExecution));
            _cinoOptions = cinoOptions?.Value ?? new CinoOptions();
            _repetitionOptions = repetitionOptions?.Value ?? new RepetitionDetectionOptions();
            _embeddingRepetitionOptions = embeddingRepetitionOptions?.Value ?? new EmbeddingRepetitionOptions();
            _storyEvaluationOptions = storyEvaluationOptions?.Value ?? new StoryEvaluationOptions();
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
                TempData["StatusMessage"] = $"Nessuna storia da valutare per l'agente {agent.Description}.";
                return RedirectToPage();
            }

            var runId = $"bulk_eval_{agentId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
            _customLogger?.Start(runId);
            _customLogger?.Append(runId, $"Avvio valutazione batch di {pending.Count} storie con {agent.Description} ({agent.Role})");

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
                    ["agentName"] = agent.Description ?? string.Empty,
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

        public IActionResult OnPostAddMissingEvaluation(long id)
        {
            var story = _stories.GetStoryById(id);
            if (story == null)
            {
                TempData["ErrorMessage"] = "Storia non trovata.";
                return RedirectToPage();
            }

            var hasEvaluations = (_stories.GetEvaluationsForStory(id)?.Count ?? 0) > 0;
            if (hasEvaluations)
            {
                TempData["StatusMessage"] = "La storia ha già almeno una valutazione.";
                return RedirectToPage();
            }

            var agent = _database.ListAgents()
                .Where(a => a.IsActive &&
                    (a.Role?.Equals("story_evaluator", StringComparison.OrdinalIgnoreCase) == true ||
                     a.Role?.Equals("coherence_evaluator", StringComparison.OrdinalIgnoreCase) == true))
                .OrderBy(a => a.Role?.Equals("story_evaluator", StringComparison.OrdinalIgnoreCase) == true ? 0 : 1)
                .ThenBy(a => a.Id)
                .FirstOrDefault();

            if (agent == null)
            {
                TempData["ErrorMessage"] = "Nessun agente valutatore attivo disponibile.";
                return RedirectToPage();
            }

            var isCoherence = agent.Role?.Equals("coherence_evaluator", StringComparison.OrdinalIgnoreCase) == true;
            var opName = isCoherence ? "evaluate_coherence" : "evaluate_story";

            var runId = QueueStoryCommand(
                id,
                opName,
                async ctx =>
                {
                    IStoryCommand cmd = isCoherence
                        ? new EvaluateCoherenceCommand(_stories, id, agent.Id)
                        : new EvaluateStoryCommand(_stories, id, agent.Id);
                    return await cmd.ExecuteAsync(ctx.CancellationToken);
                },
                "Valutazione avviata in background.",
                threadScopeOverride: $"story/{opName}/agent_{agent.Id}",
                metadata: BuildMetadata(id, opName, agent));

            TempData["StatusMessage"] = $"Valutazione avviata in background (run {runId}).";
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
            try
            {
                var result = _stories.TryCloneStoryFromRevised(id);
                if (!result.Success || !result.NewStoryId.HasValue || result.NewStoryId.Value <= 0)
                {
                    TempData["ErrorMessage"] = string.IsNullOrWhiteSpace(result.Message)
                        ? "Clonazione da revisione fallita."
                        : result.Message;
                    return RedirectToPage();
                }

                TempData["StatusMessage"] =
                    $"{result.Message} Revisione della nuova storia accodata automaticamente.";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Errore durante clonazione da revisione: " + ex.Message;
            }

            return RedirectToPage();
        }

        public IActionResult OnPostRerunNreFromPlanSinglePass(long id)
        {
            return RerunNreFromSavedPlan(id, "single_pass");
        }

        public IActionResult OnPostRerunNreFromPlanStateDriven(long id)
        {
            return RerunNreFromSavedPlan(id, "state_driven");
        }

        public IActionResult OnPostSendStoryEmail(long id)
        {
            try
            {
                if (id <= 0)
                {
                    TempData["ErrorMessage"] = "StoryId non valido.";
                    return RedirectToPage();
                }

                var story = _database.GetStoryById(id);
                if (story == null)
                {
                    TempData["ErrorMessage"] = $"Storia {id} non trovata.";
                    return RedirectToPage();
                }

                var evaluations = _database.GetStoryEvaluations(id) ?? new List<StoryEvaluation>();
                if (evaluations.Count == 0)
                {
                    TempData["ErrorMessage"] = "Invio email disponibile solo per storie gia valutate.";
                    return RedirectToPage();
                }

                var avgScoreRaw = evaluations.Average(e => e.TotalScore);
                var avgScore100 = avgScoreRaw * 100.0 / 40.0;
                var recipientsRaw = (_storyEvaluationOptions.send_story_after_evaluation_recipients
                                     ?? _storyEvaluationOptions.SendStoryAfterEvaluationRecipients
                                     ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(recipientsRaw))
                {
                    TempData["ErrorMessage"] = "Destinatari email non configurati (StoryEvaluation.send_story_after_evaluation_recipients).";
                    return RedirectToPage();
                }

                var smtpHost = (_storyEvaluationOptions.SmtpHost ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(smtpHost))
                {
                    TempData["ErrorMessage"] = "SMTP host non configurato (StoryEvaluation.SmtpHost).";
                    return RedirectToPage();
                }

                var revisedText = !string.IsNullOrWhiteSpace(story.StoryRevised)
                    ? story.StoryRevised!
                    : (story.StoryRaw ?? string.Empty);
                var planText = string.IsNullOrWhiteSpace(story.NrePlanSummary)
                    ? "(piano non disponibile)"
                    : story.NrePlanSummary!;

                var subject = $"Story #{id} - {(string.IsNullOrWhiteSpace(story.Title) ? "Untitled" : story.Title)}";
                var safeTitle = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(story.Title) ? "Untitled" : story.Title);
                var safePlan = WebUtility.HtmlEncode(planText ?? string.Empty).Replace("\r\n", "\n").Replace("\n", "<br/>");
                var safeStory = WebUtility.HtmlEncode(revisedText ?? string.Empty).Replace("\r\n", "\n").Replace("\n", "<br/>");
                var bodyHtml =
$@"<!doctype html>
<html>
<head>
  <meta charset=""utf-8"" />
</head>
<body style=""margin:0;padding:24px;background:#ece7dc;color:#2d2418;font-family:Georgia,'Times New Roman',serif;"">
  <div style=""max-width:900px;margin:0 auto;"">
    <div style=""padding:16px 18px;margin-bottom:14px;background:#f6f1e6;border:1px solid #d9cdb8;border-radius:8px;"">
      <div><strong>StoryId:</strong> {id}</div>
      <div><strong>Titolo:</strong> {safeTitle}</div>
      <div><strong>Valutazione media:</strong> {avgScore100:F1}/100</div>
      <div><strong>Numero valutazioni:</strong> {evaluations.Count}</div>
    </div>

    <div style=""padding:22px 24px;background:linear-gradient(180deg,#f8f2e7 0%,#f2e6d4 100%);border:1px solid #cdbb9d;border-radius:10px;box-shadow:0 6px 18px rgba(62,42,17,.15);"">
      <h3 style=""margin:0 0 10px 0;font-size:19px;font-weight:700;"">Piano</h3>
      <div style=""line-height:1.65;font-size:16px;white-space:normal;"">{safePlan}</div>
      <hr style=""margin:18px 0;border:none;border-top:1px solid #cdbb9d;"" />
      <h3 style=""margin:0 0 10px 0;font-size:19px;font-weight:700;"">Testo Revised</h3>
      <div style=""line-height:1.75;font-size:17px;white-space:normal;text-align:justify;"">{safeStory}</div>
    </div>
  </div>
</body>
</html>";

                using var message = new MailMessage();
                message.From = new MailAddress(string.IsNullOrWhiteSpace(_storyEvaluationOptions.SmtpFrom)
                    ? "noreply@localhost"
                    : _storyEvaluationOptions.SmtpFrom!.Trim());
                foreach (var recipient in recipientsRaw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!string.IsNullOrWhiteSpace(recipient))
                    {
                        message.To.Add(recipient);
                    }
                }
                if (message.To.Count == 0)
                {
                    TempData["ErrorMessage"] = "Nessun destinatario email valido.";
                    return RedirectToPage();
                }

                message.Subject = subject;
                message.Body = bodyHtml;
                message.IsBodyHtml = true;

                using var smtp = new SmtpClient(smtpHost, _storyEvaluationOptions.SmtpPort)
                {
                    EnableSsl = _storyEvaluationOptions.SmtpUseSsl
                };

                var smtpUser = (_storyEvaluationOptions.SmtpUsername ?? string.Empty).Trim();
                var smtpPass = _storyEvaluationOptions.SmtpPassword ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(smtpUser))
                {
                    smtp.Credentials = new NetworkCredential(smtpUser, smtpPass);
                }
                else
                {
                    smtp.UseDefaultCredentials = true;
                }

                smtp.Send(message);
                TempData["StatusMessage"] = $"Email inviata per la storia {id}.";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Errore invio email: " + ex.Message;
            }

            return RedirectToPage();
        }

        private IActionResult RerunNreFromSavedPlan(long sourceStoryId, string method)
        {
            try
            {
                if (sourceStoryId <= 0)
                {
                    TempData["ErrorMessage"] = "StoryId sorgente non valido.";
                    return RedirectToPage();
                }

                var sourceStory = _database.GetStoryById(sourceStoryId);
                if (sourceStory == null)
                {
                    TempData["ErrorMessage"] = $"Storia sorgente {sourceStoryId} non trovata.";
                    return RedirectToPage();
                }

                var savedPlan = sourceStory.NrePlanSummary?.Trim();
                if (string.IsNullOrWhiteSpace(savedPlan))
                {
                    TempData["ErrorMessage"] = "Nessun piano NRE salvato sulla storia sorgente.";
                    return RedirectToPage();
                }

                var prompt = sourceStory.Prompt?.Trim();
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    TempData["ErrorMessage"] = "Prompt sorgente mancante: impossibile rilanciare la storia.";
                    return RedirectToPage();
                }

                var nreEngine = HttpContext.RequestServices.GetService<NreEngine>();
                var callCenter = HttpContext.RequestServices.GetService<ICallCenter>();
                var nreOptionsAccessor = HttpContext.RequestServices.GetService<IOptions<NarrativeRuntimeEngineOptions>>();
                if (nreEngine == null || nreOptionsAccessor == null)
                {
                    TempData["ErrorMessage"] = "Servizi NRE non disponibili nel contesto corrente.";
                    return RedirectToPage();
                }

                var nreOptions = nreOptionsAccessor.Value ?? new NarrativeRuntimeEngineOptions();
                var normalizedMethod = string.Equals(method, "single_pass", StringComparison.OrdinalIgnoreCase)
                    ? "single_pass"
                    : "state_driven";
                var planSteps = CountPlanSteps(savedPlan);
                var maxSteps = planSteps > 0 ? planSteps : Math.Max(1, nreOptions.DefaultMaxSteps);

                var request = new EngineRequest
                {
                    EngineName = nreOptions.EngineName,
                    Method = normalizedMethod,
                    StructureMode = "standard",
                    CostSeverity = "medium",
                    CombatIntensity = "normal",
                    MaxSteps = maxSteps,
                    SnapshotOnFailure = true,
                    RunId = Guid.NewGuid().ToString("N"),
                    UserPrompt = prompt,
                    ResourceHints = null,
                    PreApprovedPlanSummary = savedPlan,
                    SeriesId = sourceStory.SerieId,
                    SeriesEpisodeNumber = sourceStory.SerieEpisode
                };

                var titlePrefix = string.IsNullOrWhiteSpace(sourceStory.Title) ? $"Story {sourceStoryId}" : sourceStory.Title.Trim();
                var newTitle = $"{titlePrefix} [rerun {normalizedMethod}]";
                var runId = $"run_nre_from_plan_{normalizedMethod}_{sourceStoryId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                _customLogger?.Start(runId);
                _customLogger?.Append(runId, $"Avvio rerun NRE da piano salvato (sourceStoryId={sourceStoryId}, method={normalizedMethod}).");

                var cmd = new RunNreCommand(
                    title: newTitle,
                    request: request,
                    database: _database,
                    engine: nreEngine,
                    options: Microsoft.Extensions.Options.Options.Create(nreOptions),
                    logger: _customLogger,
                    dispatcher: _commandDispatcher,
                    storiesService: _stories,
                    callCenter: callCenter);

                _commandDispatcher.Enqueue(
                    "run_nre",
                    async ctx => await cmd.ExecuteAsync(ctx.CancellationToken, ctx.RunId),
                    runId: runId,
                    threadScope: $"story/run_nre_from_plan/{sourceStoryId}",
                    metadata: new Dictionary<string, string>
                    {
                        ["operation"] = "run_nre_from_saved_plan",
                        ["sourceStoryId"] = sourceStoryId.ToString(),
                        ["method"] = normalizedMethod,
                        ["maxSteps"] = maxSteps.ToString(),
                        ["engine"] = nreOptions.EngineName
                    },
                    priority: 2);

                TempData["StatusMessage"] = $"Rigenerazione NRE accodata da piano salvato (run {runId}).";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Errore avviando la rigenerazione NRE da piano: " + ex.Message;
            }

            return RedirectToPage();
        }

        private static int CountPlanSteps(string? planSummary)
        {
            if (string.IsNullOrWhiteSpace(planSummary))
            {
                return 0;
            }

            var count = 0;
            foreach (var line in planSummary.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Regex.IsMatch(line, @"^\d+\.\s"))
                {
                    count++;
                }
            }

            return count;
        }

        public IActionResult OnPostCinoOptimize(long id)
        {
            var story = _database.GetStoryById(id);
            if (story == null)
            {
                TempData["ErrorMessage"] = $"Storia {id} non trovata.";
                return RedirectToPage();
            }

            var sourceText = string.IsNullOrWhiteSpace(story.StoryRevised)
                ? (story.StoryRaw ?? string.Empty)
                : story.StoryRevised!;

            if (string.IsNullOrWhiteSpace(sourceText))
            {
                TempData["ErrorMessage"] = $"Storia {id} senza testo utilizzabile per CINO.";
                return RedirectToPage();
            }

            var title = string.IsNullOrWhiteSpace(story.Title) ? $"Story {id}" : story.Title!.Trim();
            var runId = $"cino_optimize_story_{id}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";

            _customLogger?.Start(runId);
            _customLogger?.Append(runId, $"Avvio CINO da storia {id}");

            var cmd = new CinoOptimizeStoryCommand(
                title: title,
                prompt: sourceText,
                database: _database,
                storiesService: _stories,
                modelExecution: _modelExecution,
                dispatcher: _commandDispatcher,
                options: _cinoOptions,
                repetitionOptions: _repetitionOptions,
                embeddingRepetitionOptions: _embeddingRepetitionOptions,
                logger: _customLogger);

            _commandDispatcher.Enqueue(
                "cino_optimize_story",
                async ctx => await cmd.ExecuteAsync(ctx.CancellationToken, ctx.RunId),
                runId: runId,
                threadScope: $"story/cino_optimize_story/{id}",
                metadata: new Dictionary<string, string>
                {
                    ["operation"] = "cino_optimize_story",
                    ["storyId"] = id.ToString(),
                    ["sourceStoryId"] = id.ToString(),
                    ["title"] = title,
                    ["targetScore"] = _cinoOptions.TargetScore.ToString(),
                    ["maxDurationSec"] = _cinoOptions.MaxDurationSeconds.ToString(),
                    ["minLengthGrowthPercent"] = _cinoOptions.MinLengthGrowthPercent.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });

            TempData["StatusMessage"] = $"CINO avviato da storia {id} (run {runId}).";
            return RedirectToPage();
        }

        public IActionResult OnPostCompleteStory(long id)
        {
            var story = _database.GetStoryById(id);
            if (story == null || story.Deleted)
            {
                TempData["ErrorMessage"] = $"Storia {id} non trovata.";
                return RedirectToPage();
            }

            var statuses = _database.ListAllStoryStatuses() ?? new List<StoryStatus>();
            var evaluatedStatus = statuses.FirstOrDefault(s => string.Equals(s.Code, "evaluated", StringComparison.OrdinalIgnoreCase));
            if (evaluatedStatus == null)
            {
                TempData["ErrorMessage"] = "Status 'evaluated' non configurato.";
                return RedirectToPage();
            }

            var currentStatus = story.StatusId.HasValue
                ? statuses.FirstOrDefault(s => s.Id == story.StatusId.Value)
                : null;
            var currentStep = currentStatus?.Step ?? (story.StatusStep ?? 0);
            if (currentStep < evaluatedStatus.Step)
            {
                TempData["ErrorMessage"] = "Completa storia e' disponibile solo per storie almeno valutate (status >= evaluated).";
                return RedirectToPage();
            }

            var runId = _stories.EnqueueAllNextStatusEnqueuer(id, trigger: "manual_complete_story", priority: 3);
            if (string.IsNullOrWhiteSpace(runId))
            {
                TempData["ErrorMessage"] = "Nessuna operazione disponibile o pipeline gia in corso.";
            }
            else
            {
                TempData["StatusMessage"] = $"Pipeline accodata fino al mix finale (run {runId}).";
            }

            return RedirectToPage();
        }

        public IActionResult OnPostResetToEvaluated(long id)
        {
            var story = _database.GetStoryById(id);
            if (story == null || story.Deleted)
            {
                TempData["ErrorMessage"] = $"Storia {id} non trovata.";
                return RedirectToPage();
            }

            var evals = _database.GetStoryEvaluations(id) ?? new List<StoryEvaluation>();
            if (evals.Count == 0)
            {
                TempData["ErrorMessage"] = "Reset a evaluated non consentito: nessuna valutazione trovata.";
                return RedirectToPage();
            }

            var ok = _stories.ForceResetToEvaluatedAndCleanup(story, out var msg);
            if (!ok)
            {
                TempData["ErrorMessage"] = $"Reset a evaluated fallito: {msg}";
            }
            else
            {
                TempData["StatusMessage"] = $"Story {id} riportata a evaluated con cleanup completato.";
            }

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

                var cmd = new BatchSummarizeStoriesEnqueuerCommand(
                    _database,
                    kernelFactory,
                    _commandDispatcher,
                    _customLogger!,
                    scopeFactory: _scopeFactory,
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
                "Generazione audio ambientale avviata in background.",
                metadata: new Dictionary<string, string> { ["folder"] = folderName ?? string.Empty },
                batch: true);

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
                "Generazione effetti sonori avviata in background.",
                metadata: new Dictionary<string, string> { ["folder"] = folderName ?? string.Empty },
                batch: true);

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
                "Generazione musica avviata in background.",
                metadata: new Dictionary<string, string> { ["folder"] = folderName ?? string.Empty },
                batch: true);

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

        public IActionResult OnPostGenerateStoryVideo(long id, string folderName)
        {
            if (id <= 0 || string.IsNullOrWhiteSpace(folderName))
            {
                TempData["ErrorMessage"] = "Story o cartella non validi per la generazione video.";
                return RedirectToPage();
            }

            try
            {
                var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", folderName);
                var hasFinalMix = System.IO.File.Exists(Path.Combine(folderPath, "final_mix.mp3"))
                    || System.IO.File.Exists(Path.Combine(folderPath, "final_mix.wav"));
                if (!hasFinalMix)
                {
                    TempData["ErrorMessage"] = "Generazione video non disponibile: manca il mix finale (final_mix.mp3/final_mix.wav).";
                    return RedirectToPage();
                }
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"Errore nella verifica del mix finale: {ex.Message}";
                return RedirectToPage();
            }

            var runId = QueueStoryCommand(
                id,
                "generate_story_video",
                async ctx =>
                {
                    var (success, error) = await _stories.GenerateStoryVideoForStoryAsync(id, folderName, ctx.RunId);
                    var message = success ? "Video con sottotitoli completato." : error;
                    return new CommandResult(success, message);
                },
                "Generazione video con sottotitoli avviata in background.");

            TempData["StatusMessage"] = $"Generazione video avviata (run {runId}).";
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

        public IActionResult OnPostRepairTtsAudioMetadata(long id)
        {
            var runId = QueueStoryCommand(
                id,
                "repair_tts_audio_metadata",
                async ctx =>
                {
                    var (success, message) = await _stories.RepairTtsAudioMetadataAsync(id);
                    return new CommandResult(success, message);
                },
                "Riparazione metadati audio in tts_schema avviata in background.");

            TempData["StatusMessage"] = $"Riparazione metadati audio avviata (run {runId}).";
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
                    const int totalSteps = 5;

                    void ReportStep(int current, string description)
                    {
                        try
                        {
                            _commandDispatcher.UpdateStep(ctx.RunId, current, totalSteps, description);
                        }
                        catch { }

                        try
                        {
                            _customLogger?.Append(ctx.RunId, $"[{id}] {description}");
                        }
                        catch { }
                    }

                    ReportStep(0, "prepare_tts_schema: avvio");

                    // 1) Generate TTS schema JSON
                    try
                    {
                        ReportStep(1, "prepare_tts_schema: generazione tts_schema.json in corso");
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
                        ReportStep(2, "prepare_tts_schema: normalizzazione personaggi in corso");
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
                        ReportStep(3, "prepare_tts_schema: assegnazione voci in corso");
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
                        ReportStep(4, "prepare_tts_schema: normalizzazione sentiment in corso");
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
                    ReportStep(5, overallSuccess
                        ? "prepare_tts_schema: completato"
                        : "prepare_tts_schema: completato con errori");
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

        public IActionResult OnGetFinalVideo(long id)
        {
            var story = _stories.GetStoryById(id);
            if (story == null || string.IsNullOrWhiteSpace(story.Folder)) return NotFound();

            var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", story.Folder);
            var videoPath = Path.Combine(folderPath, "final_video.mp4");
            if (!System.IO.File.Exists(videoPath))
            {
                return NotFound("File video finale non trovato");
            }

            return new PhysicalFileResult(videoPath, "video/mp4")
            {
                FileDownloadName = "final_video.mp4",
                EnableRangeProcessing = true
            };
        }

        public IActionResult OnGetFinalVideoFullscreen(long id)
        {
            var story = _stories.GetStoryById(id);
            if (story == null || string.IsNullOrWhiteSpace(story.Folder)) return NotFound();

            var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", story.Folder);
            var videoPath = Path.Combine(folderPath, "final_video.mp4");
            if (!System.IO.File.Exists(videoPath))
            {
                return NotFound("File video finale non trovato");
            }

            var videoUrl = Url.Page("/Stories/Index", null, new { handler = "FinalVideo", id }, Request.Scheme)
                ?? $"/Stories/Index?handler=FinalVideo&id={id}";
            var safeTitle = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(story.Title) ? $"Story {id}" : story.Title);
            var safeVideoUrl = WebUtility.HtmlEncode(videoUrl);

            var html = $@"<!doctype html>
<html lang=""it"">
<head>
  <meta charset=""utf-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
  <title>{safeTitle} - Video</title>
  <style>
    html, body {{
      margin: 0;
      width: 100%;
      height: 100%;
      background: #000;
      color: #fff;
      font-family: Arial, sans-serif;
      overflow: hidden;
    }}
    #wrap {{
      position: fixed;
      inset: 0;
      display: flex;
      align-items: center;
      justify-content: center;
      background: #000;
    }}
    video {{
      width: 100vw;
      height: 100vh;
      object-fit: contain;
      background: #000;
    }}
    #fsBtn {{
      position: fixed;
      top: 12px;
      right: 12px;
      z-index: 10;
      border: 1px solid #666;
      background: rgba(0,0,0,.65);
      color: #fff;
      border-radius: 6px;
      padding: 8px 12px;
      cursor: pointer;
    }}
  </style>
</head>
<body>
  <button id=""fsBtn"" type=""button"">Pieno schermo</button>
  <div id=""wrap"">
    <video id=""video"" controls autoplay playsinline src=""{safeVideoUrl}""></video>
  </div>
  <script>
    (function() {{
      const video = document.getElementById('video');
      const wrap = document.getElementById('wrap');
      const btn = document.getElementById('fsBtn');

      async function openFullscreen() {{
        const target = wrap || video || document.documentElement;
        if (!target) return;
        try {{
          if (document.fullscreenElement) return;
          await target.requestFullscreen();
        }} catch (e) {{
          // ignore, user can click button again
        }}
      }}

      btn.addEventListener('click', openFullscreen);
      document.addEventListener('keydown', function(e) {{
        if (e.key === 'f' || e.key === 'F') openFullscreen();
      }});

      // Best effort: in alcuni browser l'apertura in nuova tab da click permette fullscreen diretto.
      setTimeout(openFullscreen, 120);
    }})();
  </script>
</body>
</html>";

            return Content(html, "text/html; charset=utf-8");
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
                static JsonNode? GetNodeCaseInsensitive(JsonObject obj, params string[] names)
                {
                    foreach (var name in names)
                    {
                        if (obj.TryGetPropertyValue(name, out var value))
                        {
                            return value;
                        }
                    }

                    foreach (var kvp in obj)
                    {
                        if (names.Any(n => string.Equals(kvp.Key, n, StringComparison.OrdinalIgnoreCase)))
                        {
                            return kvp.Value;
                        }
                    }

                    return null;
                }

                static string ReadString(JsonNode? node)
                {
                    if (node == null) return string.Empty;
                    if (node is JsonValue)
                    {
                        try { return node.GetValue<string>() ?? string.Empty; } catch { }
                    }
                    return node.ToString();
                }

                static int? ReadIntNullable(JsonNode? node)
                {
                    if (node == null) return null;
                    if (node is JsonValue)
                    {
                        try { return node.GetValue<int>(); } catch { }
                        try
                        {
                            var asLong = node.GetValue<long>();
                            if (asLong >= int.MinValue && asLong <= int.MaxValue) return (int)asLong;
                        }
                        catch { }
                        try
                        {
                            var asDouble = node.GetValue<double>();
                            if (asDouble >= int.MinValue && asDouble <= int.MaxValue) return (int)Math.Round(asDouble);
                        }
                        catch { }
                    }

                    return int.TryParse(node.ToString(), out var parsed) ? parsed : null;
                }

                var json = System.IO.File.ReadAllText(schemaPath);
                var root = JsonNode.Parse(json) as JsonObject;
                if (root == null) return NotFound("Schema TTS non valido");

                var timelineNode = GetNodeCaseInsensitive(root, "Timeline", "timeline");
                if (timelineNode is not JsonArray timeline) return NotFound("Timeline mancante nello schema");

                var items = new List<object>();

                foreach (var node in timeline.OfType<JsonObject>())
                {
                    var fileName = ReadString(GetNodeCaseInsensitive(node, "FileName", "fileName"));
                    if (string.IsNullOrWhiteSpace(fileName)) continue;

                    var character = ReadString(GetNodeCaseInsensitive(node, "Character", "character"));
                    var text = ReadString(GetNodeCaseInsensitive(node, "Text", "text"));
                    var durationMs = ReadIntNullable(GetNodeCaseInsensitive(node, "DurationMs", "durationMs"));

                    var url = Url.Page("/Stories/Index", null, new { handler = "TtsAudio", id = story.Id, file = fileName }, Request.Scheme);
                    items.Add(new
                    {
                        url,
                        character,
                        text,
                        durationMs
                    });
                }

                return new JsonResult(new { items, gapMs = 0 });
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
                        _tuning);
                    void OnProgress(object? _, CommandProgressEventArgs args)
                    {
                        _commandDispatcher.UpdateStep(ctx.RunId, args.Current, args.Max, args.Description);
                    }

                    cmd.Progress += OnProgress;
                    try
                    {
                        return await cmd.ExecuteAsync(ctx.CancellationToken, ctx.RunId);
                    }
                    finally
                    {
                        cmd.Progress -= OnProgress;
                    }
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
                            var cmd = new AddAmbientTagsToStoryCommand(id, _database, _kernelFactory, _stories, _customLogger, _tuning);
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
                            var cmd = new AddFxTagsToStoryCommand(id, _database, _kernelFactory, _stories, _customLogger, _tuning);
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
                            var cmd = new AddMusicTagsToStoryCommand(id, _database, _kernelFactory, _stories, _customLogger, _tuning);
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
            var chainId = _stories.EnqueueStatusChain(id);
            TempData["StatusMessage"] = string.IsNullOrWhiteSpace(chainId)
                ? "Nessuno stato successivo disponibile o catena gia attiva."
                : $"Comando successivo accodato (chain {chainId}).";
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
                        story.HasFinalVideo = System.IO.File.Exists(Path.Combine(folderPath, "final_video.mp4"));
                    }
                    catch { story.HasVoiceSource = false; story.HasFinalMix = false; story.HasFinalVideo = false; }
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

        private string QueueStoryCommand(long storyId, string operationCode, Func<CommandContext, Task<CommandResult>> operation, string? startMessage = null, string? threadScopeOverride = null, IReadOnlyDictionary<string, string>? metadata = null, bool batch = false)
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
                metadata: MergeMetadata(storyId, operationCode, metadata),
                batch: batch);

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
                ["agentName"] = agent.Description ?? string.Empty,
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
                LastErrorOperation = s.LastErrorOperation,
                LastErrorDate = s.LastErrorDate,
                LastErrorText = s.LastErrorText,
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
                s.HasFinalVideo,
                s.HasVoiceSource,
                s.Characters,
                Evaluations = evals.Select(e => new {
                    e.Id, e.Model, e.AgentName, e.AgentModel, e.Timestamp, e.TotalScore,
                    e.NarrativeCoherenceScore, e.NarrativeCoherenceDefects,
                    e.OriginalityScore, e.OriginalityDefects,
                    e.EmotionalImpactScore, e.EmotionalImpactDefects,
                    e.ActionScore, e.ActionDefects,
                    e.StoryLengthChars, e.LenghtPenalityCharsLimit, e.LenghtPenalityPercentageApplyed
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
            actions.Add(new { id = "reset_to_evaluated", title = "Reset a evaluated (cleanup)", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "ResetToEvaluated", id = s.Id }, Request.Scheme), confirm = true });
            actions.Add(new { id = "cino_optimize", title = "Ottimizza con CINO", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "CinoOptimize", id = s.Id }, Request.Scheme), confirm = true });

            var evalCount = s.Evaluations?.Count ?? 0;
            if (evalCount == 0)
            {
                actions.Add(new
                {
                    id = "add_missing_evaluation",
                    title = "Aggiungi valutazione",
                    method = "POST",
                    url = Url.Page("/Stories/Index", null, new { handler = "AddMissingEvaluation", id = s.Id }, Request.Scheme),
                    confirm = true
                });
            }

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

            if (!string.IsNullOrWhiteSpace(s.NrePlanSummary))
            {
                actions.Add(new
                {
                    id = "rerun_nre_from_plan_single_pass",
                    title = "Rifai NRE da piano (single_pass)",
                    method = "POST",
                    url = Url.Page("/Stories/Index", null, new { handler = "RerunNreFromPlanSinglePass", id = s.Id }, Request.Scheme),
                    confirm = true
                });
                actions.Add(new
                {
                    id = "rerun_nre_from_plan_state_driven",
                    title = "Rifai NRE da piano (state_driven)",
                    method = "POST",
                    url = Url.Page("/Stories/Index", null, new { handler = "RerunNreFromPlanStateDriven", id = s.Id }, Request.Scheme),
                    confirm = true
                });
            }

            if (!string.IsNullOrWhiteSpace(s.Folder))
            {
                actions.Add(new { id = "add_tags", title = "Rigerera TAG voci", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "AddTags", id = s.Id }, Request.Scheme) });
                actions.Add(new { id = "regen_ambient_tags", title = "Rigenera TAG RUMORI (add_ambient_tags_to_story)", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "RegenAmbientTags", id = s.Id }, Request.Scheme), confirm = true });
                actions.Add(new { id = "regen_fx_tags", title = "Rigenera TAG FX (add_fx_tags_to_story)", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "RegenFxTags", id = s.Id }, Request.Scheme), confirm = true });
                actions.Add(new { id = "regen_music_tags", title = "Rigenera TAG MUSICA (add_music_tags_to_story)", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "RegenMusicTags", id = s.Id }, Request.Scheme), confirm = true });
                actions.Add(new { id = "repair_tts_audio_metadata", title = "Ripara metadati audio TTS (no dialoghi/timing)", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "RepairTtsAudioMetadata", id = s.Id }, Request.Scheme), confirm = true });
                // Combined operation to prepare TTS schema: generate schema, normalize characters, assign voices, normalize sentiments
                actions.Add(new { id = "prepare_tts_schema", title = "Prepara TTS schema da TAGs", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "PrepareTtsSchema", id = s.Id }, Request.Scheme) });

                actions.Add(new { id = "gen_tts", title = "Genera WAV TTS", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "GenerateTts", id = s.Id, folderName = s.Folder }, Request.Scheme) });
                actions.Add(new { id = "gen_ambience", title = "Genera WAV Audio Ambientale", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "GenerateAmbience", id = s.Id, folderName = s.Folder }, Request.Scheme) });
                actions.Add(new { id = "gen_fx", title = "Genera WAV Effetti Sonori", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "GenerateFx", id = s.Id, folderName = s.Folder }, Request.Scheme) });
                actions.Add(new { id = "gen_music", title = "Genera WAV Musica", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "GenerateMusic", id = s.Id, folderName = s.Folder }, Request.Scheme) });
                actions.Add(new { id = "mix_final", title = "Mix Audio Finale WAV", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "MixFinalAudio", id = s.Id, folderName = s.Folder }, Request.Scheme) });
                actions.Add(new { id = "final_mix_play", title = "Ascolta Mix Finale", method = "GET", url = Url.Page("/Stories/Index", null, new { handler = "FinalMixAudio", id = s.Id }, Request.Scheme) });
                actions.Add(new { id = "tts_playlist", title = "Ascolta sequenza TTS", method = "GET", url = Url.Page("/Stories/Index", null, new { handler = "TtsPlaylist", id = s.Id }, Request.Scheme) });

                if (s.HasFinalMix || s.GeneratedMixedAudio)
                {
                    actions.Add(new { id = "gen_video", title = "Genera Video con Sottotitoli", method = "POST", url = Url.Page("/Stories/Index", null, new { handler = "GenerateStoryVideo", id = s.Id, folderName = s.Folder }, Request.Scheme) });
                }

                if (s.HasFinalVideo)
                {
                    actions.Add(new { id = "final_video_play", title = "Guarda Video Finale", method = "GET", url = Url.Page("/Stories/Index", null, new { handler = "FinalVideo", id = s.Id }, Request.Scheme) });
                    actions.Add(new { id = "final_video_fullscreen", title = "Guarda Video Finale (fullscreen)", method = "GET", url = Url.Page("/Stories/Index", null, new { handler = "FinalVideoFullscreen", id = s.Id }, Request.Scheme) });
                }
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

            var ensureOrder = new[] { "revise", "cino_optimize", "add_tags", "regen_ambient_tags", "regen_fx_tags", "regen_music_tags", "repair_tts_audio_metadata" };
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




