using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Services;
using TinyGenerator.Models;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Pages.Stories
{
    public class CreateModel : PageModel
    {
        private readonly StoriesService _stories;
        private readonly DatabaseService _database;
        private readonly ICommandDispatcher _dispatcher;

        public CreateModel(StoriesService stories, DatabaseService database, ICommandDispatcher dispatcher)
        {
            _stories = stories;
            _database = database;
            _dispatcher = dispatcher;
        }

        [BindProperty]
        public string Prompt { get; set; } = string.Empty;

        [BindProperty]
        public string StoryText { get; set; } = string.Empty;

        [BindProperty]
        public int? StatusId { get; set; }

        [BindProperty]
        public int? AgentId { get; set; }

        public List<StoryStatus> Statuses { get; set; } = new();
        public List<Agent> Agents { get; set; } = new();

        public IActionResult OnGet()
        {
            LoadStatuses();
            LoadAgents();
            return Page();
        }

        public IActionResult OnPost()
        {
            LoadStatuses();
            LoadAgents();

            // Enqueue creation as command
            var runId = $"create_story_{DateTime.UtcNow:yyyyMMddHHmmss}";
            var agent = AgentId.HasValue ? _database.GetAgentById(AgentId.Value) : null;
            _dispatcher.Enqueue(
                "create_story",
                async ctx =>
                {
                    var cmd = new CreateStoryCommand(_stories, Prompt, StoryText, AgentId, StatusId);
                    return await cmd.ExecuteAsync(ctx.CancellationToken);
                },
                runId: runId,
                threadScope: "story/create",
                metadata: new Dictionary<string, string>
                {
                    ["operation"] = "create_story",
                    ["prompt"] = (Prompt != null && Prompt.Length > 80) ? (Prompt.Substring(0, 80) + "...") : (Prompt ?? string.Empty),
                    ["agentId"] = AgentId?.ToString() ?? string.Empty,
                    ["agentName"] = agent?.Name ?? "unknown",
                    ["modelName"] = agent?.ModelName ?? "unknown"
                });

            TempData["StatusMessage"] = $"Creazione storia accodata (run {runId}).";
            return RedirectToPage("/Stories/Index");
        }

        private void LoadStatuses()
        {
            try
            {
                Statuses = _stories.GetAllStoryStatuses();
            }
            catch
            {
                Statuses = new List<StoryStatus>();
            }
        }

        private void LoadAgents()
        {
            try
            {
                Agents = _database.ListAgents().Where(a => a.IsActive).ToList();
            }
            catch
            {
                Agents = new List<Agent>();
            }
        }
    }
}
