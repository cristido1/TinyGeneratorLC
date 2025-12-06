using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Models;
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
        private readonly ProgressService? _progressService;
        public double Temperature { get; set; } = 0.7;
        public double TopP { get; set; } = 1.0;
        public int MaxResponseTokens { get; set; } = 8000;

        public LangChainChatBridge(
            string modelEndpoint,
            string modelId,
            string apiKey,
            HttpClient? httpClient = null,
            ICustomLogger? logger = null,
            ProgressService? progressService = null)
        {
            // Normalize endpoint - don't add /v1 suffix, just use endpoint as-is
            _modelEndpoint = new Uri(modelEndpoint.TrimEnd('/'));
            _modelId = modelId;
            _apiKey = apiKey;
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            _logger = logger;
            _progressService = progressService;
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
            if (_progressService != null)
            {
                await _progressService.ModelRequestStartedAsync(_modelId);
            }

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
                        { "temperature", Temperature },
                        { "top_p", TopP }
                    };

                    // If we have tools, pass them
                    if (tools.Any())
                    {
                        requestBody["tools"] = tools;
                    }
                    // Note: No longer forcing JSON format when no tools are present
                    // This allows natural text responses in chat mode

                    request = requestBody;
                    fullUrl = new Uri(_modelEndpoint, "/api/chat").ToString();
                }
                else
                {
                    _logger?.Log("Info", "LangChainBridge", $"Using OpenAI-compatible endpoint for {_modelId}");
                    
                    // Determine if model uses new parameter name (o1, gpt-4o series)
                    bool usesNewTokenParam = _modelId.Contains("o1", StringComparison.OrdinalIgnoreCase) ||
                                             _modelId.Contains("gpt-4o", StringComparison.OrdinalIgnoreCase) ||
                                             _modelId.Contains("gpt-5", StringComparison.OrdinalIgnoreCase);
                    
                    // Serialize messages properly for OpenAI format
                    var serializedMessages = messages.Select(m =>
                    {
                        var msgDict = new Dictionary<string, object>
                        {
                            { "role", m.Role },
                            { "content", m.Content ?? string.Empty }
                        };
                        
                        // Add tool_calls for assistant messages that have them
                        if (m.Role == "assistant" && m.ToolCalls != null && m.ToolCalls.Any())
                        {
                            msgDict["tool_calls"] = m.ToolCalls.Select(tc => new Dictionary<string, object>
                            {
                                { "id", tc.Id },
                                { "type", "function" },
                                { "function", new Dictionary<string, object>
                                    {
                                        { "name", tc.ToolName },
                                        { "arguments", tc.Arguments }
                                    }
                                }
                            }).ToList();
                        }
                        
                        // Add tool_call_id for tool messages
                        if (m.Role == "tool" && !string.IsNullOrEmpty(m.ToolCallId))
                        {
                            msgDict["tool_call_id"] = m.ToolCallId;
                        }
                        
                        return msgDict;
                    }).ToList();
                    
                    var requestDict = new Dictionary<string, object>
                    {
                        { "model", _modelId },
                        { "messages", serializedMessages },
                        { "tools", tools },
                        { "temperature", Temperature },
                        { "top_p", TopP }
                    };

                    if (tools.Any())
                    {
                        requestDict["tools"] = tools;
                    }
                    
                    // Add correct token limit parameter based on model
                    if (usesNewTokenParam)
                    {
                        requestDict["max_completion_tokens"] = MaxResponseTokens;
                    }
                    else
                    {
                        requestDict["max_tokens"] = MaxResponseTokens;
                    }
                    
                    request = requestDict;
                    fullUrl = new Uri(_modelEndpoint, "/v1/chat/completions").ToString();
                }

                var requestJson = JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = false });
                
                var jsonContent = new StringContent(
                    requestJson,
                    System.Text.Encoding.UTF8,
                    "application/json");

                _logger?.Log("Info", "LangChainBridge", $"Calling model {_modelId} at {fullUrl}");
                _logger?.LogRequestJson(_modelId, requestJson);

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
                _logger?.LogResponseJson(_modelId, responseContent);

                if (!response.IsSuccessStatusCode)
                {
                    _logger?.Log("Error", "LangChainBridge", 
                        $"Model request failed with status {response.StatusCode}: {responseContent}");
                    
                    // Check if the error indicates the model doesn't support tools
                    if (responseContent.Contains("does not support tools", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ModelNoToolsSupportException(
                            _modelId,
                            $"Model request failed with status {response.StatusCode}: {responseContent}");
                    }
                    
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
            finally
            {
                if (_progressService != null)
                {
                    await _progressService.ModelRequestFinishedAsync(_modelId);
                }
            }
        }

        /// <summary>
        /// Parse chat completion response.
        /// Handles both OpenAI format (with choices/tool_calls) and Ollama format (with message).
        /// Step 1: Try structured deserialization via ApiResponse
        /// Step 2: Fallback to manual parsing if deserialization fails
        /// </summary>
        public static (string? textContent, List<ToolCallFromModel> toolCalls) ParseChatResponse(string? jsonResponse)
        {
            var toolCalls = new List<ToolCallFromModel>();
            string? textContent = null;

            if (string.IsNullOrWhiteSpace(jsonResponse)) return (textContent, toolCalls);

            // Step 1: Try structured deserialization to ApiResponse
            try
            {
                var apiResponse = JsonSerializer.Deserialize<ApiResponse>(jsonResponse);
                
                if (apiResponse?.Message != null)
                {
                    textContent = apiResponse.Message.Content;

                    // Extract tool_calls from structured response
                    if (apiResponse.Message.ToolCalls != null && apiResponse.Message.ToolCalls.Count > 0)
                    {
                        foreach (var tc in apiResponse.Message.ToolCalls)
                        {
                            if (tc.Function == null) continue;
                            
                            var argsJson = "{}";
                            if (tc.Function.Arguments != null)
                            {
                                // Arguments can be object or already serialized string
                                if (tc.Function.Arguments is JsonElement jsonElem)
                                {
                                    argsJson = jsonElem.GetRawText();
                                }
                                else if (tc.Function.Arguments is string str)
                                {
                                    argsJson = str;
                                }
                                else
                                {
                                    argsJson = JsonSerializer.Serialize(tc.Function.Arguments);
                                }
                            }

                            toolCalls.Add(new ToolCallFromModel
                            {
                                Id = tc.Id ?? Guid.NewGuid().ToString(),
                                ToolName = tc.Function.Name ?? "unknown",
                                Arguments = argsJson
                            });
                        }
                    }

                    // Success with ApiResponse deserialization
                    return (textContent, toolCalls);
                }
            }
            catch (JsonException)
            {
                // ApiResponse deserialization failed, proceed to fallback parsing
            }

            // Step 2: Fallback manual parsing
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

                        // Some models return tool_calls serialized as JSON inside content; try to parse them
                        if (!string.IsNullOrWhiteSpace(textContent) && toolCalls.Count == 0)
                        {
                            TryParseEmbeddedToolCalls(textContent, toolCalls);
                        }
                    }

                    // Extract tool calls from Ollama format
                    if (ollamaMessage.TryGetProperty("tool_calls", out var ollamaCalls))
                    {
                        foreach (var call in ollamaCalls.EnumerateArray())
                        {
                            if (call.TryGetProperty("function", out var func))
                            {
                                var argsJson = "{}";
                                if (func.TryGetProperty("arguments", out var argsElement))
                                {
                                    // Arguments can be JSON object or string
                                    if (argsElement.ValueKind == JsonValueKind.Object)
                                    {
                                        argsJson = argsElement.GetRawText();
                                    }
                                    else if (argsElement.ValueKind == JsonValueKind.String)
                                    {
                                        argsJson = argsElement.GetString() ?? "{}";
                                    }
                                }

                                toolCalls.Add(new ToolCallFromModel
                                {
                                    Id = (call.TryGetProperty("id", out var id) ? id.GetString() : null) ?? Guid.NewGuid().ToString(),
                                    ToolName = (func.TryGetProperty("name", out var name) ? name.GetString() : null) ?? "unknown",
                                    Arguments = argsJson
                                });
                            }
                        }
                    }
                }
                // Try simple "response" format (some models return just { "response": "text" })
                else if (root.TryGetProperty("response", out var responseElement))
                {
                    textContent = responseElement.GetString();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse chat response: {ex.Message}");
            }

            return (textContent, toolCalls);
        }

        private static void TryParseEmbeddedToolCalls(string content, List<ToolCallFromModel> toolCalls)
        {
            ParseToolCallsFromString(content, toolCalls);
        }

        private static void ParseToolCallsFromString(string? content, List<ToolCallFromModel> toolCalls)
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            var trimmed = content.Trim();
            if (!(trimmed.StartsWith("{") || trimmed.StartsWith("["))) return;
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                ParseToolCallsFromElement(doc.RootElement, toolCalls);
            }
            catch
            {
                // ignore parse errors
            }
        }

        private static void ParseToolCallsFromElement(JsonElement root, List<ToolCallFromModel> toolCalls)
        {
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var call in calls.EnumerateArray())
                    {
                        if (!call.TryGetProperty("function", out var func)) continue;

                        var argsJson = "{}";
                        if (func.TryGetProperty("arguments", out var argsElement))
                        {
                            if (argsElement.ValueKind == JsonValueKind.Object)
                                argsJson = argsElement.GetRawText();
                            else if (argsElement.ValueKind == JsonValueKind.String)
                                argsJson = argsElement.GetString() ?? "{}";
                        }

                        var toolName = (func.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null) ?? "unknown";
                        var toolId = (call.TryGetProperty("id", out var idProp) ? idProp.GetString() : null) ?? Guid.NewGuid().ToString();

                        if (toolCalls.Any(tc => tc.Id == toolId && tc.Arguments == argsJson))
                            continue;

                        toolCalls.Add(new ToolCallFromModel
                        {
                            Id = toolId,
                            ToolName = toolName,
                            Arguments = argsJson
                        });
                    }
                }

                if (root.TryGetProperty("content", out var contentProp))
                {
                    if (contentProp.ValueKind == JsonValueKind.String)
                    {
                        ParseToolCallsFromString(contentProp.GetString(), toolCalls);
                    }
                    else if (contentProp.ValueKind == JsonValueKind.Object || contentProp.ValueKind == JsonValueKind.Array)
                    {
                        ParseToolCallsFromElement(contentProp, toolCalls);
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    ParseToolCallsFromElement(item, toolCalls);
                }
            }
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
            ICustomLogger? logger = null,
            int? maxTokens = null)
        {
            _logger = logger;
            _tools = tools;
            _modelBridge = new LangChainChatBridge(modelEndpoint, modelId, apiKey, httpClient, logger);
            if (maxTokens.HasValue && maxTokens.Value > 0)
            {
                _modelBridge.MaxResponseTokens = Math.Max(_modelBridge.MaxResponseTokens, maxTokens.Value);
            }
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
