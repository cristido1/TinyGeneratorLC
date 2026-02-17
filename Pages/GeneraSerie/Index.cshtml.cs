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
        => await ExecuteRandomWriterPromptAsync(
            GenerateRandomSeriesPromptCommand.PromptStyle.Standard,
            "generate_random_series_prompt",
            cancellationToken);

    public async Task<IActionResult> OnGetRandomCrazyWriterPromptAsync(CancellationToken cancellationToken)
        => await ExecuteRandomWriterPromptAsync(
            GenerateRandomSeriesPromptCommand.PromptStyle.Crazy,
            "generate_random_series_prompt_crazy",
            cancellationToken);

    public async Task<IActionResult> OnGetSpaceMilitaryWriterPromptAsync(CancellationToken cancellationToken)
        => await ExecuteRandomWriterPromptAsync(
            GenerateRandomSeriesPromptCommand.PromptStyle.SpaceMilitaryItalian,
            "generate_random_series_prompt_space_military_italian",
            cancellationToken);

    private async Task<IActionResult> ExecuteRandomWriterPromptAsync(
        GenerateRandomSeriesPromptCommand.PromptStyle style,
        string operation,
        CancellationToken cancellationToken)
    {
        var runId = $"{operation}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        var cmd = new GenerateRandomSeriesPromptCommand(
            _database,
            style,
            _logger,
            _modelExecution);

        var handle = _dispatcher.Enqueue(
            cmd,
            runId: runId,
            metadata: new Dictionary<string, string>
            {
                ["operation"] = operation,
                ["source"] = "GeneraSeriePage"
            },
            priority: 2);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(70));
            var result = await handle.CompletionTask.WaitAsync(timeoutCts.Token);
            if (!result.Success)
            {
                return new JsonResult(new { success = false, message = result.Message ?? "Generazione prompt fallita.", runId });
            }

            var prompt = cmd.GeneratedPrompt ?? result.Message;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return new JsonResult(new { success = false, message = "Il comando non ha prodotto un prompt utilizzabile.", runId });
            }

            return new JsonResult(new
            {
                success = true,
                prompt,
                writer = cmd.SelectedWriterName,
                model = cmd.SelectedModelName,
                runId
            });
        }
        catch (OperationCanceledException)
        {
            _dispatcher.CancelCommand(runId);
            return new JsonResult(new
            {
                success = false,
                message = "Timeout nella generazione del prompt casuale. Controlla il run nei log comandi.",
                runId
            });
        }
    }
}
