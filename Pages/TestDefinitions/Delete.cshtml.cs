using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.TestDefinitions
{
    public class DeleteModel : PageModel
    {
        private readonly DatabaseService _db;
        public DeleteModel(DatabaseService db)
        {
            _db = db;
        }

        [BindProperty]
        public TestDefinition? Definition { get; set; }

        public void OnGet(int id)
        {
            Definition = _db.GetTestDefinitionById(id);
        }

        public IActionResult OnPost(int id)
        {
            _db.DeleteTestDefinition(id);
            return RedirectToPage("./Index");
        }
    }
}
