using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.Stories
{
    public class CreateModel : PageModel
    {
        private readonly StoriesService _stories;

        public CreateModel(StoriesService stories)
        {
            _stories = stories;
        }

        [BindProperty]
        public string Prompt { get; set; } = string.Empty;

        [BindProperty]
        public string StoryText { get; set; } = string.Empty;

        [BindProperty]
        public int? StatusId { get; set; }

        public List<StoryStatus> Statuses { get; set; } = new();

        public IActionResult OnGet()
        {
            LoadStatuses();
            return Page();
        }

        public IActionResult OnPost()
        {
            LoadStatuses();
            var id = _stories.InsertSingleStory(Prompt, StoryText, statusId: StatusId);
            return RedirectToPage("/Stories/Details", new { id = id });
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
