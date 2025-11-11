using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
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

        // POST /synthesize (body: { voice, text, sentiment? }) -> returns SynthesisResult
        public async Task<SynthesisResult?> SynthesizeAsync(string voiceId, string text, string? sentiment = null)
        {
            if (string.IsNullOrWhiteSpace(voiceId)) throw new ArgumentException("voiceId required", nameof(voiceId));
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text required", nameof(text));

            var payload = new Dictionary<string, object?>
            {
                ["voice"] = voiceId,
                ["text"] = text
            };
            if (!string.IsNullOrWhiteSpace(sentiment)) payload["sentiment"] = sentiment;

            // try common endpoints
            var candidates = new[] { "/synthesize", "/v1/synthesize", "/api/synthesize" };

            foreach (var path in candidates)
            {
                try
                {
                    var resp = await _http.PostAsJsonAsync(path, payload);
                    if (!resp.IsSuccessStatusCode) continue;
                    var result = await resp.Content.ReadFromJsonAsync<SynthesisResult>();
                    if (result != null) return result;
                }
                catch
                {
                    // ignore and try next
                }
            }

            return null;
        }
    }
}
