using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.NameGender;

public class IndexModel : PageModel
{
    private readonly DatabaseService _database;

    public IndexModel(DatabaseService database)
    {
        _database = database;
    }

    public IReadOnlyList<NameGenderEntry> Items { get; private set; } = Array.Empty<NameGenderEntry>();
    public string? Search { get; private set; }
    public string? LoadError { get; private set; }

    public void OnGet(string? search = null)
    {
        Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        try
        {
            Items = _database.ListNameGenderEntries(Search);
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
            Items = Array.Empty<NameGenderEntry>();
        }
    }

    public IActionResult OnPostToggleVerified(long id, bool verified)
    {
        try
        {
            var ok = _database.UpdateNameGenderVerified(id, verified);
            return new JsonResult(new { ok, id, verified });
        }
        catch (Exception ex)
        {
            Response.StatusCode = 500;
            return new JsonResult(new { ok = false, error = ex.Message });
        }
    }

    public IActionResult OnPostCycleGender(long id)
    {
        try
        {
            var gender = _database.CycleNameGender(id);
            if (string.IsNullOrWhiteSpace(gender))
            {
                Response.StatusCode = 404;
                return new JsonResult(new { ok = false, error = "Record non trovato" });
            }

            return new JsonResult(new { ok = true, id, gender });
        }
        catch (Exception ex)
        {
            Response.StatusCode = 500;
            return new JsonResult(new { ok = false, error = ex.Message });
        }
    }

    public IActionResult OnPostDeleteRow(long id)
    {
        try
        {
            var ok = _database.DeleteNameGenderById(id);
            return new JsonResult(new { ok, id });
        }
        catch (Exception ex)
        {
            Response.StatusCode = 500;
            return new JsonResult(new { ok = false, error = ex.Message });
        }
    }
}
