using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TinyGenerator.Data;
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
        private readonly JsonScoreTestService _jsonScoreTester;
        private readonly InstructionScoreTestService _instructionScoreTester;
        private readonly IntelligenceScoreTestService _intelligenceTestService;
        private readonly ILogger<IndexModel>? _logger;
        private readonly ICustomLogger? _customLogger;
        private readonly TinyGeneratorDbContext _context;

        [BindProperty(SupportsGet = true)]
        public bool ShowDisabled { get; set; } = false;

        [BindProperty]
        public string Model { get; set; } = string.Empty;

        [BindProperty]
        public int? ModelId { get; set; }

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
        public int PageSize { get; set; } = 10000;
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
            JsonScoreTestService jsonScoreTester,
            InstructionScoreTestService instructionScoreTester,
            IntelligenceScoreTestService intelligenceTestService,
            TinyGeneratorDbContext context,
            ILogger<IndexModel>? logger = null,
            ICustomLogger? customLogger = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _testService = testService ?? throw new ArgumentNullException(nameof(testService));

            _ollamaService = ollamaService ?? throw new ArgumentNullException(nameof(ollamaService));
            _commandDispatcher = commandDispatcher ?? throw new ArgumentNullException(nameof(commandDispatcher));
            _jsonScoreTester = jsonScoreTester ?? throw new ArgumentNullException(nameof(jsonScoreTester));
            _instructionScoreTester = instructionScoreTester ?? throw new ArgumentNullException(nameof(instructionScoreTester));
            _intelligenceTestService = intelligenceTestService ?? throw new ArgumentNullException(nameof(intelligenceTestService));
            _context = context ?? throw new ArgumentNullException(nameof(context));
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

        public IActionResult OnPostDeleteModel()
        {
            // Prefer deletion by numeric id when provided to avoid name collisions
            if (ModelId.HasValue)
            {
                try
                {
                    // Use id-based API to check agents using this model
                    var agentsUsing = _database.GetAgentsUsingModel(ModelId.Value);
                    if (agentsUsing.Count > 0)
                    {
                        TempData["ErrorMessage"] = $"Impossibile eliminare il modello. È utilizzato dagli agenti: {string.Join(", ", agentsUsing)}";
                        return RedirectToPage();
                    }
                    _database.DeleteModel(ModelId.Value.ToString());
                    TempData["TestResultMessage"] = $"Modello eliminato con successo.";
                    return RedirectToPage();
                }
                catch (Exception ex)
                {
                    TempData["ErrorMessage"] = $"Errore durante l'eliminazione del modello: {ex.Message}";
                    return RedirectToPage();
                }
            }

            if (string.IsNullOrWhiteSpace(Model))
            {
                TempData["ErrorMessage"] = "Nome modello mancante.";
                return RedirectToPage();
            }

            try
            {
                // Legacy: check by name
                // Resolve model name to id and call id-based API
                var modelInfo = _database.ListModels().FirstOrDefault(m => string.Equals(m.Name, Model, StringComparison.OrdinalIgnoreCase));
                var agentsUsingModel = modelInfo?.Id != null ? _database.GetAgentsUsingModel(modelInfo.Id.Value) : new System.Collections.Generic.List<string>();
                if (agentsUsingModel.Count > 0)
                {
                    var agentsList = string.Join(", ", agentsUsingModel);
                    TempData["ErrorMessage"] = $"Impossibile eliminare il modello '{Model}'. È utilizzato dagli agenti: {agentsList}";
                    return RedirectToPage();
                }

                // Model is not used, proceed with deletion
                _database.DeleteModel(Model);
                TempData["TestResultMessage"] = $"Modello '{Model}' eliminato con successo.";
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"Errore durante l'eliminazione del modello: {ex.Message}";
                return RedirectToPage();
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

        public IActionResult OnPostRunJsonScore()
        {
            try
            {
                var handle = _jsonScoreTester.EnqueueJsonScoreForMissingModels();
                return new JsonResult(new { runId = handle.RunId });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        public IActionResult OnPostRunJsonScoreModel()
        {
            if (ModelId == null && string.IsNullOrWhiteSpace(Model))
            {
                return BadRequest(new { error = "ModelId o Model richiesto" });
            }

            try
            {
                var modelName = Model;
                ModelInfo? modelInfo = null;
                if (string.IsNullOrWhiteSpace(modelName) && ModelId.HasValue)
                {
                    modelInfo = _database.ListModels().FirstOrDefault(m => m.Id == ModelId.Value);
                    modelName = modelInfo?.Name ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(modelName))
                {
                    return BadRequest(new { error = "Modello non trovato" });
                }

                modelInfo ??= _database.ListModels().FirstOrDefault(m => string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase));
                if (modelInfo == null || !modelInfo.Enabled)
                {
                    return BadRequest(new { error = "Il modello è disabilitato" });
                }

                var handle = _jsonScoreTester.EnqueueJsonScoreForModel(modelName);
                return new JsonResult(new { runId = handle.RunId });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        public IActionResult OnPostRunInstructionScore()
        {
            try
            {
                var handle = _instructionScoreTester.EnqueueInstructionScoreForMissingModels();
                return new JsonResult(new { runId = handle.RunId });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        public IActionResult OnPostRunInstructionScoreModel()
        {
            if (ModelId == null && string.IsNullOrWhiteSpace(Model))
            {
                return BadRequest(new { error = "ModelId o Model richiesto" });
            }

            try
            {
                var modelName = Model;
                ModelInfo? modelInfo = null;
                if (string.IsNullOrWhiteSpace(modelName) && ModelId.HasValue)
                {
                    modelInfo = _database.ListModels().FirstOrDefault(m => m.Id == ModelId.Value);
                    modelName = modelInfo?.Name ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(modelName))
                {
                    return BadRequest(new { error = "Modello non trovato" });
                }

                modelInfo ??= _database.ListModels().FirstOrDefault(m => string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase));
                if (modelInfo == null || !modelInfo.Enabled)
                {
                    return BadRequest(new { error = "Il modello è disabilitato" });
                }

                var handle = _instructionScoreTester.EnqueueInstructionScoreForModel(modelName);
                return new JsonResult(new { runId = handle.RunId });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        public IActionResult OnPostRunIntelligenceTest()
        {
            try
            {
                var handle = _intelligenceTestService.EnqueueIntelligenceScoreForMissingModels();
                return new JsonResult(new { runId = handle.RunId });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        public IActionResult OnPostRunIntelligenceTestModel()
        {
            if (ModelId == null && string.IsNullOrWhiteSpace(Model))
            {
                return BadRequest(new { error = "ModelId o Model richiesto" });
            }

            try
            {
                var modelName = Model;
                ModelInfo? modelInfo = null;
                if (string.IsNullOrWhiteSpace(modelName) && ModelId.HasValue)
                {
                    modelInfo = _database.ListModels().FirstOrDefault(m => m.Id == ModelId.Value);
                    modelName = modelInfo?.Name ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(modelName))
                {
                    return BadRequest(new { error = "Modello non trovato" });
                }

                modelInfo ??= _database.ListModels().FirstOrDefault(m => string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase));
                if (modelInfo == null || !modelInfo.Enabled)
                {
                    return BadRequest(new { error = "Il modello è disabilitato" });
                }

                var handle = _intelligenceTestService.EnqueueIntelligenceScoreForModel(modelName);
                return new JsonResult(new { runId = handle.RunId });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        // Backward compatibility for existing UI handlers
        public IActionResult OnPostRunIntelligenceScore() => OnPostRunIntelligenceTest();
        public IActionResult OnPostRunIntelligenceScoreModel() => OnPostRunIntelligenceTestModel();

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

        public IActionResult OnGetRoleStats(int? modelId, string? model)
        {
            var query = _context.ModelRoles
                .AsNoTracking()
                .Include(mr => mr.Role)
                .Include(mr => mr.Model)
                .AsQueryable();

            if (modelId.HasValue && modelId.Value > 0)
            {
                query = query.Where(mr => mr.ModelId == modelId.Value);
            }
            else if (!string.IsNullOrWhiteSpace(model))
            {
                var normalized = model.Trim();
                query = query.Where(mr => mr.Model != null && mr.Model.Name == normalized);
            }
            else
            {
                return BadRequest(new { error = "modelId o model richiesto" });
            }

            var rows = query
                .OrderByDescending(mr => mr.IsPrimary)
                .ThenByDescending(mr => mr.TotalOutputTokens)
                .ToList();

            var modelName = rows.FirstOrDefault()?.Model?.Name ?? model ?? string.Empty;

            return new JsonResult(new
            {
                modelId = rows.FirstOrDefault()?.ModelId ?? modelId,
                modelName,
                rows = rows.Select(mr => new
                {
                    role = mr.Role?.Ruolo ?? "-",
                    isPrimary = mr.IsPrimary,
                    enabled = mr.Enabled,
                    useCount = mr.UseCount,
                    successRatePct = mr.SuccessRate * 100.0,
                    totalPromptTokens = mr.TotalPromptTokens,
                    totalOutputTokens = mr.TotalOutputTokens,
                    totalPromptTimeNs = mr.TotalPromptTimeNs,
                    totalGenTimeNs = mr.TotalGenTimeNs,
                    totalLoadTimeNs = mr.TotalLoadTimeNs,
                    totalTotalTimeNs = mr.TotalTotalTimeNs,
                    avgPromptTps = mr.AvgPromptTps,
                    avgGenTps = mr.AvgGenTps,
                    avgE2eTps = mr.AvgE2eTps,
                    loadRatio = mr.LoadRatio
                })
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
