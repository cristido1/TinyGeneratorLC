using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Services;

namespace TinyGenerator.Pages;

public class IndexModel : PageModel
{
    private readonly ILogger<IndexModel> _logger;
    private readonly StoryGeneratorService _svc;

    // Home page is a launcher; generation is handled on /Genera

    public IndexModel(ILogger<IndexModel> logger, StoryGeneratorService svc)
    {
        _logger = logger;
        _svc = svc;
    }

    public void OnGet()
    {

    }
}
