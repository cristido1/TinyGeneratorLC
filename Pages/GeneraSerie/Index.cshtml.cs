using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TinyGenerator.Services;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Pages.GeneraSerie;

public sealed class IndexModel : PageModel
{
    private readonly DatabaseService _database;
    private readonly ILangChainKernelFactory _kernelFactory;
    private readonly ICommandDispatcher _dispatcher;
    private readonly ICustomLogger? _logger;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IOptionsMonitor<SeriesGenerationOptions>? _optionsMonitor;
    private readonly CommandModelExecutionService? _modelExecution;

    [BindProperty]
    public string Prompt { get; set; } = string.Empty;

    public IndexModel(
        DatabaseService database,
        ILangChainKernelFactory kernelFactory,
        ICommandDispatcher dispatcher,
        IServiceScopeFactory? scopeFactory = null,
        ICustomLogger? logger = null,
        IOptionsMonitor<SeriesGenerationOptions>? optionsMonitor = null,
        CommandModelExecutionService? modelExecution = null)
    {
        _database = database;
        _kernelFactory = kernelFactory;
        _dispatcher = dispatcher;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _optionsMonitor = optionsMonitor;
        _modelExecution = modelExecution;
    }

    public void OnGet()
    {
    }

    public IActionResult OnPostEnqueue()
    {
        if (string.IsNullOrWhiteSpace(Prompt))
        {
            TempData["ErrorMessage"] = "Inserisci un prompt valido.";
            return Page();
        }

        var runId = $"generate_new_serie_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        var prompt = Prompt.Trim();
        var activeSeriesAgent = _database.ListAgents()
            .FirstOrDefault(a => a.IsActive && !string.IsNullOrWhiteSpace(a.Role) && a.Role.StartsWith("serie_", StringComparison.OrdinalIgnoreCase));
        var activeAgentName = activeSeriesAgent?.Name ?? "series_orchestrator";
        var activeAgentRole = activeSeriesAgent?.Role ?? "series_orchestrator";
        var activeModelName = activeSeriesAgent?.ModelName;
        if (string.IsNullOrWhiteSpace(activeModelName) && activeSeriesAgent?.ModelId is int modelId)
        {
            activeModelName = _database.GetModelInfoById(modelId)?.Name;
        }

        _dispatcher.Enqueue(
            "generate_new_serie",
            async ctx =>
            {
                var cmd = new GenerateNewSerieCommand(
                    prompt,
                    _database,
                    _kernelFactory,
                    _optionsMonitor,
                    _logger,
                    _scopeFactory,
                    _dispatcher,
                    _modelExecution);
                return await cmd.ExecuteAsync(ctx.RunId, ctx.CancellationToken);
            },
            runId: runId,
            metadata: new Dictionary<string, string>
            {
                ["operation"] = "generate_new_serie",
                ["promptLength"] = prompt.Length.ToString(),
                ["agentName"] = activeAgentName,
                ["agentRole"] = activeAgentRole,
                ["modelName"] = activeModelName ?? "multi-model"
            },
            priority: 2);

        TempData["StatusMessage"] = $"Generazione serie accodata (run {runId}).";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnGetRandomWriterPromptAsync(CancellationToken cancellationToken)
    {
        var writerAgents = _database.ListAgents()
            .Where(a =>
                a.IsActive &&
                !string.IsNullOrWhiteSpace(a.Role) &&
                (a.Role.StartsWith("writer_", StringComparison.OrdinalIgnoreCase) ||
                 a.Role.Equals("writer", StringComparison.OrdinalIgnoreCase) ||
                 a.Role.Equals("story_writer", StringComparison.OrdinalIgnoreCase) ||
                 a.Role.Equals("text_writer", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (writerAgents.Count == 0)
        {
            return new JsonResult(new { success = false, message = "Nessun agente writer disponibile." });
        }

        var randomWriterAgent = writerAgents[Random.Shared.Next(writerAgents.Count)];
        var modelName = randomWriterAgent.ModelName;
        if (string.IsNullOrWhiteSpace(modelName) && randomWriterAgent.ModelId.HasValue)
        {
            modelName = _database.GetModelInfoById(randomWriterAgent.ModelId.Value)?.Name;
        }

        if (string.IsNullOrWhiteSpace(modelName))
        {
            return new JsonResult(new { success = false, message = $"L'agente writer '{randomWriterAgent.Name}' non ha un modello valido." });
        }

        try
        {
            var bridge = _kernelFactory.CreateChatBridge(
                modelName,
                randomWriterAgent.Temperature,
                randomWriterAgent.TopP,
                randomWriterAgent.RepeatPenalty,
                randomWriterAgent.TopK,
                randomWriterAgent.RepeatLastN,
                randomWriterAgent.NumPredict);

            var messages = new List<ConversationMessage>
            {
                new()
                {
                    Role = "system",
                    Content = "Sei un autore creativo di serie TV. Rispondi solo con un singolo prompt pronto all'uso, in italiano, massimo 120 parole."
                },
                new()
                {
                    Role = "user",
                    Content = "Genera un prompt originale per una nuova serie non storica. Includi concept, mondo, conflitto centrale, tono emotivo e temi umani."
                }
            };

            var raw = await bridge.CallModelWithToolsAsync(
                messages,
                tools: new List<Dictionary<string, object>>(),
                ct: cancellationToken);
            var (textContent, _) = LangChainChatBridge.ParseChatResponse(raw);
            var prompt = string.IsNullOrWhiteSpace(textContent) ? raw?.Trim() : textContent.Trim();

            if (string.IsNullOrWhiteSpace(prompt))
            {
                return new JsonResult(new { success = false, message = "Il writer non ha restituito un prompt valido." });
            }

            return new JsonResult(new { success = true, prompt, writer = randomWriterAgent.Name, model = modelName });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, message = $"Errore durante la generazione del prompt: {ex.Message}" });
        }
    }
}
