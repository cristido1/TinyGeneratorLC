using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TinyGenerator.Models;
using TinyGenerator.Skills;

namespace TinyGenerator.Services
{
    /// <summary>
    /// LangChain-based kernel factory implementation (replaces deprecated IKernelFactory).
    /// 
    /// Creates and caches HybridLangChainOrchestrator instances configured with:
    /// - Model client (ChatOpenAI for Ollama, OpenAI, Azure)
    /// - Tools registry (Text, Math, Memory, Evaluator)
    /// - Agent-specific skill filters
    /// - ReAct loop execution engine
    /// 
    /// Manages model credentials from config/environment and database.
    /// </summary>
    public class LangChainKernelFactory : ILangChainKernelFactory
    {
        private readonly IConfiguration _config;
        private readonly DatabaseService _database;
        private readonly ICustomLogger? _logger;
        private readonly LangChainToolFactory _toolFactory;
        private readonly Dictionary<int, HybridLangChainOrchestrator> _agentOrchestrators;

        public LangChainKernelFactory(
            IConfiguration config,
            DatabaseService database,
            ICustomLogger? logger = null,
            LangChainToolFactory? toolFactory = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _logger = logger;
            _toolFactory = toolFactory ?? throw new ArgumentNullException(nameof(toolFactory));
            _agentOrchestrators = new Dictionary<int, HybridLangChainOrchestrator>();
        }

