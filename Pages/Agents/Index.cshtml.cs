using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.Agents
{
    public class IndexModel : PageModel
    {
        private readonly DatabaseService _database;
        public List<Agent> Agents { get; set; } = new();
        public List<ModelInfo> Models { get; set; } = new();

        public IndexModel(DatabaseService database)
        {
            _database = database;
        }

        [BindProperty(SupportsGet = true)]
        public string? Search { get; set; }

        [BindProperty(SupportsGet = true)]
        public new int Page { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public string? ModelFilter { get; set; }

        public int PageSize { get; set; } = 20;

        public int TotalCount { get; set; }

        public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);

        public void OnGet()
        {
            try
            {
                Models = _database.ListModels() ?? new List<ModelInfo>();
                var modelLookup = Models
                    .Where(m => m.Id.HasValue)
                    .ToDictionary(m => m.Id!.Value, m => m, EqualityComparer<int>.Default);

                var list = _database.ListAgents();

                foreach (var agent in list)
                {
                    if (agent.ModelId.HasValue && modelLookup.TryGetValue(agent.ModelId.Value, out var modelInfo))
                    {
                        agent.ModelName = modelInfo?.Name;
                    }
                }

                // Apply simple search
                if (!string.IsNullOrWhiteSpace(Search))
                {
                    var q = Search.Trim();
                    list = list.FindAll(a => (a.Name ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase)
                        || (a.Role ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase)
                        || (a.Skills ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrWhiteSpace(ModelFilter))
                {
                    if (ModelFilter.Equals("__none__", StringComparison.OrdinalIgnoreCase))
                    {
                        list = list.FindAll(a => !a.ModelId.HasValue);
                    }
                    else
                    {
                        list = list.FindAll(a => string.Equals(a.ModelName ?? string.Empty, ModelFilter, StringComparison.OrdinalIgnoreCase));
                    }
                }

                TotalCount = list.Count;
                if (Page < 1) Page = 1;
                var skip = (Page - 1) * PageSize;
                Agents = list.Skip(skip).Take(PageSize).ToList();
            }
            catch { Agents = new List<Agent>(); }
        }

        public IActionResult OnPostDelete(int id)
        {
            try
            {
                _database.DeleteAgent(id);
                return RedirectToPage("/Agents/Index");
            }
            catch (Exception ex)
            {
                TempData["Error"] = ex.Message;
                return RedirectToPage("/Agents/Index");
            }
        }
    }
}
