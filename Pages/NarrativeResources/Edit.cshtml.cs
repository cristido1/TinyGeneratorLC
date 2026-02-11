using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using TinyGenerator.Data;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.NarrativeResources;

public class EditModel : PageModel
{
    private readonly TinyGeneratorDbContext _context;

    public EditModel(TinyGeneratorDbContext context)
    {
        _context = context;
    }

    [BindProperty]
    public NarrativeResource Item { get; set; } = new();

    public SelectList NarrativeProfiles { get; private set; } = default!;
    public bool IsNew => Item.Id == 0;

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        await LoadSelectsAsync();

        if (id is null)
        {
            Item = new NarrativeResource();
            return Page();
        }

        var existing = await _context.NarrativeResources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id.Value);
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
            _context.NarrativeResources.Add(Item);
        }
        else
        {
            var existing = await _context.NarrativeResources.FirstOrDefaultAsync(r => r.Id == Item.Id);
            if (existing is null)
            {
                return NotFound();
            }

            existing.NarrativeProfileId = Item.NarrativeProfileId;
            existing.Name = Item.Name;
            existing.InitialValue = Item.InitialValue;
            existing.MinValue = Item.MinValue;
            existing.MaxValue = Item.MaxValue;
        }

        await _context.SaveChangesAsync();
        return RedirectToPage("./Index");
    }

    public async Task<IActionResult> OnPostAjaxAsync()
    {
        // Handles AJAX inline updates from modal
        if (!ModelState.IsValid)
        {
            var errors = ModelState
                .Where(kv => kv.Value is { Errors.Count: > 0 })
                .ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value!.Errors.Select(e => e.ErrorMessage).ToArray());
            return new JsonResult(new { success = false, errors });
        }

        if (Item.Id == 0)
        {
            _context.NarrativeResources.Add(Item);
        }
        else
        {
            var existing = await _context.NarrativeResources.FirstOrDefaultAsync(r => r.Id == Item.Id);
            if (existing is null)
            {
                return new JsonResult(new { success = false, message = "NotFound" });
            }

            existing.NarrativeProfileId = Item.NarrativeProfileId;
            existing.Name = Item.Name;
            existing.InitialValue = Item.InitialValue;
            existing.MinValue = Item.MinValue;
            existing.MaxValue = Item.MaxValue;
        }

        await _context.SaveChangesAsync();

        return new JsonResult(new { success = true, item = new { Item.Id, Item.Name, initial = Item.InitialValue, min = Item.MinValue, max = Item.MaxValue } });
    }

    private async Task LoadSelectsAsync()
    {
        var profiles = await _context.NarrativeProfiles.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
        NarrativeProfiles = new SelectList(profiles, nameof(NarrativeProfile.Id), nameof(NarrativeProfile.Name));
    }
}
