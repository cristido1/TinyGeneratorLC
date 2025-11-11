using Microsoft.SemanticKernel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.ComponentModel;

namespace TinyGenerator.Skills
{
    [Description("Provides AudioCraft music and sound generation functions.")]
    public class AudioCraftSkill
    {
        private readonly HttpClient _http;
        private readonly bool _forceCpu;

        public string? LastCalled { get; set; }
            // Last returned filenames (as returned by the generation endpoints). These may be server-side names.
            public string? LastGeneratedMusicFile { get; set; }
            public string? LastGeneratedSoundFile { get; set; }

        public AudioCraftSkill(HttpClient httpClient, bool forceCpu = false)
        {
            _http = httpClient;
            _http.BaseAddress = new System.Uri("http://localhost:8000"); // endpoint del container
            _forceCpu = forceCpu;
        }

        // 1️⃣ Health check
        [KernelFunction("check_health"),Description("Checks the health of the AudioCraft service.")]
        public async Task<string> CheckHealthAsync()
        {
            LastCalled = nameof(CheckHealthAsync);
            var response = await _http.GetAsync("/health");
            return response.IsSuccessStatusCode
                ? "AudioCraft è online ✅"
                : $"Errore AudioCraft: {response.StatusCode}";
        }

        // 2️⃣ Lista modelli
        [KernelFunction("list_models"),Description("Lists all available models.")]
        public async Task<string> ListModelsAsync()
        {
            LastCalled = nameof(ListModelsAsync);
            var models = await _http.GetStringAsync("/api/models");
            return models;
        }

        // 3️⃣ Genera musica
        [KernelFunction("generate_music"),Description("Generates music based on a text prompt.")]
        public async Task<string> GenerateMusicAsync(
            [Description("The text prompt to generate music from.")] string prompt,
            [Description("The model to use for music generation.")] string model = "facebook/musicgen-small",
            [Description("The duration of the generated music in seconds.")] int duration = 30)
        {
            var payload = new System.Collections.Generic.Dictionary<string, object>
            {
                ["model"] = model,
                ["prompt"] = prompt,
                ["duration"] = duration
            };
            if (_forceCpu) payload["device"] = "cpu";

            LastCalled = nameof(GenerateMusicAsync);
            // First attempt
            var response = await _http.PostAsJsonAsync("/api/musicgen", payload);
            if (response.IsSuccessStatusCode)
            {
                var respBody = await response.Content.ReadAsStringAsync();
                // Try to parse returned body to extract a file name if possible (JSON or plain string)
                try
                {
                    if (!string.IsNullOrWhiteSpace(respBody))
                    {
                        // Try JSON { "file": "name" } or { "filename": "name" }
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(respBody);
                            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                if (doc.RootElement.TryGetProperty("file", out var pf)) LastGeneratedMusicFile = pf.GetString();
                                else if (doc.RootElement.TryGetProperty("filename", out var pfn)) LastGeneratedMusicFile = pfn.GetString();
                            }
                        }
                        catch { /* not JSON */ }

                        // If still null and body looks like a simple filename, use it
                        if (string.IsNullOrWhiteSpace(LastGeneratedMusicFile))
                        {
                            var trimmed = respBody.Trim();
                            if (trimmed.Length > 0 && trimmed.IndexOf(' ') < 0 && trimmed.IndexOf('\n') < 0) LastGeneratedMusicFile = trimmed;
                        }
                    }
                }
                catch { }

                return respBody;
            }

            // Read body for diagnostics
            var body = await SafeReadContentAsync(response);

