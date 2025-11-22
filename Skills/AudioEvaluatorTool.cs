using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    /// <summary>
    /// LangChain tool for PAM (audio evaluator) service integration.
    /// Converted from AudioEvaluatorSkill (Semantic Kernel).
    /// </summary>
    public class AudioEvaluatorTool : BaseLangChainTool, ITinyTool
    {
        private readonly HttpClient _http;

        public int? ModelId { get; set; }
        public string? ModelName { get; set; }
        public int? AgentId { get; set; }
        public string? LastFunctionCalled { get; set; }
        public string? LastFunctionResult { get; set; }

        public AudioEvaluatorTool(HttpClient httpClient, ICustomLogger? logger = null) 
            : base("audioevaluator", "Provides functions to call the PAM Audio Evaluator service (analyze and verify audio files).", logger)
        {
            _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _http.BaseAddress = new Uri("http://localhost:8010");
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
                            { "description", "The operation: 'check_health', 'list_models', 'analyze', 'verify', 'describe'" }
                        }
                    },
                    {
                        "filePath",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Path to local audio file" }
                        }
                    },
                    {
                        "modelName",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Optional PAM model name" }
                        }
                    },
                    {
                        "referenceFile",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Optional reference audio file for comparison" }
                        }
                    },
                    {
                        "verificationType",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Verification type (e.g. 'speaker_verification')" }
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
                var request = ParseInput<AudioEvaluatorToolRequest>(input);
                if (request == null)
                    return SerializeResult(new { error = "Invalid input format" });

                CustomLogger?.Log("Info", "AudioEvaluatorTool", $"Executing operation: {request.Operation}");

                return request.Operation?.ToLowerInvariant() switch
                {
                    "check_health" => await ExecuteCheckHealthAsync(),
                    "list_models" => await ExecuteListModelsAsync(),
                    "analyze" => await ExecuteAnalyzeAsync(request),
                    "verify" => await ExecuteVerifyAsync(request),
                    "describe" => SerializeResult(new { result = "Available operations: check_health(), list_models(), analyze(filePath, modelName?), verify(filePath, referenceFile?, verificationType?)." }),
                    _ => SerializeResult(new { error = $"Unknown operation: {request.Operation}" })
                };
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "AudioEvaluatorTool", $"Error executing operation: {ex.Message}", ex.ToString());
                return SerializeResult(new { error = ex.Message });
            }
        }

        private async Task<string> ExecuteCheckHealthAsync()
        {
            try
            {
                var resp = await _http.GetAsync("/health");
                if (resp.IsSuccessStatusCode)
                {
                    var content = await resp.Content.ReadAsStringAsync();
                    return SerializeResult(new { result = content });
                }
                var body = await SafeReadContentAsync(resp);
                return SerializeResult(new { error = $"PAM health check failed: {resp.StatusCode}. Server: {body}" });
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
                var resp = await _http.GetAsync("/models");
                if (resp.IsSuccessStatusCode)
                {
                    var content = await resp.Content.ReadAsStringAsync();
                    return SerializeResult(new { result = content });
                }
                var body = await SafeReadContentAsync(resp);
                return SerializeResult(new { error = $"Failed to list PAM models (status {resp.StatusCode}). Server: {body}" });
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private async Task<string> ExecuteAnalyzeAsync(AudioEvaluatorToolRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.FilePath) || !File.Exists(request.FilePath))
                    return SerializeResult(new { error = $"Audio file not found: {request.FilePath}" });

                using var content = new MultipartFormDataContent();
                var fs = File.OpenRead(request.FilePath);
                var streamContent = new StreamContent(fs);
                streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                content.Add(streamContent, "file", Path.GetFileName(request.FilePath));

                if (!string.IsNullOrWhiteSpace(request.ModelName))
                    content.Add(new StringContent(request.ModelName), "model_name");

                var resp = await _http.PostAsync("/analyze", content);
                var body = await SafeReadContentAsync(resp);

                if (resp.IsSuccessStatusCode)
                {
                    return SerializeResult(new { result = body });
                }

                return SerializeResult(new { error = $"PAM analyze failed (status {resp.StatusCode}). Server: {body}" });
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private async Task<string> ExecuteVerifyAsync(AudioEvaluatorToolRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.FilePath) || !File.Exists(request.FilePath))
                    return SerializeResult(new { error = $"Audio file not found: {request.FilePath}" });

                using var content = new MultipartFormDataContent();
                var mainFs = File.OpenRead(request.FilePath);
                var mainContent = new StreamContent(mainFs);
                mainContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                content.Add(mainContent, "file", Path.GetFileName(request.FilePath));

                if (!string.IsNullOrWhiteSpace(request.ReferenceFile))
                {
                    if (!File.Exists(request.ReferenceFile))
                        return SerializeResult(new { error = $"Reference audio file not found: {request.ReferenceFile}" });

                    var refFs = File.OpenRead(request.ReferenceFile);
                    var refContent = new StreamContent(refFs);
                    refContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                    content.Add(refContent, "reference_file", Path.GetFileName(request.ReferenceFile));
                }

                if (!string.IsNullOrWhiteSpace(request.VerificationType))
                    content.Add(new StringContent(request.VerificationType), "verification_type");

                var resp = await _http.PostAsync("/verify", content);
                var body = await SafeReadContentAsync(resp);

                if (resp.IsSuccessStatusCode)
                {
                    return SerializeResult(new { result = body });
                }

                return SerializeResult(new { error = $"PAM verify failed (status {resp.StatusCode}). Server: {body}" });
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private static async Task<string?> SafeReadContentAsync(HttpResponseMessage resp)
        {
            try { return await resp.Content.ReadAsStringAsync(); }
            catch { return null; }
        }

        private class AudioEvaluatorToolRequest
        {
            public string? Operation { get; set; }
            public string? FilePath { get; set; }
            public string? ModelName { get; set; }
            public string? ReferenceFile { get; set; }
            public string? VerificationType { get; set; }
        }
    }
}
