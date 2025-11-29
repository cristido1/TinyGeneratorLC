using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages
{
    [IgnoreAntiforgeryToken]
    public class ModelsModel : PageModel
    {
        private readonly DatabaseService _database;
        private readonly LangChainTestService _testService;
        private readonly CostController _costController;
        private readonly IOllamaManagementService _ollamaService;
        private readonly ICommandDispatcher _commandDispatcher;
        private readonly ILogger<ModelsModel>? _logger;

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
        public List<ModelInfo> Models { get; set; } = new();

        public ModelsModel(
            DatabaseService database,
            LangChainTestService testService,
            CostController costController,
            IOllamaManagementService ollamaService,
            ICommandDispatcher commandDispatcher,
            ILogger<ModelsModel>? logger = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _testService = testService ?? throw new ArgumentNullException(nameof(testService));
            _costController = costController ?? throw new ArgumentNullException(nameof(costController));
            _ollamaService = ollamaService ?? throw new ArgumentNullException(nameof(ollamaService));
            _commandDispatcher = commandDispatcher ?? throw new ArgumentNullException(nameof(commandDispatcher));
            _logger = logger;
        }

        public void OnGet()
        {
            TestGroups = _database.GetTestGroups() ?? new List<string>();
            Models = _database.ListModels()
                .Where(m => ShowDisabled || m.Enabled)
                .ToList();
            
            // Populate LastGroupScores and base group duration for each model
            foreach (var model in Models)
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
        }

        public Task<IActionResult> OnPostRunGroupAsync()
        {
            var modelName = Model?.Trim();
            var groupName = Group?.Trim();

            if (string.IsNullOrWhiteSpace(modelName) || string.IsNullOrWhiteSpace(groupName))
                return Task.FromResult<IActionResult>(BadRequest("model and group required"));

            var context = _testService.PrepareGroupRun(modelName, groupName);
            if (context == null)
                return Task.FromResult<IActionResult>(BadRequest("Impossibile preparare il test richiesto"));

            try
            {
                QueueTestCommand(context);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to enqueue test {Group} for model {Model}", groupName, modelName);
                return Task.FromResult<IActionResult>(BadRequest(new { error = ex.Message }));
            }

            return Task.FromResult<IActionResult>(new JsonResult(new { runId = context.RunId }));
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
                var added = await _costController.PopulateLocalOllamaModelsAsync();
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
                    var context = _testService.PrepareGroupRun(model.Name, selectedGroup);
                    if (context == null) continue;
                    QueueTestCommand(context);
                    runIds.Add(context.RunId);
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
        private void QueueTestCommand(LangChainTestService.TestGroupRunContext context)
        {
            var safeGroup = string.IsNullOrWhiteSpace(context.Group) ? "test" : context.Group;
            var commandCode = $"test_{SanitizeIdentifier(safeGroup)}";
            var scope = $"test/{SanitizeIdentifier(safeGroup)}";

            _commandDispatcher.Enqueue(
                commandCode,
                async _ =>
                {
                    try
                    {
                        var result = await _testService.ExecuteGroupRunAsync(context);
                        if (result == null)
                        {
                            return new CommandResult(false, $"Test {safeGroup} non eseguito");
                        }

                        var message = result.PassedAll
                            ? $"Test {safeGroup} completato ({result.PassedSteps}/{result.Steps})"
                            : $"Test {safeGroup} completato con errori ({result.PassedSteps}/{result.Steps})";
                        return new CommandResult(result.PassedAll, message);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Test {Group} run {RunId} failed", safeGroup, context.RunId);
                        return new CommandResult(false, ex.Message);
                    }
                },
                runId: context.RunId.ToString(),
                threadScope: scope,
                metadata: new Dictionary<string, string>
                {
                    ["runId"] = context.RunId.ToString(),
                    ["model"] = context.ModelInfo?.Name ?? context.ModelName,
                    ["group"] = safeGroup
                });
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
