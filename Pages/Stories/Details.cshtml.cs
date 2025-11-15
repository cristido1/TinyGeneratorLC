using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using System.Linq;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.Stories
{
    public class DetailsModel : PageModel
    {
        private readonly StoriesService _stories;
        private readonly DatabaseService _db;
        private readonly TtsService _tts;
        private readonly ILogger<DetailsModel> _logger;

        public DetailsModel(StoriesService stories, DatabaseService db, TtsService tts, ILogger<DetailsModel> logger)
        {
            _stories = stories;
            _db = db;
            _tts = tts;
            _logger = logger;
        }

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        public StoriesService.StoryRecord? Story { get; set; }
        public List<StoriesService.StoryEvaluationRecord> Evaluations { get; set; } = new List<StoriesService.StoryEvaluationRecord>();
        public string? StoryText { get; set; }

        public void OnGet(long id)
        {
            Id = id;
            Story = _stories.GetStoryById(id);
            if (Story != null) StoryText = Story.StoryA ?? Story.StoryB ?? Story.StoryC ?? string.Empty;
            Evaluations = _stories.GetEvaluationsForStory(id);
        }

        public async Task<IActionResult> OnPostTtsAsync(long id)
        {
            try
            {
                var story = _stories.GetStoryById(id);
                if (story == null) return NotFound();
                var text = story.StoryA ?? story.StoryB ?? story.StoryC ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text)) return BadRequest("No story text");

                var voices = _db.ListTtsVoices();
                // Select a narrator voice by name or tag
                var narrator = voices.FirstOrDefault(v => v.Name != null && v.Name.ToLowerInvariant().Contains("narrator"))
                    ?? voices.FirstOrDefault(v => v.Tags != null && v.Tags.ToLowerInvariant().Contains("narrator"))
                    ?? voices.FirstOrDefault();

                if (narrator == null) return BadRequest("No TTS voice available");

                var res = await _tts.SynthesizeAsync(narrator.VoiceId, text, null);
                if (res == null) return StatusCode(500, "TTSSynthesisFailed");
                var json = new { audio_url = res.AudioUrl, audio_base64 = res.AudioBase64 };
                return new JsonResult(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in TTS synthesis for story {Id}", id);
                return StatusCode(500, ex.Message);
            }
        }
    }
}
