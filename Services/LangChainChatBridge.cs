using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Services
{
    /// <summary>
    /// Bridge between LangChain ChatModel and ReActLoop.
    /// Handles model communication via OpenAI-compatible API (works with Ollama, OpenAI, Azure).
    /// 
    /// This is a placeholder that prepares the infrastructure. Full integration requires:
    /// - HttpClient calls to OpenAI-compatible endpoints
    /// - Proper message serialization with tool_choice="auto"
    /// - Function calling response parsing
    /// </summary>
    public class LangChainChatBridge
    {
        private readonly Uri _modelEndpoint;
        private readonly string _modelId;
        private readonly string _apiKey;
        private readonly HttpClient _httpClient;
        private readonly ICustomLogger? _logger;

        public LangChainChatBridge(
            string modelEndpoint,
            string modelId,
            string apiKey,
            HttpClient? httpClient = null,
            ICustomLogger? logger = null)
        {
            // Normalize endpoint - don't add /v1 suffix, just use endpoint as-is
            _modelEndpoint = new Uri(modelEndpoint.TrimEnd('/'));
            _modelId = modelId;
            _apiKey = apiKey;
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            _logger = logger;
        }

        /// <summary>
        /// Test connection to the model endpoint.
        /// For Ollama, checks if model is loaded.
        /// </summary>
        public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
        {
            try
            {
                _logger?.Log("Info", "LangChainBridge", $"Testing connection to {_modelEndpoint} for model {_modelId}");

                // For Ollama, test with /api/tags to see if model is available
                if (_modelEndpoint.ToString().Contains("11434", StringComparison.OrdinalIgnoreCase))
                {
                    var tagsUrl = new Uri(_modelEndpoint, "/api/tags").ToString();
                    var response = await _httpClient.GetAsync(tagsUrl, ct);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync(ct);
                        _logger?.Log("Info", "LangChainBridge", $"Ollama tags response: {content}");
                        return true;
                    }
                    else
                    {
                        _logger?.Log("Error", "LangChainBridge", $"Ollama connection failed: {response.StatusCode}");
                        return false;
                    }
                }
                
                // For OpenAI-compatible, test with a simple text request
                var testRequest = new
                {
                    model = _modelId,
                    messages = new[] { new { role = "user", content = "test" } }
                };

                var content2 = new StringContent(
                    JsonSerializer.Serialize(testRequest),
                    System.Text.Encoding.UTF8,
                    "application/json");

                var testUrl = new Uri(_modelEndpoint, "/v1/chat/completions").ToString();
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, testUrl) { Content = content2 };

                if (!_apiKey.Contains("ollama", StringComparison.OrdinalIgnoreCase))
                {
                    httpRequest.Headers.Add("Authorization", $"Bearer {_apiKey}");
                }

                var testResponse = await _httpClient.SendAsync(httpRequest, ct);
                _logger?.Log("Info", "LangChainBridge", $"Test connection status: {testResponse.StatusCode}");
                return testResponse.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LangChainBridge", $"Connection test failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Send chat request to model with tools and get response.
        /// For Ollama: sends with format=json when no tools, tools when available
        /// For OpenAI: sends with tool_choice="auto"
        /// </summary>
        public async Task<string> CallModelWithToolsAsync(
            List<ConversationMessage> messages,
            List<Dictionary<string, object>> tools,
            CancellationToken ct = default)
        {
            try
            {
                var isOllama = _modelEndpoint.ToString().Contains("11434", StringComparison.OrdinalIgnoreCase);
                
                // For Ollama, create request with format or tools
                // For OpenAI, include tools with tool_choice="auto"
                object request;
                string fullUrl;

                if (isOllama)
                {
                    _logger?.Log("Info", "LangChainBridge", $"Using Ollama endpoint for {_modelId} with {tools.Count} tools");
                    
                    var requestBody = new Dictionary<string, object>
                    {
                        { "model", _modelId },
                        { "messages", messages.Select(m => new { role = m.Role, content = m.Content }).ToList() },
                        { "stream", false },
                        { "temperature", 0.7 }
                    };

                    // If we have tools, pass them; otherwise request JSON format for structured output
                    if (tools.Any())
                    {
                        requestBody["tools"] = tools;
                    }
                    else
                    {
                        // No tools - request JSON format for structured output (Ollama feature)
                        requestBody["format"] = "json";
                        _logger?.Log("Info", "LangChainBridge", "No tools available, requesting JSON format from model");
                    }

                    request = requestBody;
                    fullUrl = new Uri(_modelEndpoint, "/api/chat").ToString();
                }
                else
                {
                    _logger?.Log("Info", "LangChainBridge", $"Using OpenAI-compatible endpoint for {_modelId}");
                    
                    request = new
                    {
                        model = _modelId,
                        messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToList(),
                        tools = tools,
                        tool_choice = "auto",
                        temperature = 0.7,
                        max_tokens = 2000
                    };
                    fullUrl = new Uri(_modelEndpoint, "/v1/chat/completions").ToString();
                }

                var requestJson = JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = false });
                
                var jsonContent = new StringContent(
                    requestJson,
                    System.Text.Encoding.UTF8,
                    "application/json");

                _logger?.Log("Info", "LangChainBridge", $"Calling model {_modelId} at {fullUrl}");
                _logger?.Log("Info", "LangChainBridge", $"Request payload: {requestJson}");

                var httpRequest = new HttpRequestMessage(HttpMethod.Post, fullUrl)
                {
                    Content = jsonContent
                };

                // Add auth header if not Ollama (Ollama doesn't require it)
                if (!isOllama && !_apiKey.Contains("ollama", StringComparison.OrdinalIgnoreCase))
                {
                    httpRequest.Headers.Add("Authorization", $"Bearer {_apiKey}");
                }

                var response = await _httpClient.SendAsync(httpRequest, ct);
                
                var responseContent = await response.Content.ReadAsStringAsync(ct);
                
                _logger?.Log("Info", "LangChainBridge", $"Model responded (status={response.StatusCode})");
                _logger?.Log("Info", "LangChainBridge", $"Response payload: {responseContent}");

                if (!response.IsSuccessStatusCode)
                {
                    _logger?.Log("Error", "LangChainBridge", 
                        $"Model request failed with status {response.StatusCode}: {responseContent}");
                    throw new HttpRequestException(
                        $"Model request failed with status {response.StatusCode}: {responseContent}");
                }

                return responseContent;
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LangChainBridge", $"Model call failed: {ex.Message}", ex.ToString());
                throw;
            }
        }

        /// <summary>
        /// Parse chat completion response.
        /// Handles both OpenAI format (with choices/tool_calls) and Ollama format (with message).
        /// </summary>
        public static (string? textContent, List<ToolCallFromModel> toolCalls) ParseChatResponse(string jsonResponse)
        {
            var toolCalls = new List<ToolCallFromModel>();
            string? textContent = null;

            try
            {
                var doc = JsonDocument.Parse(jsonResponse);
                var root = doc.RootElement;

                // Try OpenAI format first (has "choices" array)
                if (root.TryGetProperty("choices", out var choicesElement))
                {
                    var choices = choicesElement.EnumerateArray();

                    foreach (var choice in choices)
                    {
                        if (choice.TryGetProperty("message", out var message))
                        {
                            // Extract text content
                            if (message.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
                            {
                                textContent = content.GetString();
                            }

                            // Extract tool calls
                            if (message.TryGetProperty("tool_calls", out var calls))
                            {
                                foreach (var call in calls.EnumerateArray())
                                {
                                    if (call.TryGetProperty("function", out var func))
                                    {
                                        toolCalls.Add(new ToolCallFromModel
                                        {
                                            Id = (call.TryGetProperty("id", out var id) ? id.GetString() : null) ?? Guid.NewGuid().ToString(),
                                            ToolName = (func.TryGetProperty("name", out var name) ? name.GetString() : null) ?? "unknown",
                                            Arguments = (func.TryGetProperty("arguments", out var args) ? args.GetString() : null) ?? "{}"
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
                // Try Ollama format (has "message" object directly)
                else if (root.TryGetProperty("message", out var ollamaMessage))
                {
                    if (ollamaMessage.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
                    {
                        textContent = content.GetString();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse chat response: {ex.Message}");
            }

            return (textContent, toolCalls);
        }
    }

    public class ToolCallFromModel
    {
        public string Id { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;
        public string Arguments { get; set; } = "{}";
    }

    /// <summary>
    /// Full orchestrator: ReAct loop + LangChain chat bridge.
    /// This is the main entry point for story generation with proper function calling.
    /// </summary>
    public class LangChainStoryOrchestrator
    {
        private readonly LangChainChatBridge _modelBridge;
        private readonly HybridLangChainOrchestrator _tools;
        private readonly ReActLoopOrchestrator _reactLoop;
        private readonly ICustomLogger? _logger;

        public LangChainStoryOrchestrator(
            string modelEndpoint,
            string modelId,
            string apiKey,
            HybridLangChainOrchestrator tools,
            HttpClient? httpClient = null,
            ICustomLogger? logger = null)
        {
            _logger = logger;
            _tools = tools;
            _modelBridge = new LangChainChatBridge(modelEndpoint, modelId, apiKey, httpClient, logger);
            _reactLoop = new ReActLoopOrchestrator(tools, logger);
        }

        /// <summary>
        /// Execute story generation with full ReAct loop and tool use.
        /// </summary>
        public async Task<string> GenerateStoryAsync(
            string theme,
            string systemPrompt = "You are a creative story writer. Use available tools to enhance and structure the story.",
            CancellationToken ct = default)
        {
            try
            {
                _logger?.Log("Info", "StoryOrchestrator", $"Starting story generation for theme: {theme}");

                _reactLoop.ClearHistory();

                // Prepare messages with system prompt
                var messages = new List<ConversationMessage>
                {
                    new ConversationMessage { Role = "system", Content = systemPrompt },
                    new ConversationMessage { Role = "user", Content = $"Generate a story about: {theme}" }
                };

                // Get tool schemas
                var toolSchemas = _tools.GetToolSchemas();

                // TODO: Implement actual ReAct loop with model integration
                // For now, this is a placeholder structure
                var story = await GenerateWithModelIntegrationAsync(messages, toolSchemas, ct);

                return story;
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "StoryOrchestrator", $"Story generation failed: {ex.Message}", ex.ToString());
                return $"Error: {ex.Message}";
            }
        }

        private async Task<string> GenerateWithModelIntegrationAsync(
            List<ConversationMessage> messages,
            List<Dictionary<string, object>> toolSchemas,
            CancellationToken ct)
        {
            // This method would:
            // 1. Call model with messages + tool definitions
            // 2. Parse response for tool calls
            // 3. Execute tools
            // 4. Loop until model returns final story
            
            // For now, return a placeholder
            return await Task.FromResult("Story generation with LangChain integration pending full model bridge implementation");
        }
    }
}
