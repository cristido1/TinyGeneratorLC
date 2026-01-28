using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Data;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.Roles;

public class EditModel : PageModel
{
    private readonly TinyGeneratorDbContext _context;

    public EditModel(TinyGeneratorDbContext context)
    {
        _context = context;
    }

    [BindProperty]
    public Role Role { get; set; } = new();

    public IActionResult OnGet(int? id)
    {
        if (id.HasValue)
        {
            var existing = _context.Roles.Find(id.Value);
            if (existing == null)
            {
                return NotFound();
            }
            Role = existing;
        }
        return Page();
    }

    public IActionResult OnPost()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var now = DateTime.UtcNow.ToString("o");
        if (Role.Id > 0)
        {
            // Update existing
            var existing = _context.Roles.Find(Role.Id);
            if (existing == null)
            {
                return NotFound();
            }
            existing.Ruolo = Role.Ruolo;
            existing.ComandoCollegato = Role.ComandoCollegato;
            existing.UpdatedAt = now;
        }
        else
        {
            // Create new
            Role.CreatedAt = now;
            Role.UpdatedAt = now;
            _context.Roles.Add(Role);
        }

        _context.SaveChanges();
        TempData["Message"] = "Ruolo salvato con successo.";
        return RedirectToPage("Index");
    }
}
