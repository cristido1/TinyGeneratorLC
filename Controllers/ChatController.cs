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

        public ChatController(
            DatabaseService database,
            LangChainKernelFactory kernelFactory,
            ICustomLogger logger)
        {
            _database = database;
            _kernelFactory = kernelFactory;
            _logger = logger;
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

                // Get model info
                var modelInfo = _database.GetModelInfo(request.Model);
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

                // Parse response
                var (textContent, _) = LangChainChatBridge.ParseChatResponse(response);
                var assistantMessage = textContent ?? "Nessuna risposta dal modello";

                // Add assistant response to history
                var assistantMsg = new Dictionary<string, object>
                {
                    { "role", "assistant" },
                    { "content", assistantMessage }
                };
                history.Add(assistantMsg);

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

        public class ChatRequest
        {
            public string? Model { get; set; }
            public string? Message { get; set; }
        }
    }
}
