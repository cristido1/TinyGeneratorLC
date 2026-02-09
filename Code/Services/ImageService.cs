using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TinyGenerator.Models;

namespace TinyGenerator.Services
{
    public class ImageService
    {
        private readonly HttpClient _httpClient;
        private readonly string _startServerScript = "C:\\Users\\User\\Documents\\ai\\scene_renderer\\start_server.bat";
        private readonly string _baseUrl = "http://localhost:8010";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public ImageService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<bool> IsServiceActiveAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync(_baseUrl + "/status");
                if (!response.IsSuccessStatusCode) return false;

                var json = await response.Content.ReadAsStringAsync();
                var status = JsonSerializer.Deserialize<SceneRendererStatusResponse>(json, JsonOptions);
                return string.Equals(status?.Status, "running", System.StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public async Task EnsureServiceRunningAsync(int maxWaitMs = 15000, int pollEveryMs = 500)
        {
            if (await IsServiceActiveAsync()) return;

            StartService();

            var waited = 0;
            while (waited < maxWaitMs)
            {
                await Task.Delay(pollEveryMs);
                waited += pollEveryMs;
                if (await IsServiceActiveAsync()) return;
            }

            throw new HttpRequestException("Scene Renderer non risponde su /status dopo l'avvio.");
        }

        public void StartService()
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _startServerScript,
                UseShellExecute = true
            });
        }

        public async Task<GenerateCharactersResponse> GenerateCharactersAsync(GenerateCharactersRequest request)
        {
            await EnsureServiceRunningAsync();
            var jsonPayload = JsonSerializer.Serialize(request, JsonOptions);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(_baseUrl + "/generate_characters", content);
            response.EnsureSuccessStatusCode();
            var responseContent = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<GenerateCharactersResponse>(responseContent, JsonOptions)
                ?? new GenerateCharactersResponse();
        }

        public async Task<GenerateBackgroundsResponse> GenerateBackgroundsAsync(GenerateBackgroundsRequest request)
        {
            await EnsureServiceRunningAsync();
            var jsonPayload = JsonSerializer.Serialize(request, JsonOptions);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(_baseUrl + "/generate_backgrounds", content);
            response.EnsureSuccessStatusCode();
            var responseContent = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<GenerateBackgroundsResponse>(responseContent, JsonOptions)
                ?? new GenerateBackgroundsResponse();
        }

        public async Task<GenerateScenesResponse> GenerateScenesAsync(GenerateScenesRequest request)
        {
            await EnsureServiceRunningAsync();
            var jsonPayload = JsonSerializer.Serialize(request, JsonOptions);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(_baseUrl + "/generate_scenes", content);
            response.EnsureSuccessStatusCode();
            var responseContent = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<GenerateScenesResponse>(responseContent, JsonOptions)
                ?? new GenerateScenesResponse();
        }
    }
}