using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StoryGeneratorWeb.Services;
using System.Text;

namespace StoryGeneratorWeb.Pages;

public class GeneraModel : PageModel
{
    private readonly StoryGeneratorService _generator;
    private readonly ILogger<GeneraModel> _logger;

    public GeneraModel(StoryGeneratorService generator, ILogger<GeneraModel> logger)
    {
        _generator = generator;
        _logger = logger;
    }

    [BindProperty]
    public string Prompt { get; set; } = string.Empty;

    public string? Story { get; set; }
    public string Status => _status.ToString();
    public bool IsProcessing { get; set; }

    private StringBuilder _status = new();

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Prompt))
        {
            ModelState.AddModelError(nameof(Prompt), "Il prompt è obbligatorio.");
            return Page();
        }

        IsProcessing = true;
        _status.AppendLine("🟡 Inizio generazione...");

        void Progress(string msg)
        {
            _status.AppendLine("🟢 " + msg);
        }

        try
        {
            Story = await _generator.GenerateStoryAsync(Prompt, Progress);
            _status.AppendLine("✅ Completato.");
        }
        catch (Exception ex)
        {
            _status.AppendLine("❌ Errore: " + ex.Message);
            _logger.LogError(ex, "Errore durante la generazione");
        }

        return Page();
    }
}