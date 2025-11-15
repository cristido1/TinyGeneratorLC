using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Services;

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

        public IActionResult OnGet() => Page();

        public IActionResult OnPost()
        {
            var id = _stories.InsertSingleStory(Prompt, StoryText);
            return RedirectToPage("/Stories/Details", new { id = id });
        }
    }
}
