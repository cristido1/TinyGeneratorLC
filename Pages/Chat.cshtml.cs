using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;
using System.Text.Json;

namespace TinyGenerator.Pages
{
    public class ChatModel : PageModel
    {
        private readonly DatabaseService _database;
        private readonly LangChainKernelFactory _kernelFactory;
        private readonly PersistentMemoryService _memoryService;
        private readonly ICustomLogger? _logger;

        public string? SelectedModel { get; set; }
        public ModelInfo? SelectedModelInfo { get; set; }
        public List<ModelInfo>? AvailableModels { get; set; }
        public List<ConversationMessage>? ConversationHistory { get; set; }

        public ChatModel(
            DatabaseService database,
            LangChainKernelFactory kernelFactory,
            PersistentMemoryService memoryService,
            ICustomLogger? logger = null)
        {
            _database = database;
            _kernelFactory = kernelFactory;
            _memoryService = memoryService;
            _logger = logger;
        }

        public void OnGet(string? model)
        {
            // Load available models
            AvailableModels = _database.ListModels();

            if (!string.IsNullOrEmpty(model) && AvailableModels.Any(m => m.Name == model))
            {
                SelectedModel = model;
                SelectedModelInfo = AvailableModels.FirstOrDefault(m => m.Name == model);
                
                // Load conversation history from session
                ConversationHistory = GetConversationHistory(model);
            }
        }

        public async Task<IActionResult> OnPostSendMessage(string model, string message)
        {
            try
            {
                // Get model info
                var modelInfo = _database.GetModelInfo(model);
                if (modelInfo == null)
                {
                    TempData["Error"] = "Modello non trovato";
                    return RedirectToPage("Chat", new { model });
                }

                // Get conversation history from session
                var history = GetConversationHistoryForApi(model);

                // Add user message
                history.Add(new Services.ConversationMessage
                {
                    Role = "user",
                    Content = message
                });
                await RememberChatAsync(model, "user", message);

                // Call model via LangChain bridge. If model supports tools, expose default tools (memory).
                var chatBridge = _kernelFactory.CreateChatBridge(model);
                var tools = _kernelFactory.GetDefaultToolSchemasForModel(model) ?? new List<Dictionary<string, object>>();
                var response = await chatBridge.CallModelWithToolsAsync(
                    history,
                    tools, // Default tools (may include memory if model supports tools)
                    CancellationToken.None);

                // Parse response and potential tool calls
                var (textContent, toolCalls) = LangChainChatBridge.ParseChatResponse(response);

                // Add assistant response
                history.Add(new Services.ConversationMessage
                {
                    Role = "assistant",
                    Content = textContent ?? "No response"
                });
                await RememberChatAsync(model, "assistant", textContent ?? "No response");

                // ReAct loop: execute tool calls, inject tool results and call model again
                int maxIterations = 3;
                int iteration = 0;
                var currentToolCalls = toolCalls ?? new List<ToolCallFromModel>();

                while (currentToolCalls.Any() && iteration < maxIterations)
                {
                    try
                    {
                        var toolNames = currentToolCalls.Select(tc => tc.ToolName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        var orchestrator = _kernelFactory.CreateOrchestrator(model, toolNames);

                        foreach (var tc in currentToolCalls)
                        {
                            var argsJson = tc.Arguments ?? "{}";
                            var resultJson = await orchestrator.ExecuteToolAsync(tc.ToolName, argsJson);

                            // Visible summary
                            history.Add(new Services.ConversationMessage
                            {
                                Role = "assistant",
                                Content = $"[Tool called: {tc.ToolName}] Params: {argsJson} | Result: {resultJson}"
                            });

                            // Tool role message for model consumption
                            history.Add(new Services.ConversationMessage
                            {
                                Role = "tool",
                                Content = resultJson
                            });
                        }

                        // Call model again with updated history and orchestrator's tool schemas
                        var toolSchemas = orchestrator.GetToolSchemas();
                        var nextResponse = await chatBridge.CallModelWithToolsAsync(history, toolSchemas, CancellationToken.None);
                        var (nextText, nextToolCalls) = LangChainChatBridge.ParseChatResponse(nextResponse);

                        history.Add(new Services.ConversationMessage
                        {
                            Role = "assistant",
                            Content = nextText ?? ""
                        });
                        await RememberChatAsync(model, "assistant", nextText ?? "");

                        currentToolCalls = nextToolCalls ?? new List<ToolCallFromModel>();
                        iteration++;
                    }
                    catch (Exception ex)
                    {
                        history.Add(new Services.ConversationMessage
                        {
                            Role = "assistant",
                            Content = $"[Tool execution error]: {ex.Message}"
                        });
                        break;
                    }
                }

                // Save updated history to session
                SaveConversationHistory(model, history);

                return RedirectToPage("Chat", new { model });
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Errore: {ex.Message}";
                return RedirectToPage("Chat", new { model });
            }
        }

        public IActionResult OnGetClearChat(string model, string? returnUrl)
        {
            if (!string.IsNullOrEmpty(model))
            {
                HttpContext.Session.Remove($"chat_{model}");
            }

            if (!string.IsNullOrEmpty(returnUrl))
            {
                return LocalRedirect(returnUrl);
            }

            return RedirectToPage("Chat", new { model });
        }

        private List<ConversationMessage> GetConversationHistory(string modelName)
        {
            var sessionKey = $"chat_{modelName}";
            
            if (HttpContext.Session.TryGetValue(sessionKey, out var data))
            {
                var json = System.Text.Encoding.UTF8.GetString(data);
                try
                {
                    return JsonSerializer.Deserialize<List<ConversationMessage>>(json) ?? new();
                }
                catch
                {
                    return new();
                }
            }
            
            return new();
        }

        private List<Services.ConversationMessage> GetConversationHistoryForApi(string modelName)
        {
            var sessionKey = $"chat_{modelName}";
            
            if (HttpContext.Session.TryGetValue(sessionKey, out var data))
            {
                var json = System.Text.Encoding.UTF8.GetString(data);
                try
                {
                    var messages = JsonSerializer.Deserialize<List<ConversationMessage>>(json) ?? new();
                    return messages.Select(m => new Services.ConversationMessage
                    {
                        Role = m.Role,
                        Content = m.Content
                    }).ToList();
                }
                catch
                {
                    return new();
                }
            }
            
            return new();
        }

        private void SaveConversationHistory(string modelName, List<Services.ConversationMessage> history)
        {
            var sessionKey = $"chat_{modelName}";
            var messages = history.Select(m => new ConversationMessage
            {
                Role = m.Role,
                Content = m.Content
            }).ToList();
            var json = JsonSerializer.Serialize(messages);
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
                await _memoryService.SaveAsync("chat", text, new { role, model });
            }
            catch (Exception ex)
            {
                _logger?.Log("Warn", "ChatPage", $"Failed to store chat memory: {ex.Message}");
            }
        }

        public class ConversationMessage
        {
            public string Role { get; set; } = string.Empty;
            public string Content { get; set; } = string.Empty;
        }
    }
}
