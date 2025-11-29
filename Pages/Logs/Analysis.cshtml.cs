using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.Logs
{
    public class AnalysisModel : PageModel
    {
        private readonly DatabaseService _db;

        public List<LogAnalysis> Analyses { get; private set; } = new();

        [BindProperty(SupportsGet = true)]
        public string? ThreadId { get; set; }

        public AnalysisModel(DatabaseService db)
        {
            _db = db;
        }

        public void OnGet()
        {
            Analyses = string.IsNullOrWhiteSpace(ThreadId)
                ? _db.GetLogAnalyses(200)
                : _db.GetLogAnalysesByThread(ThreadId);
        }
    }
}
