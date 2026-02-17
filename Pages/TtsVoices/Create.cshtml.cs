using System;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.TtsVoices
{
    public class CreateModel : PageModel
    {
        private readonly DatabaseService _db;

        [BindProperty]
        public TtsVoice Voice { get; set; } = new();

        public CreateModel(DatabaseService db)
        {
            _db = db;
        }

        public void OnGet()
        {
            Voice.Disabled = false;
            Voice.Provider = "localtts";
            Voice.CreatedAt = DateTime.UtcNow.ToString("o");
        }

        public IActionResult OnPost()
        {
            if (!ModelState.IsValid) return Page();
            Voice.Provider = NormalizeProvider(Voice.Provider);

            try
            {
                _db.InsertTtsVoice(Voice);
                TempData["TtsVoiceMessage"] = "Voce creata";
                return RedirectToPage("/TtsVoices/Index");
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                return Page();
            }
        }

        private static string NormalizeProvider(string? provider)
        {
            if (string.Equals(provider, "elevenlabs", StringComparison.OrdinalIgnoreCase))
                return "elevenlabs";
            return "localtts";
        }
    }
}
