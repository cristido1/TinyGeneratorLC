using System;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.Agents
{
    public class EditModel : PageModel
    {
        private readonly DatabaseService _database;
        [BindProperty]
        public Agent Agent { get; set; } = new();
        public List<TinyGenerator.Models.TtsVoice> Voices { get; set; } = new();
        public List<TinyGenerator.Models.ModelInfo> Models { get; set; } = new();
        [BindProperty]
        public string? SelectedModelName { get; set; }
        [BindProperty]
        public string[] SelectedSkills { get; set; } = new string[] { };
        public string[] AvailableSkills { get; } = new string[] { "text", "math", "time", "filesystem", "http", "memory", "audiocraft", "audioevaluator", "tts", "evaluator", "story" };

        public EditModel(DatabaseService database)
        {
            _database = database;
        }

        public IActionResult OnGet(int id)
        {
            try
            {
                var a = _database.GetAgentById(id);
                if (a == null) return RedirectToPage("/Agents/Index");
                Agent = a;
                Voices = _database.ListTtsVoices();
                Models = _database.ListModels();
                // Resolve selected model name from numeric ModelId
                try { SelectedModelName = Agent.ModelId.HasValue ? _database.GetModelNameById(Agent.ModelId.Value) : null; } catch { SelectedModelName = null; }
                // Load selected skills from JSON array stored in Agent.Skills
                try {
                    if (!string.IsNullOrWhiteSpace(Agent.Skills)) {
                        using var doc = System.Text.Json.JsonDocument.Parse(Agent.Skills);
                        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array) {
                            var list = new System.Collections.Generic.List<string>();
                            foreach (var el in doc.RootElement.EnumerateArray()) { if (el.ValueKind == System.Text.Json.JsonValueKind.String) list.Add(el.GetString() ?? string.Empty); }
                            SelectedSkills = list.ToArray();
                        }
                    }
                } catch { }
                return Page();
            }
            catch
            {
                return RedirectToPage("/Agents/Index");
            }
        }

        public IActionResult OnPost()
        {
            // Ensure lists are available when re-displaying the page after validation errors
            Voices = _database.ListTtsVoices();
            Models = _database.ListModels();
            if (!ModelState.IsValid) return Page();
            // Validate JSON fields
            try
            {
                // Ensure Agent.Skills is serialised from SelectedSkills
                try { Agent.Skills = System.Text.Json.JsonSerializer.Serialize(SelectedSkills ?? new string[] {}); } catch { Agent.Skills = "[]"; }

                // If a model name was selected, resolve it to numeric id
                try { if (!string.IsNullOrWhiteSpace(SelectedModelName)) { var mid = _database.GetModelIdByName(SelectedModelName); Agent.ModelId = mid.HasValue ? (int?)mid.Value : null; } else { Agent.ModelId = null; } } catch { }
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
                _database.UpdateAgent(Agent);
                return RedirectToPage("/Agents/Index");
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                Voices = _database.ListTtsVoices();
                Models = _database.ListModels();
                return Page();
            }
        }
    }
}
