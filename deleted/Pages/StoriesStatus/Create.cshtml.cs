using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TinyGenerator.Pages.StoriesStatus
{
    public class CreateModel : PageModel
    {
        public IActionResult OnGet()
        {
            return RedirectToPage("./Edit");
        }
    }
}