            // If error mentions unsupported 'mps' autocast, retry forcing CPU
            if (body != null && body.IndexOf("mps", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var retryPayload = new
                {
                    model = model,
                    prompt = prompt,
                    duration = duration,
                    device = "cpu"
                };
                var r2 = await _http.PostAsJsonAsync("/api/musicgen", retryPayload);
                if (r2.IsSuccessStatusCode) return await r2.Content.ReadAsStringAsync();
                var body2 = await SafeReadContentAsync(r2);
                throw new HttpRequestException($"AudioCraft music generation failed after retry (status {r2.StatusCode}). Server: {body2}");
            }

            throw new HttpRequestException($"AudioCraft music generation failed (status {response.StatusCode}). Server: {body}");
        }

        // 4️⃣ Genera effetto sonoro
        [KernelFunction("generate_sound"),Description("Generates a sound effect based on a text prompt.")]
        public async Task<string> GenerateSoundAsync(
            [Description("The text prompt to generate the sound effect from.")] string prompt,
            [Description("The model to use for sound generation.")] string model = "facebook/audiogen-medium",
            [Description("The duration of the generated sound effect in seconds.")] int duration = 10)
        {
            var payload = new System.Collections.Generic.Dictionary<string, object>
            {
                ["model"] = model,
                ["prompt"] = prompt,
                ["duration"] = duration
            };
            if (_forceCpu) payload["device"] = "cpu";

            LastCalled = nameof(GenerateSoundAsync);

            var response = await _http.PostAsJsonAsync("/api/audiogen", payload);
            if (response.IsSuccessStatusCode)
            {
                var respBody = await response.Content.ReadAsStringAsync();
                try
                {
                    if (!string.IsNullOrWhiteSpace(respBody))
                    {
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(respBody);
                            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                if (doc.RootElement.TryGetProperty("file", out var pf)) LastGeneratedSoundFile = pf.GetString();
                                else if (doc.RootElement.TryGetProperty("filename", out var pfn)) LastGeneratedSoundFile = pfn.GetString();
                            }
                        }
                        catch { }

                        if (string.IsNullOrWhiteSpace(LastGeneratedSoundFile))
                        {
                            var trimmed = respBody.Trim();
                            if (trimmed.Length > 0 && trimmed.IndexOf(' ') < 0 && trimmed.IndexOf('\n') < 0) LastGeneratedSoundFile = trimmed;
                        }
                    }
                }
                catch { }

                return respBody;
            }

            var body = await SafeReadContentAsync(response);
            if (body != null && body.IndexOf("mps", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var retryPayload = new
                {
                    model = model,
                    prompt = prompt,
                    duration = duration,
                    device = "cpu"
                };
                var r2 = await _http.PostAsJsonAsync("/api/audiogen", retryPayload);
                if (r2.IsSuccessStatusCode) return await r2.Content.ReadAsStringAsync();
                var body2 = await SafeReadContentAsync(r2);
                throw new HttpRequestException($"AudioCraft sound generation failed after retry (status {r2.StatusCode}). Server: {body2}");
            }

            throw new HttpRequestException($"AudioCraft sound generation failed (status {response.StatusCode}). Server: {body}");
        }

        // 5️⃣ Download file
        [KernelFunction("download_file"), Description("Downloads a file.")]
        public async Task<byte[]> DownloadFileAsync([Description("The name of the file to download.")] string file)
        {
            LastCalled = nameof(DownloadFileAsync);
            var response = await _http.GetAsync($"/download/{file}");
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsByteArrayAsync();
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var body = await SafeReadContentAsync(response);
                throw new System.IO.FileNotFoundException($"Requested audio file not found on server: {file}. Server message: {body}");
            }

            var err = await SafeReadContentAsync(response);
            throw new HttpRequestException($"Failed to download file {file} from AudioCraft server (status {response.StatusCode}). Server: {err}");
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

        [KernelFunction("describe"), Description("Describes the available AudioCraft functions.")]
        public string Describe() =>
            "Available functions: check_health(), list_models(), generate_music(prompt, model, duration), generate_sound(prompt, model, duration), download_file(file)." +
            "Example: audio.generate_music('A calm piano melody', 'facebook/musicgen-small', 30) generates a 30-second music clip.";
    }
}
