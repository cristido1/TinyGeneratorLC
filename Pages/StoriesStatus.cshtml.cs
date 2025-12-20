using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages
{
    public class StoriesStatusModel : PageModel
    {
        private readonly DatabaseService _database;

        public StoriesStatusModel(DatabaseService database)
        {
            _database = database;
        }

        // Expose a simple DTO list for AG Grid
        public List<StoryRecord> GridData { get; set; } = new List<StoryRecord>();

        public void OnGet()
        {
            // Retrieve lightweight list of stories for the grid
            // PageModel must not implement filtering/sorting/pagination logic
            try
            {
                GridData = _database.GetAllStories() ?? new List<StoryRecord>();
            }
            catch
            {
                GridData = new List<StoryRecord>();
            }
        }
    }
}
