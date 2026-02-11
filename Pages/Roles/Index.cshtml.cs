using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TinyGenerator.Data;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.Roles;

public class IndexModel : PageModel
{
    private readonly TinyGeneratorDbContext _context;

    public IndexModel(TinyGeneratorDbContext context)
    {
        _context = context;
    }

    public IReadOnlyList<Role> Items { get; set; } = new List<Role>();

    public void OnGet()
    {
        Items = _context.Roles.OrderBy(r => r.Ruolo).ToList();
    }

    public IActionResult OnGetDetail(int id)
    {
        var role = _context.Roles.FirstOrDefault(r => r.Id == id);
        if (role == null)
        {
            return new JsonResult(new { title = "Ruolo non trovato", description = string.Empty }) { StatusCode = 404 };
        }

        var fallbackItems = _context.ModelRoles
            .Include(mr => mr.Model)
            .Where(mr => mr.RoleId == id)
            .ToList()
            .OrderByDescending(mr => mr.SuccessRate)
            .Select(mr =>
                new
                {
                    modelName = mr.Model?.Name ?? "[modello eliminato]",
                    success = mr.UseSuccessed,
                    useCount = mr.UseCount,
                    failed = mr.UseFailed,
                    rate = (mr.SuccessRate * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                    enabled = mr.Enabled
                })
            .ToList();

        var description = $"Comando collegato: {(string.IsNullOrWhiteSpace(role.ComandoCollegato) ? "-" : role.ComandoCollegato)}\n" +
                          $"Creato: {FormatTimestamp(role.CreatedAt)}\n" +
                          $"Aggiornato: {FormatTimestamp(role.UpdatedAt)}";

        return new JsonResult(new
        {
            title = role.Ruolo,
            description,
            fallbackModels = fallbackItems
        });
    }

    public IActionResult OnPostDelete(int id)
    {
        var role = _context.Roles.Find(id);
        if (role == null)
        {
            TempData["ErrorMessage"] = "Ruolo non trovato.";
            return RedirectToPage();
        }

        // Check if any model_roles reference this role
        var hasModelRoles = _context.ModelRoles.Any(mr => mr.RoleId == id);
        if (hasModelRoles)
        {
            TempData["ErrorMessage"] = $"Impossibile eliminare il ruolo '{role.Ruolo}' perché è utilizzato da uno o più model_roles.";
            return RedirectToPage();
        }

        _context.Roles.Remove(role);
        _context.SaveChanges();
        TempData["Message"] = $"Ruolo '{role.Ruolo}' eliminato con successo.";
        return RedirectToPage();
    }

    public static string FormatTimestamp(string? timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp)) return "-";
        return DateTime.TryParse(timestamp, out var parsed)
            ? parsed.ToString("dd/MM/yyyy HH:mm")
            : "-";
    }

    public record RowAction(string Id, string Title, string Method, string Url, bool Confirm = false);

    public List<RowAction> GetActionsForRole(Role role)
    {
        var id = role.Id.ToString();
        var editUrl = Url.Page("Edit", new { id }) ?? "#";
        var deleteUrl = Url.Page("Index", new { handler = "Delete", id }) ?? "#";
        return new List<RowAction>
        {
            new("edit", "Modifica", "GET", editUrl),
            new("delete", "Elimina", "POST", deleteUrl, true)
        };
    }
}
