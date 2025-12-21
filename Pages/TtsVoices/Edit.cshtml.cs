using System;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.TtsVoices
{
    public class EditModel : PageModel
    {
        private readonly DatabaseService _db;

        [BindProperty]
        public TtsVoice Voice { get; set; } = new();

        public EditModel(DatabaseService db)
        {
            _db = db;
        }

        public IActionResult OnGet(int? id)
        {
            if (!id.HasValue) return RedirectToPage("/TtsVoices/Index");
            var v = _db.GetTtsVoiceById(id.Value);
            if (v == null) return NotFound();
            Voice = v;
            return Page();
        }

        public IActionResult OnPost()
        {
            if (Voice == null || Voice.Id <= 0)
            {
                ModelState.AddModelError(string.Empty, "Invalid voice data");
                return Page();
            }

            // Load existing record to protect uneditable fields (Model must not change)
            var existing = _db.GetTtsVoiceById(Voice.Id);
            if (existing == null) return NotFound();

            // Copy allowed properties only (do not allow changing VoiceId or Model)
            existing.Name = Voice.Name;
            existing.Language = Voice.Language;
            existing.Gender = Voice.Gender;
            existing.Age = Voice.Age;
            existing.Confidence = Voice.Confidence;
            existing.Score = Voice.Score;
            existing.Tags = Voice.Tags;
            existing.TemplateWav = Voice.TemplateWav;
            existing.Archetype = Voice.Archetype;
            existing.Notes = Voice.Notes;
            existing.Disabled = Voice.Disabled;
            existing.UpdatedAt = DateTime.UtcNow.ToString("o");

            _db.UpdateTtsVoice(existing);

            TempData["TtsVoiceMessage"] = "Voce aggiornata";
            return RedirectToPage("/TtsVoices/Index");
        }
    }
}
