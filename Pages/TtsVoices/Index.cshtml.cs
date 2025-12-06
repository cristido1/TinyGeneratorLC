using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.TtsVoices
{
    public class IndexModel : PageModel
    {
        private readonly DatabaseService _db;
        private readonly TtsService _tts;

        public List<TtsVoice> Voices { get; set; } = new();

        public IndexModel(DatabaseService db, TtsService tts)
        {
            _db = db;
            _tts = tts;
        }

        public void OnGet()
        {
            try
            {
                Voices = _db.ListTtsVoices() ?? new List<TtsVoice>();
            }
            catch
            {
                Voices = new List<TtsVoice>();
            }
        }

        public async Task<IActionResult> OnPostRefreshAsync()
        {
            try
            {
                // Use the detailed version that returns newly inserted voice ids
                var syncResult = await _db.AddOrUpdateTtsVoicesAsyncDetailed(_tts);
                var voices = _db.ListTtsVoices() ?? new List<TtsVoice>();
                return new JsonResult(new
                {
                    added = syncResult.AddedIds.Count,
                    addedIds = syncResult.AddedIds,
                    updated = syncResult.UpdatedIds.Count,
                    updatedIds = syncResult.UpdatedIds,
                    errors = syncResult.Errors,
                    voices
                });
            }
            catch (Exception ex)
            {
                return new JsonResult(new { error = ex.Message }) { StatusCode = 500 };
            }
        }

        // Server-side list endpoint for DataTables
        public IActionResult OnGetList()
        {
            try
            {
                var all = _db.ListTtsVoices() ?? new List<TtsVoice>();
                var total = all.Count;

                var draw = 0; int.TryParse(Request.Query.ContainsKey("draw") ? Request.Query["draw"].ToString() : null, out draw);
                var start = 0; int.TryParse(Request.Query.ContainsKey("start") ? Request.Query["start"].ToString() : null, out start);
                var length = 25; int.TryParse(Request.Query.ContainsKey("length") ? Request.Query["length"].ToString() : null, out length);
                var search = Request.Query.ContainsKey("search[value]") ? Request.Query["search[value]"].ToString() : string.Empty;

                IEnumerable<TtsVoice> filtered = all;
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var q = search.Trim();
                    filtered = filtered.Where(v => (v.Name ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase)
                        || (v.VoiceId ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase)
                        || (v.Model ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase)
                        || (v.Language ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase));
                }

                var recordsFiltered = filtered.Count();

                var orderCol = Request.Query.ContainsKey("order[0][column]") ? int.Parse(Request.Query["order[0][column]"]) : 0;
                var orderDir = Request.Query.ContainsKey("order[0][dir]") ? Request.Query["order[0][dir]"].ToString() : "asc";

                // Client table includes an initial Actions column at index 0, then Name at index 1 etc.
                string colName = orderCol switch
                {
                    1 => "Name",
                    2 => "VoiceId",
                    3 => "Model",
                    4 => "Language",
                    5 => "Gender",
                    6 => "Age",
                    7 => "Confidence",
                    8 => "Score",
                    9 => "Tags",
                    10 => "TemplateWav",
                    11 => "TemplateWav",
                    12 => "Archetype",
                    13 => "Notes",
                    14 => "CreatedAt",
                    15 => "UpdatedAt",
                    _ => "Name"
                };

                if (orderDir == "asc") filtered = filtered.OrderBy(v => GetPropValue(v, colName)); else filtered = filtered.OrderByDescending(v => GetPropValue(v, colName));

                var page = filtered.Skip(start).Take(length).ToList();

                // include an initial placeholder for the "Actions" column so client-side column indices remain stable
                var data = page.Select(v => new object[] {
                    string.Empty,
                    v.Name ?? string.Empty,
                    v.VoiceId ?? string.Empty,
                    v.Model ?? string.Empty,
                    v.Language ?? string.Empty,
                    v.Gender ?? string.Empty,
                    v.Age ?? string.Empty,
                    v.Confidence?.ToString("0.##") ?? string.Empty,
                    v.Score?.ToString("0.##") ?? string.Empty,
                    v.Tags ?? string.Empty,
                    v.TemplateWav ?? string.Empty,
                    v.TemplateWav ?? string.Empty,
                    v.Archetype ?? string.Empty,
                    v.Notes ?? string.Empty,
                    v.CreatedAt ?? string.Empty,
                    v.UpdatedAt ?? string.Empty,
                    v.Id
                }).ToList();

                return new JsonResult(new { draw = draw, recordsTotal = total, recordsFiltered = recordsFiltered, data = data });
            }
            catch (Exception ex)
            {
                return new JsonResult(new { error = ex.Message }) { StatusCode = 500 };
            }
        }

        private static object? GetPropValue(TtsVoice v, string name)
        {
            return name switch
            {
                "Name" => v.Name,
                "VoiceId" => v.VoiceId,
                "Model" => v.Model,
                "Language" => v.Language,
                "Gender" => v.Gender,
                "Age" => v.Age,
                "Confidence" => v.Confidence ?? 0,
                "Tags" => v.Tags,
                "Score" => v.Score,
                "TemplateWav" => v.TemplateWav,
                "Archetype" => v.Archetype,
                "Notes" => v.Notes,
                "CreatedAt" => v.CreatedAt,
                "UpdatedAt" => v.UpdatedAt,
                _ => v.Name
            };
        }

        // Serve sample audio bytes for a given voice id
        public async Task<IActionResult> OnGetSampleAsync(string voiceId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(voiceId)) return NotFound();
                var v = _db.GetTtsVoiceByVoiceId(voiceId);
                if (v == null) return NotFound();

                // If template_wav filename exists and the file is present, serve it. Otherwise, synthesize a sample
                var templateFile = v.TemplateWav;
                var baseDir = AppContext.BaseDirectory ?? Directory.GetCurrentDirectory();
                string? possible = null;
                if (!string.IsNullOrWhiteSpace(templateFile))
                {
                    // template_wav contains filename (e.g. my_voice_20251101.wav). Look under wwwroot/data_voices_samples
                    var candidate = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "data_voices_samples", templateFile.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (System.IO.File.Exists(candidate)) possible = candidate; else possible = null;
                }

                if (possible != null && System.IO.File.Exists(possible))
                {
                    var bytes = await System.IO.File.ReadAllBytesAsync(possible);
                    var ext = Path.GetExtension(possible).ToLowerInvariant();
                    var mime = ext switch { ".wav" => "audio/wav", ".mp3" => "audio/mpeg", ".ogg" => "audio/ogg", _ => "application/octet-stream" };
                    return File(bytes, mime);
                }

                // Need to generate sample using voice.Notes (fallback to name)
                var textToRead = !string.IsNullOrWhiteSpace(v.Notes) ? v.Notes : (v.Name ?? v.VoiceId ?? "Sample");
                if (string.IsNullOrWhiteSpace(textToRead)) return NotFound();

                // Call TTS to synthesize sample. Use voice's Model if present (pass model:voiceId) so the TTS service picks correct model/speaker.
                var voiceParam = !string.IsNullOrWhiteSpace(v.Model) ? (v.Model + ":" + (v.VoiceId ?? string.Empty)) : (v.VoiceId ?? string.Empty);
                var synth = await _tts.SynthesizeAsync(voiceParam, textToRead, v.Language);
                if (synth == null) return StatusCode(500, "Synthesis failed or returned no audio");

                byte[] audioBytes = Array.Empty<byte>();
                try
                {
                    if (!string.IsNullOrWhiteSpace(synth.AudioBase64))
                    {
                        audioBytes = Convert.FromBase64String(synth.AudioBase64);
                    }
                    else if (!string.IsNullOrWhiteSpace(synth.AudioUrl))
                    {
                        audioBytes = await _tts.DownloadAudioAsync(synth.AudioUrl);
                    }
                }
                catch (Exception ex)
                {
                    return StatusCode(500, ex.Message);
                }

                if (audioBytes == null || audioBytes.Length == 0) return StatusCode(500, "No audio bytes received");

                // Ensure storage folder under wwwroot so file is easily served
                var samplesDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "data_voices_samples");
                try { Directory.CreateDirectory(samplesDir); } catch { }

                string safeName = string.Concat((v.VoiceId ?? v.Name ?? "voice").Where(c => char.IsLetterOrDigit(c) || c=='_'|| c=='-')).Trim();
                if (string.IsNullOrWhiteSpace(safeName)) safeName = "voice";
                var ts = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                var filename = safeName + "_" + ts + ".wav";
                var fullPath = Path.Combine(samplesDir, filename);
                await System.IO.File.WriteAllBytesAsync(fullPath, audioBytes);

                // Update DB with the filename in template_wav (per new schema)
                var filenameOnly = filename; // keep only filename
                _db.UpdateTtsVoiceTemplateWavById(v.Id, filenameOnly);

                // Return the audio bytes
                return File(audioBytes, "audio/wav");

                return NotFound();
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }

        // Delete a voice record by DB id (AJAX)
        public async Task<IActionResult> OnPostDeleteAsync(int id)
        {
            try
            {
                if (id <= 0) return new JsonResult(new { error = "Invalid id" }) { StatusCode = 400 };
                _db.DeleteTtsVoiceById(id);
                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                return new JsonResult(new { error = ex.Message }) { StatusCode = 500 };
            }
        }

        // Adjust score by delta (+1/-1 or any double) for a given voice id
        public IActionResult OnPostAdjustScore(int id, double delta)
        {
            try
            {
                if (id <= 0) return new JsonResult(new { error = "Invalid id" }) { StatusCode = 400 };
                var v = _db.GetTtsVoiceById(id);
                if (v == null) return new JsonResult(new { error = "Voice not found" }) { StatusCode = 404 };

                double newScore = (v.Score ?? 0) + delta;
                // optional: clamp score to sensible range (e.g., 0..10)
                if (newScore < 0) newScore = 0;
                // if you want an upper bound, change here; leave unbounded for now

                _db.UpdateTtsVoiceScoreById(id, newScore);
                return new JsonResult(new { success = true, id = id, score = newScore });
            }
            catch (Exception ex)
            {
                return new JsonResult(new { error = ex.Message }) { StatusCode = 500 };
            }
        }

        // Regenerate sample for a voice by id (AJAX). This forces synth and updates template_wav.
        public async Task<IActionResult> OnPostRegenerateAsync(int id)
        {
            try
            {
                if (id <= 0) return new JsonResult(new { error = "Invalid id" }) { StatusCode = 400 };
                var v = _db.GetTtsVoiceById(id);
                if (v == null) return new JsonResult(new { error = "Voice not found" }) { StatusCode = 404 };

                var textToRead = !string.IsNullOrWhiteSpace(v.Notes) ? v.Notes : (v.Name ?? v.VoiceId ?? "Sample");
                if (string.IsNullOrWhiteSpace(textToRead)) return new JsonResult(new { error = "No sample text available" }) { StatusCode = 400 };

                var voiceParam = !string.IsNullOrWhiteSpace(v.Model) ? (v.Model + ":" + (v.VoiceId ?? string.Empty)) : (v.VoiceId ?? string.Empty);
                var synth = await _tts.SynthesizeAsync(voiceParam, textToRead, v.Language);
                if (synth == null) return new JsonResult(new { error = "Synthesis failed" }) { StatusCode = 500 };

                byte[] audioBytes = Array.Empty<byte>();
                try
                {
                    if (!string.IsNullOrWhiteSpace(synth.AudioBase64)) audioBytes = Convert.FromBase64String(synth.AudioBase64);
                    else if (!string.IsNullOrWhiteSpace(synth.AudioUrl)) audioBytes = await _tts.DownloadAudioAsync(synth.AudioUrl);
                }
                catch (Exception ex)
                {
                    return new JsonResult(new { error = ex.Message }) { StatusCode = 500 };
                }

                if (audioBytes == null || audioBytes.Length == 0) return new JsonResult(new { error = "No audio bytes received" }) { StatusCode = 500 };

                var samplesDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "data_voices_samples");
                try { Directory.CreateDirectory(samplesDir); } catch { }
                string safeName = string.Concat((v.VoiceId ?? v.Name ?? "voice").Where(c => char.IsLetterOrDigit(c) || c=='_'|| c=='-')).Trim();
                if (string.IsNullOrWhiteSpace(safeName)) safeName = "voice";
                var ts = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                var filename = safeName + "_" + ts + ".wav";
                var fullPath = Path.Combine(samplesDir, filename);
                await System.IO.File.WriteAllBytesAsync(fullPath, audioBytes);

                _db.UpdateTtsVoiceTemplateWavById(v.Id, filename);

                return new JsonResult(new { success = true, filename });
            }
            catch (Exception ex)
            {
                return new JsonResult(new { error = ex.Message }) { StatusCode = 500 };
            }
        }
    }
}
