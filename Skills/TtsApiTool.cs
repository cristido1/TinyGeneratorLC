using System;
using System.Collections.Generic;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    /// <summary>
    /// LangChain tool for TTS (Text-to-Speech) operations via local FastAPI service.
    /// Converted from TtsApiSkill (Semantic Kernel).
    /// </summary>
    public class TtsApiTool : BaseLangChainTool, ITinyTool
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl = "http://0.0.0.0:8004";
        public string? LastSynthFormat { get; set; }

        public int? ModelId { get; set; }
        public string? ModelName { get; set; }
        public int? AgentId { get; set; }
        public string? LastFunctionCalled { get; set; }
        public string? LastFunctionResult { get; set; }

        public TtsApiTool(HttpClient httpClient, ICustomLogger? logger = null) 
            : base("ttsapi", "Client for localTTS (local FastAPI TTS service)", logger)
        {
            _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            // Don't set BaseAddress here - HttpClient may already be in use
        }

        public override Dictionary<string, object> GetSchema()
        {
            return CreateFunctionSchema(
                Name,
                Description,
                new Dictionary<string, object>
                {
                    {
                        "operation",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "The TTS operation: 'check_health', 'list_voices', 'patch_transformers', 'synthesize', 'describe'" }
                        }
                    },
                    {
                        "text",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Text to synthesize (for synthesize operation)" }
                        }
                    },
                    {
                        "model",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Model to use (default: voice_templates)" }
                        }
                    },
                    {
                        "voice",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Voice alias or template id" }
                        }
                    },
                    {
                        "speaker",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Speaker id or name" }
                        }
                    },
                    {
                        "language",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Language code (e.g. 'it')" }
                        }
                    },
                    {
                        "format",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Output format: 'wav' or 'base64' (default: wav)" }
                        }
                    }
                },
                new List<string> { "operation" }
            );
        }

        public override async Task<string> ExecuteAsync(string input)
        {
            try
            {
                var request = ParseInput<TtsApiToolRequest>(input);
                if (request == null)
                    return SerializeResult(new { error = "Invalid input format" });

                CustomLogger?.Log("Info", "TtsApiTool", $"Executing operation: {request.Operation}");

                return request.Operation?.ToLowerInvariant() switch
                {
                    "check_health" => await ExecuteCheckHealthAsync(),
                    "list_voices" => await ExecuteListVoicesAsync(),
                    "patch_transformers" => await ExecutePatchTransformersAsync(),
                    "synthesize" => await ExecuteSynthesizeAsync(request),
                    "describe" => SerializeResult(new { result = "Available operations: check_health(), list_voices(), patch_transformers(), synthesize(text, model, voice, speaker, language, format). Returns audio bytes (wav) when server returns audio, or JSON bytes when format=base64." }),
                    _ => SerializeResult(new { error = $"Unknown operation: {request.Operation}" })
                };
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "TtsApiTool", $"Error executing operation: {ex.Message}", ex.ToString());
                return SerializeResult(new { error = ex.Message });
            }
        }

        private async Task<string> ExecuteCheckHealthAsync()
        {
            try
            {
                var response = await _http.GetAsync($"{_baseUrl}/health");
                if (response.IsSuccessStatusCode)
                {
                    try 
                    { 
                        var content = await response.Content.ReadAsStringAsync();
                        return SerializeResult(new { result = content });
                    }
                    catch
                    { 
                        return SerializeResult(new { result = "{\"status\":\"ok\"}" });
                    }
                }
                return SerializeResult(new { error = $"TTS service error: {response.StatusCode}" });
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private async Task<string> ExecuteListVoicesAsync()
        {
            try
            {
                var content = await _http.GetStringAsync($"{_baseUrl}/voices");
                return SerializeResult(new { result = content });
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private async Task<string> ExecutePatchTransformersAsync()
        {
            try
            {
                var response = await _http.PostAsync($"{_baseUrl}/patch_transformers", null);
                var content = await SafeReadContentAsync(response) ?? string.Empty;
                return SerializeResult(new { result = content });
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private async Task<string> ExecuteSynthesizeAsync(TtsApiToolRequest request)
        {
            try
            {
                // Sanitize text: remove forbidden characters '*' and '#' before sending to TTS
                var textToSynthesize = request.Text ?? string.Empty;
                if (textToSynthesize.IndexOfAny(new[] { '*', '#' }) >= 0)
                {
                    CustomLogger?.Log("Information", "TtsApiTool", "Sanitizing TTS text: removed '*' and '#' characters from payload.");
                    textToSynthesize = textToSynthesize.Replace("*", string.Empty).Replace("#", string.Empty);
                    // Collapse multiple whitespace to single spaces and trim
                    textToSynthesize = Regex.Replace(textToSynthesize, "\\s{2,}", " ").Trim();
                }

                var payload = new Dictionary<string, object>
                {
                    ["text"] = textToSynthesize,
                    ["model"] = request.Model ?? "voice_templates",
                    ["format"] = request.Format ?? "wav"
                };

                if (!string.IsNullOrWhiteSpace(request.Voice)) payload["voice"] = request.Voice;
                if (!string.IsNullOrWhiteSpace(request.Speaker)) payload["speaker"] = request.Speaker;
                if (!string.IsNullOrWhiteSpace(request.Language)) payload["language"] = request.Language;

                LastSynthFormat = request.Format;

                var response = await _http.PostAsJsonAsync($"{_baseUrl}/synthesize", payload);
                if (!response.IsSuccessStatusCode)
                {
                    var err = await SafeReadContentAsync(response);
                    return SerializeResult(new { error = $"TTS synthesis failed (status {response.StatusCode}). Server: {err}" });
                }

                // If audio content, return base64 encoded
                var media = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(media) && media.IndexOf("audio", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var audioBytes = await response.Content.ReadAsByteArrayAsync();
                    var base64 = Convert.ToBase64String(audioBytes);
                    return SerializeResult(new { result = base64, format = "base64" });
                }

                // Try to extract base64 from JSON response
                var body = await response.Content.ReadAsStringAsync();
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (doc.RootElement.TryGetProperty("audio_base64", out var a))
                        {
                            var b64 = a.GetString();
                            if (!string.IsNullOrWhiteSpace(b64))
                            {
                                return SerializeResult(new { result = b64, format = "base64" });
                            }
                        }
                    }
                }
                catch
                {
                    // not JSON or parse failed - return as-is
                }

                return SerializeResult(new { result = body });
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private static async Task<string?> SafeReadContentAsync(HttpResponseMessage resp)
        {
            try 
            { 
                return await resp.Content.ReadAsStringAsync(); 
            }
            catch 
            { 
                return null; 
            }
        }

        private class TtsApiToolRequest
        {
            public string? Operation { get; set; }
            public string? Text { get; set; }
            public string? Model { get; set; }
            public string? Voice { get; set; }
            public string? Speaker { get; set; }
            public string? Language { get; set; }
            public string? Format { get; set; }
        }
    }
}
