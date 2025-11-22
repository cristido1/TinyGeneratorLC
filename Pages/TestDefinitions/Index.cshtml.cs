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

        public List<TestDefinition> Definitions { get; set; } = new();

        [BindProperty(SupportsGet = true)]
        public bool ShowDisabled { get; set; } = false;

        public void OnGet()
        {
            Definitions = _db.ListAllTestDefinitions();
            if (!ShowDisabled)
            {
                Definitions = Definitions.FindAll(d => d.Active);
            }
        }
    }
}
