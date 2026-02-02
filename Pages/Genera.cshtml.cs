using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;
using TinyGenerator.Services.Commands;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

using SeriesModel = TinyGenerator.Models.Series;

namespace TinyGenerator.Pages;

public class GeneraModel : PageModel
{
    [BindProperty]
    public string? Title { get; set; }
    private readonly DatabaseService _database;
    private readonly MultiStepOrchestrationService? _orchestrator;
    private readonly StoriesService _storiesService;
    private readonly CommandDispatcher _dispatcher;
    private readonly ILangChainKernelFactory _kernelFactory;
    private readonly ICustomLogger _customLogger;
    private readonly ILogger<GeneraModel> _logger;
    private readonly CommandTuningOptions _tuning;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TextValidationService _textValidationService;

    public GeneraModel(
        DatabaseService database,
        CommandDispatcher dispatcher,
        ILangChainKernelFactory kernelFactory,
        StoriesService storiesService,
        ICustomLogger customLogger,
        ILogger<GeneraModel> logger,
        IOptions<CommandTuningOptions> tuningOptions,
        TextValidationService textValidationService,
        MultiStepOrchestrationService? orchestrator = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        _database = database;
        _orchestrator = orchestrator;
        _storiesService = storiesService;
        _dispatcher = dispatcher;
        _kernelFactory = kernelFactory;
        _customLogger = customLogger;
        _logger = logger;
        _tuning = tuningOptions.Value ?? new CommandTuningOptions();
        _scopeFactory = scopeFactory ?? throw new InvalidOperationException("IServiceScopeFactory not available");
        _textValidationService = textValidationService ?? throw new ArgumentNullException(nameof(textValidationService));
    }

    [BindProperty]
    public string Prompt { get; set; } = string.Empty;

    [BindProperty]
    public string Writer { get; set; } = "All";

    [BindProperty]
    public int WriterAgentId { get; set; } = 0;

    [BindProperty]
    public int SeriesId { get; set; } = 0;

    [BindProperty]
    public int EpisodeId { get; set; } = 0;

    [BindProperty]
    public int SeriesWriterAgentId { get; set; } = 0;

    // State-driven (Narrative Engine) series episode generation
    [BindProperty]
    public int StateSeriesId { get; set; } = 0;

    [BindProperty]
    public int StateEpisodeId { get; set; } = 0;

    [BindProperty]
    public int StateWriterAgentId { get; set; } = 0;

    [BindProperty]
    public int StateTargetMinutes { get; set; } = 20;

    [BindProperty]
    public int StateWordsPerMinute { get; set; } = 150;

    [BindProperty]
    public long StateStoryId { get; set; } = 0;

    [BindProperty]
    public int StateNextChunkWriterAgentId { get; set; } = 0;

    public List<Agent> Agents { get; set; } = new();
    public List<Agent> StateWriterAgents { get; set; } = new();
    public List<TinyGenerator.Models.Series> SeriesList { get; set; } = new();
    public List<SeriesEpisode> SeriesEpisodes { get; set; } = new();

    public object? Story { get; set; }
    public string Status => _status.ToString();
    public bool IsProcessing { get; set; }

    private StringBuilder _status = new();

