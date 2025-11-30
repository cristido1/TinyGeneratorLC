using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace TinyGenerator.Services
{
    public sealed class OllamaEmbeddingGenerator : IMemoryEmbeddingGenerator
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IOptionsMonitor<MemoryEmbeddingOptions> _optionsMonitor;
        private readonly string _fallbackEndpoint;
        private readonly ICustomLogger? _logger;

        public OllamaEmbeddingGenerator(
            IHttpClientFactory httpClientFactory,
            IOptionsMonitor<MemoryEmbeddingOptions> optionsMonitor,
            IConfiguration configuration,
            ICustomLogger? logger = null)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
            _logger = logger;
            _fallbackEndpoint = configuration["Memory:EmbeddingEndpoint"]
                ?? configuration["Ollama:endpoint"]
                ?? "http://localhost:11434";
        }

        public async Task<float[]> GenerateAsync(string text, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<float>();
            }

            var options = _optionsMonitor.CurrentValue;
            var endpoint = BuildEndpoint(options);
            var payload = JsonSerializer.Serialize(new
            {
                model = string.IsNullOrWhiteSpace(options.Model) ? "nomic-embed-text:latest" : options.Model,
                prompt = text
            });

            var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            var client = _httpClientFactory.CreateClient("ollama-embeddings");
            var timeoutSeconds = Math.Clamp(options.RequestTimeoutSeconds, 5, 600);
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var message = $"Embedding request failed ({(int)response.StatusCode}): {body}";
                _logger?.Log("Error", "MemoryEmbedding", message);
                throw new InvalidOperationException(message);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var data = await JsonSerializer.DeserializeAsync<OllamaEmbeddingResponse>(stream, cancellationToken: cancellationToken);
            if (data?.Embedding == null || data.Embedding.Count == 0)
            {
                throw new InvalidOperationException("Embedding response vuoto");
            }

            var vector = new float[data.Embedding.Count];
            for (var i = 0; i < data.Embedding.Count; i++)
            {
                vector[i] = (float)data.Embedding[i];
            }
            return vector;
        }

        private Uri BuildEndpoint(MemoryEmbeddingOptions options)
        {
            var baseUrl = options.Endpoint ?? _fallbackEndpoint;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = "http://localhost:11434";
            }
            if (!baseUrl.EndsWith("/", StringComparison.Ordinal))
            {
                baseUrl += "/";
            }
            return new Uri(new Uri(baseUrl), "api/embeddings");
        }

        private sealed class OllamaEmbeddingResponse
        {
            [JsonPropertyName("embedding")]
            public List<double>? Embedding { get; set; }
        }
    }
}
