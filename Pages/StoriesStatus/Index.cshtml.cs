using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.StoriesStatus
{
    public class IndexModel : PageModel
    {
        private readonly DatabaseService _db;
        public IndexModel(DatabaseService db)
        {
            _db = db;
        }

        public List<StoryStatus> Statuses { get; set; } = new();

        public void OnGet()
        {
            Statuses = _db.ListAllStoryStatuses();
        }
    }
}