using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.SystemReports;

public class IndexModel : PageModel
{
    private readonly DatabaseService _database;

    public List<SystemReport> Reports { get; set; } = new();
    public int PageIndex { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public int TotalCount { get; set; }

    public IndexModel(DatabaseService database)
    {
        _database = database;
    }

    public void OnGet()
    {
        if (int.TryParse(Request.Query["page"], out var p) && p > 0) PageIndex = p;
        if (int.TryParse(Request.Query["pageSize"], out var ps) && ps > 0) PageSize = ps;

        var (items, total) = _database.GetPagedSystemReports(PageIndex, PageSize);
        Reports = items;
        TotalCount = total;
    }

    public IActionResult OnPostDeleteAll(int? page, int? pageSize)
    {
        try
        {
            _database.DeleteAllSystemReports();
        }
        catch
        {
            // best-effort: avoid error page on delete
        }
        return RedirectToPage("/SystemReports/Index", new { page = page ?? 1, pageSize = pageSize ?? 20 });
    }

    public IActionResult OnPostDeleteReport(long reportId, int? page, int? pageSize)
    {
        try
        {
            _database.DeleteSystemReport(reportId);
        }
        catch
        {
            // best-effort: avoid error page on delete
        }
        return RedirectToPage("/SystemReports/Index", new { page = page ?? 1, pageSize = pageSize ?? 20 });
    }
}
