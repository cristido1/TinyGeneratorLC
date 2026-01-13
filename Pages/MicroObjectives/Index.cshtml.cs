using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TinyGenerator.Data;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.MicroObjectives;

public class IndexModel : PageModel
{
    private readonly TinyGeneratorDbContext _context;

    public IndexModel(TinyGeneratorDbContext context)
    {
        _context = context;
    }

    public IReadOnlyList<MicroObjective> Items { get; private set; } = Array.Empty<MicroObjective>();
    public string? Search { get; private set; }

    public void OnGet()
    {
        Search = string.IsNullOrWhiteSpace(Request.Query["search"]) ? null : Request.Query["search"].ToString();

        IQueryable<MicroObjective> query = _context.MicroObjectives
            .AsNoTracking()
            .Include(o => o.NarrativeProfile);

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var q = Search.Trim();
            query = query.Where(o =>
                (o.Code ?? string.Empty).Contains(q) ||
                (o.Description ?? string.Empty).Contains(q) ||
                (o.NarrativeProfile != null && o.NarrativeProfile.Name.Contains(q)));
        }

        Items = query
            .OrderBy(o => o.NarrativeProfileId)
            .ThenBy(o => o.Code)
            .ToList();
    }
}
