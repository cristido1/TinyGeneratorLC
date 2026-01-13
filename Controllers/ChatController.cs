using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using TinyGenerator.Services;

namespace TinyGenerator.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ChatController : ControllerBase
    {
        private readonly DatabaseService _database;
        private readonly LangChainKernelFactory _kernelFactory;
        private readonly ICustomLogger _logger;
        private readonly PersistentMemoryService _memory;

        public ChatController(
            DatabaseService database,
            LangChainKernelFactory kernelFactory,
            ICustomLogger logger,
            PersistentMemoryService memory)
        {
            _database = database;
            _kernelFactory = kernelFactory;
            _logger = logger;
            _memory = memory;
        }

        [HttpGet("test")]
        public IActionResult Test()
        {
            return Ok(new { message = "Chat controller is working!" });
        }

        [HttpPost("send")]
        public async Task<IActionResult> SendMessage([FromBody] ChatRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.Model) || string.IsNullOrEmpty(request.Message))
                {
                    return BadRequest(new { success = false, error = "Model and message are required" });
                }

                // Get model info (resolve by name via ListModels)
                var modelInfo = _database.ListModels().FirstOrDefault(m => string.Equals(m.Name, request.Model, StringComparison.OrdinalIgnoreCase));
                if (modelInfo == null)
                {
                    return BadRequest(new { success = false, error = "Model not found" });
                }

                // Load conversation history from session
                var sessionKey = $"chat_{request.Model}";
                var history = GetConversationHistory(sessionKey);

                // Add user message to history
                var userMsg = new Dictionary<string, object>
                {
                    { "role", "user" },
                    { "content", request.Message }
                };
                history.Add(userMsg);
                await RememberChatAsync(request.Model, "user", request.Message);

                // Create ConversationMessage list for API
                var messages = new List<Services.ConversationMessage>();
                foreach (var m in history)
                {
                    try
                    {
                        var role = m.ContainsKey("role") ? m["role"]?.ToString() ?? "user" : "user";
                        var content = m.ContainsKey("content") ? m["content"]?.ToString() ?? "" : "";
                        messages.Add(new Services.ConversationMessage
                        {
                            Role = role,
                            Content = content
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger?.Log("Error", "ChatController", $"Error parsing message: {ex.Message}");
                    }
                }

                // Call model and expose default tools (memory) if the model supports tools
                var chatBridge = _kernelFactory.CreateChatBridge(request.Model);
                var tools = _kernelFactory.GetDefaultToolSchemasForModel(request.Model) ?? new List<Dictionary<string, object>>();
                var response = await chatBridge.CallModelWithToolsAsync(messages, tools, CancellationToken.None);

                // ReAct loop: if the model returns tool_calls, execute them and re-call the model
                var (textContent, toolCalls) = LangChainChatBridge.ParseChatResponse(response);
                var assistantMessage = textContent ?? "Nessuna risposta dal modello";

                // Add assistant response to history
                history.Add(new Dictionary<string, object>
                {
                    { "role", "assistant" },
                    { "content", assistantMessage }
                });
                await RememberChatAsync(request.Model, "assistant", assistantMessage);

                int maxIterations = 3;
                int iteration = 0;
                var currentToolCalls = toolCalls ?? new List<ToolCallFromModel>();

                while (currentToolCalls.Any() && iteration < maxIterations)
                {
                    try
                    {
                        // Create orchestrator with tools required by current tool calls
                        var toolNames = currentToolCalls.Select(tc => tc.ToolName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        var orchestrator = _kernelFactory.CreateOrchestrator(request.Model, toolNames);

                        // Execute each tool and append results as tool messages
                        foreach (var tc in currentToolCalls)
                        {
                            var argsJson = tc.Arguments ?? "{}";
                            var resultJson = await orchestrator.ExecuteToolAsync(tc.ToolName, argsJson);

                            // Add visible summary for the user
                            history.Add(new Dictionary<string, object>
                            {
                                { "role", "assistant" },
                                { "content", $"[Tool called: {tc.ToolName}] Params: {argsJson} | Result: {resultJson}" }
                            });

                            // Add a tool role message so the model can consume the tool output on next call
                            history.Add(new Dictionary<string, object>
                            {
                                { "role", "tool" },
                                { "content", resultJson }
                            });
                        }

                        // Rebuild messages for the next model call from history
                        var nextMessages = new List<Services.ConversationMessage>();
                        foreach (var m in history)
                        {
                            try
                            {
                                var role = m.ContainsKey("role") ? m["role"]?.ToString() ?? "user" : "user";
                                var content = m.ContainsKey("content") ? m["content"]?.ToString() ?? "" : "";
                                nextMessages.Add(new Services.ConversationMessage { Role = role, Content = content });
                            }
                            catch (Exception ex)
                            {
                                _logger?.Log("Error", "ChatController", $"Error parsing message for next call: {ex.Message}");
                            }
                        }

                        // Get tool schemas from orchestrator to pass to the model
                        var toolSchemas = orchestrator.GetToolSchemas();

                        // Call model again with updated history
                        var nextResponse = await chatBridge.CallModelWithToolsAsync(nextMessages, toolSchemas, CancellationToken.None);
                        var (nextText, nextToolCalls) = LangChainChatBridge.ParseChatResponse(nextResponse);

                        // Append assistant response
                        history.Add(new Dictionary<string, object>
                        {
                            { "role", "assistant" },
                            { "content", nextText ?? "" }
                        });
                        await RememberChatAsync(request.Model, "assistant", nextText ?? "");

                        // Prepare for possible next iteration
                        currentToolCalls = nextToolCalls ?? new List<ToolCallFromModel>();
                        iteration++;
                    }
                    catch (Exception ex)
                    {
                        _logger?.Log("Error", "ChatController", $"Tool execution loop failed: {ex.Message}", ex.ToString());
                        history.Add(new Dictionary<string, object>
                        {
                            { "role", "assistant" },
                            { "content", $"[Tool execution error]: {ex.Message}" }
                        });
                        break;
                    }
                }

                // Save to session
                SaveConversationHistory(sessionKey, history);

                return Ok(new { success = true, message = assistantMessage });
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "ChatController", $"Error sending message: {ex.Message}", ex.ToString());
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        [HttpPost("clear")]
        public IActionResult ClearChat([FromQuery] string model)
        {
            try
            {
                if (string.IsNullOrEmpty(model))
                {
                    return BadRequest(new { success = false, error = "Model is required" });
                }

                var sessionKey = $"chat_{model}";
                HttpContext.Session.Remove(sessionKey);

                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "ChatApi", $"Error clearing chat: {ex.Message}");
                return BadRequest(new { success = false, error = ex.Message });
            }
        }

        private List<Dictionary<string, object>> GetConversationHistory(string sessionKey)
        {
            if (HttpContext.Session.TryGetValue(sessionKey, out var data))
            {
                var json = System.Text.Encoding.UTF8.GetString(data);
                try
                {
                    return JsonSerializer.Deserialize<List<Dictionary<string, object>>>(json) ?? new();
                }
                catch
                {
                    return new();
                }
            }

            return new();
        }

        private void SaveConversationHistory(string sessionKey, List<Dictionary<string, object>> history)
        {
            var json = JsonSerializer.Serialize(history);
            HttpContext.Session.SetString(sessionKey, json);
        }

        private async Task RememberChatAsync(string model, string role, string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            try
            {
                await _memory.SaveAsync("chat", text, new { role, model });
            }
            catch (Exception ex)
            {
                _logger?.Log("Warn", "ChatController", $"Failed to store chat memory: {ex.Message}");
            }
        }

        public class ChatRequest
        {
            public string? Model { get; set; }
            public string? Message { get; set; }
        }
    }
}
