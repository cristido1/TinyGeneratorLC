using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TinyGenerator.Data;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.Agents;

public class EditModel : PageModel
{
    private readonly TinyGeneratorDbContext _context;

    public EditModel(TinyGeneratorDbContext context)
    {
        _context = context;
    }

    [BindProperty]
    public Agent Item { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is null)
            return NotFound();

        var existing = await _context.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id.Value);
        if (existing is null)
            return NotFound();

        Item = existing;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        var existing = await _context.Agents.FirstOrDefaultAsync(a => a.Id == Item.Id);
        if (existing is null)
            return NotFound();

        existing.Description = Item.Description;
        existing.Role = Item.Role;
        existing.SystemPrompt = Item.SystemPrompt;
        existing.UserPrompt = Item.UserPrompt;
        existing.JsonResponseFormat = Item.JsonResponseFormat;
        existing.IsActive = Item.IsActive;
        existing.Notes = Item.Notes;
        existing.UpdatedAt = DateTime.UtcNow.ToString("o");

        await _context.SaveChangesAsync();

        TempData["SuccessMessage"] = $"Agente \"{existing.Description}\" aggiornato con successo.";
        return RedirectToPage("/Agents/SystemPrompts");
    }
}
