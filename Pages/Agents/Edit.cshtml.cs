using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.Agents
{
    public class EditModel : PageModel
    {
        private readonly DatabaseService _database;
        private readonly ICustomLogger? _logger;

        [BindProperty]
        public Agent Agent { get; set; } = new();
        public List<TinyGenerator.Models.TtsVoice> Voices { get; set; } = new();
        public List<TinyGenerator.Models.ModelInfo> Models { get; set; } = new();
        public List<TinyGenerator.Models.StepTemplate> StepTemplates { get; set; } = new();
        public List<string> ResponseFormatFiles { get; set; } = new();
        [BindProperty]
        public int? SelectedModelId { get; set; }
        [BindProperty]
        public string[] SelectedSkills { get; set; } = new string[] { };
        public string[] AvailableSkills { get; } = new string[] { "text", "math", "time", "filesystem", "http", "memory", "audiocraft", "audioevaluator", "tts", "ttsschema", "voicechoser", "evaluator", "story" };

        public EditModel(DatabaseService database, ICustomLogger? logger = null)
        {
            _database = database;
            _logger = logger;
        }

        public IActionResult OnGet(int id)
        {
            var a = _database.GetAgentById(id);
            if (a == null)
            {
                TempData["Error"] = $"Agente non trovato (id={id})";
                return RedirectToPage("/Agents/Index");
            }

            Agent = a;
            Voices = _database.ListTtsVoices();
            StepTemplates = _database.ListStepTemplates();
            ResponseFormatFiles = LoadResponseFormatFiles();

            // Show enabled models, but keep agent's assigned model visible even if it's currently disabled
            Models = _database.ListModels()
                .Where(m => m.Enabled || (Agent.ModelId.HasValue && m.Id == Agent.ModelId.Value))
                .ToList();

            SelectedModelId = Agent.ModelId;

            // Load selected skills from JSON array stored in Agent.Skills
            try
            {
                if (!string.IsNullOrWhiteSpace(Agent.Skills))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(Agent.Skills);
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        var list = new System.Collections.Generic.List<string>();
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                                list.Add(el.GetString() ?? string.Empty);
                        }
                        SelectedSkills = list.ToArray();
                    }
                }
            }
            catch { }

            return Page();
        }

        public IActionResult OnPost()
        {
            // Ensure lists are available when re-displaying the page after validation errors
            Voices = _database.ListTtsVoices();
            Models = _database.ListModels().Where(m => m.Enabled || (Agent.ModelId.HasValue && m.Id == Agent.ModelId.Value)).ToList();
            StepTemplates = _database.ListStepTemplates();
            ResponseFormatFiles = LoadResponseFormatFiles();
            
            // Handle empty string for MultiStepTemplateId (convert to null)
            if (Agent.MultiStepTemplateId.HasValue && Agent.MultiStepTemplateId.Value == 0)
            {
                Agent.MultiStepTemplateId = null;
            }
            
            if (!ModelState.IsValid) return Page();
            if (!string.IsNullOrWhiteSpace(Agent.JsonResponseFormat))
            {
                var selected = Agent.JsonResponseFormat.Trim();
                if (!ResponseFormatFiles.Contains(selected, StringComparer.OrdinalIgnoreCase))
                {
                    ModelState.AddModelError("Agent.JsonResponseFormat", "Formato risposta JSON non valido.");
                    return Page();
                }
            }
            // Validate JSON fields
            try
            {
                // Ensure Agent.Skills is serialised from SelectedSkills
                try { Agent.Skills = System.Text.Json.JsonSerializer.Serialize(SelectedSkills ?? new string[] {}); } catch { Agent.Skills = "[]"; }

                // Model id is bound directly to Agent.ModelId by the form (no extra mapping needed)
                if (!string.IsNullOrWhiteSpace(Agent.Skills))
                {
                    var doc = System.Text.Json.JsonDocument.Parse(Agent.Skills);
                    if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                    {
                        ModelState.AddModelError("Agent.Skills", "Skills must be a JSON array.");
                        return Page();
                    }
                }

                if (!string.IsNullOrWhiteSpace(Agent.Config))
                {
                    var doc = System.Text.Json.JsonDocument.Parse(Agent.Config);
                    if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                    {
                        ModelState.AddModelError("Agent.Config", "Config must be a JSON object.");
                        return Page();
                    }
                }
            }
            catch (System.Text.Json.JsonException jex)
            {
                ModelState.AddModelError(string.Empty, "Invalid JSON: " + jex.Message);
                return Page();
            }

            try
            {
                var existingAgent = _database.GetAgentById(Agent.Id);
                var previousModelId = existingAgent?.ModelId;
                _database.UpdateAgent(Agent);
                if (existingAgent != null && previousModelId != Agent.ModelId)
                {
                    var previousModelName = ResolveModelDisplayName(previousModelId);
                    var nextModelName = ResolveModelDisplayName(Agent.ModelId);
                    _logger?.Log(
                        "Information",
                        "Agents",
                        $"Model change: agent_id={Agent.Id}; agent_name={Agent.Name}; role={Agent.Role}; from_model_id={(previousModelId?.ToString() ?? "null")}; from_model={previousModelName}; to_model_id={(Agent.ModelId?.ToString() ?? "null")}; to_model={nextModelName}",
                        result: "SUCCESS");
                }
                return RedirectToPage("/Agents/Index");
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                Voices = _database.ListTtsVoices();
                Models = _database.ListModels().Where(m => m.Enabled || (Agent.ModelId.HasValue && m.Id == Agent.ModelId.Value)).ToList();
                StepTemplates = _database.ListStepTemplates();
                ResponseFormatFiles = LoadResponseFormatFiles();
                return Page();
            }
        }

        private static List<string> LoadResponseFormatFiles()
        {
            try
            {
                var rfDir = Path.Combine(Directory.GetCurrentDirectory(), "response_formats");
                if (!Directory.Exists(rfDir))
                {
                    return new List<string>();
                }

                return Directory.GetFiles(rfDir, "*.json")
                    .Select(Path.GetFileName)
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .Select(f => f!)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private string ResolveModelDisplayName(int? modelId)
        {
            if (!modelId.HasValue || modelId.Value <= 0)
            {
                return "(none)";
            }

            var model = _database.GetModelInfoById(modelId.Value);
            if (model == null)
            {
                return $"id:{modelId.Value}";
            }

            return string.IsNullOrWhiteSpace(model.CallName)
                ? model.Name
                : $"{model.Name} (call:{model.CallName})";
        }
    }
}
