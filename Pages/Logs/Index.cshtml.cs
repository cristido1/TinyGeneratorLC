using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.Logs
{
    public class LogsModel : PageModel
    {
        private readonly DatabaseService _db;
        public List<LogEntry> LogEntries { get; private set; } = new();

        public LogsModel(DatabaseService db)
        {
            _db = db;
        }

        public void OnGet()
        {
            // Load latest 1000 records from Log table (newest first)
            LogEntries = _db.GetRecentLogs(limit: 1000);
        }
    }
}
