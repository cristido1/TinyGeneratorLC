using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.GenericLookup;

public class IndexModel : PageModel
{
    private readonly DatabaseService _database;

    public IndexModel(DatabaseService database)
    {
        _database = database;
    }

    public IReadOnlyList<GenericLookupEntry> Items { get; private set; } = Array.Empty<GenericLookupEntry>();
    public IReadOnlyList<string> Types { get; private set; } = Array.Empty<string>();
    public string? Search { get; private set; }
    public string? TypeFilter { get; private set; }
    public string? LoadError { get; private set; }

    public void OnGet(string? search = null, string? type = null)
    {
        Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        TypeFilter = string.IsNullOrWhiteSpace(type) ? null : type.Trim();

        try
        {
            Types = _database.ListGenericLookupTypes();
            Items = _database.ListGenericLookupEntries(Search, TypeFilter);
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
            Types = Array.Empty<string>();
            Items = Array.Empty<GenericLookupEntry>();
        }
    }
}

