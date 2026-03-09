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
            InitializePage(model);
        }

        public async Task<IActionResult> OnPostSendMessage(string? model, string message)
        {
            try
            {
                // Fallback: try to get model from form if route param is empty
                if (string.IsNullOrWhiteSpace(model) && Request.Form.ContainsKey("model"))
                {
                    model = Request.Form["model"].ToString();
                }

                if (string.IsNullOrWhiteSpace(model))
                {
                    TempData["Error"] = "Nessun modello selezionato";
                    return RedirectToPage();
                }

                var modelLookup = model.Trim();
                var modelInfo = ResolveModelInfo(modelLookup);
                if (modelInfo == null)
                {
                    TempData["Error"] = $"Modello '{model}' non trovato nel database";
                    return RedirectToPage(new { model = model });
                }

                var effectiveModel = ResolveModelKeyForProvider(modelInfo);
                if (string.IsNullOrWhiteSpace(effectiveModel))
                {
                    TempData["Error"] = $"Il modello '{modelInfo.Name}' non ha un identificativo valido (call_name/name)";
                    return RedirectToPage(new { model = modelInfo.Name });
                }

                // Get conversation history from session
                var history = GetConversationHistoryForApi(modelInfo.Name);

                // Add user message
                history.Add(new Services.ConversationMessage
                {
                    Role = "user",
                    Content = message
                });
                await RememberChatAsync(modelInfo.Name, "user", message);

                using var chatScope = LogScope.Push(
                    "chat",
                    null,
                    null,
                    null,
                    "chat",
                    agentRole: "chat");

                // Call model via LangChain bridge. If model supports tools, expose default tools (memory).
                var chatBridge = _kernelFactory.CreateChatBridge(effectiveModel);
                var tools = _kernelFactory.GetDefaultToolSchemasForModel(effectiveModel) ?? new List<Dictionary<string, object>>();
                var response = await chatBridge.CallModelWithToolsAsync(
                    history,
                    tools, // Default tools (may include memory if model supports tools)
                    CancellationToken.None,
                    skipResponseChecker: true,
                    skipResponseValidation: true);

                // Parse response and potential tool calls
                var (textContent, toolCalls) = LangChainChatBridge.ParseChatResponse(response);

                // Add assistant response
                history.Add(new Services.ConversationMessage
                {
                    Role = "assistant",
                    Content = textContent ?? "No response"
                });
                await RememberChatAsync(modelInfo.Name, "assistant", textContent ?? "No response");

                // ReAct loop: execute tool calls, inject tool results and call model again
                int maxIterations = 3;
                int iteration = 0;
                var currentToolCalls = toolCalls ?? new List<ToolCallFromModel>();

                while (currentToolCalls.Any() && iteration < maxIterations)
                {
                    try
                    {
                        var toolNames = currentToolCalls.Select(tc => tc.ToolName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        var orchestrator = _kernelFactory.CreateOrchestrator(effectiveModel, toolNames);

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
                        var nextResponse = await chatBridge.CallModelWithToolsAsync(
                            history,
                            toolSchemas,
                            CancellationToken.None,
                            skipResponseChecker: true,
                            skipResponseValidation: true);
                        var (nextText, nextToolCalls) = LangChainChatBridge.ParseChatResponse(nextResponse);

                        history.Add(new Services.ConversationMessage
                        {
                            Role = "assistant",
                            Content = nextText ?? ""
                        });
                        await RememberChatAsync(modelInfo.Name, "assistant", nextText ?? "");

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
                SaveConversationHistory(modelInfo.Name, history);

                // Use PRG pattern to avoid form resubmission and preserve model in URL
                return RedirectToPage(new { model = modelInfo.Name });
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Errore: {ex.Message}";
                return RedirectToPage(new { model = model });
            }
        }

        public async Task<IActionResult> OnGetClearChat(string model, string? returnUrl)
        {
            if (!string.IsNullOrEmpty(model))
            {
                // Also attempt to remove persisted memories stored in PersistentMemoryService
                try
                {
                    var apiHistory = GetConversationHistoryForApi(model);
                    if (apiHistory != null && apiHistory.Count > 0)
                    {
                        foreach (var m in apiHistory)
                        {
                            try
                            {
                                await _memoryService.DeleteAsync("chat", m.Content).ConfigureAwait(false);
                            }
                            catch (Exception exInner)
                            {
                                _logger?.Log("Warn", "ChatPage", $"Failed to delete persisted memory: {exInner.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Log("Warn", "ChatPage", $"Error while deleting persisted chat memory: {ex.Message}");
                }

                HttpContext.Session.Remove($"chat_{model}");
            }

            if (!string.IsNullOrEmpty(returnUrl))
            {
                return LocalRedirect(returnUrl);
            }

            return Redirect($"/Chat?model={System.Net.WebUtility.UrlEncode(model)}");
        }

        private void InitializePage(string? model)
        {
            // Load available models
            AvailableModels = _database.ListModels();

            // If route parameter is empty, try fallback to querystring (safer for names containing slashes)
            if (string.IsNullOrWhiteSpace(model))
            {
                if (Request?.Query != null && Request.Query.ContainsKey("model"))
                {
                    var q = Request.Query["model"].ToString();
                    if (!string.IsNullOrWhiteSpace(q))
                    {
                        model = System.Net.WebUtility.UrlDecode(q);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(model))
            {
                var modelInfo = ResolveModelInfo(model.Trim());
                if (modelInfo != null)
                {
                    SelectedModel = modelInfo.Name;
                    SelectedModelInfo = modelInfo;
                    ConversationHistory = GetConversationHistory(modelInfo.Name);
                }
            }
        }

        private ModelInfo? ResolveModelInfo(string lookup)
        {
            var models = _database.ListModels();
            return models.FirstOrDefault(m =>
                string.Equals(m.Name, lookup, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.CallName, lookup, StringComparison.OrdinalIgnoreCase));
        }

        private static string ResolveModelKeyForProvider(ModelInfo modelInfo)
        {
            if (!string.IsNullOrWhiteSpace(modelInfo.CallName))
            {
                return modelInfo.CallName.Trim();
            }

            return modelInfo.Name?.Trim() ?? string.Empty;
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
