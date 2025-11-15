using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using TinyGenerator.Services;

namespace TinyGenerator.Pages
{
    public class StoriesModel : PageModel
    {
        private readonly StoriesService _stories;
        private readonly ILogger<StoriesModel> _logger;

        public StoriesModel(StoriesService stories, ILogger<StoriesModel> logger)
        {
            _stories = stories;
            _logger = logger;
        }

        public IEnumerable<StoriesService.StoryRecord> Stories { get; private set; } = Enumerable.Empty<StoriesService.StoryRecord>();

        public void OnGet()
        {
            Stories = _stories.GetAllStories();
        }

        public IActionResult OnGetEvaluations(long id)
        {
            _logger.LogInformation("OnGetEvaluations called for %d", id);
            try
            {
                var evals = _stories.GetEvaluationsForStory(id);
                return new JsonResult(evals);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching evaluations for story {Id}", id);
                return StatusCode(500);
            }
        }

        public IActionResult OnPost(long id)
        {
            _stories.Delete(id);
            return RedirectToPage();
        }

        
    }
}
