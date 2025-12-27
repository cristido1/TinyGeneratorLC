using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.Models
{
    [IgnoreAntiforgeryToken]
    public class IndexModel : PageModel
    {
        private readonly DatabaseService _database;
        private readonly LangChainTestService _testService;
        private readonly IOllamaManagementService _ollamaService;
        private readonly ICommandDispatcher _commandDispatcher;
        private readonly ILogger<IndexModel>? _logger;
        private readonly ICustomLogger? _customLogger;

        [BindProperty(SupportsGet = true)]
        public bool ShowDisabled { get; set; } = false;

        [BindProperty]
        public string Model { get; set; } = string.Empty;

        [BindProperty]
        public string Group { get; set; } = string.Empty;

        [BindProperty]
        public int ContextToUse { get; set; }

        [BindProperty]
        public double CostInPer1k { get; set; }

        [BindProperty]
        public double CostOutPer1k { get; set; }

        public List<string> TestGroups { get; set; } = new();
        public IReadOnlyList<ModelInfo> Items { get; set; } = Array.Empty<ModelInfo>();
        public int PageIndex { get; set; } = 1;
        public int PageSize { get; set; } = 25;
        public int TotalCount { get; set; }
        public string? Search { get; set; }
        public string? OrderBy { get; set; }
        public IReadOnlyList<CommandSnapshot> ActiveCommands { get; set; } = Array.Empty<CommandSnapshot>();

        public IndexModel(
            DatabaseService database,
            LangChainTestService testService,
            // CostController removed
            IOllamaManagementService ollamaService,
            ICommandDispatcher commandDispatcher,
            ILogger<IndexModel>? logger = null,
            ICustomLogger? customLogger = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _testService = testService ?? throw new ArgumentNullException(nameof(testService));

            _ollamaService = ollamaService ?? throw new ArgumentNullException(nameof(ollamaService));
            _commandDispatcher = commandDispatcher ?? throw new ArgumentNullException(nameof(commandDispatcher));
            _logger = logger;
            _customLogger = customLogger;
        }

        public void OnGet()
        {
            if (int.TryParse(Request.Query["page"], out var p) && p > 0) PageIndex = p;
            if (int.TryParse(Request.Query["pageSize"], out var ps) && ps > 0) PageSize = ps;
            Search = string.IsNullOrWhiteSpace(Request.Query["search"]) ? null : Request.Query["search"].ToString();
            OrderBy = string.IsNullOrWhiteSpace(Request.Query["orderBy"]) ? null : Request.Query["orderBy"].ToString();

            TestGroups = _database.GetTestGroups() ?? new List<string>();
            var (items, total) = _database.GetPagedModels(Search, OrderBy, PageIndex, PageSize, ShowDisabled);
            Items = items;
            TotalCount = total;
            
            // Populate LastGroupScores and base group duration for each model
            foreach (var model in Items)
            {
                model.LastGroupScores = new Dictionary<string, int?>();
                var groupSummaries = _database.GetModelTestGroupsSummary(model.Name);
                
                foreach (var summary in groupSummaries)
                {
                    model.LastGroupScores[summary.Group] = summary.Score;
                }
                
                // Get duration specifically from "base" group test run
                var baseGroupDuration = _database.GetGroupTestDuration(model.Name, "base");
                if (baseGroupDuration.HasValue)
                {
                    model.TestDurationSeconds = baseGroupDuration.Value / 1000.0; // Convert ms to seconds
                }
            }

            ActiveCommands = _commandDispatcher.GetActiveCommands();
        }

        public Task<IActionResult> OnPostRunGroupAsync()
        {
            var modelName = Model?.Trim();
            var groupName = Group?.Trim();

            if (string.IsNullOrWhiteSpace(modelName) || string.IsNullOrWhiteSpace(groupName))
                return Task.FromResult<IActionResult>(BadRequest("model and group required"));

            var handle = _testService.EnqueueGroupRun(modelName, groupName);
            if (handle == null)
                return Task.FromResult<IActionResult>(BadRequest("Impossibile preparare il test richiesto"));

            return Task.FromResult<IActionResult>(new JsonResult(new { runId = handle.RunId }));
        }

        public IActionResult OnPostUpdateContext()
        {
            if (string.IsNullOrWhiteSpace(Model))
                return BadRequest("model required");

            try
            {
                _database.UpdateModelContext(Model, ContextToUse);
                TempData["TestResultMessage"] = $"Updated context for {Model} to {ContextToUse} tokens.";
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostAddOllamaModelsAsync()
        {
            try
            {
                    var added = await _database.AddLocalOllamaModelsAsync();
                    TempData["TestResultMessage"] = $"Discovered and upserted {added} local Ollama model(s).";
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        public Task<IActionResult> OnPostRunAllAsync()
        {
            var selectedGroup = !string.IsNullOrWhiteSpace(Group)
                ? Group
                : (_database.GetTestGroups() ?? new List<string>()).FirstOrDefault();

            if (string.IsNullOrWhiteSpace(selectedGroup))
                return Task.FromResult<IActionResult>(BadRequest("Nessun gruppo di test disponibile"));

            var runIds = new List<int>();
            var models = _database.ListModels().Where(m => m.Enabled).ToList();

            foreach (var model in models)
            {
                try
                {
                    var handle = _testService.EnqueueGroupRun(model.Name, selectedGroup);
                    if (handle == null) continue;
                    if (int.TryParse(handle.RunId, out var rid))
                    {
                        runIds.Add(rid);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to enqueue test {Group} for model {Model}", selectedGroup, model.Name);
                }
            }

            return Task.FromResult<IActionResult>(new JsonResult(new { runIds }));
        }

        public async Task<IActionResult> OnPostPurgeDisabledOllamaAsync()
        {
            try
            {
                var results = await _ollamaService.PurgeDisabledModelsAsync();
                return new JsonResult(new { results });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostRefreshContextsAsync()
        {
            try
            {
                var updated = await _ollamaService.RefreshRunningContextsAsync();
                TempData["TestResultMessage"] = $"Refreshed contexts for {updated} running Ollama model(s).";
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        public IActionResult OnPostUpdateCost()
        {
            if (string.IsNullOrWhiteSpace(Model))
                return BadRequest("model required");

            try
            {
                _database.UpdateModelCosts(Model, CostInPer1k, CostOutPer1k);
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        public IActionResult OnPostRecalculateScores()
        {
            try
            {
                _database.RecalculateAllWriterScores();
                TempData["TestResultMessage"] = "Punteggi writer ricalcolati per tutti i modelli.";
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        public IActionResult OnGetTestGroups(string model)
        {
            if (string.IsNullOrWhiteSpace(model))
                return BadRequest("model required");

            try
            {
                var groups = _database.GetModelTestGroupsSummary(model);
                return new JsonResult(groups);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        public IActionResult OnGetTestSteps(string model, string group)
        {
            if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(group))
                return BadRequest("model and group required");

            try
            {
                var steps = _database.GetModelTestStepsDetail(model, group);
                return new JsonResult(steps);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }
        private static string SanitizeIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "test";
            var chars = value.Trim().ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) ? c : '_')
                .ToArray();
            var sanitized = new string(chars).Trim('_');
            return string.IsNullOrWhiteSpace(sanitized) ? "test" : sanitized;
        }
    }
}
