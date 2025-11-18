using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages
{
    [IgnoreAntiforgeryToken]
    public class ModelsModel : PageModel
    {
        private readonly DatabaseService _database;
        private readonly ITestService _testService;
        private readonly CostController _costController;
        private readonly IOllamaManagementService _ollamaService;

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
            ITestService testService,
            CostController costController,
            IOllamaManagementService ollamaService)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _testService = testService ?? throw new ArgumentNullException(nameof(testService));
            _costController = costController ?? throw new ArgumentNullException(nameof(costController));
            _ollamaService = ollamaService ?? throw new ArgumentNullException(nameof(ollamaService));
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
                
                // Recalculate total score if different from sum of group scores
                var calculatedTotal = model.LastGroupScores.Values.Where(v => v.HasValue).Sum(v => v!.Value);
                if (model.FunctionCallingScore != calculatedTotal)
                {
                    model.FunctionCallingScore = calculatedTotal;
                    _database.RecalculateModelScore(model.Name);
                }
            }
        }

        public async Task<IActionResult> OnPostRunGroupAsync()
        {
            if (string.IsNullOrWhiteSpace(Model) || string.IsNullOrWhiteSpace(Group))
                return BadRequest("model and group required");

            try
            {
                var result = await _testService.RunGroupAsync(Model, Group);
                return result != null ? new JsonResult(result) : BadRequest("Test execution failed");
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
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

        public async Task<IActionResult> OnPostRunAllAsync()
        {
            try
            {
                var results = await _testService.RunAllEnabledModelsAsync(Group);
                return new JsonResult(new { results });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
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
    }
}
