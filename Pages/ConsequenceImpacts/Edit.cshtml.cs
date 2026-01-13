using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using TinyGenerator.Data;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.ConsequenceImpacts;

public class EditModel : PageModel
{
    private readonly TinyGeneratorDbContext _context;

    public EditModel(TinyGeneratorDbContext context)
    {
        _context = context;
    }

    [BindProperty]
    public ConsequenceImpact Item { get; set; } = new();

    public List<SelectListItem> ConsequenceRules { get; private set; } = new();
    public bool IsNew => Item.Id == 0;

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        await LoadSelectsAsync();

        if (id is null)
        {
            Item = new ConsequenceImpact { DeltaValue = 0 };
            return Page();
        }

        var existing = await _context.ConsequenceImpacts.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id.Value);
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
            _context.ConsequenceImpacts.Add(Item);
        }
        else
        {
            var existing = await _context.ConsequenceImpacts.FirstOrDefaultAsync(i => i.Id == Item.Id);
            if (existing is null)
            {
                return NotFound();
            }

            existing.ConsequenceRuleId = Item.ConsequenceRuleId;
            existing.ResourceName = Item.ResourceName;
            existing.DeltaValue = Item.DeltaValue;
        }

        await _context.SaveChangesAsync();
        return RedirectToPage("./Index");
    }

    private async Task LoadSelectsAsync()
    {
        var rules = await _context.ConsequenceRules
            .AsNoTracking()
            .Include(r => r.NarrativeProfile)
            .OrderBy(r => r.NarrativeProfileId)
            .ThenBy(r => r.Id)
            .ToListAsync();

        ConsequenceRules = rules.Select(r => new SelectListItem
        {
            Value = r.Id.ToString(),
            Text = $"{r.NarrativeProfile?.Name ?? "-"} - Rule #{r.Id}" + (!string.IsNullOrWhiteSpace(r.Description) ? $": {Truncate(r.Description, 60)}" : string.Empty)
        }).ToList();
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
        return value.Substring(0, max) + "…";
    }
}
