using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.Sounds;

public class CreateModel : PageModel
{
    private readonly DatabaseService _database;
    private readonly SoundScoringService _soundScoring;
    private static readonly string SoundsRoot = Path.GetFullPath(@"C:\Users\User\Documents\ai\sounds_library");

    public CreateModel(DatabaseService database, SoundScoringService soundScoring)
    {
        _database = database;
        _soundScoring = soundScoring;
    }

    [BindProperty]
    public Sound Sound { get; set; } = new();

    public IReadOnlyList<string> AllowedTypes { get; } = new[] { "fx", "music", "amb" };

    public void OnGet()
    {
        Sound.Type = "fx";
    }

    public IActionResult OnPost()
    {
        ValidateSound();
        if (!ModelState.IsValid) return Page();

        try
        {
            Sound.DurationSeconds = _soundScoring.GetWavDurationSeconds(Sound.FilePath);
            _database.InsertSound(Sound);
            TempData["Success"] = "Suono creato.";
            return RedirectToPage("/Sounds/Index");
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    private void ValidateSound()
    {
        Sound.Type = (Sound.Type ?? string.Empty).Trim().ToLowerInvariant();
        if (!AllowedTypes.Contains(Sound.Type))
        {
            ModelState.AddModelError("Sound.Type", "Tipo non valido.");
        }

        if (string.IsNullOrWhiteSpace(Sound.FilePath))
        {
            ModelState.AddModelError("Sound.FilePath", "FilePath obbligatorio.");
            return;
        }

        try
        {
            var full = Path.GetFullPath(Sound.FilePath);
            if (!full.StartsWith(SoundsRoot, StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError("Sound.FilePath", "Il file deve stare dentro sounds_library.");
            }
            else if (!string.Equals(Path.GetExtension(full), ".wav", StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError("Sound.FilePath", "Sono ammessi solo file .wav.");
            }
            else
            {
                Sound.FilePath = full;
                if (string.IsNullOrWhiteSpace(Sound.FileName))
                {
                    Sound.FileName = Path.GetFileName(full);
                }
            }
        }
        catch
        {
            ModelState.AddModelError("Sound.FilePath", "Percorso file non valido.");
        }

        if (string.IsNullOrWhiteSpace(Sound.FileName))
        {
            ModelState.AddModelError("Sound.FileName", "FileName obbligatorio.");
        }
    }
}
