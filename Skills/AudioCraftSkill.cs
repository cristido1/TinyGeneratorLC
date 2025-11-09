using Microsoft.SemanticKernel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace TinyGenerator.Skills
{
    public class AudioCraftSkill
    {
        private readonly HttpClient _http;

        public AudioCraftSkill(HttpClient httpClient)
        {
            _http = httpClient;
            _http.BaseAddress = new System.Uri("http://localhost:8000"); // endpoint del container
        }

        // 1️⃣ Health check
        [KernelFunction("check_health")]
        public async Task<string> CheckHealthAsync()
        {
            var response = await _http.GetAsync("/health");
            return response.IsSuccessStatusCode
                ? "AudioCraft è online ✅"
                : $"Errore AudioCraft: {response.StatusCode}";
        }

        // 2️⃣ Lista modelli
        [KernelFunction("list_models")]
        public async Task<string> ListModelsAsync()
        {
            var models = await _http.GetStringAsync("/api/models");
            return models;
        }

        // 3️⃣ Genera musica
        [KernelFunction("generate_music")]
        public async Task<string> GenerateMusicAsync(
            string prompt,
            string model = "facebook/musicgen-small",
            int duration = 30)
        {
            var payload = new
            {
                model = model,
                prompt = prompt,
                duration = duration
            };

            var response = await _http.PostAsJsonAsync("/api/musicgen", payload);
            response.EnsureSuccessStatusCode();

            // Il server ritorna JSON con percorso o nome file
            var result = await response.Content.ReadAsStringAsync();
            return result;
        }

        // 4️⃣ Genera effetto sonoro
        [KernelFunction("generate_sound")]
        public async Task<string> GenerateSoundAsync(
            string prompt,
            string model = "facebook/audiogen-medium",
            int duration = 10)
        {
            var payload = new
            {
                model = model,
                prompt = prompt,
                duration = duration
            };

            var response = await _http.PostAsJsonAsync("/api/audiogen", payload);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadAsStringAsync();
            return result;
        }

        // 5️⃣ Download file
        [KernelFunction("download_file")]
        public async Task<byte[]> DownloadFileAsync(string file)
        {
            var response = await _http.GetAsync($"/download/{file}");
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsByteArrayAsync();
        }
    }
}
