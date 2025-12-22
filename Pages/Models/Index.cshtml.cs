using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System;
using System.Collections.Generic;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.Models
{
    public class IndexModel : PageModel
    {
        private readonly DatabaseService _database;

        public IndexModel(DatabaseService database)
        {
            _database = database;
        }

        public IReadOnlyList<ModelInfo> Items { get; set; } = Array.Empty<ModelInfo>();
        public int PageIndex { get; set; } = 1;
        public int PageSize { get; set; } = 25;
        public int TotalCount { get; set; }
        public string? Search { get; set; }
        public string? OrderBy { get; set; }
        public bool Ascending { get; set; } = true;

        public void OnGet()
        {
            if (int.TryParse(Request.Query["page"], out var p) && p > 0) PageIndex = p;
            if (int.TryParse(Request.Query["pageSize"], out var ps) && ps > 0) PageSize = ps;
            Search = string.IsNullOrWhiteSpace(Request.Query["search"]) ? null : Request.Query["search"].ToString();
            OrderBy = string.IsNullOrWhiteSpace(Request.Query["orderBy"]) ? null : Request.Query["orderBy"].ToString();

            var (items, total) = _database.GetPagedModels(Search, OrderBy, PageIndex, PageSize);
            Items = items;
            TotalCount = total;
        }

        // Row-level delete action (POST) using antiforgery
        public IActionResult OnPostDelete(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return RedirectToPage();
            try
            {
                _database.DeleteModel(name);
                TempData["StatusMessage"] = "Modello eliminato.";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "Errore eliminazione modello: " + ex.Message;
            }
            return RedirectToPage();
        }
    }
}
