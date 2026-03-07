using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.ModelRoleErrors;

public class IndexModel : PageModel
{
    private readonly DatabaseService _database;

    public List<ModelRoleErrorGridRow> Items { get; set; } = new();
    public List<string> AvailableModels { get; set; } = new();
    public List<string> AvailableRoles { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ModelFilter { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? RoleFilter { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public IndexModel(DatabaseService database)
    {
        _database = database;
    }

    public void OnGet()
    {
        Items = _database.ListModelRoleErrorsGrid(ModelFilter, RoleFilter, 10000);
        var allRows = _database.ListModelRoleErrorsGrid(null, null, 10000);

        AvailableModels = allRows
            .Select(x => x.ModelName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AvailableRoles = allRows
            .Select(x => x.RoleCode)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IActionResult OnPostDelete(long id, string? modelFilter, string? roleFilter)
    {
        if (id <= 0)
        {
            StatusMessage = "ID non valido.";
            return RedirectToPage(new { ModelFilter = modelFilter, RoleFilter = roleFilter });
        }

        var deleted = _database.DeleteModelRoleError(id);
        StatusMessage = deleted
            ? $"Record {id} eliminato."
            : $"Eliminazione record {id} non riuscita.";

        return RedirectToPage(new { ModelFilter = modelFilter, RoleFilter = roleFilter });
    }
}
