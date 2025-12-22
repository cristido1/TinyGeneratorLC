using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.TestDefinitions
{
    public class IndexModel : PageModel
    {
        private readonly DatabaseService _db;
        public IndexModel(DatabaseService db)
        {
            _db = db;
        }

        // Paged results exposed to the view
        public IReadOnlyList<TestDefinition> Definitions { get; set; } = new List<TestDefinition>();

        [BindProperty(SupportsGet = true)]
        public bool ShowDisabled { get; set; } = false;

        [BindProperty(SupportsGet = true, Name = "page")]
        public int PageIndex { get; set; } = 1;

        [BindProperty(SupportsGet = true, Name = "pageSize")]
        public int PageSize { get; set; } = 25;

        [BindProperty(SupportsGet = true)]
        public string? Search { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? OrderBy { get; set; }

        [BindProperty(SupportsGet = true)]
        public bool Ascending { get; set; } = true;

        public int TotalCount { get; set; }

        public void OnGet()
        {
            // Use DB-level paging (pageIndex starts at 1)
            var (items, total) = _db.GetPagedTestDefinitions(PageIndex, PageSize, Search, OrderBy, Ascending);
            Definitions = items;
            TotalCount = total;
        }

        // Endpoint used by the preview button to retrieve the full prompt text
        public IActionResult OnGetDetailsPrompt(int id)
        {
            var td = _db.GetTestDefinitionById(id);
            if (td == null) return NotFound();
            return Content(td.Prompt ?? string.Empty, "text/plain");
        }
    }
}
