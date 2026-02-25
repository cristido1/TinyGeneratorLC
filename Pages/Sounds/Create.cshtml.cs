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
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3" };

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

    public IActionResult OnGetBrowseFiles(string? q = null)
    {
        try
        {
            if (!Directory.Exists(SoundsRoot))
            {
                return new JsonResult(Array.Empty<object>());
            }

            var query = (q ?? string.Empty).Trim();
            var files = Directory.EnumerateFiles(SoundsRoot, "*.*", SearchOption.AllDirectories)
                .Where(f => AllowedExtensions.Contains(Path.GetExtension(f)))
                .Select(full => new
                {
                    fullPath = Path.GetFullPath(full),
                    fileName = Path.GetFileName(full),
                    relativePath = Path.GetRelativePath(SoundsRoot, full).Replace('\\', '/')
                })
                .Where(x => string.IsNullOrWhiteSpace(query) ||
                            x.fileName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            x.relativePath.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.relativePath, StringComparer.OrdinalIgnoreCase)
                .Take(1000)
                .ToList();

            return new JsonResult(files);
        }
        catch (Exception ex)
        {
            return new JsonResult(new { error = ex.Message }) { StatusCode = 500 };
        }
    }

    public IActionResult OnPost()
    {
        ValidateSound();
        if (!ModelState.IsValid) return Page();

        try
        {
            if (string.Equals(Path.GetExtension(Sound.FilePath), ".wav", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetExtension(Sound.FilePath), ".mp3", StringComparison.OrdinalIgnoreCase))
            {
                Sound.DurationSeconds = _soundScoring.GetAudioDurationSeconds(Sound.FilePath);
            }
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
            else if (!AllowedExtensions.Contains(Path.GetExtension(full)))
            {
                ModelState.AddModelError("Sound.FilePath", "Sono ammessi file .wav e .mp3.");
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
