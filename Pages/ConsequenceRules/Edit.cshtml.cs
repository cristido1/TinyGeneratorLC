using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using TinyGenerator.Data;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.ConsequenceRules;

public class EditModel : PageModel
{
    private readonly TinyGeneratorDbContext _context;

    public EditModel(TinyGeneratorDbContext context)
    {
        _context = context;
    }

    [BindProperty]
    public ConsequenceRule Item { get; set; } = new();

    public SelectList NarrativeProfiles { get; private set; } = default!;
    public bool IsNew => Item.Id == 0;

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        await LoadSelectsAsync();

        if (id is null)
        {
            Item = new ConsequenceRule();
            return Page();
        }

        var existing = await _context.ConsequenceRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id.Value);
        if (existing is null)
        {
            return NotFound();
        }

        Item = existing;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadSelectsAsync();

        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (Item.Id == 0)
        {
            _context.ConsequenceRules.Add(Item);
        }
        else
        {
            var existing = await _context.ConsequenceRules.FirstOrDefaultAsync(r => r.Id == Item.Id);
            if (existing is null)
            {
                return NotFound();
            }

            existing.NarrativeProfileId = Item.NarrativeProfileId;
            existing.Description = Item.Description;
        }

        await _context.SaveChangesAsync();
        return RedirectToPage("./Index");
    }

    private async Task LoadSelectsAsync()
    {
        var profiles = await _context.NarrativeProfiles.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
        NarrativeProfiles = new SelectList(profiles, nameof(NarrativeProfile.Id), nameof(NarrativeProfile.Name));
    }
}
