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
        public string Title { get; set; } = string.Empty;

        [BindProperty]
        public int? ModelId { get; set; }

        [BindProperty]
        public int? StatusId { get; set; }

        [BindProperty]
        public int? AgentId { get; set; }

        [BindProperty]
        public int? SerieId { get; set; }

        [BindProperty]
        public int? SerieEpisode { get; set; }

        public List<StoryStatus> Statuses { get; set; } = new();
        public List<Agent> Agents { get; set; } = new();
        public List<TinyGenerator.Models.ModelInfo> Models { get; set; } = new();
        public List<TinyGenerator.Models.Series> Series { get; set; } = new();

        public IActionResult OnGet()
        {
            LoadStatuses();
            LoadAgents();
            LoadModels();
            LoadSeries();
            return Page();
        }

        public IActionResult OnPost()
        {
            LoadStatuses();
            LoadAgents();

            // Save story immediately
            var storyId = _stories.InsertSingleStory(Prompt, StoryText, modelId: ModelId, agentId: AgentId, statusId: StatusId, title: Title, serieId: SerieId, serieEpisode: SerieEpisode);
            
            // Enqueue status chain as background task
            var chainId = _stories.EnqueueStatusChain(storyId);
            
            var message = string.IsNullOrWhiteSpace(chainId)
                ? $"Storia creata (id={storyId})"
                : $"Storia creata (id={storyId}) - catena stati avviata ({chainId})";
            
            TempData["StatusMessage"] = message;
            return RedirectToPage("/Stories/Details", new { id = storyId });
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

        private void LoadModels()
        {
            try
            {
                Models = _database.ListModels().ToList();
            }
            catch
            {
                Models = new List<TinyGenerator.Models.ModelInfo>();
            }
        }

        private void LoadSeries()
        {
            try
            {
                Series = _database.ListAllSeries();
            }
            catch
            {
                Series = new List<TinyGenerator.Models.Series>();
            }
        }
    }
}
