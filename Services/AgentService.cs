using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using TinyGenerator.Models;

namespace TinyGenerator.Services
{
    /// <summary>
    /// Service for retrieving and configuring agents with their associated kernels and skills
    /// </summary>
    public sealed class AgentService
    {
        private readonly DatabaseService _database;
        private readonly IKernelFactory _kernelFactory;
        private readonly ProgressService? _progress;
        private readonly ILogger<AgentService>? _logger;
        private readonly ICustomLogger? _customLogger;

        public AgentService(
            DatabaseService database, 
            IKernelFactory kernelFactory, 
            ProgressService? progress = null,
            ILogger<AgentService>? logger = null,
            ICustomLogger? customLogger = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
            _progress = progress;
            _logger = logger;
            _customLogger = customLogger;
        }

        /// <summary>
        /// Gets a configured agent with its kernel ready to use
        /// </summary>
        /// <param name="agentId">The agent ID</param>
        /// <returns>Configured kernel for the agent, or null if agent not found or configuration failed</returns>
        public Kernel? GetConfiguredAgent(int agentId)
        {
            try
            {
                var agent = _database.GetAgentById(agentId);
                if (agent == null)
                {
                    _logger?.LogWarning("Agent {AgentId} not found", agentId);
                    return null;
                }

                if (!agent.IsActive)
                {
                    _logger?.LogWarning("Agent {AgentId} ({Name}) is not active", agentId, agent.Name);
                    return null;
                }

                // Get model info for this agent
                var modelInfo = agent.ModelId.HasValue 
                    ? _database.GetModelInfoById(agent.ModelId.Value) 
                    : null;
                var modelName = modelInfo?.Name;

                if (string.IsNullOrWhiteSpace(modelName))
                {
                    _logger?.LogWarning("Agent {AgentId} ({Name}) has no valid model", agentId, agent.Name);
                    return null;
                }

                // Parse skills from agent configuration
                var skills = ParseAgentSkills(agent);

                // Create kernel with agent's skills
                var kernel = _kernelFactory.CreateKernel(modelName, skills.ToArray());
                
                if (kernel == null)
                {
                    _logger?.LogError("Failed to create kernel for agent {AgentId} ({Name})", agentId, agent.Name);
                    return null;
                }

                _logger?.LogInformation("Configured agent {AgentId} ({Name}) with model {Model} and skills [{Skills}]", 
                    agentId, agent.Name, modelName, string.Join(", ", skills));

                return kernel;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error configuring agent {AgentId}", agentId);
                return null;
            }
        }

        /// <summary>
        /// Gets agent metadata without creating a kernel
        /// </summary>
        public Agent? GetAgent(int agentId)
        {
            return _database.GetAgentById(agentId);
        }

        /// <summary>
        /// Gets all active agents by role
        /// </summary>
        public List<Agent> GetAgentsByRole(string role)
        {
            return _database.ListAgents()
                .Where(a => a.Role?.Equals(role, StringComparison.OrdinalIgnoreCase) == true && a.IsActive)
                .ToList();
        }