    public void OnGet()
    {
        // Load writer agents for dropdown (only those with a multi-step template)
        Agents = _database.ListAgents()
            .Where(a => a.IsActive && a.Role.Contains("writer", StringComparison.OrdinalIgnoreCase) && a.MultiStepTemplateId.HasValue)
            .OrderBy(a => a.Name)
            .ToList();

        // Populate MultiStepTemplateName for display
        foreach (var agent in Agents)
        {
            if (agent.MultiStepTemplateId.HasValue)
            {
                var template = _database.GetStepTemplateById(agent.MultiStepTemplateId.Value);
                agent.MultiStepTemplateName = template?.Name;
            }
        }

        // Default selection: first available agent
        if (WriterAgentId == 0 && Agents.Count > 0)
        {
            WriterAgentId = Agents[0].Id;
        }

        // State-driven writers: allow any active writer (multi-step template not required)
        StateWriterAgents = _database.ListAgents()
            .Where(a => a.IsActive && a.Role.Contains("writer", StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Name)
            .ToList();

        // Populate MultiStepTemplateName where available (for nicer display)
        foreach (var agent in StateWriterAgents)
        {
            if (agent.MultiStepTemplateId.HasValue)
            {
                var template = _database.GetStepTemplateById(agent.MultiStepTemplateId.Value);
                agent.MultiStepTemplateName = template?.Name;
            }
        }

        SeriesList = _database.ListAllSeries();
        SeriesEpisodes = _database.ListAllSeriesEpisodes();

        if (SeriesId == 0 && SeriesList.Count > 0)
        {
            SeriesId = SeriesList[0].Id;
        }
        if (SeriesWriterAgentId == 0 && Agents.Count > 0)
        {
            SeriesWriterAgentId = Agents[0].Id;
        }
        if (EpisodeId == 0)
        {
            var firstEpisode = SeriesEpisodes.FirstOrDefault(e => e.SerieId == SeriesId);
            if (firstEpisode != null) EpisodeId = firstEpisode.Id;
        }

        if (StateSeriesId == 0 && SeriesList.Count > 0)
        {
            StateSeriesId = SeriesList[0].Id;
        }
        if (StateWriterAgentId == 0 && StateWriterAgents.Count > 0)
        {
            StateWriterAgentId = StateWriterAgents[0].Id;
        }
        if (StateNextChunkWriterAgentId == 0 && StateWriterAgents.Count > 0)
        {
            StateNextChunkWriterAgentId = StateWriterAgents[0].Id;
        }
        if (StateEpisodeId == 0)
        {
            var firstEpisode = SeriesEpisodes.FirstOrDefault(e => e.SerieId == StateSeriesId);
            if (firstEpisode != null) StateEpisodeId = firstEpisode.Id;
        }
    }

    // Start generation in background. Returns a JSON with generation id.
    public async Task<IActionResult> OnPostStartAsync()
    {
        if (string.IsNullOrWhiteSpace(Prompt))
        {
            return BadRequest(new { error = "Il prompt è obbligatorio." });
        }
        if (string.IsNullOrWhiteSpace(Title))
        {
            return BadRequest(new { error = "Il titolo è obbligatorio." });
        }
        if (WriterAgentId <= 0)
        {
            return BadRequest(new { error = "Seleziona un writer agent per avviare la generazione." });
        }
        if (_orchestrator == null)
        {
            return BadRequest(new { error = "Orchestrator non configurato per la generazione multi-step." });
        }
        // Validate agent and template before enqueueing
        var agent = _database.GetAgentById(WriterAgentId);
        if (agent == null)
        {
            return BadRequest(new { error = $"Agente {WriterAgentId} non trovato." });
        }
        if (!agent.MultiStepTemplateId.HasValue)
        {
            return BadRequest(new { error = $"L'agente {agent.Name} non ha un template multi-step configurato." });
        }
        var template = _database.GetStepTemplateById(agent.MultiStepTemplateId.Value);
        if (template == null)
        {
            return BadRequest(new { error = $"Template multi-step {agent.MultiStepTemplateId.Value} non trovato." });
        }

        var genId = Guid.NewGuid();
        _customLogger.Start(genId.ToString());

        var cmd = new StartMultiStepStoryCommand(
            Prompt,
            WriterAgentId,
            genId,
            _database,
            _orchestrator,
            _dispatcher,
            _customLogger,
            Title
        );

        _dispatcher.Enqueue(
            "StartMultiStepStory",
            async ctx => {
                await cmd.ExecuteAsync(ctx.CancellationToken);
                return new CommandResult(true, "Multi-step generation started");
            },
            runId: genId.ToString(),
            metadata: new Dictionary<string, string>
            {
                ["agentName"] = agent.Name ?? "unknown",
                ["modelName"] = agent.ModelName ?? "unknown"
            }
        );

        _customLogger.Append(genId.ToString(), "🟢 Multi-step generation enqueued");

        try { await _customLogger.NotifyGroupAsync(genId.ToString(), "Started", "Generation started", "info"); } catch { }

        return new JsonResult(new { id = genId.ToString() });
    }

    // Poll progress for a given generation id
    public IActionResult OnGetProgress(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return BadRequest(new { error = "id mancante" });
        var messages = _customLogger.Get(id);
        var completed = _customLogger.IsCompleted(id);
        var result = _customLogger.GetResult(id);
        return new JsonResult(new { messages, completed, result });
    }

    /// <summary>
    /// Avvia il pipeline completo: genera storie da tutti i writer, valuta, seleziona la migliore,
    /// e poi esegue l'intero pipeline audio (TTS, ambience, FX, mix finale).
    /// </summary>
    public async Task<IActionResult> OnPostStartFullPipelineAsync()
    {
        if (string.IsNullOrWhiteSpace(Prompt))
        {
            return BadRequest(new { error = "Il prompt è obbligatorio." });
        }

        if (_orchestrator == null)
        {
            return BadRequest(new { error = "Orchestrator non configurato per il pipeline completo." });
        }

        var genId = Guid.NewGuid();
        _customLogger.Start(genId.ToString());

        var cmd = new FullStoryPipelineCommand(
            Prompt,
            genId,
            _database,
            _orchestrator,
            _storiesService,
            _dispatcher,
            _customLogger,
            _tuning
        );

        _dispatcher.Enqueue(
            "FullStoryPipeline",
            async ctx => {
                await cmd.ExecuteAsync(ctx.CancellationToken);
                return new CommandResult(true, "Full story pipeline completed");
            },
            runId: genId.ToString(),
            metadata: new Dictionary<string, string>
            {
                ["operation"] = "full_story_pipeline",
                ["prompt"] = Prompt.Length > 100 ? Prompt.Substring(0, 100) + "..." : Prompt,
                ["transparent"] = "1"
            }
        );

        _customLogger.Append(genId.ToString(), "🎬 Pipeline completo avviato");

        try { await _customLogger.NotifyGroupAsync(genId.ToString(), "Started", "Full pipeline started", "info"); } catch { }

        return new JsonResult(new { id = genId.ToString() });
    }

    public async Task<IActionResult> OnPostStartSeriesEpisodeAsync()
    {
        if (SeriesId <= 0) return BadRequest(new { error = "Seleziona una serie valida." });
        if (EpisodeId <= 0) return BadRequest(new { error = "Seleziona un episodio valido." });
        if (SeriesWriterAgentId <= 0) return BadRequest(new { error = "Seleziona un writer agent." });
        if (_orchestrator == null)
        {
            return BadRequest(new { error = "Orchestrator non configurato per la generazione multi-step." });
        }

        var agent = _database.GetAgentById(SeriesWriterAgentId);
        if (agent == null) return BadRequest(new { error = "Agente writer non trovato." });
        if (!agent.MultiStepTemplateId.HasValue) return BadRequest(new { error = "Agente writer senza template multi-step." });

        var genId = Guid.NewGuid();
        _customLogger.Start(genId.ToString());

        var cmd = new GenerateSeriesEpisodeFromDbCommand(
            SeriesId,
            EpisodeId,
            SeriesWriterAgentId,
            genId,
            _database,
            _orchestrator,
            _dispatcher,
            _customLogger
        );

        _dispatcher.Enqueue(
            "GenerateSeriesEpisode",
            async ctx => {
                await cmd.ExecuteAsync(ctx.CancellationToken);
                return new CommandResult(true, "Series episode generation started");
            },
            runId: genId.ToString(),
            metadata: new Dictionary<string, string>
            {
                ["agentName"] = agent.Name ?? "unknown",
                ["modelName"] = agent.ModelName ?? "unknown",
                ["operation"] = "series_episode"
            }
        );

        _customLogger.Append(genId.ToString(), "Avvio generazione episodio di serie");
        try { await _customLogger.NotifyGroupAsync(genId.ToString(), "Started", "Series episode started", "info"); } catch { }

        return new JsonResult(new { id = genId.ToString() });
    }

    public async Task<IActionResult> OnPostStartStateSeriesEpisodeAsync()
    {
        if (StateSeriesId <= 0) return BadRequest(new { error = "Seleziona una serie valida." });
        if (StateEpisodeId <= 0) return BadRequest(new { error = "Seleziona un episodio valido." });
        if (StateWriterAgentId <= 0) return BadRequest(new { error = "Seleziona un writer agent." });

        var serie = _database.GetSeriesById(StateSeriesId);
        if (serie == null) return BadRequest(new { error = "Serie non trovata." });

        var episode = _database.GetSeriesEpisodeById(StateEpisodeId);
        if (episode == null || episode.SerieId != StateSeriesId) return BadRequest(new { error = "Episodio non trovato per la serie selezionata." });

        var writer = _database.GetAgentById(StateWriterAgentId);
        if (writer == null) return BadRequest(new { error = "Writer agent non trovato." });

        // Choose narrative profile: series default if available, otherwise fallback to seeded profile id=1.
        var narrativeProfileId = serie.DefaultNarrativeProfileId.GetValueOrDefault(1);
        if (narrativeProfileId <= 0) narrativeProfileId = 1;

        var plannerMode = string.IsNullOrWhiteSpace(serie.DefaultPlannerMode) ? null : serie.DefaultPlannerMode!.Trim();

        var theme = BuildStateDrivenEpisodeTheme(serie, episode);
        var title = BuildStateDrivenEpisodeTitle(serie, episode);

        var genId = Guid.NewGuid();
        _customLogger.Start(genId.ToString());

        var startCmd = new StartStateDrivenStoryCommand(_database);
        var (success, storyId, error) = await startCmd.ExecuteAsync(
            theme: theme,
            title: title,
            narrativeProfileId: narrativeProfileId,
            serieId: serie.Id,
            serieEpisode: episode.Number,
            plannerMode: plannerMode,
            ct: HttpContext.RequestAborted);

        if (!success)
        {
            return BadRequest(new { error = error ?? "Failed to start state-driven story" });
        }

        _customLogger.Append(genId.ToString(), $"✅ Story creata. storyId={storyId}", "success");
        _customLogger.Append(genId.ToString(), "✍️ Generazione primo chunk...", "info");

        _dispatcher.Enqueue(
            "StateDrivenNextChunk",
            async ctx =>
            {
                try
                {
                    var chunkCmd = new GenerateNextChunkCommand(
                        storyId: storyId,
                        writerAgentId: writer.Id,
                        database: _database,
                        kernelFactory: _kernelFactory,
                        logger: _customLogger,
                        textValidationService: _textValidationService,
                        tuning: _tuning,
                        scopeFactory: _scopeFactory);

                    var result = await chunkCmd.ExecuteAsync(ctx.CancellationToken);
                    if (!result.Success)
                    {
                        _customLogger.Append(genId.ToString(), "❌ " + result.Message, "error");
                        await _customLogger.MarkCompletedAsync(genId.ToString(), result.Message);
                        _ = _customLogger.BroadcastTaskComplete(genId, "failed");
                        return result;
                    }

                    _customLogger.Append(genId.ToString(), "✅ " + result.Message, "success");
                    await _customLogger.MarkCompletedAsync(genId.ToString(), $"storyId={storyId}");
                    _ = _customLogger.BroadcastTaskComplete(genId, "completed");
                    return result;
                }
                catch (OperationCanceledException)
                {
                    _customLogger.Append(genId.ToString(), "⚠️ Operazione annullata.", "warning");
                    await _customLogger.MarkCompletedAsync(genId.ToString(), "cancelled");
                    _ = _customLogger.BroadcastTaskComplete(genId, "cancelled");
                    return new CommandResult(false, "cancelled");
                }
                catch (Exception ex)
                {
                    _customLogger.Append(genId.ToString(), "❌ Errore: " + ex.Message, "error");
                    await _customLogger.MarkCompletedAsync(genId.ToString(), ex.Message);
                    _ = _customLogger.BroadcastTaskComplete(genId, "failed");
                    return new CommandResult(false, ex.Message);
                }
            },
            runId: genId.ToString(),
            metadata: new Dictionary<string, string>
            {
                ["operation"] = "state_driven_series_episode",
                ["storyId"] = storyId.ToString(),
                ["serieId"] = serie.Id.ToString(),
                ["episodeId"] = episode.Id.ToString(),
                ["episodeNumber"] = episode.Number.ToString(),
                ["writerAgentId"] = writer.Id.ToString(),
                ["writerName"] = writer.Name
            },
            priority: 2);

        _customLogger.Append(genId.ToString(), "🟢 Episodio state-driven accodato");
        try { _ = _customLogger.NotifyGroupAsync(genId.ToString(), "Started", "State-driven episode started", "info"); } catch { }

        StateStoryId = storyId;
        return new JsonResult(new { id = genId.ToString(), storyId });
    }

    public async Task<IActionResult> OnPostStartStateSeriesEpisodeAutoAsync()
    {
        if (StateSeriesId <= 0) return BadRequest(new { error = "Seleziona una serie valida." });
        if (StateEpisodeId <= 0) return BadRequest(new { error = "Seleziona un episodio valido." });
        if (StateWriterAgentId <= 0) return BadRequest(new { error = "Seleziona un writer agent." });

        var serie = _database.GetSeriesById(StateSeriesId);
        if (serie == null) return BadRequest(new { error = "Serie non trovata." });

        var episode = _database.GetSeriesEpisodeById(StateEpisodeId);
        if (episode == null || episode.SerieId != StateSeriesId) return BadRequest(new { error = "Episodio non trovato per la serie selezionata." });

        var writer = _database.GetAgentById(StateWriterAgentId);
        if (writer == null) return BadRequest(new { error = "Writer agent non trovato." });

        var narrativeProfileId = serie.DefaultNarrativeProfileId.GetValueOrDefault(1);
        if (narrativeProfileId <= 0) narrativeProfileId = 1;

        var plannerMode = string.IsNullOrWhiteSpace(serie.DefaultPlannerMode) ? null : serie.DefaultPlannerMode!.Trim();

        var theme = BuildStateDrivenEpisodeTheme(serie, episode);
        var title = BuildStateDrivenEpisodeTitle(serie, episode);

        var genId = Guid.NewGuid();
        _customLogger.Start(genId.ToString());

        var startCmd = new StartStateDrivenStoryCommand(_database);
        var (success, storyId, error) = await startCmd.ExecuteAsync(
            theme: theme,
            title: title,
            narrativeProfileId: narrativeProfileId,
            serieId: serie.Id,
            serieEpisode: episode.Number,
            plannerMode: plannerMode,
            ct: HttpContext.RequestAborted);

        if (!success)
        {
            return BadRequest(new { error = error ?? "Failed to start state-driven story" });
        }

        var minutes = StateTargetMinutes <= 0 ? 20 : StateTargetMinutes;
        var wpm = StateWordsPerMinute <= 0 ? 150 : StateWordsPerMinute;

        _customLogger.Append(genId.ToString(), $"✅ Story creata. storyId={storyId}", "success");
        _customLogger.Append(genId.ToString(), $"▶️ Avvio generazione episodio completo (~{minutes} min TTS)", "info");

        _dispatcher.Enqueue(
            "StateDrivenEpisodeAuto",
            async ctx =>
            {
                try
                {
                    var cmd = new GenerateStateDrivenEpisodeToDurationCommand(
                        storyId: storyId,
                        writerAgentId: writer.Id,
                        targetMinutes: minutes,
                        wordsPerMinute: wpm,
                        database: _database,
                        kernelFactory: _kernelFactory,
                        storiesService: _storiesService,
                        logger: _customLogger,
                        textValidationService: _textValidationService,
                        tuning: _tuning,
                        scopeFactory: _scopeFactory);

                    var result = await cmd.ExecuteAsync(runIdForProgress: genId.ToString(), ct: ctx.CancellationToken);
                    if (!result.Success)
                    {
                        _customLogger.Append(genId.ToString(), "❌ " + result.Message, "error");
                        await _customLogger.MarkCompletedAsync(genId.ToString(), result.Message);
                        _ = _customLogger.BroadcastTaskComplete(genId, "failed");
                        return result;
                    }

                    _customLogger.Append(genId.ToString(), "✅ " + result.Message, "success");
                    await _customLogger.MarkCompletedAsync(genId.ToString(), result.Message);
                    _ = _customLogger.BroadcastTaskComplete(genId, "completed");
                    return result;
                }
                catch (OperationCanceledException)
                {
                    _customLogger.Append(genId.ToString(), "⚠️ Operazione annullata.", "warning");
                    await _customLogger.MarkCompletedAsync(genId.ToString(), "cancelled");
                    _ = _customLogger.BroadcastTaskComplete(genId, "cancelled");
                    return new CommandResult(false, "cancelled");
                }
                catch (Exception ex)
                {
                    _customLogger.Append(genId.ToString(), "❌ Errore: " + ex.Message, "error");
                    await _customLogger.MarkCompletedAsync(genId.ToString(), ex.Message);
                    _ = _customLogger.BroadcastTaskComplete(genId, "failed");
                    return new CommandResult(false, ex.Message);
                }
            },
            runId: genId.ToString(),
            metadata: new Dictionary<string, string>
            {
                ["operation"] = "state_driven_series_episode_auto",
                ["storyId"] = storyId.ToString(),
                ["serieId"] = serie.Id.ToString(),
                ["episodeId"] = episode.Id.ToString(),
                ["episodeNumber"] = episode.Number.ToString(),
                ["writerAgentId"] = writer.Id.ToString(),
                ["writerName"] = writer.Name,
                ["targetMinutes"] = minutes.ToString(),
                ["wordsPerMinute"] = wpm.ToString()
            },
            priority: 2);

        StateStoryId = storyId;
        return new JsonResult(new { id = genId.ToString(), storyId });
    }

    public IActionResult OnPostStateNextChunk()
    {
        if (StateStoryId <= 0) return BadRequest(new { error = "StoryId non valido." });
        if (StateNextChunkWriterAgentId <= 0) return BadRequest(new { error = "Seleziona un writer agent." });

        var snap = _database.GetStateDrivenStorySnapshot(StateStoryId);
        if (snap == null) return BadRequest(new { error = "Story state-driven non trovata." });
        if (!snap.IsActive) return BadRequest(new { error = "Story runtime non attivo (IsActive=false)." });

        var writer = _database.GetAgentById(StateNextChunkWriterAgentId);
        if (writer == null) return BadRequest(new { error = "Writer agent non trovato." });

        var genId = Guid.NewGuid();
        _customLogger.Start(genId.ToString());
        _customLogger.Append(genId.ToString(), $"✍️ Generazione chunk successivo per storyId={StateStoryId} (chunkIndex={snap.CurrentChunkIndex})");

        _dispatcher.Enqueue(
            "StateDrivenNextChunk",
            async ctx =>
            {
                try
                {
                    var cmd = new GenerateNextChunkCommand(
                        storyId: StateStoryId,
                        writerAgentId: writer.Id,
                        database: _database,
                        kernelFactory: _kernelFactory,
                        logger: _customLogger,
                        textValidationService: _textValidationService,
                        tuning: _tuning,
                        scopeFactory: _scopeFactory);

                    var result = await cmd.ExecuteAsync(ctx.CancellationToken);
                    if (!result.Success)
                    {
                        _customLogger.Append(genId.ToString(), "❌ " + result.Message, "error");
                        await _customLogger.MarkCompletedAsync(genId.ToString(), result.Message);
                        _ = _customLogger.BroadcastTaskComplete(genId, "failed");
                        return result;
                    }

                    _customLogger.Append(genId.ToString(), "✅ " + result.Message, "success");
                    await _customLogger.MarkCompletedAsync(genId.ToString(), result.Message);
                    _ = _customLogger.BroadcastTaskComplete(genId, "completed");
                    return result;
                }
                catch (OperationCanceledException)
                {
                    _customLogger.Append(genId.ToString(), "⚠️ Operazione annullata.", "warning");
                    await _customLogger.MarkCompletedAsync(genId.ToString(), "cancelled");
                    _ = _customLogger.BroadcastTaskComplete(genId, "cancelled");
                    return new CommandResult(false, "cancelled");
                }
                catch (Exception ex)
                {
                    _customLogger.Append(genId.ToString(), "❌ Errore: " + ex.Message, "error");
                    await _customLogger.MarkCompletedAsync(genId.ToString(), ex.Message);
                    _ = _customLogger.BroadcastTaskComplete(genId, "failed");
                    return new CommandResult(false, ex.Message);
                }
            },
            runId: genId.ToString(),
            metadata: new Dictionary<string, string>
            {
                ["operation"] = "state_driven_next_chunk",
                ["storyId"] = StateStoryId.ToString(),
                ["writerAgentId"] = writer.Id.ToString(),
                ["writerName"] = writer.Name
            },
            priority: 2);

        return new JsonResult(new { id = genId.ToString() });
    }

    private static string BuildStateDrivenEpisodeTitle(SeriesModel serie, SeriesEpisode episode)
    {
        if (!string.IsNullOrWhiteSpace(episode.Title))
        {
            return $"{serie.Titolo} - Ep {episode.Number}: {episode.Title}";
        }

        return $"{serie.Titolo} - Ep {episode.Number}";
    }

    private static string BuildStateDrivenEpisodeTheme(SeriesModel serie, SeriesEpisode episode)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# CONTESTO SERIE");
        sb.AppendLine($"Titolo: {serie.Titolo}");
        if (!string.IsNullOrWhiteSpace(serie.Genere)) sb.AppendLine($"Genere: {serie.Genere}");
        if (!string.IsNullOrWhiteSpace(serie.Sottogenere)) sb.AppendLine($"Sottogenere: {serie.Sottogenere}");
        if (!string.IsNullOrWhiteSpace(serie.PeriodoNarrativo)) sb.AppendLine($"Periodo narrativo: {serie.PeriodoNarrativo}");
        if (!string.IsNullOrWhiteSpace(serie.TonoBase)) sb.AppendLine($"Tono: {serie.TonoBase}");
        if (!string.IsNullOrWhiteSpace(serie.Lingua)) sb.AppendLine($"Lingua: {serie.Lingua}");

        if (!string.IsNullOrWhiteSpace(serie.AmbientazioneBase))
        {
            sb.AppendLine();
            sb.AppendLine("Ambientazione:");
            sb.AppendLine(serie.AmbientazioneBase);
        }

        if (!string.IsNullOrWhiteSpace(serie.PremessaSerie))
        {
            sb.AppendLine();
            sb.AppendLine("Premessa serie:");
            sb.AppendLine(serie.PremessaSerie);
        }

        if (!string.IsNullOrWhiteSpace(serie.ArcoNarrativoSerie))
        {
            sb.AppendLine();
            sb.AppendLine("Arco narrativo serie:");
            sb.AppendLine(serie.ArcoNarrativoSerie);
        }

        if (!string.IsNullOrWhiteSpace(serie.StileScrittura))
        {
            sb.AppendLine();
            sb.AppendLine("Stile scrittura:");
            sb.AppendLine(serie.StileScrittura);
        }

        if (!string.IsNullOrWhiteSpace(serie.RegoleNarrative))
        {
            sb.AppendLine();
            sb.AppendLine("Regole narrative:");
            sb.AppendLine(serie.RegoleNarrative);
        }

        sb.AppendLine();
        sb.AppendLine("# EPISODIO");
        sb.AppendLine($"Numero: {episode.Number}");
        if (!string.IsNullOrWhiteSpace(episode.Title)) sb.AppendLine($"Titolo episodio: {episode.Title}");
        if (!string.IsNullOrWhiteSpace(episode.StartSituation))
        {
            sb.AppendLine();
            sb.AppendLine("Situazione iniziale:");
            sb.AppendLine(episode.StartSituation);
        }
        if (!string.IsNullOrWhiteSpace(episode.EpisodeGoal))
        {
            sb.AppendLine();
            sb.AppendLine("Obiettivo episodio:");
            sb.AppendLine(episode.EpisodeGoal);
        }
        if (!string.IsNullOrWhiteSpace(episode.Trama))
        {
            sb.AppendLine();
            sb.AppendLine("Trama:");
            sb.AppendLine(episode.Trama);
        }

        sb.AppendLine();
        sb.AppendLine("VINCOLI:");
        sb.AppendLine("- Scrivi in italiano.");
        sb.AppendLine("- Chunk continuo, nessuna conclusione: termina sempre in cliffhanger.");

        return sb.ToString();
    }
}
