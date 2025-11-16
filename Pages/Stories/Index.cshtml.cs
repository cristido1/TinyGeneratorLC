using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Collections.Generic;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.Stories
{
    public class IndexModel : PageModel
    {
        private readonly StoriesService _stories;

        public IndexModel(StoriesService stories)
        {
            _stories = stories;
        }

        public IEnumerable<StoryRecord> Stories { get; set; } = new List<StoryRecord>();

        public void OnGet()
        {
            Stories = _stories.GetAllStories();
        }

        public void OnPostDelete(long id)
        {
            _stories.Delete(id);
            // Refresh list
            Stories = _stories.GetAllStories();
        }
    }
}
