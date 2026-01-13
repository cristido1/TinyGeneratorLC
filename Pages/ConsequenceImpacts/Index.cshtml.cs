using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TinyGenerator.Data;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.ConsequenceImpacts;

public class IndexModel : PageModel
{
    private readonly TinyGeneratorDbContext _context;

    public IndexModel(TinyGeneratorDbContext context)
    {
        _context = context;
    }

    public IReadOnlyList<ConsequenceImpact> Items { get; private set; } = Array.Empty<ConsequenceImpact>();
    public string? Search { get; private set; }

    public void OnGet()
    {
        Search = string.IsNullOrWhiteSpace(Request.Query["search"]) ? null : Request.Query["search"].ToString();

        IQueryable<ConsequenceImpact> query = _context.ConsequenceImpacts
            .AsNoTracking()
            .Include(i => i.ConsequenceRule)
            .ThenInclude(r => r!.NarrativeProfile);

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var q = Search.Trim();
            query = query.Where(i =>
                (i.ResourceName ?? string.Empty).Contains(q) ||
                (i.ConsequenceRule != null && (i.ConsequenceRule.Description ?? string.Empty).Contains(q)) ||
                (i.ConsequenceRule != null && i.ConsequenceRule.NarrativeProfile != null && i.ConsequenceRule.NarrativeProfile.Name.Contains(q)));
        }

        Items = query
            .OrderBy(i => i.ConsequenceRuleId)
            .ThenBy(i => i.ResourceName)
            .ToList();
    }
}
