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

        public IReadOnlyList<StoryStatus> Statuses { get; set; } = new List<StoryStatus>();

        [BindProperty(SupportsGet = true, Name = "page")]
        public int PageIndex { get; set; } = 1;

        [BindProperty(SupportsGet = true, Name = "pageSize")]
        public int PageSize { get; set; } = 10000;

        [BindProperty(SupportsGet = true)]
        public string? Search { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? OrderBy { get; set; }

        [BindProperty(SupportsGet = true)]
        public bool Ascending { get; set; } = true;

        public int TotalCount { get; set; }

        public void OnGet()
        {
            var (items, total) = _db.GetPagedStoryStatuses(PageIndex, PageSize, Search, OrderBy, Ascending);
            Statuses = items;
            TotalCount = total;
        }
    }
}