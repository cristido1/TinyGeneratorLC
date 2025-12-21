using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.Stories
{
    public class EditModel : PageModel
    {
        private readonly StoriesService _stories;

        public EditModel(StoriesService stories)
        {
            _stories = stories;
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

        public List<StoryStatus> Statuses { get; set; } = new();

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
            LoadStatuses();
            return Page();
        }

        public IActionResult OnPost()
        {
            if (Id <= 0) return BadRequest();
            LoadStatuses();
            _stories.UpdateStoryById(Id, StoryText, null, null, StatusId, updateStatus: true);
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
    }
}
