using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TinyGenerator.Pages.Shared;

public class IndexModel : PageModel
{
    [FromQuery(Name = "entity")]
    public string? Entity { get; set; }

    [FromQuery(Name = "title")]
    public string? Title { get; set; }

    public void OnGet()
    {
        Entity = string.IsNullOrWhiteSpace(Entity) ? "roles" : Entity.Trim().ToLowerInvariant();
        Title = string.IsNullOrWhiteSpace(Title) ? Entity : Title.Trim();
    }
}
