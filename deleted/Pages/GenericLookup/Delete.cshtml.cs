using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.GenericLookup;

public class DeleteModel : PageModel
{
    private readonly DatabaseService _database;

    public DeleteModel(DatabaseService database)
    {
        _database = database;
    }

    [BindProperty]
    public GenericLookupEntry Item { get; set; } = new();

    public IActionResult OnGet(long id)
    {
        var existing = _database.GetGenericLookupById(id);
        if (existing == null)
        {
            return NotFound();
        }

        Item = existing;
        return Page();
    }

    public IActionResult OnPost(long id)
    {
        _database.DeleteGenericLookupById(id);
        TempData["Message"] = "Record eliminato.";
        return RedirectToPage("./Index");
    }
}

