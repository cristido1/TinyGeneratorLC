using Microsoft.SemanticKernel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.ComponentModel;
using System;
using System.Text;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    [Description("Client for localTTS (local FastAPI TTS service)")]
    public class TtsApiSkill : ITinySkill
    {
        private readonly HttpClient _http;
        private readonly ICustomLogger? _logger;
        private int? _modelId;
        private string? _modelName;
        private DateTime? _lastCalled;
        private string? _lastFunction;

        public string? LastSynthFormat { get; set; }

        // ITinySkill implementation
        int? ITinySkill.ModelId { get => _modelId; set => _modelId = value; }
        string? ITinySkill.ModelName { get => _modelName; set => _modelName = value; }
        int? ITinySkill.AgentId => null;
        string? ITinySkill.AgentName => null;
        DateTime? ITinySkill.LastCalled { get => _lastCalled; set => _lastCalled = value; }
        string? ITinySkill.LastFunction { get => _lastFunction; set => _lastFunction = value; }
        ICustomLogger? ITinySkill.Logger { get => _logger; set { } }

        public TtsApiSkill(HttpClient httpClient, ICustomLogger? logger = null)
        {
            _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger;
            _http.BaseAddress = new Uri("http://localhost:8004/");
        }

        [KernelFunction("check_health"), Description("Checks the health of the TTS service.")]
        public async Task<string> CheckHealthAsync()
        {
            ((ITinySkill)this).LogFunctionCall("check_health");
            ((ITinySkill)this).LastCalled = DateTime.UtcNow;
            ((ITinySkill)this).LastFunction = nameof(CheckHealthAsync);
            var r = await _http.GetAsync("/health");
            if (r.IsSuccessStatusCode)
            {
                try { return await r.Content.ReadAsStringAsync(); } catch { return "{\"status\":\"ok\"}"; }
            }
            return $"TTS service error: {r.StatusCode}";
        }

        [KernelFunction("list_voices"), Description("Lists available TTS voices and templates.")]
        public async Task<string> ListVoicesAsync()
        {
            ((ITinySkill)this).LogFunctionCall("list_voices");
            ((ITinySkill)this).LastCalled = DateTime.UtcNow;
            ((ITinySkill)this).LastFunction = nameof(ListVoicesAsync);
            return await _http.GetStringAsync("/voices");
        }

        [KernelFunction("patch_transformers"), Description("Attempt to apply runtime patch to transformers on the server.")]
        public async Task<string> PatchTransformersAsync()
        {
            ((ITinySkill)this).LogFunctionCall("patch_transformers");
            ((ITinySkill)this).LastCalled = DateTime.UtcNow;
            ((ITinySkill)this).LastFunction = nameof(PatchTransformersAsync);
            var r = await _http.PostAsync("/patch_transformers", null);
            return await SafeReadContentAsync(r) ?? string.Empty;
        }

        /// <summary>
        /// Synthesizes text to audio. Returns raw audio bytes (wav) when the server responds with audio content.
        /// If the server responds with JSON containing "audio_base64" (format: "base64"), the base64 is decoded and returned as bytes.
        /// When the server returns a JSON diagnostic, the JSON bytes are returned.
        /// </summary>
        [KernelFunction("synthesize"), Description("Synthesize text to audio. Returns audio bytes (wav) or JSON bytes when format=base64.)")]
        public async Task<byte[]> SynthesizeAsync(
            [Description("Text to synthesize")] string text,
            [Description("Model to use (e.g. voice_templates or a Coqui model string)")] string model = "voice_templates",
            [Description("Voice alias or template id")] string? voice = null,
            [Description("Speaker id or name, or template id when using voice_templates")] object? speaker = null,
            [Description("Speaker index for multi-speaker models")] int speaker_idx = -1,
            [Description("Speaker wav path or base64 string to be used as reference")] string? speaker_wav = null,
            [Description("Language code, e.g. 'it'")] string? language = null,
            [Description("Emotion, e.g. 'neutral'")] string? emotion = null,
            [Description("Speed factor, e.g. 0.8 or 1.2")] double speed = 1.0,
            [Description("format: 'wav' (default) or 'base64'")] string format = "wav")
        {
            ((ITinySkill)this).LogFunctionCall("synthesize");
            ((ITinySkill)this).LastCalled = DateTime.UtcNow;
            ((ITinySkill)this).LastFunction = nameof(SynthesizeAsync);
            LastSynthFormat = format;

            var payload = new System.Collections.Generic.Dictionary<string, object>
            {
                ["text"] = text ?? string.Empty,
                ["model"] = model ?? "voice_templates",
                ["format"] = format ?? "wav"
            };

            if (!string.IsNullOrWhiteSpace(voice)) payload["voice"] = voice!;
            if (speaker != null) payload["speaker"] = speaker!;
            if (speaker_idx >= 0) payload["speaker_idx"] = speaker_idx;
            if (!string.IsNullOrWhiteSpace(speaker_wav)) payload["speaker_wav"] = speaker_wav!;
            if (!string.IsNullOrWhiteSpace(language)) payload["language"] = language!;
            if (!string.IsNullOrWhiteSpace(emotion)) payload["emotion"] = emotion!;
            if (Math.Abs(speed - 1.0) > 1e-9) payload["speed"] = speed;

            var response = await _http.PostAsJsonAsync("/synthesize", payload);
            if (!response.IsSuccessStatusCode)
            {
                var err = await SafeReadContentAsync(response);
                throw new HttpRequestException($"TTS synthesis failed (status {response.StatusCode}). Server: {err}");
            }

            // If the server returns audio content, return raw bytes
            var media = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(media) && media.IndexOf("audio", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return await response.Content.ReadAsByteArrayAsync();
            }

            // Otherwise read as string and try to extract base64 or return JSON bytes
            var body = await response.Content.ReadAsStringAsync();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("audio_base64", out var a))
                    {
                        var b64 = a.GetString();
                        if (!string.IsNullOrWhiteSpace(b64))
                        {
                            return Convert.FromBase64String(b64);
                        }
                    }
                }
            }
            catch
            {
                // not JSON or parse failed - fallthrough
            }

            // Return the raw response as UTF8 bytes (useful for diagnostics)
            return Encoding.UTF8.GetBytes(body ?? string.Empty);
        }

        private static async Task<string?> SafeReadContentAsync(HttpResponseMessage resp)
        {
            try { return await resp.Content.ReadAsStringAsync(); } catch { return null; }
        }

        [KernelFunction("describe"), Description("Describes the available TTS functions.")]
        public string Describe()
        {
            ((ITinySkill)this).LogFunctionCall("describe");
            return "Available functions: check_health(), list_voices(), patch_transformers(), synthesize(text, model, voice, speaker, speaker_idx, speaker_wav, language, emotion, speed, format). Returns audio bytes (wav) when server returns audio, or JSON bytes when format=base64.";
        }
    }
}
