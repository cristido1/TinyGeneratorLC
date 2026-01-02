using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using System.Linq;
using TinyGenerator.Services;
using TinyGenerator.Models;
using System.IO;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

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
        public string? StoryRawText { get; set; }
        public string? StoryRevisedText { get; set; }
        public string? StoryTaggedText { get; set; }

        public bool HasRevisionDiff { get; set; }
        public string? RawDiffHtml { get; set; }
        public string? RevisedDiffHtml { get; set; }

        public void OnGet(long id)
        {
            Id = id;
            Story = _stories.GetStoryById(id);
            if (Story != null)
            {
                StoryRawText = Story.StoryRaw;
                StoryRevisedText = Story.StoryRevised;
                StoryTaggedText = Story.StoryTagged;
            }
            Evaluations = _stories.GetEvaluationsForStory(id);

            BuildRevisionDiffIfAvailable();
        }

        private void BuildRevisionDiffIfAvailable()
        {
            var raw = StoryRawText ?? string.Empty;
            var revised = StoryRevisedText ?? string.Empty;

            if (string.IsNullOrWhiteSpace(revised))
            {
                HasRevisionDiff = false;
                RawDiffHtml = null;
                RevisedDiffHtml = null;
                return;
            }

            if (string.Equals(raw, revised, System.StringComparison.Ordinal))
            {
                HasRevisionDiff = false;
                RawDiffHtml = null;
                RevisedDiffHtml = null;
                return;
            }

            // Ignore whitespace-only differences (spaces/newlines/tabs).
            // If the texts are equal after normalization, do not show any diff.
            if (string.Equals(NormalizeWhitespaceForComparison(raw), NormalizeWhitespaceForComparison(revised), System.StringComparison.Ordinal))
            {
                HasRevisionDiff = false;
                RawDiffHtml = null;
                RevisedDiffHtml = null;
                return;
            }

            try
            {
                var diffBuilder = new SideBySideDiffBuilder(new Differ());
                // For prose, line-break and whitespace changes create a lot of noise.
                // Normalize and wrap before diffing to improve alignment of equal text.
                var rawForDiff = PrepareTextForDiff(raw);
                var revisedForDiff = PrepareTextForDiff(revised);
                var model = diffBuilder.BuildDiffModel(rawForDiff, revisedForDiff, ignoreWhitespace: true);

                RawDiffHtml = RenderDiffSide(model.OldText, highlight: HighlightSide.Old);
                RevisedDiffHtml = RenderDiffSide(model.NewText, highlight: HighlightSide.New);
                HasRevisionDiff = true;
            }
            catch (System.Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute revision diff for story {Id}", Id);
                HasRevisionDiff = false;
                RawDiffHtml = null;
                RevisedDiffHtml = null;
            }
        }

        private static string NormalizeWhitespaceForComparison(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            // Replace any whitespace sequence (including newlines/tabs) with a single space.
            // This makes the comparison ignore spacing and line breaks.
            var normalized = Regex.Replace(text, "\\s+", " ");
            return normalized.Trim();
        }

        private static string PrepareTextForDiff(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            // Ignore whitespace differences by collapsing all whitespace (spaces/newlines/tabs)
            // to a single space, then wrapping for stable diff alignment.
            var normalized = NormalizeWhitespaceForComparison(text);
            if (string.IsNullOrEmpty(normalized)) return string.Empty;

            var wrapped = new StringBuilder(normalized.Length + 128);
            foreach (var w in WrapLine(normalized, maxWidth: 120))
            {
                wrapped.Append(w);
                wrapped.Append('\n');
            }

            return wrapped.ToString();
        }

        private static IEnumerable<string> WrapLine(string line, int maxWidth)
        {
            if (string.IsNullOrEmpty(line)) yield break;
            if (maxWidth <= 10)
            {
                yield return line;
                yield break;
            }

            // At this point the input is already normalized (single spaces), but keep it defensive.
            line = Regex.Replace(line, "\\s+", " ").Trim();

            var remaining = line;
            while (remaining.Length > maxWidth)
            {
                var cut = remaining.LastIndexOf(' ', maxWidth);
                if (cut <= 0)
                {
                    // No space found (very long token). Hard cut.
                    yield return remaining.Substring(0, maxWidth);
                    remaining = remaining.Substring(maxWidth);
                    continue;
                }

                yield return remaining.Substring(0, cut);
                remaining = remaining.Substring(cut + 1);
            }

            if (remaining.Length > 0)
            {
                yield return remaining;
            }
        }

        private enum HighlightSide
        {
            Old,
            New
        }

        private static string RenderDiffSide(DiffPaneModel pane, HighlightSide highlight)
        {
            var sb = new StringBuilder();
            var encoder = HtmlEncoder.Default;

            foreach (var line in pane.Lines)
            {
                if (line.Type == ChangeType.Imaginary)
                {
                    sb.Append('\n');
                    continue;
                }

                // For modified lines, prefer sub-piece rendering (more granular).
                if (line.Type == ChangeType.Modified && line.SubPieces != null && line.SubPieces.Count > 0)
                {
                    foreach (var piece in line.SubPieces)
                    {
                        sb.Append(RenderPiece(piece.Text ?? string.Empty, piece.Type, highlight, encoder));
                    }
                    sb.Append('\n');
                    continue;
                }

                sb.Append(RenderPiece(line.Text ?? string.Empty, line.Type, highlight, encoder));
                sb.Append('\n');
            }

            return sb.ToString();
        }

        private static string RenderPiece(string text, ChangeType type, HighlightSide highlight, HtmlEncoder encoder)
        {
            var safe = encoder.Encode(text);

            // Orange (warning) for deletions/changes in RAW (old)
            if (highlight == HighlightSide.Old)
            {
                if (type == ChangeType.Deleted || type == ChangeType.Modified)
                {
                    return $"<mark class=\"bg-warning-subtle\">{safe}</mark>";
                }

                // Do not emphasize inserted-only pieces in raw
                if (type == ChangeType.Inserted) return string.Empty;
                return safe;
            }

            // Green (success) for insertions/changes in REVISED (new)
            if (type == ChangeType.Inserted || type == ChangeType.Modified)
            {
                return $"<mark class=\"bg-success-subtle\">{safe}</mark>";
            }

            // Do not show deleted-only pieces in revised
            if (type == ChangeType.Deleted) return string.Empty;
            return safe;
        }

        public async Task<IActionResult> OnPostTtsAsync(long id)
        {
            try
            {
                var story = _stories.GetStoryById(id);
                if (story == null) return NotFound();
                var text = !string.IsNullOrWhiteSpace(story.StoryRevised) ? story.StoryRevised! : story.StoryRaw;
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

        public async Task<IActionResult> OnPostMacTtsAsync(long id)
        {
            try
            {
                var story = _stories.GetStoryById(id);
                if (story == null) return NotFound();
                var text = !string.IsNullOrWhiteSpace(story.StoryRevised) ? story.StoryRevised! : story.StoryRaw;
                if (string.IsNullOrWhiteSpace(text)) return BadRequest("No story text");

                // Check if we're on macOS
                if (!OperatingSystem.IsMacOS())
                {
                    return BadRequest("macOS TTS is only available on macOS");
                }

                // Use macOS 'say' command to generate audio file
                var webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                var ttsDir = Path.Combine(webRoot, "tts");
                if (!Directory.Exists(ttsDir)) Directory.CreateDirectory(ttsDir);

                var mp3FileName = $"story_{id}_mac.mp3";
                var mp3FilePath = Path.Combine(ttsDir, mp3FileName);
                var aiffFileName = $"story_{id}_mac.aiff";
                var aiffFilePath = Path.Combine(ttsDir, aiffFileName);

                // If already generated, return it
                if (System.IO.File.Exists(mp3FilePath))
                {
                    var url = $"/tts/{mp3FileName}";
                    _logger.LogInformation("Returning cached macOS TTS file for story {Id}: {File}", id, mp3FileName);
                    return new JsonResult(new { audio_url = url });
                }

                // Save text to temp file (to handle special characters properly)
                var tempTextFile = Path.GetTempFileName();
                await System.IO.File.WriteAllTextAsync(tempTextFile, text);

                try
                {
                    // Run 'say' command with Italian voice to generate AIFF
                    // -v Alice (Italian voice), -f input file, -o output file
                    var sayProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "say",
                            Arguments = $"-v Alice -f \"{tempTextFile}\" -o \"{aiffFilePath}\"",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };

                    _logger.LogInformation("Running macOS 'say' command for story {Id}", id);
                    sayProcess.Start();
                    await sayProcess.WaitForExitAsync();

                    if (sayProcess.ExitCode != 0)
                    {
                        var error = await sayProcess.StandardError.ReadToEndAsync();
                        _logger.LogError("macOS 'say' command failed with exit code {Code}: {Error}", sayProcess.ExitCode, error);
                        return StatusCode(500, "TTS generation failed");
                    }

                    if (!System.IO.File.Exists(aiffFilePath))
                    {
                        _logger.LogError("macOS 'say' command completed but AIFF file not found: {File}", aiffFilePath);
                        return StatusCode(500, "TTS AIFF file not generated");
                    }

                    // Convert AIFF to MP3 using ffmpeg
                    _logger.LogInformation("Converting AIFF to MP3 for story {Id}", id);
                    var ffmpegProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "ffmpeg",
                            Arguments = $"-i \"{aiffFilePath}\" -acodec libmp3lame -ab 128k \"{mp3FilePath}\" -y",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };

                    ffmpegProcess.Start();
                    await ffmpegProcess.WaitForExitAsync();

                    if (ffmpegProcess.ExitCode != 0)
                    {
                        var error = await ffmpegProcess.StandardError.ReadToEndAsync();
                        _logger.LogWarning("ffmpeg conversion failed with exit code {Code}: {Error}", ffmpegProcess.ExitCode, error);
                        // Fall back to returning AIFF if ffmpeg fails
                        var aiffUrl = $"/tts/{aiffFileName}";
                        return new JsonResult(new { audio_url = aiffUrl });
                    }

                    // Delete AIFF file to save space (keep only MP3)
                    if (System.IO.File.Exists(aiffFilePath))
                    {
                        System.IO.File.Delete(aiffFilePath);
                    }

                    if (System.IO.File.Exists(mp3FilePath))
                    {
                        var url = $"/tts/{mp3FileName}";
                        _logger.LogInformation("Generated macOS TTS MP3 file for story {Id}: {File}", id, mp3FileName);
                        return new JsonResult(new { audio_url = url });
                    }
                    else
                    {
                        _logger.LogError("MP3 conversion completed but file not found: {File}", mp3FilePath);
                        return StatusCode(500, "TTS MP3 file not generated");
                    }
                }
                finally
                {
                    // Clean up temp text file
                    if (System.IO.File.Exists(tempTextFile))
                    {
                        System.IO.File.Delete(tempTextFile);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in macOS TTS synthesis for story {Id}", id);
                return StatusCode(500, ex.Message);
            }
        }

        public IActionResult OnPostDeleteEvaluation(long id, long storyId)
        {
            try
            {
                _db.DeleteStoryEvaluationById(id);
                TempData["StatusMessage"] = "Valutazione eliminata.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete evaluation {Id}", id);
                TempData["ErrorMessage"] = "Errore eliminazione valutazione: " + ex.Message;
            }
            return RedirectToPage("/Stories/Details", new { id = storyId });
        }
    }
}
