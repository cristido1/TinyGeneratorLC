using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;
using TinyGenerator.Services.Commands;
using System.Text;

namespace TinyGenerator.Pages;

public class GeneraModel : PageModel
{
    private readonly DatabaseService _database;
    private readonly MultiStepOrchestrationService? _orchestrator;
    private readonly StoriesService _storiesService;
    private readonly CommandDispatcher _dispatcher;
    private readonly ICustomLogger _customLogger;
    private readonly ILogger<GeneraModel> _logger;

    public GeneraModel(
        DatabaseService database,
        CommandDispatcher dispatcher,
        StoriesService storiesService,
        ICustomLogger customLogger,
        ILogger<GeneraModel> logger,
        MultiStepOrchestrationService? orchestrator = null)
    {
        _database = database;
        _orchestrator = orchestrator;
        _storiesService = storiesService;
        _dispatcher = dispatcher;
        _customLogger = customLogger;
        _logger = logger;
    }

    [BindProperty]
    public string Prompt { get; set; } = string.Empty;

    [BindProperty]
    public string Writer { get; set; } = "All";

    [BindProperty]
    public int WriterAgentId { get; set; } = 0;

    public List<Agent> Agents { get; set; } = new();

    public object? Story { get; set; }
    public string Status => _status.ToString();
    public bool IsProcessing { get; set; }

    private StringBuilder _status = new();

    public void OnGet()
    {
        // Load writer agents for dropdown (only those with a multi-step template)
        Agents = _database.ListAgents()
            .Where(a => a.Role.Contains("writer", StringComparison.OrdinalIgnoreCase) && a.MultiStepTemplateId.HasValue)
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
    }

    // Start generation in background. Returns a JSON with generation id.
    public async Task<IActionResult> OnPostStartAsync()
    {
        if (string.IsNullOrWhiteSpace(Prompt))
        {
            return BadRequest(new { error = "Il prompt è obbligatorio." });
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
            _customLogger
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
            _customLogger
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
                ["prompt"] = Prompt.Length > 100 ? Prompt.Substring(0, 100) + "..." : Prompt
            }
        );

        _customLogger.Append(genId.ToString(), "🎬 Pipeline completo avviato");

        try { await _customLogger.NotifyGroupAsync(genId.ToString(), "Started", "Full pipeline started", "info"); } catch { }

        return new JsonResult(new { id = genId.ToString() });
    }
}
