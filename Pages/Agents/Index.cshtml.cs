using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.Agents
{
    public class IndexModel : PageModel
    {
        private readonly DatabaseService _database;
        public IReadOnlyList<Agent> Items { get; set; } = Array.Empty<Agent>();
        public IReadOnlyList<ModelInfo> Models { get; set; } = Array.Empty<ModelInfo>();
        public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();

        public int PageIndex { get; set; } = 1;
        public int PageSize { get; set; } = 25;
        public int TotalCount { get; set; }
        public string? OrderBy { get; set; }

        public IndexModel(DatabaseService database)
        {
            _database = database;
        }

        [BindProperty]
        public string? StoryTheme { get; set; }

        [BindProperty]
        public int AgentId { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? Search { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? ModelFilter { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? RoleFilter { get; set; }

        public void OnGet()
        {
            // Load all data for client-side DataTables processing
            PageSize = 10000; // Large number to get all records
            PageIndex = 1;

            try
            {
                Models = _database.ListModels() ?? new List<ModelInfo>();
                Roles = _database.ListAgentRoles() ?? new List<string>();
                var (items, total) = _database.GetPagedAgents(null, null, 1, 10000, null, null);
                Items = items;
                TotalCount = total;
            }
            catch
            {
                Items = Array.Empty<Agent>();
                TotalCount = 0;
            }
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

        public IEnumerable<RowAction> GetActionsForAgent(Agent agent)
        {
            var actions = new List<RowAction>
            {
                new RowAction { Id = "edit", Title = "Edit", Method = "GET", Url = $"/Agents/Edit?id={agent.Id}" },
                new RowAction
                {
                    Id = "clone",
                    Title = "Duplica",
                    Method = "POST",
                    Url = "?handler=Clone",
                    Fields = new Dictionary<string, string> { ["id"] = agent.Id.ToString() }
                },
                new RowAction
                {
                    Id = "delete",
                    Title = "Delete",
                    Method = "POST",
                    Url = "?handler=Delete",
                    Confirm = true,
                    Fields = new Dictionary<string, string> { ["id"] = agent.Id.ToString() }
                }
            };

            if (!string.IsNullOrWhiteSpace(agent.Role) && agent.Role.Equals("writer", StringComparison.OrdinalIgnoreCase))
            {
                actions.Insert(2, new RowAction
                {
                    Id = "generate",
                    Title = "Generate story",
                    Method = "CLIENT",
                    Url = "#",
                    Fields = new Dictionary<string, string>
                    {
                        ["agentId"] = agent.Id.ToString(),
                        ["agentName"] = agent.Name ?? string.Empty
                    }
                });
            }

            return actions;
        }

        public IActionResult OnPostDelete(int id)
        {
            try
            {
                _database.DeleteAgent(id);
                return RedirectToPage("/Agents/Index");
            }
            catch (Exception ex)
            {
                TempData["Error"] = ex.Message;
                return RedirectToPage("/Agents/Index");
            }
        }

        public IActionResult OnPostGenerateStory()
        {
            if (string.IsNullOrWhiteSpace(StoryTheme))
            {
                TempData["Error"] = "Story theme is required";
                return RedirectToPage("/Agents/Index");
            }

            // Redirect to Genera page with agent and theme
            return RedirectToPage("/Genera", new { writerAgentId = AgentId, prompt = StoryTheme });
        }

        public IActionResult OnPostClone(int id)
        {
            try
            {
                var source = _database.GetAgentById(id);
                if (source == null)
                {
                    TempData["Error"] = "Agente non trovato";
                    return RedirectToPage("/Agents/Index");
                }

                // Build a new name ensuring uniqueness by appending counters if needed
                var baseName = $"{source.Name} copia".Trim();
                var newName = baseName;
                var suffix = 2;
                while (_database.GetAgentIdByName(newName) != null)
                {
                    newName = $"{baseName} {suffix}";
                    suffix++;
                }

                var clone = new Agent
                {
                    VoiceId = source.VoiceId,
                    Name = newName,
                    Role = source.Role,
                    ModelId = source.ModelId,
                    Skills = source.Skills,
                    Config = source.Config,
                    JsonResponseFormat = source.JsonResponseFormat,
                    Prompt = source.Prompt,
                    Instructions = source.Instructions,
                    Temperature = source.Temperature,
                    TopP = source.TopP,
                    RepeatPenalty = source.RepeatPenalty,
                    TopK = source.TopK,
                    RepeatLastN = source.RepeatLastN,
                    NumPredict = source.NumPredict,
                    ExecutionPlan = source.ExecutionPlan,
                    IsActive = source.IsActive,
                    MultiStepTemplateId = source.MultiStepTemplateId,
                    Notes = source.Notes
                };

                _database.InsertAgent(clone);
                TempData["Success"] = $"Agente duplicato come \"{newName}\"";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Impossibile duplicare l'agente: {ex.Message}";
            }

            return RedirectToPage("/Agents/Index");
        }
    }
}
