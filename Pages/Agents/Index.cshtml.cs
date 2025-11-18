using System;
using System.Collections.Generic;
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

        public IndexModel(DatabaseService database)
        {
            _database = database;
        }

        [BindProperty(SupportsGet = true)]
        public string? Search { get; set; }

        [BindProperty(SupportsGet = true)]
        public new int Page { get; set; } = 1;

        public int PageSize { get; set; } = 20;

        public int TotalCount { get; set; }

        public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);

        public void OnGet()
        {
            try
            {
                var list = _database.ListAgents();
                // Apply simple search
                if (!string.IsNullOrWhiteSpace(Search))
                {
                    var q = Search.Trim();
                    list = list.FindAll(a => (a.Name ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase)
                        || (a.Role ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase)
                        || (a.Skills ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase));
                }

                TotalCount = list.Count;
                if (Page < 1) Page = 1;
                var skip = (Page - 1) * PageSize;
                Agents = list.Skip(skip).Take(PageSize).ToList();

                // Resolve model names for display
                foreach (var a in Agents)
                {
                    try
                    {
                        if (a.ModelId.HasValue)
                        {
                            var name = _database.GetModelNameById(a.ModelId.Value);
                            a.ModelName = name;
                        }
                    }
                    catch { }
                }
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
