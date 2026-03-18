using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TinyGenerator.Pages.SystemReportsErrors;

public class IndexModel : PageModel
{
    public IActionResult OnGet()
    {
        return RedirectToPage("/Shared/Index", new { entity = "system_reports_errors", title = "System Reports Errors" });
    }
}
