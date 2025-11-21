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
            _modelEndpoint = new Uri(modelEndpoint.EndsWith("/v1") ? modelEndpoint : modelEndpoint.TrimEnd('/') + "/v1");
            _modelId = modelId;
            _apiKey = apiKey;
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            _logger = logger;
        }

        /// <summary>
        /// Send chat request to model with tools and get response.
        /// Compatible with OpenAI and Ollama /v1/chat/completions endpoint.
        /// </summary>
        public async Task<string> CallModelWithToolsAsync(
            List<ConversationMessage> messages,
            List<Dictionary<string, object>> tools,
            CancellationToken ct = default)
        {
            try
            {
                var request = new
                {
                    model = _modelId,
                    messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToList(),
                    tools = tools,
                    tool_choice = "auto",
                    temperature = 0.7,
                    max_tokens = 2000
                };

                var jsonContent = new StringContent(
                    JsonSerializer.Serialize(request),
                    System.Text.Encoding.UTF8,
                    "application/json");

                var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(_modelEndpoint, "chat/completions"))
                {
                    Content = jsonContent
                };

                // Add auth header if not Ollama (Ollama doesn't require it)
                if (!_apiKey.Contains("ollama", StringComparison.OrdinalIgnoreCase))
                {
                    httpRequest.Headers.Add("Authorization", $"Bearer {_apiKey}");
                }

                _logger?.Log("Info", "LangChainBridge", $"Calling model {_modelId} with {tools.Count} tools");

                var response = await _httpClient.SendAsync(httpRequest, ct);
                response.EnsureSuccessStatusCode();

                var responseContent = await response.Content.ReadAsStringAsync(ct);
                _logger?.Log("Info", "LangChainBridge", $"Model responded (status={response.StatusCode})");

                return responseContent;
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LangChainBridge", $"Model call failed: {ex.Message}", ex.ToString());
                throw;
            }
        }

        /// <summary>
        /// Parse OpenAI-format chat completion response to extract tool calls and text.
        /// </summary>
        public static (string? textContent, List<ToolCallFromModel> toolCalls) ParseChatResponse(string jsonResponse)
        {
            var toolCalls = new List<ToolCallFromModel>();
            string? textContent = null;

            try
            {
                var doc = JsonDocument.Parse(jsonResponse);
                var choices = doc.RootElement.GetProperty("choices").EnumerateArray();

                foreach (var choice in choices)
                {
                    var message = choice.GetProperty("message");

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
                            var func = call.GetProperty("function");
                            toolCalls.Add(new ToolCallFromModel
                            {
                                Id = call.GetProperty("id").GetString() ?? Guid.NewGuid().ToString(),
                                ToolName = func.GetProperty("name").GetString() ?? "unknown",
                                Arguments = func.GetProperty("arguments").GetString() ?? "{}"
                            });
                        }
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
