using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.Stories
{
    public class EditModel : PageModel
    {
        private readonly StoriesService _stories;
        private readonly DatabaseService _database;

        public EditModel(StoriesService stories, DatabaseService database)
        {
            _stories = stories;
            _database = database;
        }

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        [BindProperty]
        public string Prompt { get; set; } = string.Empty;

        [BindProperty]
        public string StoryText { get; set; } = string.Empty;

        [BindProperty]
        public string? Characters { get; set; }

        [BindProperty]
        public int? StatusId { get; set; }

        [BindProperty]
        public int? AgentId { get; set; }

        [BindProperty]
        public int? ModelId { get; set; }

        public List<StoryStatus> Statuses { get; set; } = new();
        public List<Agent> Agents { get; set; } = new();
        public List<TinyGenerator.Models.ModelInfo> Models { get; set; } = new();

        [BindProperty]
        public string? Title { get; set; }

        public IActionResult OnGet(long id)
        {
            Id = id;
            var s = _stories.GetStoryById(id);
            if (s == null) return NotFound();
            Prompt = s.Prompt;
            StoryText = s.Story;
            Characters = s.Characters;
            StatusId = s.StatusId;
            Title = s.Title;
            AgentId = s.AgentId;
            ModelId = s.ModelId;
            LoadStatuses();
            LoadAgents();
            LoadModels();
            return Page();
        }

        public IActionResult OnPost()
        {
            if (Id <= 0) return BadRequest();
            LoadStatuses();
            _stories.UpdateStoryById(Id, StoryText, ModelId, AgentId, StatusId, updateStatus: true);
            if (!string.IsNullOrEmpty(Characters))
            {
                _stories.UpdateStoryCharacters(Id, Characters);
            }
            _stories.UpdateStoryTitle(Id, Title ?? string.Empty);
            return RedirectToPage("/Stories/Details", new { id = Id });
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
    }
}
