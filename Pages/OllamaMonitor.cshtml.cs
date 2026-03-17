using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TinyGenerator.Pages;

public sealed class OllamaMonitorModel : PageModel
{
    public IActionResult OnGet()
    {
        return RedirectToPage("/ExternalServicesMonitor");
    }
}
