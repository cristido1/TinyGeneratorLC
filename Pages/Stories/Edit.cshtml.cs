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
        public int? StatusId { get; set; }

        public List<StoryStatus> Statuses { get; set; } = new();

        public IActionResult OnGet(long id)
        {
            Id = id;
            var s = _stories.GetStoryById(id);
            if (s == null) return NotFound();
            Prompt = s.Prompt;
            StoryText = s.Story;
            StatusId = s.StatusId;
            LoadStatuses();
            return Page();
        }

        public IActionResult OnPost()
        {
            if (Id <= 0) return BadRequest();
            LoadStatuses();
            _stories.UpdateStoryById(Id, StoryText, null, null, StatusId, updateStatus: true);
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
