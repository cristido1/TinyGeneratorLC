using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Services;

namespace TinyGenerator.Pages
{
    public class StoriesModel : PageModel
    {
        private readonly StoriesService _stories;

        public StoriesModel(StoriesService stories)
        {
            _stories = stories;
        }

        public IEnumerable<StoriesService.StoryRecord> Stories { get; private set; } = Enumerable.Empty<StoriesService.StoryRecord>();

        public void OnGet()
        {
            Stories = _stories.GetAllStories();
        }

        public IActionResult OnPost(long id)
        {
            _stories.Delete(id);
            return RedirectToPage();
        }
    }
}
