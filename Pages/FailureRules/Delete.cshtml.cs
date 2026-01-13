using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TinyGenerator.Data;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.FailureRules;

public class DeleteModel : PageModel
{
    private readonly TinyGeneratorDbContext _context;

    public DeleteModel(TinyGeneratorDbContext context)
    {
        _context = context;
    }

    [BindProperty]
    public FailureRule Item { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var existing = await _context.FailureRules
            .AsNoTracking()
            .Include(r => r.NarrativeProfile)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (existing is null)
        {
            return NotFound();
        }

        Item = existing;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var existing = await _context.FailureRules.FirstOrDefaultAsync(r => r.Id == id);
        if (existing is null)
        {
            return NotFound();
        }

        _context.FailureRules.Remove(existing);
        await _context.SaveChangesAsync();

        return RedirectToPage("./Index");
    }
}
