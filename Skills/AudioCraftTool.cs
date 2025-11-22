using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    /// <summary>
    /// LangChain tool for AudioCraft music and sound generation.
    /// Converted from AudioCraftSkill (Semantic Kernel).
    /// </summary>
    public class AudioCraftTool : BaseLangChainTool, ITinyTool
    {
        private readonly HttpClient _http;
        private readonly bool _forceCpu;

        public string? LastGeneratedMusicFile { get; set; }
        public string? LastGeneratedSoundFile { get; set; }

        public int? ModelId { get; set; }
        public string? ModelName { get; set; }
        public int? AgentId { get; set; }
        public string? LastFunctionCalled { get; set; }
        public string? LastFunctionResult { get; set; }

        public AudioCraftTool(HttpClient httpClient, bool forceCpu = false, ICustomLogger? logger = null) 
            : base("audiocraft", "Provides AudioCraft music and sound generation functions.", logger)
        {
            _http = httpClient;
            _forceCpu = forceCpu;
            _http.BaseAddress = new Uri("http://localhost:8000");
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
                            { "description", "The operation: 'check_health', 'list_models', 'generate_music', 'generate_sound', 'download_file', 'describe'" }
                        }
                    },
                    {
                        "prompt",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Text prompt for generation (for generate_music/generate_sound)" }
                        }
                    },
                    {
                        "model",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Model to use for generation" }
                        }
                    },
                    {
                        "duration",
                        new Dictionary<string, object>
                        {
                            { "type", "integer" },
                            { "description", "Duration in seconds" }
                        }
                    },
                    {
                        "file",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "File name to download" }
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
                var request = ParseInput<AudioCraftToolRequest>(input);
                if (request == null)
                    return SerializeResult(new { error = "Invalid input format" });

                CustomLogger?.Log("Info", "AudioCraftTool", $"Executing operation: {request.Operation}");

                return request.Operation?.ToLowerInvariant() switch
                {
                    "check_health" => await ExecuteCheckHealthAsync(),
                    "list_models" => await ExecuteListModelsAsync(),
                    "generate_music" => await ExecuteGenerateMusicAsync(request),
                    "generate_sound" => await ExecuteGenerateSoundAsync(request),
                    "download_file" => await ExecuteDownloadFileAsync(request),
                    "describe" => SerializeResult(new { result = "Available operations: check_health(), list_models(), generate_music(prompt, model, duration), generate_sound(prompt, model, duration), download_file(file). Example: generate_music('A calm piano melody', 'facebook/musicgen-small', 30) generates a 30-second music clip." }),
                    _ => SerializeResult(new { error = $"Unknown operation: {request.Operation}" })
                };
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "AudioCraftTool", $"Error executing operation: {ex.Message}", ex.ToString());
                return SerializeResult(new { error = ex.Message });
            }
        }

        private async Task<string> ExecuteCheckHealthAsync()
        {
            try
            {
                var response = await _http.GetAsync("/health");
                return response.IsSuccessStatusCode
                    ? SerializeResult(new { result = "AudioCraft is online ✅" })
                    : SerializeResult(new { error = $"AudioCraft error: {response.StatusCode}" });
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private async Task<string> ExecuteListModelsAsync()
        {
            try
            {
                var models = await _http.GetStringAsync("/api/models");
                return SerializeResult(new { result = models });
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private async Task<string> ExecuteGenerateMusicAsync(AudioCraftToolRequest request)
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["model"] = request.Model ?? "facebook/musicgen-small",
                    ["prompt"] = request.Prompt ?? string.Empty,
                    ["duration"] = request.Duration ?? 30
                };
                if (_forceCpu) payload["device"] = "cpu";

                var response = await _http.PostAsJsonAsync("/api/musicgen", payload);
                if (response.IsSuccessStatusCode)
                {
                    var respBody = await response.Content.ReadAsStringAsync();
                    ExtractGeneratedFile(respBody, isMusic: true);
                    return SerializeResult(new { result = respBody });
                }

                var body = await SafeReadContentAsync(response);
                if (body != null && body.IndexOf("mps", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var retryPayload = new Dictionary<string, object>
                    {
                        ["model"] = request.Model ?? "facebook/musicgen-small",
                        ["prompt"] = request.Prompt ?? string.Empty,
                        ["duration"] = request.Duration ?? 30,
                        ["device"] = "cpu"
                    };
                    var r2 = await _http.PostAsJsonAsync("/api/musicgen", retryPayload);
                    if (r2.IsSuccessStatusCode)
                    {
                        var body2 = await r2.Content.ReadAsStringAsync();
                        ExtractGeneratedFile(body2, isMusic: true);
                        return SerializeResult(new { result = body2 });
                    }
                    var body2Err = await SafeReadContentAsync(r2);
                    return SerializeResult(new { error = $"AudioCraft music generation failed after retry (status {r2.StatusCode}). Server: {body2Err}" });
                }

                return SerializeResult(new { error = $"AudioCraft music generation failed (status {response.StatusCode}). Server: {body}" });
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private async Task<string> ExecuteGenerateSoundAsync(AudioCraftToolRequest request)
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["model"] = request.Model ?? "facebook/audiogen-medium",
                    ["prompt"] = request.Prompt ?? string.Empty,
                    ["duration"] = request.Duration ?? 10
                };
                if (_forceCpu) payload["device"] = "cpu";

                var response = await _http.PostAsJsonAsync("/api/audiogen", payload);
                if (response.IsSuccessStatusCode)
                {
                    var respBody = await response.Content.ReadAsStringAsync();
                    ExtractGeneratedFile(respBody, isMusic: false);
                    return SerializeResult(new { result = respBody });
                }

                var body = await SafeReadContentAsync(response);
                if (body != null && body.IndexOf("mps", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var retryPayload = new Dictionary<string, object>
                    {
                        ["model"] = request.Model ?? "facebook/audiogen-medium",
                        ["prompt"] = request.Prompt ?? string.Empty,
                        ["duration"] = request.Duration ?? 10,
                        ["device"] = "cpu"
                    };
                    var r2 = await _http.PostAsJsonAsync("/api/audiogen", retryPayload);
                    if (r2.IsSuccessStatusCode)
                    {
                        var body2 = await r2.Content.ReadAsStringAsync();
                        ExtractGeneratedFile(body2, isMusic: false);
                        return SerializeResult(new { result = body2 });
                    }
                    var body2Err = await SafeReadContentAsync(r2);
                    return SerializeResult(new { error = $"AudioCraft sound generation failed after retry (status {r2.StatusCode}). Server: {body2Err}" });
                }

                return SerializeResult(new { error = $"AudioCraft sound generation failed (status {response.StatusCode}). Server: {body}" });
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private async Task<string> ExecuteDownloadFileAsync(AudioCraftToolRequest request)
        {
            try
            {
                var response = await _http.GetAsync($"/download/{request.File}");
                if (response.IsSuccessStatusCode)
                {
                    var audioBytes = await response.Content.ReadAsByteArrayAsync();
                    var base64 = Convert.ToBase64String(audioBytes);
                    return SerializeResult(new { result = base64, format = "base64" });
                }

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    var body = await SafeReadContentAsync(response);
                    return SerializeResult(new { error = $"File not found on server: {request.File}. Server: {body}" });
                }

                var err = await SafeReadContentAsync(response);
                return SerializeResult(new { error = $"Failed to download file {request.File} (status {response.StatusCode}). Server: {err}" });
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private void ExtractGeneratedFile(string respBody, bool isMusic)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(respBody))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(respBody);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            if (doc.RootElement.TryGetProperty("file", out var pf))
                            {
                                var fileName = pf.GetString();
                                if (isMusic) LastGeneratedMusicFile = fileName;
                                else LastGeneratedSoundFile = fileName;
                                return;
                            }
                            else if (doc.RootElement.TryGetProperty("filename", out var pfn))
                            {
                                var fileName = pfn.GetString();
                                if (isMusic) LastGeneratedMusicFile = fileName;
                                else LastGeneratedSoundFile = fileName;
                                return;
                            }
                        }
                    }
                    catch { }

                    var trimmed = respBody.Trim();
                    if (trimmed.Length > 0 && trimmed.IndexOf(' ') < 0 && trimmed.IndexOf('\n') < 0)
                    {
                        if (isMusic) LastGeneratedMusicFile = trimmed;
                        else LastGeneratedSoundFile = trimmed;
                    }
                }
            }
            catch { }
        }

        private static async Task<string?> SafeReadContentAsync(HttpResponseMessage resp)
        {
            try { return await resp.Content.ReadAsStringAsync(); }
            catch { return null; }
        }

        private class AudioCraftToolRequest
        {
            public string? Operation { get; set; }
            public string? Prompt { get; set; }
            public string? Model { get; set; }
            public int? Duration { get; set; }
            public string? File { get; set; }
        }
    }
}
