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

        public IReadOnlyList<StoryStatus> Items { get; set; } = Array.Empty<StoryStatus>();
        public int PageIndex { get; set; } = 1;
        public int PageSize { get; set; } = 25;
        public int TotalCount { get; set; }
        public string? Search { get; set; }
        public string? OrderBy { get; set; }

        public void OnGet()
        {
            if (int.TryParse(Request.Query["page"], out var p) && p > 0) PageIndex = p;
            if (int.TryParse(Request.Query["pageSize"], out var ps) && ps > 0) PageSize = ps;
            Search = string.IsNullOrWhiteSpace(Request.Query["search"]) ? null : Request.Query["search"].ToString();
            OrderBy = string.IsNullOrWhiteSpace(Request.Query["orderBy"]) ? null : Request.Query["orderBy"].ToString();

            try
            {
                var (items, total) = _database.GetPagedStoryStatuses(PageIndex, PageSize, Search, OrderBy);
                Items = items;
                TotalCount = total;
            }
            catch
            {
                Items = Array.Empty<StoryStatus>();
                TotalCount = 0;
            }
        }
    }
}
