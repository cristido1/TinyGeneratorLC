using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TinyGenerator.Pages.Models;

public class Index2Model : PageModel
{
    [FromQuery(Name = "title")]
    public string? TitleQuery { get; set; }

    public string Title { get; private set; } = "Models (VuePrime)";

    public void OnGet()
    {
        if (!string.IsNullOrWhiteSpace(TitleQuery))
        {
            Title = TitleQuery.Trim();
        }
    }
}
