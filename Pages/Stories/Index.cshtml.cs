using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.Stories
{
    public class IndexModel : PageModel
    {
        private readonly StoriesService _stories;
        private readonly DatabaseService _database;

        public IndexModel(StoriesService stories, DatabaseService database)
        {
            _stories = stories;
            _database = database;
        }

        public IEnumerable<StoryRecord> Stories { get; set; } = new List<StoryRecord>();
        public List<Agent> Evaluators { get; set; } = new List<Agent>();

        public void OnGet()
        {
            LoadData();
        }

        public void OnPostDelete(long id)
        {
            _stories.Delete(id);
            LoadData();
        }

        public async Task<IActionResult> OnPostEvaluateAsync(long id, int agentId)
        {
            await _stories.EvaluateStoryWithAgentAsync(id, agentId);
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostGenerateTtsAsync(long id, string folderName)
        {
            await _stories.GenerateTtsForStoryAsync(id, folderName);
            return RedirectToPage();
        }

        private void LoadData()
        {
            var allStories = _stories.GetAllStories().ToList();
            
            // Load evaluations for each story
            foreach (var story in allStories)
            {
                story.Evaluations = _stories.GetEvaluationsForStory(story.Id);
            }
            
            Stories = allStories;

            // Load active evaluator agents
            Evaluators = _database.ListAgents()
                .Where(a => a.Role?.Equals("story_evaluator", System.StringComparison.OrdinalIgnoreCase) == true && a.IsActive)
                .ToList();
        }
    }
}
