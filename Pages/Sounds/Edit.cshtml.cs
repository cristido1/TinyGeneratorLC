using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.Sounds;

public class EditModel : PageModel
{
    private readonly DatabaseService _database;
    private static readonly string SoundsRoot = Path.GetFullPath(@"C:\Users\User\Documents\ai\sounds_library");
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3" };

    public EditModel(DatabaseService database)
    {
        _database = database;
    }

    [BindProperty]
    public Sound Sound { get; set; } = new();

    public IReadOnlyList<string> AllowedTypes { get; } = new[] { "fx", "music", "amb" };

    public IActionResult OnGet(int id)
    {
        var item = _database.GetSoundById(id);
        if (item == null)
        {
            TempData["Error"] = $"Suono non trovato (id={id})";
            return RedirectToPage("/Sounds/Index");
        }

        Sound = item;
        return Page();
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
            _database.UpdateSound(Sound);
            TempData["Success"] = "Suono aggiornato.";
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

        if (Sound.Id <= 0)
        {
            ModelState.AddModelError(string.Empty, "Id non valido.");
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

        if (Sound.ScoreHuman.HasValue)
        {
            if (Sound.ScoreHuman.Value < 0 || Sound.ScoreHuman.Value > 100)
            {
                ModelState.AddModelError("Sound.ScoreHuman", "ScoreHuman deve essere tra 0 e 100.");
            }
        }
    }
}
