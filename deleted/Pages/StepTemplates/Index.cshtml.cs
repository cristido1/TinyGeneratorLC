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

        public sealed class StepTemplateListItem
        {
            public long Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public string TaskType { get; set; } = string.Empty;
            public string? Description { get; set; }
            public string? Instructions { get; set; }
            public string StepPrompt { get; set; } = string.Empty;
            public string? CreatedAt { get; set; }
            public string? UpdatedAt { get; set; }
            public int StepCount { get; set; }
            public int? MinCharsTrama { get; set; }
            public int? MinCharsStory { get; set; }
            public int? FullStoryStep { get; set; }
        }

        public IReadOnlyList<StepTemplateListItem> Items { get; set; } = Array.Empty<StepTemplateListItem>();
        public int PageIndex { get; set; } = 1;
        public int PageSize { get; set; } = 10000;
        public int TotalCount { get; set; }
        public string? Search { get; set; }
        public string? OrderBy { get; set; }

        public void OnGet()
        {
            if (int.TryParse(Request.Query["page"], out var p) && p > 0) PageIndex = p;
            if (int.TryParse(Request.Query["pageSize"], out var ps) && ps > 0) PageSize = ps;
            Search = string.IsNullOrWhiteSpace(Request.Query["search"]) ? null : Request.Query["search"].ToString();
            OrderBy = string.IsNullOrWhiteSpace(Request.Query["orderBy"]) ? null : Request.Query["orderBy"].ToString();

            var (items, total) = _db.GetPagedStepTemplates(PageIndex, PageSize, Search, OrderBy);
            TotalCount = total;
            Items = items.Select(t => new StepTemplateListItem
            {
                Id = t.Id,
                Name = t.Name ?? string.Empty,
                TaskType = t.TaskType ?? string.Empty,
                Description = t.Description,
                Instructions = t.Instructions,
                StepPrompt = t.StepPrompt ?? string.Empty,
                CreatedAt = t.CreatedAt,
                UpdatedAt = t.UpdatedAt,
                StepCount = Regex.Matches(t.StepPrompt ?? string.Empty, @"^\d+\.", RegexOptions.Multiline).Count,
                MinCharsTrama = t.MinCharsTrama,
                MinCharsStory = t.MinCharsStory,
                FullStoryStep = t.FullStoryStep
            }).ToList();
        }

        public sealed class RowAction
        {
            public string Id { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string Method { get; set; } = "GET";
            public string Url { get; set; } = string.Empty;
            public bool Confirm { get; set; }
            public Dictionary<string, string> Fields { get; set; } = new();
        }

        public IEnumerable<RowAction> GetActionsForTemplate(StepTemplateListItem item)
        {
            return new List<RowAction>
            {
                new RowAction { Id = "details", Title = "Details", Method = "CLIENT", Url = "#" },
                new RowAction { Id = "edit", Title = "Edit", Method = "GET", Url = $"/StepTemplates/Edit?id={item.Id}" },
                new RowAction
                {
                    Id = "copy",
                    Title = "Copy",
                    Method = "POST",
                    Url = "?handler=Copy",
                    Fields = new Dictionary<string, string> { ["id"] = item.Id.ToString() }
                },
                new RowAction
                {
                    Id = "delete",
                    Title = "Delete",
                    Method = "POST",
                    Url = "?handler=Delete",
                    Confirm = true,
                    Fields = new Dictionary<string, string> { ["id"] = item.Id.ToString() }
                }
            };
        }

        public IActionResult OnPostSave(
            int id,
            string name,
            string taskType,
            string? description,
            string stepPrompt,
            string? instructions,
            int? minCharsTrama,
            int? minCharsStory,
            int? fullStoryStep)
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
                Instructions = string.IsNullOrWhiteSpace(instructions) ? null : instructions,
                StepPrompt = stepPrompt,
                MinCharsTrama = minCharsTrama,
                MinCharsStory = minCharsStory,
                FullStoryStep = fullStoryStep,
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

        public IActionResult OnPostCopy(int id)
        {
            var template = _db.GetStepTemplateById(id);
            if (template == null)
            {
                TempData["Error"] = "Template not found";
                return RedirectToPage();
            }

            // Build a unique name for the copied template
            var baseName = template.Name + " copia";
            var newName = baseName;
            int idx = 1;
            while (_db.GetStepTemplateByName(newName) != null)
            {
                newName = baseName + " (" + idx + ")";
                idx++;
            }

            var copy = new TinyGenerator.Models.StepTemplate
            {
                Id = 0,
                Name = newName,
                TaskType = template.TaskType,
                StepPrompt = template.StepPrompt,
                Instructions = template.Instructions,
                Description = template.Description,
                CharactersStep = template.CharactersStep,
                EvaluationSteps = template.EvaluationSteps,
                TramaSteps = template.TramaSteps,
                MinCharsTrama = template.MinCharsTrama,
                MinCharsStory = template.MinCharsStory,
                FullStoryStep = template.FullStoryStep,
                CreatedAt = DateTime.UtcNow.ToString("o"),
                UpdatedAt = DateTime.UtcNow.ToString("o")
            };

            _db.UpsertStepTemplate(copy);
            TempData["Success"] = $"Template '{template.Name}' copied to '{newName}'";
            return RedirectToPage();
        }
    }
}
