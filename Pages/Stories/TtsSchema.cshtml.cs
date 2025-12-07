using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.Stories
{
    public class TtsSchemaModel : PageModel
    {
        private readonly StoriesService _storiesService;

        public TtsSchemaModel(StoriesService storiesService)
        {
            _storiesService = storiesService;
        }

        public long StoryId { get; set; }
        public string SchemaContent { get; set; } = "";

        public IActionResult OnGet(long id)
        {
            StoryId = id;
            var story = _storiesService.GetStoryById(id);
            
            if (story == null)
            {
                TempData["ErrorMessage"] = "Story non trovata.";
                return RedirectToPage("/Stories/Index");
            }

            if (string.IsNullOrWhiteSpace(story.Folder))
            {
                TempData["ErrorMessage"] = "Story non ha una cartella associata.";
                return RedirectToPage("/Stories/Index");
            }

            var schemaPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", story.Folder, "tts_schema.json");
            
            if (!System.IO.File.Exists(schemaPath))
            {
                TempData["ErrorMessage"] = "File tts_schema.json non trovato.";
                return RedirectToPage("/Stories/Index");
            }

            try
            {
                SchemaContent = System.IO.File.ReadAllText(schemaPath);
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"Errore lettura file: {ex.Message}";
                return RedirectToPage("/Stories/Index");
            }

            return Page();
        }

        public async Task<JsonResult> OnPostSave([FromBody] SaveRequest request)
        {
            var story = _storiesService.GetStoryById(request.StoryId);
            
            if (story == null)
                return new JsonResult(new { success = false, message = "Story non trovata" });

            if (string.IsNullOrWhiteSpace(story.Folder))
                return new JsonResult(new { success = false, message = "Story non ha cartella" });

            try
            {
                // Validate JSON
                using (JsonDocument.Parse(request.Content)) { }

                var schemaPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", story.Folder, "tts_schema.json");
                await System.IO.File.WriteAllTextAsync(schemaPath, request.Content);

                return new JsonResult(new { success = true, message = "File salvato" });
            }
            catch (JsonException ex)
            {
                return new JsonResult(new { success = false, message = $"JSON non valido: {ex.Message}" });
            }
            catch (Exception ex)
            {
                return new JsonResult(new { success = false, message = $"Errore salvataggio: {ex.Message}" });
            }
        }

        public JsonResult OnPostDelete([FromBody] DeleteRequest request)
        {
            var story = _storiesService.GetStoryById(request.StoryId);
            
            if (story == null)
                return new JsonResult(new { success = false, message = "Story non trovata" });

            if (string.IsNullOrWhiteSpace(story.Folder))
                return new JsonResult(new { success = false, message = "Story non ha cartella" });

            try
            {
                var schemaPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", story.Folder, "tts_schema.json");
                
                if (System.IO.File.Exists(schemaPath))
                {
                    System.IO.File.Delete(schemaPath);
                }

                return new JsonResult(new { success = true, message = "File eliminato" });
            }
            catch (Exception ex)
            {
                return new JsonResult(new { success = false, message = $"Errore eliminazione: {ex.Message}" });
            }
        }

        public class SaveRequest
        {
            public long StoryId { get; set; }
            public string Content { get; set; } = string.Empty;
        }

        public class DeleteRequest
        {
            public long StoryId { get; set; }
        }
    }
}