        /// <summary>
        /// Parses skills from agent's Skills JSON field, adding role-specific defaults
        /// </summary>
        private List<string> ParseAgentSkills(Agent agent)
        {
            var skills = new List<string>();

            // Add role-specific default skills
            switch (agent.Role?.ToLowerInvariant())
            {
                case "story_evaluator":
                    skills.Add("evaluator");
                    break;
                case "writer":
                    skills.Add("text");
                    break;
                case "tts":
                    skills.Add("tts");
                    break;
            }

            // Parse and add skills from agent's Skills JSON field
            if (!string.IsNullOrWhiteSpace(agent.Skills))
            {
                try
                {
                    var skillsArray = JsonSerializer.Deserialize<string[]>(agent.Skills);
                    if (skillsArray != null)
                    {
                        skills.AddRange(skillsArray);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to parse skills for agent {AgentId} ({Name})", agent.Id, agent.Name);
                }
            }

            return skills.Distinct().ToList();
        }

        /// <summary>
        /// Invoca un modello/agente con badge e timeout.
        /// Funzione centralizzata per tutte le chiamate API.
        /// </summary>
        /// <param name="kernel">Il kernel da usare</param>
        /// <param name="history">La chat history</param>
        /// <param name="settings">Le impostazioni di esecuzione</param>
        /// <param name="agentId">ID univoco per il badge (es: "question_model_123_1")</param>
        /// <param name="displayName">Nome da mostrare nel badge (es: "phi3:mini")</param>
        /// <param name="statusMessage">Messaggio di stato (es: "Question 1")</param>
        /// <param name="timeoutSeconds">Timeout in secondi</param>
        /// <param name="testType">Tipo di test per icona appropriata: question, writer, tts, evaluator, music</param>
        /// <returns>La risposta del modello</returns>
        public async Task<Microsoft.SemanticKernel.ChatMessageContent?> InvokeModelAsync(
            Kernel kernel,
            ChatHistory history,
            OpenAIPromptExecutionSettings settings,
            string agentId,
            string displayName,
            string statusMessage,
            int timeoutSeconds = 30,
            string testType = "question")
        {
            var chatService = kernel.GetRequiredService<IChatCompletionService>();
            var timeoutSecs = timeoutSeconds;
            var startTime = DateTime.UtcNow;
            
            _progress?.ShowAgentActivity(displayName, statusMessage, agentId, testType);
            
            _logger?.LogInformation(
                "Model invocation started: Model={Model}, Status={Status}, Type={Type}, Timeout={Timeout}s, AgentId={AgentId}",
                displayName, statusMessage, testType, timeoutSeconds, agentId);
            
            // Registra nota per Ollama models
            if (displayName.Contains("ollama", StringComparison.OrdinalIgnoreCase) || 
                displayName.Contains(":", StringComparison.OrdinalIgnoreCase))
            {
                OllamaMonitorService.RecordModelNote(displayName, testType);
                _logger?.LogInformation("Ollama model note recorded: Model={Model}, Type={Type}", displayName, testType);
            }
            
            try
            {
                using (var cts = new System.Threading.CancellationTokenSource(timeoutSecs * 1000))
                {
                    // Log complete request (chat history) as JSON
                    try
                    {
                        await (_progress?.AppendAsync(agentId, $"=== RAW REQUEST (JSON) ===", "raw-json-header") ?? Task.CompletedTask);
                        
                        // Convert ChatHistory to JSON-serializable format
                        var historyJson = new List<Dictionary<string, object?>>();
                        foreach (var msg in history)
                        {
                            historyJson.Add(new Dictionary<string, object?>
                            {
                                { "role", msg.Role.ToString() },
                                { "content", msg.Content ?? "(empty)" }
                            });
                        }
                        
                        // Serialize as compact JSON (no indentation) for single-line display
                        var jsonString = System.Text.Json.JsonSerializer.Serialize(historyJson, new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
                        
                        // Log complete request to database via CustomLogger (no truncation)
                        _customLogger?.Log("Information", "RawRequest", $"[{displayName}] RAW REQUEST: {jsonString}");
                        
                        // Show entire JSON on ONE line with horizontal scroll (for Live Monitor)
                        await (_progress?.AppendAsync(agentId, jsonString, "raw-json-content") ?? Task.CompletedTask);
                        
                        await (_progress?.AppendAsync(agentId, $"=== END REQUEST ({jsonString.Length} chars) ===", "raw-json-header") ?? Task.CompletedTask);
                    }
                    catch (Exception ex) 
                    { 
                        await (_progress?.AppendAsync(agentId, $"Error serializing request: {ex.Message}") ?? Task.CompletedTask);
                    }
                    
                    // WORKAROUND for SK 1.67.1 bug: Add available kernel functions as tools in system message
                    // since ToolCallBehavior.AutoInvokeKernelFunctions doesn't send tools to the API
                    var toolsMessage = BuildToolsSystemMessage(kernel);
                    if (!string.IsNullOrWhiteSpace(toolsMessage))
                    {
                        history.AddSystemMessage(toolsMessage);
                        await (_progress?.AppendAsync(agentId, "=== TOOLS INJECTED ===", "tools-header") ?? Task.CompletedTask);
                        await (_progress?.AppendAsync(agentId, toolsMessage, "tools-content") ?? Task.CompletedTask);
                    }
                    
                    var result = await chatService.GetChatMessageContentAsync(history, settings, kernel, cts.Token);
                    var elapsed = DateTime.UtcNow - startTime;
                    
                    // Log complete response
                    try
                    {
                        var responseText = result?.ToString() ?? "(empty)";
                        await (_progress?.AppendAsync(agentId, $"=== RAW RESPONSE ===", "raw-json-header") ?? Task.CompletedTask);
                        
                        // Log complete response to database via CustomLogger (no truncation)
                        _customLogger?.Log("Information", "RawResponse", $"[{displayName}] RAW RESPONSE: {responseText}");
                        
                        // Show entire response on ONE line with horizontal scroll (for Live Monitor)
                        await (_progress?.AppendAsync(agentId, responseText, "raw-json-content") ?? Task.CompletedTask);
                        
                        await (_progress?.AppendAsync(agentId, $"=== END RESPONSE ({responseText.Length} chars, {elapsed.TotalMilliseconds}ms) ===", "raw-json-header") ?? Task.CompletedTask);
                    }
                    catch { }
                    
                    _logger?.LogInformation(
                        "Model invocation completed successfully: Model={Model}, Duration={Duration}ms, ResponseLength={Length}",
                        displayName, elapsed.TotalMilliseconds, result?.ToString()?.Length ?? 0);
                    return result;
                }
            }
            catch (System.OperationCanceledException)
            {
                var elapsed = DateTime.UtcNow - startTime;
                _logger?.LogWarning(
                    "Model invocation timed out: Model={Model}, Timeout={Timeout}s, Elapsed={Elapsed}ms",
                    displayName, timeoutSeconds, elapsed.TotalMilliseconds);
                throw new TimeoutException($"Operation timed out after {timeoutSeconds}s");
            }
            catch (Exception ex)
            {
                var elapsed = DateTime.UtcNow - startTime;
                _logger?.LogError(
                    ex,
                    "Model invocation failed: Model={Model}, Type={Type}, Duration={Duration}ms, Error={Error}",
                    displayName, testType, elapsed.TotalMilliseconds, ex.Message);
                throw;
            }
            finally
            {
                _progress?.HideAgentActivity(agentId);
                _logger?.LogInformation("Model activity badge hidden: AgentId={AgentId}", agentId);
            }
        }

        /// <summary>
        /// Overload semplificato senza settings personalizzati (usa defaults)
        /// </summary>
        public async Task<Microsoft.SemanticKernel.ChatMessageContent?> InvokeModelAsync(
            Kernel kernel,
            ChatHistory history,
            string agentId,
            string displayName,
            string statusMessage,
            int timeoutSeconds = 30,
            double temperature = 0.0,
            int maxTokens = 8000,
            string testType = "question")
        {
            var settings = new OpenAIPromptExecutionSettings
            {
                Temperature = temperature,
                MaxTokens = maxTokens
            };

            return await InvokeModelAsync(kernel, history, settings, agentId, displayName, statusMessage, timeoutSeconds, testType);
        }

        /// <summary>
        /// Build a system message containing all available kernel functions as tools.
        /// This is a workaround for SK 1.67.1 bug where tools aren't sent to the API.
        /// The model needs to know what functions it can call, so we include them in the system message.
        /// </summary>
        private string BuildToolsSystemMessage(Kernel kernel)
        {
            try
            {
                var functionsMetadata = kernel.Plugins.GetFunctionsMetadata().ToList();
                if (functionsMetadata.Count == 0)
                    return string.Empty;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# Available Tools/Functions");
                sb.AppendLine();
                sb.AppendLine("You have access to the following functions. Call them by using their names with appropriate parameters.");
                sb.AppendLine();

                foreach (var func in functionsMetadata)
                {
                    sb.AppendLine($"## {func.PluginName}/{func.Name}");
                    sb.AppendLine($"**Description:** {func.Description ?? "No description"}");
                    
                    if (func.Parameters != null && func.Parameters.Count > 0)
                    {
                        sb.AppendLine("**Parameters:**");
                        foreach (var param in func.Parameters)
                        {
                            var required = param.IsRequired ? "(required)" : "(optional)";
                            sb.AppendLine($"  - `{param.Name}` {required}: {param.Description ?? "No description"}");
                        }
                    }
                    else
                    {
                        sb.AppendLine("**Parameters:** None");
                    }
                    sb.AppendLine();
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to build tools system message: {ex.Message}");
                return string.Empty;
            }
        }
    }
}
