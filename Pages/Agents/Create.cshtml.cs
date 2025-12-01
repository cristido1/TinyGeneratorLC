using System;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.Agents
{
    public class CreateModel : PageModel
    {
        private readonly DatabaseService _database;
        [BindProperty]
        public Agent Agent { get; set; } = new();
        public List<TinyGenerator.Models.TtsVoice> Voices { get; set; } = new();
        public List<TinyGenerator.Models.StepTemplate> StepTemplates { get; set; } = new();

        public CreateModel(DatabaseService database)
        {
            _database = database;
        }

        public void OnGet()
        {
            // initialize defaults
            Agent.IsActive = true;
            Agent.CreatedAt = DateTime.UtcNow.ToString("o");
            Voices = _database.ListTtsVoices();
            StepTemplates = _database.ListStepTemplates();
        }

        public IActionResult OnPost()
        {
            // Handle empty string for MultiStepTemplateId (convert to null)
            if (Agent.MultiStepTemplateId.HasValue && Agent.MultiStepTemplateId.Value == 0)
            {
                Agent.MultiStepTemplateId = null;
            }
            
            if (!ModelState.IsValid) return Page();
            // Validate JSON fields
            try
            {
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
                var id = _database.InsertAgent(Agent);
                return RedirectToPage("/Agents/Index");
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                Voices = _database.ListTtsVoices();
                StepTemplates = _database.ListStepTemplates();
                return Page();
            }
        }
    }
}
