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

        public AgentService(
            DatabaseService database, 
            IKernelFactory kernelFactory, 
            ProgressService? progress = null,
            ILogger<AgentService>? logger = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
            _progress = progress;
            _logger = logger;
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

                // Get model name for this agent
                var modelName = agent.ModelId.HasValue 
                    ? _database.GetModelNameById(agent.ModelId.Value) 
                    : null;

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
        public async Task<ChatMessageContent?> InvokeModelAsync(
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
            var timeoutMs = timeoutSeconds * 1000;
            
            _progress?.ShowAgentActivity(displayName, statusMessage, agentId, testType);
            
            try
            {
                var completedTask = await Task.WhenAny(
                    chatService.GetChatMessageContentAsync(history, settings, kernel),
                    Task.Delay(timeoutMs));

                if (completedTask is Task<ChatMessageContent> resultTask)
                {
                     _progress?.HideAgentActivity(agentId);
                    return await resultTask;
                }
                else
                {
                     _progress?.HideAgentActivity(agentId);
                    throw new TimeoutException($"Operation timed out after {timeoutSeconds}s");
                }
            }
            finally
            {
                _progress?.HideAgentActivity(agentId);
            }
        }

        /// <summary>
        /// Overload semplificato senza settings personalizzati (usa defaults)
        /// </summary>
        public async Task<ChatMessageContent?> InvokeModelAsync(
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
    }
}
