using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    /// <summary>
    /// LangChain tool for HTTP requests.
    /// Converted from HttpSkill (Semantic Kernel).
    /// </summary>
    public class HttpTool : BaseLangChainTool, ITinyTool
    {
        private static readonly HttpClient _http = new HttpClient();

        public int? ModelId { get; set; }
        public string? ModelName { get; set; }
        public int? AgentId { get; set; }
        public string? LastFunctionCalled { get; set; }
        public string? LastFunctionResult { get; set; }

        public HttpTool(ICustomLogger? logger = null) 
            : base("http", "Provides HTTP request functions such as GET and POST.", logger)
        {
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
                            { "description", "The HTTP operation: 'http_get', 'http_post', 'describe'" }
                        }
                    },
                    {
                        "url",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "The URL to request" }
                        }
                    },
                    {
                        "content",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Content to POST (for http_post operation)" }
                        }
                    }
                },
                new List<string> { "operation", "url" }
            );
        }

        public override async Task<string> ExecuteAsync(string input)
        {
            try
            {
                var request = ParseInput<HttpToolRequest>(input);
                if (request == null)
                    return SerializeResult(new { error = "Invalid input format" });

                CustomLogger?.Log("Info", "HttpTool", $"Executing operation: {request.Operation} on URL: {request.Url}");

                return request.Operation?.ToLowerInvariant() switch
                {
                    "http_get" => await ExecuteHttpGetAsync(request),
                    "http_post" => await ExecuteHttpPostAsync(request),
                    "describe" => SerializeResult(new { result = "Available operations: http_get(url), http_post(url, content). Example: http_get('https://api.example.com/data') returns the response from the URL." }),
                    _ => SerializeResult(new { error = $"Unknown operation: {request.Operation}" })
                };
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "HttpTool", $"Error executing operation: {ex.Message}", ex.ToString());
                return SerializeResult(new { error = ex.Message });
            }
        }

        private async Task<string> ExecuteHttpGetAsync(HttpToolRequest request)
        {
            try
            {
                var response = await _http.GetAsync(request.Url);
                var content = await response.Content.ReadAsStringAsync();
                return SerializeResult(new { result = content, status_code = response.StatusCode });
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private async Task<string> ExecuteHttpPostAsync(HttpToolRequest request)
        {
            try
            {
                var response = await _http.PostAsync(request.Url, new StringContent(request.Content ?? string.Empty));
                var content = await response.Content.ReadAsStringAsync();
                return SerializeResult(new { result = content, status_code = response.StatusCode });
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private class HttpToolRequest
        {
            public string? Operation { get; set; }
            public string? Url { get; set; }
            public string? Content { get; set; }
        }
    }
}
