using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.IO;

namespace TinyGenerator.Skills
{
    [Description("Provides functions to call the PAM Audio Evaluator service (analyze and verify audio files).")]
    public class AudioEvaluatorSkill : ITinySkill
    {
        private readonly HttpClient _http;
        private DateTime? _lastCalled;
        private string? _lastFunction;

        // ITinySkill implementation
        int? ITinySkill.ModelId => null;
        string? ITinySkill.ModelName => null;
        int? ITinySkill.AgentId => null;
        string? ITinySkill.AgentName => null;
        DateTime? ITinySkill.LastCalled { get => _lastCalled; set => _lastCalled = value; }
        string? ITinySkill.LastFunction { get => _lastFunction; set => _lastFunction = value; }

        public AudioEvaluatorSkill(HttpClient httpClient)
        {
            _http = httpClient;
            // PAM default base address per PAM.md
            _http.BaseAddress = new System.Uri("http://localhost:8010");
        }

        [KernelFunction("check_health"), Description("Checks PAM service health.")]
        public async Task<string> CheckHealthAsync()
        {
            ((ITinySkill)this).LastCalled = DateTime.UtcNow;
            ((ITinySkill)this).LastFunction = nameof(CheckHealthAsync);
            var resp = await _http.GetAsync("/health");
            if (resp.IsSuccessStatusCode)
            {
                return await resp.Content.ReadAsStringAsync();
            }
            var body = await SafeReadContentAsync(resp);
            return $"PAM health check failed: {resp.StatusCode}. Server: {body}";
        }

        [KernelFunction("list_models"), Description("Lists available PAM models.")]
        public async Task<string> ListModelsAsync()
        {
            ((ITinySkill)this).LastCalled = DateTime.UtcNow;
            ((ITinySkill)this).LastFunction = nameof(ListModelsAsync);
            var resp = await _http.GetAsync("/models");
            if (resp.IsSuccessStatusCode)
            {
                return await resp.Content.ReadAsStringAsync();
            }
            var body = await SafeReadContentAsync(resp);
            throw new HttpRequestException($"Failed to list PAM models (status {resp.StatusCode}). Server: {body}");
        }

        [KernelFunction("analyze"), Description("Uploads an audio file to PAM for analysis and returns the analysis JSON.")]
        public async Task<string> AnalyzeAsync(
            [Description("Path to the local audio file to analyze.")] string filePath,
            [Description("Optional PAM model name to use.")] string? modelName = null)
        {
            ((ITinySkill)this).LastCalled = DateTime.UtcNow;
            ((ITinySkill)this).LastFunction = nameof(AnalyzeAsync);

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException($"Audio file not found: {filePath}");

            using var content = new MultipartFormDataContent();
            var fs = File.OpenRead(filePath);
            var streamContent = new StreamContent(fs);
            // Set a sensible default content type — server will sniff if needed
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            content.Add(streamContent, "file", Path.GetFileName(filePath));
            if (!string.IsNullOrWhiteSpace(modelName)) content.Add(new StringContent(modelName), "model_name");

            var resp = await _http.PostAsync("/analyze", content);
            var body = await SafeReadContentAsync(resp);
            if (resp.IsSuccessStatusCode)
            {
                return body ?? string.Empty;
            }

            throw new HttpRequestException($"PAM analyze failed (status {resp.StatusCode}). Server: {body}");
        }

        [KernelFunction("verify"), Description("Uploads an audio file (and optional reference file) to PAM for verification and returns the verification JSON.")]
        public async Task<string> VerifyAsync(
            [Description("Path to the audio file to verify.")] string filePath,
            [Description("Optional path to a reference audio file for comparison.")] string? referenceFile = null,
            [Description("Verification type, e.g. 'speaker_verification' or 'audio_authenticity'.")] string verificationType = "speaker_verification")
        {
            ((ITinySkill)this).LastCalled = DateTime.UtcNow;
            ((ITinySkill)this).LastFunction = nameof(VerifyAsync);

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException($"Audio file not found: {filePath}");

            using var content = new MultipartFormDataContent();
            var mainFs = File.OpenRead(filePath);
            var mainContent = new StreamContent(mainFs);
            mainContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            content.Add(mainContent, "file", Path.GetFileName(filePath));

            if (!string.IsNullOrWhiteSpace(referenceFile))
            {
                if (!File.Exists(referenceFile)) throw new FileNotFoundException($"Reference audio file not found: {referenceFile}");
                var refFs = File.OpenRead(referenceFile);
                var refContent = new StreamContent(refFs);
                refContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                content.Add(refContent, "reference_file", Path.GetFileName(referenceFile));
            }

            if (!string.IsNullOrWhiteSpace(verificationType)) content.Add(new StringContent(verificationType), "verification_type");

            var resp = await _http.PostAsync("/verify", content);
            var body = await SafeReadContentAsync(resp);
            if (resp.IsSuccessStatusCode)
            {
                return body ?? string.Empty;
            }

            throw new HttpRequestException($"PAM verify failed (status {resp.StatusCode}). Server: {body}");
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

        [KernelFunction("describe"), Description("Describes the PAM audio evaluator functions.")]
        public string Describe() =>
            "Available functions: check_health(), list_models(), analyze(filePath, modelName?), verify(filePath, referenceFile?, verificationType?).";
    }
}
