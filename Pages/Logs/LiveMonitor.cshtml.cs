using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Collections.Generic;
using System.Threading.Tasks;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.Logs
{
    public class LiveMonitorModel : PageModel
    {
        private readonly DatabaseService _db;

        public List<LogEntry> RecentLogs { get; set; } = new();

        public LiveMonitorModel(DatabaseService db)
        {
            _db = db;
        }

        public async Task OnGetAsync()
        {
            // Load last 1000 logs (newest first) for initial hydration if needed
            RecentLogs = _db.GetRecentLogs(limit: 1000);
            await Task.CompletedTask;
        }
    }
}
