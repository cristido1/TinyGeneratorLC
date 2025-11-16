using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using System.Linq;
using TinyGenerator.Services;
using TinyGenerator.Models;
using System.IO;
using System.Text.RegularExpressions;

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

        public StoryRecord? Story { get; set; }
        public List<StoryEvaluation> Evaluations { get; set; } = new List<StoryEvaluation>();
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
                // Select a narrator voice by name or tag (tags is a dictionary)
                var narrator = voices.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v.Name) && v.Name.ToLowerInvariant().Contains("narrator"))
                    ?? voices.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v.Tags) && v.Tags!.ToLowerInvariant().Contains("narrator"))
                    ?? voices.FirstOrDefault();

                _logger.LogInformation("TTS request for story {Id}: storyLength={Len}, voicesFound={Count}, selectedVoiceId={VoiceId}, selectedVoiceName={VoiceName}, selectedLanguage={Language}", id, text.Length, voices?.Count ?? 0, narrator?.VoiceId ?? "<none>", narrator?.Name ?? "<none>", narrator?.Language ?? "<none>");

                if (narrator == null)
                {
                    _logger.LogWarning("No TTS voices available when requesting TTS for story {Id}", id);
                    return BadRequest("No TTS voice available");
                }

                // Prepare a cache folder under wwwroot/tts and a deterministic filename
                var webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                var ttsDir = Path.Combine(webRoot, "tts");
                if (!Directory.Exists(ttsDir)) Directory.CreateDirectory(ttsDir);

                string SanitizeFilename(string s)
                {
                    if (string.IsNullOrEmpty(s)) return "unknown";
                    var invalid = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
                    var regex = new Regex($"[{Regex.Escape(invalid)}]");
                    return regex.Replace(s, "_");
                }

                var voiceId = narrator.VoiceId ?? narrator.Name ?? "voice";
                var voiceSafe = SanitizeFilename(voiceId);
                var fileName = $"story_{id}_{voiceSafe}_dett.wav";
                var filePath = Path.Combine(ttsDir, fileName);

                // If we've already generated a detail TTS file for this story+voice, return it
                if (System.IO.File.Exists(filePath))
                {
                    try
                    {
                        var bytes = await System.IO.File.ReadAllBytesAsync(filePath);
                        var b64 = Convert.ToBase64String(bytes);
                        var url = $"/tts/{fileName}";
                        _logger.LogInformation("Returning cached TTS file for story {Id}: {File}", id, fileName);
                        return new JsonResult(new { audio_url = url, audio_base64 = b64 });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed reading cached TTS file {File} for story {Id}", fileName, id);
                        // fall through to regenerate
                    }
                }

                var res = await _tts.SynthesizeAsync(voiceId, text, narrator.Language, null);
                if (res == null)
                {
                    _logger.LogError("TTS synthesis returned null for story {Id} using voice {VoiceId}", id, voiceId);
                    return StatusCode(500, "TTSSynthesisFailed");
                }

                // Persist synthesized audio (if base64 present) using the _dett naming convention
                if (!string.IsNullOrWhiteSpace(res.AudioBase64))
                {
                    try
                    {
                        var bytes = Convert.FromBase64String(res.AudioBase64);
                        await System.IO.File.WriteAllBytesAsync(filePath, bytes);
                        var url = $"/tts/{fileName}";
                        _logger.LogInformation("Saved TTS file for story {Id} at {File}", id, fileName);
                        return new JsonResult(new { audio_url = url, audio_base64 = res.AudioBase64 });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed saving TTS file {File} for story {Id}", fileName, id);
                        // fallback to returning the synthesis result as-is
                    }
                }

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
