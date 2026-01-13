using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TinyGenerator.Data;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.NarrativeProfiles;

public class EditModel : PageModel
{
    private readonly TinyGeneratorDbContext _context;

    public EditModel(TinyGeneratorDbContext context)
    {
        _context = context;
    }

    [BindProperty]
    public NarrativeProfile Item { get; set; } = new();

    public bool IsNew => Item.Id == 0;

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is null)
        {
            Item = new NarrativeProfile();
            return Page();
        }

        var existing = await _context.NarrativeProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id.Value);
        if (existing is null)
        {
            return NotFound();
        }

        Item = existing;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (Item.Id == 0)
        {
            _context.NarrativeProfiles.Add(Item);
        }
        else
        {
            var existing = await _context.NarrativeProfiles.FirstOrDefaultAsync(p => p.Id == Item.Id);
            if (existing is null)
            {
                return NotFound();
            }

            existing.Name = Item.Name;
            existing.Description = Item.Description;
            existing.BaseSystemPrompt = Item.BaseSystemPrompt;
            existing.StylePrompt = Item.StylePrompt;
        }

        await _context.SaveChangesAsync();
        return RedirectToPage("./Index");
    }
}
