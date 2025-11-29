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

        public string? SelectedModel { get; set; }
        public ModelInfo? SelectedModelInfo { get; set; }
        public List<ModelInfo>? AvailableModels { get; set; }
        public List<ConversationMessage>? ConversationHistory { get; set; }

        public ChatModel(DatabaseService database, LangChainKernelFactory kernelFactory)
        {
            _database = database;
            _kernelFactory = kernelFactory;
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

                // Call model via LangChain bridge. If model supports tools, expose default tools (memory).
                var chatBridge = _kernelFactory.CreateChatBridge(model);
                var tools = _kernelFactory.GetDefaultToolSchemasForModel(model) ?? new List<Dictionary<string, object>>();
                var response = await chatBridge.CallModelWithToolsAsync(
                    history,
                    tools, // Default tools (may include memory if model supports tools)
                    CancellationToken.None);

                // Parse response
                var (textContent, _) = LangChainChatBridge.ParseChatResponse(response);

                // Add assistant response
                history.Add(new Services.ConversationMessage
                {
                    Role = "assistant",
                    Content = textContent ?? "No response"
                });

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

        public class ConversationMessage
        {
            public string Role { get; set; } = string.Empty;
            public string Content { get; set; } = string.Empty;
        }
    }
}