        /// <summary>
        /// Create a HybridLangChainOrchestrator with tools for a given model.
        /// Registers only the allowed tools (most efficient approach).
        /// </summary>
        public HybridLangChainOrchestrator CreateOrchestrator(
            string? model = null,
            IEnumerable<string>? allowedPlugins = null,
            int? agentId = null,
            string? ttsWorkingFolder = null,
            string? ttsStoryText = null)
        {
            try
            {
                _logger?.Log("Info", "LangChainKernelFactory", $"Creating orchestrator for model: {model}, agentId: {agentId}");

                HybridLangChainOrchestrator orchestrator;

                // If specific tools are requested, create orchestrator with ONLY those tools
                if (allowedPlugins != null && allowedPlugins.Any())
                {
                    var pluginsList = allowedPlugins.ToList();
                    _logger?.Log("Info", "LangChainKernelFactory", 
                        $"Creating orchestrator with specific tools: {string.Join(", ", pluginsList)}");
                    orchestrator = _toolFactory.CreateOrchestratorWithTools(pluginsList, agentId, null, ttsWorkingFolder, ttsStoryText);
                }
                else
                {
                    // No tools requested, create empty orchestrator
                    _logger?.Log("Info", "LangChainKernelFactory", "Creating empty orchestrator (no tools requested)");
                    orchestrator = new HybridLangChainOrchestrator(_logger);
                }

                _logger?.Log("Info", "LangChainKernelFactory", 
                    $"Orchestrator created successfully with {orchestrator.GetToolSchemas().Count} tools");
                return orchestrator;
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LangChainKernelFactory", $"Failed to create orchestrator: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Get a cached orchestrator for an agent.
        /// Returns null if not found or not yet created.
        /// </summary>
        public HybridLangChainOrchestrator? GetOrchestratorForAgent(int agentId)
        {
            lock (_agentOrchestrators)
            {
                if (_agentOrchestrators.TryGetValue(agentId, out var orchestrator))
                {
                    _logger?.Log("Info", "LangChainKernelFactory", $"Found cached orchestrator for agent {agentId}");
                    return orchestrator;
                }
            }

            _logger?.Log("Info", "LangChainKernelFactory", $"No cached orchestrator found for agent {agentId}");
            return null;
        }

        /// <summary>
        /// Ensure orchestrator is created and cached for an agent.
        /// </summary>
        public void EnsureOrchestratorForAgent(
            int agentId,
            string? modelId = null,
            IEnumerable<string>? allowedPlugins = null)
        {
            lock (_agentOrchestrators)
            {
                // Return if already cached
                if (_agentOrchestrators.ContainsKey(agentId))
                {
                    _logger?.Log("Info", "LangChainKernelFactory", $"Orchestrator already cached for agent {agentId}");
                    return;
                }

                try
                {
                    _logger?.Log("Info", "LangChainKernelFactory", $"Creating and caching orchestrator for agent {agentId}");

                    // Get agent from database to resolve model and skills
                    var agent = _database.GetAgentById(agentId);
                    if (agent == null)
                    {
                        throw new InvalidOperationException($"Agent {agentId} not found in database");
                    }

                    // Resolve model
                    var actualModel = modelId;
                    if (string.IsNullOrEmpty(actualModel) && agent.ModelId.HasValue)
                    {
                        // Try to resolve model name from database
                        var modelInfo = _database.ListModels()
                            .FirstOrDefault(m => m.Id == agent.ModelId.Value);
                        actualModel = modelInfo?.Name ?? "phi3:mini";
                    }
                    else if (string.IsNullOrEmpty(actualModel))
                    {
                        actualModel = "phi3:mini"; // Default fallback
                    }

                    // Parse skills from agent JSON config
                    var skillsList = ParseAgentSkills(agent);
                    var skillFilter = skillsList.Any() ? skillsList : null;

                    // Create orchestrator
                    var orchestrator = CreateOrchestrator(actualModel, skillFilter, agentId);

                    // Cache it
                    _agentOrchestrators[agentId] = orchestrator;

                    _logger?.Log("Info", "LangChainKernelFactory",
                        $"Orchestrator created and cached for agent {agentId} with model {actualModel}");
                }
                catch (Exception ex)
                {
                    _logger?.Log("Error", "LangChainKernelFactory",
                        $"Failed to ensure orchestrator for agent {agentId}: {ex.Message}");
                    throw;
                }
            }
        }

        /// <summary>
        /// Clear the cache of agent orchestrators (useful for testing or reload scenarios).
        /// </summary>
        public void ClearCache()
        {
            lock (_agentOrchestrators)
            {
                var count = _agentOrchestrators.Count;
                _agentOrchestrators.Clear();
                _logger?.Log("Info", "LangChainKernelFactory", $"Cleared cache of {count} orchestrators");
            }
        }

        /// <summary>
        /// Get cached orchestrators count (diagnostic).
        /// </summary>
        public int GetCachedCount()
        {
            lock (_agentOrchestrators)
            {
                return _agentOrchestrators.Count;
            }
        }

        // ==================== Helper Methods ====================

        /// <summary>
        /// Parse skills from agent JSON configuration.
        /// Expects agent to have JSON Skills field like ["text", "memory", "evaluator"]
        /// </summary>
        private List<string> ParseAgentSkills(Agent agent)
        {
            var skills = new List<string>();

            if (string.IsNullOrEmpty(agent.Skills))
                return skills;

            try
            {
                // Simple JSON array parsing - expects format: ["skill1", "skill2"]
                var json = agent.Skills.Trim();
                if (!json.StartsWith("[") || !json.EndsWith("]"))
                    return skills;

                // Remove brackets and quotes
                var content = json.Substring(1, json.Length - 2);
                var items = content.Split(',');

                foreach (var item in items)
                {
                    var skill = item.Trim().Trim('"').Trim();
                    if (!string.IsNullOrEmpty(skill))
                    {
                        skills.Add(skill);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Log("Warning", "LangChainKernelFactory",
                    $"Failed to parse agent skills JSON: {ex.Message}. Using all skills.");
            }

            return skills;
        }

        /// <summary>
        /// Create a LangChainChatBridge for direct model communication.
        /// Resolves model endpoint and API key from configuration.
        /// </summary>
        public LangChainChatBridge CreateChatBridge(string model, double? temperature = null, double? topP = null, bool useMaxTokens = false)
        {
            try
            {
                var modelInfo = _database.GetModelInfo(model);
                if (modelInfo == null)
                {
                    throw new InvalidOperationException($"Model '{model}' not found in database");
                }

                // Determine endpoint based on model provider
                string endpoint;
                string apiKey = "ollama"; // Default for Ollama (no auth required)

                if (modelInfo.Provider?.Equals("ollama", StringComparison.OrdinalIgnoreCase) == true)
                {
                    endpoint = _config.GetSection("Ollama:endpoint").Value ?? "http://localhost:11434";
                }
                else if (modelInfo.Provider?.Equals("openai", StringComparison.OrdinalIgnoreCase) == true)
                {
                    endpoint = _config.GetSection("OpenAI:endpoint").Value ?? "https://api.openai.com/v1";
                    // Try to get API key from config first, then environment variable
                    apiKey = _config.GetSection("Secrets:OpenAI:ApiKey").Value 
                        ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") 
                        ?? "sk-";
                }
                else if (modelInfo.Provider?.Equals("azure", StringComparison.OrdinalIgnoreCase) == true)
                {
                    endpoint = modelInfo.Endpoint ?? "https://api.openai.azure.com";
                    // Try to get API key from config first, then environment variable
                    apiKey = _config.GetSection("Secrets:Azure:ApiKey").Value
                        ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") 
                        ?? "key-";
                }
                else
                {
                    endpoint = modelInfo.Endpoint ?? _config.GetSection("Ollama:endpoint").Value ?? "http://localhost:11434";
                }

                _logger?.Log("Info", "LangChainKernelFactory",
                    $"Creating ChatBridge for model '{model}' with provider '{modelInfo.Provider}' and endpoint '{endpoint}'");

                var bridge = new LangChainChatBridge(endpoint, model, apiKey, null, _logger);
                if (temperature.HasValue) bridge.Temperature = temperature.Value;
                if (topP.HasValue) bridge.TopP = topP.Value;
                if (useMaxTokens)
                {
                    var maxTokens = DetermineMaxTokensForModel(modelInfo);
                    if (maxTokens > 0)
                    {
                        bridge.MaxResponseTokens = Math.Max(bridge.MaxResponseTokens, maxTokens);
                    }
                }
                return bridge;
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LangChainKernelFactory",
                    $"Failed to create ChatBridge for model '{model}': {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Return default tool schemas to expose to a model for simple chat scenarios.
        /// Currently exposes the `memory` function when the model supports tools (NoTools == false).
        /// </summary>
        public List<Dictionary<string, object>> GetDefaultToolSchemasForModel(string model)
        {
            var schemas = new List<Dictionary<string, object>>();
            try
            {
                var modelInfo = _database.GetModelInfo(model);
                if (modelInfo == null) return schemas;

                // If model explicitly does not support tools, return empty
                if (modelInfo.NoTools) return schemas;

                // Build memory function schema (matches MemoryTool.GetSchema output)
                var properties = new Dictionary<string, object>
                {
                    { "operation", new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "enum", new List<string> { "remember", "recall", "forget" } },
                            { "description", "Operation" }
                        }
                    },
                    { "collection", new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Collection name" }
                        }
                    },
                    { "text", new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Text" }
                        }
                    },
                    { "query", new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Search query" }
                        }
                    }
                };

                var functionSchema = new Dictionary<string, object>
                {
                    { "type", "function" },
                    { "function", new Dictionary<string, object>
                        {
                            { "name", "memory" },
                            { "description", "Memory operations" },
                            { "parameters", new Dictionary<string, object>
                                {
                                    { "type", "object" },
                                    { "properties", properties },
                                    { "required", new List<string> { "operation", "collection" } }
                                }
                            }
                        }
                    }
                };

                schemas.Add(functionSchema);
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LangChainKernelFactory", $"Failed to build default tool schemas for {model}: {ex.Message}");
            }

            return schemas;
        }

        private static int DetermineMaxTokensForModel(Models.ModelInfo modelInfo)
        {
            if (modelInfo == null) return 0;
            if (modelInfo.ContextToUse > 0) return modelInfo.ContextToUse;
            if (modelInfo.MaxContext > 0) return modelInfo.MaxContext;
            return 32000;
        }
    }
}
