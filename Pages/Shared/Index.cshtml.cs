using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TinyGenerator.Pages.Shared;

public class IndexModel : PageModel
{
    [FromQuery(Name = "entity")]
    public string? Entity { get; set; }

    [FromQuery(Name = "table_name")]
    public string? TableName { get; set; }

    [FromQuery(Name = "title")]
    public string? Title { get; set; }

    public void OnGet()
    {
        var resolved = !string.IsNullOrWhiteSpace(TableName)
            ? TableName
            : Entity;
        Entity = string.IsNullOrWhiteSpace(resolved) ? "roles" : resolved.Trim().ToLowerInvariant();
        Title = string.IsNullOrWhiteSpace(Title) ? Entity : Title.Trim();
    }
}
