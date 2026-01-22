using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TinyGenerator.Data;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.NarrativeProfiles;

public class IndexModel : PageModel
{
    private readonly TinyGeneratorDbContext _context;

    public IndexModel(TinyGeneratorDbContext context)
    {
        _context = context;
    }

    public IReadOnlyList<NarrativeProfile> Items { get; private set; } = Array.Empty<NarrativeProfile>();
    public string? Search { get; private set; }

    public void OnGet()
    {
        Search = string.IsNullOrWhiteSpace(Request.Query["search"]) ? null : Request.Query["search"].ToString();

        IQueryable<NarrativeProfile> query = _context.NarrativeProfiles
            .AsNoTracking()
            .Include(p => p.Resources)
            .Include(p => p.MicroObjectives)
            .Include(p => p.FailureRules)
            .Include(p => p.ConsequenceRules);

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var q = Search.Trim();
            query = query.Where(p =>
                p.Name.Contains(q) ||
                (p.Description ?? string.Empty).Contains(q));
        }

        Items = query
            .OrderBy(p => p.Name)
            .ToList();
    }

    public async Task<IActionResult> OnGetDetailAsync(int id)
    {
        var profile = await _context.NarrativeProfiles
            .AsNoTracking()
            .Include(p => p.Resources)
            .Include(p => p.MicroObjectives)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (profile is null)
            return NotFound();

        var result = new {
            title = profile.Name,
            description = profile.Description,
            resources = profile.Resources.Select(r => new { r.Id, r.Name, initial = r.InitialValue, min = r.MinValue, max = r.MaxValue }),
            microObjectives = profile.MicroObjectives.Select(m => new { m.Id, m.Code, m.Description })
        };

        return new JsonResult(result);
    }
}
