using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.StepTemplates
{
    public class IndexModel : PageModel
    {
        private readonly DatabaseService _db;

        public IndexModel(DatabaseService db)
        {
            _db = db;
        }

        public List<StepTemplate> Templates { get; set; } = new();

        public void OnGet()
        {
            Templates = _db.ListStepTemplates();
        }

        public IActionResult OnPostSave(
            int id,
            string name,
            string taskType,
            string? description,
            string stepPrompt)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(taskType) || string.IsNullOrWhiteSpace(stepPrompt))
            {
                TempData["Error"] = "Name, Task Type, and Step Prompt are required";
                return RedirectToPage();
            }

            // Validate step format (numbered steps)
            var stepMatches = Regex.Matches(stepPrompt, @"^\d+\.", RegexOptions.Multiline);
            if (stepMatches.Count == 0)
            {
                TempData["Error"] = "Step Prompt must contain numbered steps (e.g., '1. Step description')";
                return RedirectToPage();
            }

            var template = new StepTemplate
            {
                Id = id,
                Name = name,
                TaskType = taskType,
                Description = description,
                StepPrompt = stepPrompt,
                CreatedAt = id == 0 ? DateTime.UtcNow.ToString("o") : string.Empty,
                UpdatedAt = DateTime.UtcNow.ToString("o")
            };

            _db.UpsertStepTemplate(template);

            TempData["Success"] = id == 0 ? $"Template '{name}' created" : $"Template '{name}' updated";
            return RedirectToPage();
        }

        public IActionResult OnPostDelete(int id)
        {
            var template = _db.GetStepTemplateById(id);
            if (template == null)
            {
                TempData["Error"] = "Template not found";
                return RedirectToPage();
            }

            // Check if any agents use this template
            var agents = _db.ListAgents();
            var usedBy = agents.Where(a => a.MultiStepTemplateId == id).Select(a => a.Name).ToList();

            if (usedBy.Any())
            {
                TempData["Error"] = $"Cannot delete template '{template.Name}' - used by agents: {string.Join(", ", usedBy)}";
                return RedirectToPage();
            }

            _db.DeleteStepTemplate(id);
            TempData["Success"] = $"Template '{template.Name}' deleted";
            return RedirectToPage();
        }
    }
}
