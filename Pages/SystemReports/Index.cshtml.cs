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
        _database.DeleteAllSystemReports();
        return RedirectToPage(new { page = page ?? PageIndex, pageSize = pageSize ?? PageSize });
    }

    public IActionResult OnPostDeleteReport(long reportId, int? page, int? pageSize)
    {
        _database.DeleteSystemReport(reportId);
        return RedirectToPage(new { page = page ?? PageIndex, pageSize = pageSize ?? PageSize });
    }
}
