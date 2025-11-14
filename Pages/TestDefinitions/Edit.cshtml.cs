using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.TestDefinitions
{
    public class EditModel : PageModel
    {
        private readonly DatabaseService _db;
        public EditModel(DatabaseService db)
        {
            _db = db;
        }

        [BindProperty]
        public TestDefinition Definition { get; set; } = new TestDefinition();

        public void OnGet(int? id)
        {
            if (id.HasValue && id.Value > 0)
            {
                var td = _db.GetTestDefinitionById(id.Value);
                if (td != null) Definition = td;
            }
            else
            {
                // set defaults for new
                Definition.TestType ??= "functioncall";
            }
        }

        public IActionResult OnPost()
        {
            if (!ModelState.IsValid) return Page();

            if (Definition.Id > 0)
            {
                _db.UpdateTestDefinition(Definition);
            }
            else
            {
                _db.InsertTestDefinition(Definition);
            }

            return RedirectToPage("./Index");
        }
    }
}
