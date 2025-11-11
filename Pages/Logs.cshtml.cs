using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System;
using System.Collections.Generic;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages
{
    public class LogsModel : PageModel
    {
        private readonly DatabaseService _database;

        public LogsModel(DatabaseService database)
        {
            _database = database;
        }

        public List<LogEntry> Logs { get; set; } = new List<LogEntry>();
        public int CurrentPage { get; set; }
        public int PageSize { get; set; }
        public int TotalCount { get; set; }
        public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 1;

        public bool OnlyErrors { get; set; }

        public async System.Threading.Tasks.Task OnGetAsync(int page = 1, int pageSize = 50, bool onlyErrors = false, string? category = null, string? level = null)
        {
            if (page < 1) page = 1;
            if (pageSize <= 0) pageSize = 50;
            CurrentPage = page;
            PageSize = pageSize;
            OnlyErrors = onlyErrors;

            try
            {
                var levelFilter = !string.IsNullOrWhiteSpace(level) ? level : (onlyErrors ? "Error" : null);
                var categoryFilter = !string.IsNullOrWhiteSpace(category) ? category : null;
                TotalCount = _database.GetLogCount(levelFilter);
                var offset = (page - 1) * pageSize;
                Logs = _database.GetRecentLogs(pageSize, offset, levelFilter, categoryFilter);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Logs] Failed to load logs: {ex.Message}");
                Logs = new List<LogEntry>();
                TotalCount = 0;
            }
        }

        public IActionResult OnPostClear()
        {
            try
            {
                _database.ClearLogs();
            }
            catch { }

            return RedirectToPage();
        }
    }
}
