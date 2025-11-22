using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Services;
using System.Text;

namespace TinyGenerator.Pages;

public class GeneraModel : PageModel
{
    private readonly LangChainStoryGenerationService _generator;
    private readonly ILogger<GeneraModel> _logger;
    private readonly ProgressService _progress;
    private readonly NotificationService _notifications;

    public GeneraModel(LangChainStoryGenerationService generator, ILogger<GeneraModel> logger, ProgressService progress, NotificationService notifications)
    {
        _generator = generator;
        _logger = logger;
        _progress = progress;
        _notifications = notifications;
    }

    [BindProperty]
    public string Prompt { get; set; } = string.Empty;

    [BindProperty]
    public string Writer { get; set; } = "All";

    public object? Story { get; set; }
    public string Status => _status.ToString();
    public bool IsProcessing { get; set; }

    private StringBuilder _status = new();

    // Start generation in background. Returns a JSON with generation id.
    public async Task<IActionResult> OnPostStartAsync()
    {
        if (string.IsNullOrWhiteSpace(Prompt))
        {
            return BadRequest(new { error = "Il prompt è obbligatorio." });
        }

        var genId = Guid.NewGuid().ToString();
        _progress.Start(genId);

        // run generation in background, append progress messages to ProgressService
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _generator.GenerateStoryAsync(Prompt, msg => _progress.Append(genId, msg), Writer);
                // mark completed and store an indicative final result (approved or candidate)
                var finalText = result?.Approved ?? result?.StoryA ?? result?.StoryB;
                _progress.MarkCompleted(genId, finalText);
                try { await _notifications.NotifyGroupAsync(genId, "Completed", "Generation completed", "success"); } catch { }
            }
            catch (Exception ex)
            {
                _progress.Append(genId, "ERROR: " + ex.Message);
                _progress.MarkCompleted(genId, null);
                _logger.LogError(ex, "Errore background generazione");
            }
        });

        try { await _notifications.NotifyGroupAsync(genId, "Started", "Generation started", "info"); } catch { }

        return new JsonResult(new { id = genId });
    }

    // Poll progress for a given generation id
    public IActionResult OnGetProgress(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return BadRequest(new { error = "id mancante" });
        var messages = _progress.Get(id);
        var completed = _progress.IsCompleted(id);
        var result = _progress.GetResult(id);
        return new JsonResult(new { messages, completed, result });
    }
}