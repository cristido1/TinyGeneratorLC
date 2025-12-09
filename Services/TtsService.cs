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
        private static readonly JsonSerializerOptions VoiceJsonOptions = new() { PropertyNameCaseInsensitive = true };

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
                    var payload = await resp.Content.ReadAsStringAsync();
                    var list = ParseVoicesPayload(payload);
                    if (list != null && list.Count > 0) return list;
                }
                catch
                {
                    // ignore and try next
                }
        }

        return new List<VoiceInfo>();
    }

    public async Task<byte[]> DownloadAudioAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("url is required", nameof(url));

        try
        {
            return await _http.GetByteArrayAsync(url);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TtsService] Failed to download audio from {url}: {ex}");
            throw;
        }
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

                    if (resp.IsSuccessStatusCode)
                    {
                        // Handle JSON or raw audio responses
                        var media = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
                        if (media.StartsWith("application/json") || media.Contains("json"))
                        {
                            var result = await resp.Content.ReadFromJsonAsync<SynthesisResult>();
                            if (result != null) return result;
                            throw new InvalidOperationException("TTS response JSON vuoto/non interpretabile");
                        }

                        if (media.StartsWith("audio/") || media == "application/octet-stream" || string.IsNullOrEmpty(media))
                        {
                            // Treat body as raw audio bytes -> return base64
                            var bytes = await resp.Content.ReadAsByteArrayAsync();
                            var b64 = Convert.ToBase64String(bytes);
                            return new SynthesisResult { AudioBase64 = b64 };
                        }

                        throw new InvalidOperationException($"TTS risposta con media type non gestito: {media}");
                    }

                    // Only probe the next endpoint if the current one is clearly missing (404/405)
                    if (resp.StatusCode != System.Net.HttpStatusCode.NotFound &&
                        resp.StatusCode != System.Net.HttpStatusCode.MethodNotAllowed)
                    {
                        throw new InvalidOperationException($"TTS endpoint {path} ha risposto {(int)resp.StatusCode} {resp.StatusCode}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TtsService] Exception posting to {path}: {ex.Message}");
                    if (ex is InvalidOperationException)
                    {
                        throw;
                    }
                }
            }

            return null;
        }

    private static List<VoiceInfo>? ParseVoicesPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<VoiceInfo>>(payload, VoiceJsonOptions);
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("voices", out var voicesElement))
                {
                    return JsonSerializer.Deserialize<List<VoiceInfo>>(voicesElement.GetRawText(), VoiceJsonOptions);
                }

                if (root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
                {
                    return JsonSerializer.Deserialize<List<VoiceInfo>>(dataElement.GetRawText(), VoiceJsonOptions);
                }

                var list = new List<VoiceInfo>();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        var voice = prop.Value.Deserialize<VoiceInfo>(VoiceJsonOptions) ?? new VoiceInfo();
                        if (string.IsNullOrWhiteSpace(voice.Id))
                        {
                            voice.Id = prop.Name;
                        }
                        list.Add(voice);
                    }
                }

                if (list.Count > 0)
                    return list;
            }
        }
        catch
        {
            // Ignore parsing errors
        }

        return null;
    }
    }
}
