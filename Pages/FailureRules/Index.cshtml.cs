using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TinyGenerator.Data;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.FailureRules;

public class IndexModel : PageModel
{
    private readonly TinyGeneratorDbContext _context;

    public IndexModel(TinyGeneratorDbContext context)
    {
        _context = context;
    }

    public IReadOnlyList<FailureRule> Items { get; private set; } = Array.Empty<FailureRule>();
    public string? Search { get; private set; }

    public void OnGet()
    {
        Search = string.IsNullOrWhiteSpace(Request.Query["search"]) ? null : Request.Query["search"].ToString();

        IQueryable<FailureRule> query = _context.FailureRules
            .AsNoTracking()
            .Include(r => r.NarrativeProfile);

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var q = Search.Trim();
            query = query.Where(r =>
                (r.TriggerType ?? string.Empty).Contains(q) ||
                (r.Description ?? string.Empty).Contains(q) ||
                (r.NarrativeProfile != null && r.NarrativeProfile.Name.Contains(q)));
        }

        Items = query
            .OrderBy(r => r.NarrativeProfileId)
            .ThenBy(r => r.TriggerType)
            .ToList();
    }
}
