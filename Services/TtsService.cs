using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace TinyGenerator.Services
{
    // Options for the TTS service; will use HOST/PORT environment values from Program.cs when registering
    public class TtsOptions
    {
        public string Host { get; set; } = "0.0.0.0";
        public int Port { get; set; } = 8004;
        public string BaseUrl => $"http://{Host}:{Port}";
        // Timeout in seconds for TTS HTTP requests (configurable via TTS_TIMEOUT_SECONDS env var)
        public int TimeoutSeconds { get; set; } = 300;
        public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);
    }

    // Minimal DTOs matching typical FastAPI responses described by the user
    public class VoiceInfo
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("language")] public string? Language { get; set; }
        [JsonPropertyName("gender")] public string? Gender { get; set; }
        // Additional fields that help to decide assignment (confidence/age/style/etc.)
        [JsonPropertyName("age")] public string? Age { get; set; }
        [JsonPropertyName("confidence")] public double? Confidence { get; set; }
        [JsonPropertyName("tags")] public Dictionary<string,string>? Tags { get; set; }
    }

    public class SynthesisResult
    {
        // Service may return an url or raw base64 audio, accept both
        [JsonPropertyName("audio_url")] public string? AudioUrl { get; set; }
        [JsonPropertyName("audio_base64")] public string? AudioBase64 { get; set; }
        [JsonPropertyName("duration_seconds")] public double? DurationSeconds { get; set; }
        [JsonPropertyName("sentiment")] public string? Sentiment { get; set; }
        [JsonPropertyName("meta")] public Dictionary<string,string>? Meta { get; set; }
    }

    public sealed class TtsService
    {
        private readonly HttpClient _http;
        private readonly TtsOptions _options;

        public TtsService(HttpClient http, TtsOptions? options = null)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _options = options ?? new TtsOptions();
        }

        // GET /voices  => returns list of voices and evaluation fields
        public async Task<List<VoiceInfo>> GetVoicesAsync()
        {
            // try a few common paths used by FastAPI-based TTS services
            var candidates = new[] { 
                "/voices", 
                "/v1/voices", 
                "/api/voices" 
            };

            foreach (var path in candidates)
            {
                try
                {
                    var resp = await _http.GetAsync(path);
                    if (!resp.IsSuccessStatusCode) continue;
                    var list = await resp.Content.ReadFromJsonAsync<List<VoiceInfo>>();
                    if (list != null) return list;
                }
                catch
                {
                    // ignore and try next
                }
            }

            return new List<VoiceInfo>();
        }

        // POST /synthesize (body: { voice, text, language?, sentiment? }) -> returns SynthesisResult
        // If no language is provided, default to Italian ("it").
        public async Task<SynthesisResult?> SynthesizeAsync(string voiceId, string text, string? language = null, string? sentiment = null)
        {
            if (string.IsNullOrWhiteSpace(voiceId)) throw new ArgumentException("voiceId required", nameof(voiceId));
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text required", nameof(text));

            var payload = new Dictionary<string, object?>
            {
                ["voice"] = voiceId,
                ["text"] = text
            };
            // Ensure we always send a language; default to Italian if not specified
            if (string.IsNullOrWhiteSpace(language))
            {
                language = "it";
            }
            payload["language"] = language;
            if (!string.IsNullOrWhiteSpace(sentiment)) payload["sentiment"] = sentiment;

            // try common endpoints
            var candidates = new[] { "/synthesize", "/v1/synthesize", "/api/synthesize" };

            foreach (var path in candidates)
            {
                try
                {
                    var payloadJson = JsonSerializer.Serialize(payload);
                    Console.WriteLine($"[TtsService] Attempt POST {path} -> payload: {payloadJson}");

                    var resp = await _http.PostAsJsonAsync(path, payload);

                    var respText = "";
                    try
                    {
                        respText = await resp.Content.ReadAsStringAsync();
                    }
                    catch { /* ignore read errors */ }

                    Console.WriteLine($"[TtsService] Response {path} -> Status: {(int)resp.StatusCode} {resp.StatusCode}; BodyLen={respText?.Length ?? 0}");
                    if (!string.IsNullOrWhiteSpace(respText) && respText.Length < 2000)
                    {
                        Console.WriteLine($"[TtsService] Response body: {respText}");
                    }

                    if (!resp.IsSuccessStatusCode) continue;

                    // Handle JSON or raw audio responses
                    var media = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
                    if (media.StartsWith("application/json") || media.Contains("json"))
                    {
                        var result = await resp.Content.ReadFromJsonAsync<SynthesisResult>();
                        if (result != null) return result;
                    }
                    else if (media.StartsWith("audio/") || media == "application/octet-stream" || string.IsNullOrEmpty(media))
                    {
                        // Treat body as raw audio bytes -> return base64
                        try
                        {
                            var bytes = await resp.Content.ReadAsByteArrayAsync();
                            var b64 = Convert.ToBase64String(bytes);
                            return new SynthesisResult { AudioBase64 = b64 };
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[TtsService] Error reading binary response for {path}: {ex}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TtsService] Exception posting to {path}: {ex}");
                    // ignore and try next
                }
            }

            // If initial attempts fail, try alternative payload shape used by some TTS servers
            // e.g. { model: "voice_templates", speaker: "<id>", text: "...", language: "it" }
            try
            {
                var altPayload = new Dictionary<string, object?>
                {
                    ["text"] = text,
                    ["language"] = language
                };

                // If voiceId looks like 'model:speaker' split it, otherwise use as speaker
                if (voiceId.Contains(":"))
                {
                    var parts = voiceId.Split(new[] { ':' }, 2);
                    altPayload["model"] = parts[0];
                    altPayload["speaker"] = parts[1];
                }
                else
                {
                    altPayload["model"] = "voice_templates";
                    altPayload["speaker"] = voiceId;
                }

                var altJson = JsonSerializer.Serialize(altPayload);
                Console.WriteLine($"[TtsService] Attempting alt payload for synth: {altJson}");

                foreach (var path in candidates)
                {
                    try
                    {
                        var resp = await _http.PostAsJsonAsync(path, altPayload);
                        var media = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
                        Console.WriteLine($"[TtsService] Alt Response {path} -> Status: {(int)resp.StatusCode} {resp.StatusCode}; Media={media}");
                        if (!resp.IsSuccessStatusCode) continue;

                        if (media.Contains("json"))
                        {
                            var result = await resp.Content.ReadFromJsonAsync<SynthesisResult>();
                            if (result != null) return result;
                        }
                        else
                        {
                            var bytes = await resp.Content.ReadAsByteArrayAsync();
                            return new SynthesisResult { AudioBase64 = Convert.ToBase64String(bytes) };
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[TtsService] Alt payload exception posting to {path}: {ex}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TtsService] Exception building/sending alt payload: {ex}");
            }

            return null;
        }
    }
}
