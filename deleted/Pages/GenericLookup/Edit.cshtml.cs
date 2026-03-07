using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.GenericLookup;

public class EditModel : PageModel
{
    private readonly DatabaseService _database;

    public EditModel(DatabaseService database)
    {
        _database = database;
    }

    [BindProperty]
    public GenericLookupEntry Item { get; set; } = new();

    public IReadOnlyList<string> ExistingTypes { get; private set; } = Array.Empty<string>();
    public bool IsNew => Item.Id <= 0;

    public IActionResult OnGet(long? id = null)
    {
        LoadTypeSuggestions();

        if (id is null || id <= 0)
        {
            Item = new GenericLookupEntry
            {
                SortOrder = 0,
                IsActive = true,
                Weight = 1
            };
            return Page();
        }

        var existing = _database.GetGenericLookupById(id.Value);
        if (existing == null)
        {
            return NotFound();
        }

        Item = existing;
        return Page();
    }

    public IActionResult OnPost()
    {
        LoadTypeSuggestions();

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var id = _database.UpsertGenericLookup(Item);
            TempData["Message"] = Item.Id > 0
                ? "GenericLookup aggiornato."
                : $"GenericLookup creato (Id={id}).";
            return RedirectToPage("./Index", new { type = Item.Type });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    private void LoadTypeSuggestions()
    {
        ExistingTypes = _database.ListGenericLookupTypes();
    }
}

