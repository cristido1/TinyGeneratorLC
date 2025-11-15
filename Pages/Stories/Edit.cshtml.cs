using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Services;

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
        public string Status { get; set; } = string.Empty;

        public IActionResult OnGet(long id)
        {
            Id = id;
            var s = _stories.GetStoryById(id);
            if (s == null) return NotFound();
            Prompt = s.Prompt;
            StoryText = s.StoryA ?? s.StoryB ?? s.StoryC ?? string.Empty;
            Status = s.Status;
            return Page();
        }

        public IActionResult OnPost()
        {
            if (Id <= 0) return BadRequest();
            _stories.UpdateStoryById(Id, StoryText, null, null, Status);
            return RedirectToPage("/Stories/Details", new { id = Id });
        }
    }
}
