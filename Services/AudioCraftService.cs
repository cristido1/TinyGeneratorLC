using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TinyGenerator.Services
{
    /// <summary>
    /// Service for interacting with the AudioCraft API server.
    /// Supports music generation (MusicGen) and sound effects generation (AudioGen).
    /// Base URL: http://localhost:8003
    /// </summary>
    public class AudioCraftService
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        public AudioCraftService(string baseUrl = "http://localhost:8003")
        {
            _baseUrl = baseUrl;
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(_baseUrl),
                Timeout = TimeSpan.FromMinutes(5) // Audio generation can take time
            };
        }

        /// <summary>
        /// Generates music using MusicGen model.
        /// </summary>
        /// <param name="prompt">Text description of the music to generate</param>
        /// <param name="durationSeconds">Duration in seconds (default 10.0)</param>
        /// <param name="modelName">Model to use (default: facebook/musicgen-medium)</param>
        /// <returns>Tuple with success flag, generated file URL, and error message if any</returns>
        public async Task<(bool success, string? fileUrl, string? error)> GenerateMusicAsync(
            string prompt,
            float durationSeconds = 10.0f,
            string modelName = "facebook/musicgen-medium",
            float temperature = 1.0f,
            int topK = 250,
            float topP = 0.0f)
        {
            try
            {
                var request = new
                {
                    prompt,
                    duration = durationSeconds,
                    model_name = modelName,
                    temperature,
                    top_k = topK,
                    top_p = topP
                };

                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("/api/musicgen", content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    // Extract 'detail' field from error response as per API spec
                    var errorDetail = ExtractErrorDetail(responseBody) ?? responseBody;
                    return (false, null, $"HTTP {response.StatusCode}: {errorDetail}");
                }

                var result = JsonSerializer.Deserialize<GenerationResponse>(responseBody, 
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (result == null || !result.Success)
                {
                    return (false, null, result?.Error ?? "Generazione non riuscita o risposta non valida");
                }

                // Support both 'url' and 'file_url' response formats
                var fileUrl = result.Url ?? result.FileUrl;
                return (true, fileUrl, null);
            }
            catch (Exception ex)
            {
                return (false, null, $"Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Generates sound effects using AudioGen model.
        /// </summary>
        /// <param name="prompt">Text description of the sound effect to generate</param>
        /// <param name="durationSeconds">Duration in seconds (default 5.0)</param>
        /// <param name="modelName">Model to use (default: facebook/audiogen-medium)</param>
        /// <returns>Tuple with success flag, generated file URL, and error message if any</returns>
        public async Task<(bool success, string? fileUrl, string? error)> GenerateSoundEffectAsync(
            string prompt,
            float durationSeconds = 5.0f,
            string modelName = "facebook/audiogen-medium",
            float temperature = 1.0f,
            int topK = 250,
            float topP = 0.0f)
        {
            try
            {
                var request = new
                {
                    prompt,
                    duration = durationSeconds,
                    model_name = modelName,
                    temperature,
                    top_k = topK,
                    top_p = topP
                };

                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("/api/audiogen", content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    // Extract 'detail' field from error response as per API spec
                    var errorDetail = ExtractErrorDetail(responseBody) ?? responseBody;
                    return (false, null, $"HTTP {response.StatusCode}: {errorDetail}");
                }

                var result = JsonSerializer.Deserialize<GenerationResponse>(responseBody, 
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (result == null || !result.Success)
                {
                    return (false, null, result?.Error ?? "Generazione non riuscita o risposta non valida");
                }

                // Support both 'url' and 'file_url' response formats
                var fileUrl = result.Url ?? result.FileUrl;
                return (true, fileUrl, null);
            }
            catch (Exception ex)
            {
                return (false, null, $"Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Downloads a generated audio file from the AudioCraft server.
        /// </summary>
        /// <param name="fileUrl">Relative URL returned by generation endpoint (e.g., /download/music_xxx.wav)</param>
        /// <returns>Byte array of the audio file, or null on error</returns>
        public async Task<byte[]?> DownloadFileAsync(string fileUrl)
        {
            try
            {
                var response = await _httpClient.GetAsync(fileUrl);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                return await response.Content.ReadAsByteArrayAsync();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Response model for AudioCraft generation endpoints.
        /// </summary>
        private class GenerationResponse
        {
            public bool Success { get; set; }
            public string? FilePath { get; set; }
            public string? FileUrl { get; set; }
            public string? Url { get; set; }
            public string? Prompt { get; set; }
            public float Duration { get; set; }
            public string? GeneratedAt { get; set; }
            public string? Error { get; set; }
            public string? Detail { get; set; }
        }

        /// <summary>
        /// Extracts 'detail' field from error response JSON as per API spec.
        /// </summary>
        private static string? ExtractErrorDetail(string responseBody)
        {
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("detail", out var detail))
                {
                    return detail.GetString();
                }
                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    return error.GetString();
                }
            }
            catch
            {
                // Not valid JSON, return null
            }
            return null;
        }
    }
}
