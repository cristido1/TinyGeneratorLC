using System;
using System.Collections.Generic;
using System.Linq;
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
}
