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
        private readonly IOllamaMonitorService? _ollamaMonitor;
        private readonly LlamaService? _llamaService;
        private readonly object _providerSwitchLock = new();
        private string? _lastLocalProvider;
        private string? _lastLocalModelName;

        public LangChainKernelFactory(
            IConfiguration config,
            DatabaseService database,
            ICustomLogger? logger = null,
            LangChainToolFactory? toolFactory = null,
            IOllamaMonitorService? ollamaMonitor = null,
            LlamaService? llamaService = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _logger = logger;
            _toolFactory = toolFactory ?? throw new ArgumentNullException(nameof(toolFactory));
            _agentOrchestrators = new Dictionary<int, HybridLangChainOrchestrator>();
            _ollamaMonitor = ollamaMonitor;
            _llamaService = llamaService;
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
                        actualModel = modelInfo?.Name;
                    }
                    if (string.IsNullOrEmpty(actualModel))
                    {
                        throw new InvalidOperationException($"No model configured for agent {agentId}.");
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
        public LangChainChatBridge CreateChatBridge(
            string model,
            double? temperature = null,
            double? topP = null,
            double? repeatPenalty = null,
            int? topK = null,
            int? repeatLastN = null,
            int? numPredict = null,
            bool useMaxTokens = false)
        {
            try
            {
                var modelInfo = _database.ListModels().FirstOrDefault(m => string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase));
                if (modelInfo == null)
                {
                    throw new InvalidOperationException($"Model '{model}' not found in database");
                }

                HandleProviderSwitch(modelInfo, model);

                // Determine endpoint based on model provider
                string endpoint;
                string apiKey = "ollama"; // Default for Ollama (no auth required)
                bool? forceOllama = null;
                Func<CancellationToken, Task>? beforeCall = null;
                Func<CancellationToken, Task>? afterCall = null;

                if (modelInfo.Provider?.Equals("ollama", StringComparison.OrdinalIgnoreCase) == true)
                {
                    endpoint = _config.GetSection("Ollama:endpoint").Value ?? "http://localhost:11434";
                    forceOllama = true;
                }
                else if (modelInfo.Provider?.Equals("llama.cpp", StringComparison.OrdinalIgnoreCase) == true)
                {
                    endpoint = modelInfo.Endpoint ?? "http://127.0.0.1:11436";
                    forceOllama = false;
                    apiKey = "ollama";

                    var ctxSize = modelInfo.ContextToUse > 0 ? modelInfo.ContextToUse : 32768;
                    var modelName = modelInfo.Name;
                    _logger?.Log("Info", "llama.cpp",
                        $"llama.cpp selected: model={modelName}, ctx={ctxSize}, endpoint={endpoint}");
                    beforeCall = async ct =>
                    {
                        _logger?.Log("Info", "llama.cpp", "llama.cpp beforeCall: ensuring server restart");
                        if (_llamaService != null)
                        {
                            try
                            {
                                await _llamaService.EnsureRestartAsync(modelName, ctxSize).ConfigureAwait(false);
                                _logger?.Log("Info", "llama.cpp", "llama.cpp beforeCall: start complete");
                            }
                            catch (Exception ex)
                            {
                                _logger?.Log("Warning", "llama.cpp", $"llama.cpp beforeCall failed: {ex.Message}");
                                throw;
                            }
                        }
                    };
                    afterCall = _ => Task.Run(() =>
                    {
                        _logger?.Log("Info", "llama.cpp", "llama.cpp afterCall: stopping server");
                        _llamaService?.StopServer();
                    });
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

                var logAsLlama = modelInfo.Provider?.Equals("llama.cpp", StringComparison.OrdinalIgnoreCase) == true;
                // Read NoTemperatureModels from config
                var noTempModels = _config.GetSection("OpenAI:NoTemperatureModels").Get<string[]>() ?? Array.Empty<string>();
                var noRepeatPenaltyModels = _config.GetSection("OpenAI:NoRepeatPenaltyModels").Get<string[]>() ?? Array.Empty<string>();
                var noTopPModels = _config.GetSection("OpenAI:NoTopPModels").Get<string[]>() ?? Array.Empty<string>();
                var noFrequencyPenaltyModels = _config.GetSection("OpenAI:NoFrequencyPenaltyModels").Get<string[]>() ?? Array.Empty<string>();
                var noMaxTokensModels = _config.GetSection("OpenAI:NoMaxTokensModels").Get<string[]>() ?? Array.Empty<string>();
                var noTopKModels = _config.GetSection("OpenAI:NoTopKModels").Get<string[]>() ?? Array.Empty<string>();
                var noRepeatLastNModels = _config.GetSection("OpenAI:NoRepeatLastNModels").Get<string[]>() ?? Array.Empty<string>();
                var noNumPredictModels = _config.GetSection("OpenAI:NoNumPredictModels").Get<string[]>() ?? Array.Empty<string>();
                var bridge = new LangChainChatBridge(endpoint, model, apiKey, null, _logger, forceOllama, beforeCall, afterCall, logAsLlama, noTempModels, noRepeatPenaltyModels, noTopPModels, noFrequencyPenaltyModels, noMaxTokensModels, noTopKModels, noRepeatLastNModels, noNumPredictModels);
                if (temperature.HasValue) bridge.Temperature = temperature.Value;
                if (topP.HasValue) bridge.TopP = topP.Value;
                if (repeatPenalty.HasValue) bridge.RepeatPenalty = repeatPenalty.Value;
                if (topK.HasValue) bridge.TopK = topK.Value;
                if (repeatLastN.HasValue) bridge.RepeatLastN = repeatLastN.Value;
                if (numPredict.HasValue) bridge.NumPredict = numPredict.Value;
                if (useMaxTokens)
                {
                    var maxTokens = DetermineMaxTokensForModel(modelInfo);
                    if (maxTokens > 0)
                    {
                        if (!bridge.MaxResponseTokens.HasValue)
                        {
                            bridge.MaxResponseTokens = maxTokens;
                        }
                        else
                        {
                            bridge.MaxResponseTokens = Math.Max(bridge.MaxResponseTokens.Value, maxTokens);
                        }
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

        private void HandleProviderSwitch(ModelInfo modelInfo, string modelName)
        {
            var provider = modelInfo.Provider?.Trim();
            if (string.IsNullOrWhiteSpace(provider))
            {
                return;
            }

            // OpenAI/Azure are external providers: do not manage local memory.
            if (provider.Equals("openai", StringComparison.OrdinalIgnoreCase) ||
                provider.Equals("azure", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var normalized = provider.ToLowerInvariant();
            if (normalized != "ollama" && normalized != "llama.cpp")
            {
                return;
            }

            lock (_providerSwitchLock)
            {
                if (!string.IsNullOrWhiteSpace(_lastLocalProvider) &&
                    !string.Equals(_lastLocalProvider, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(_lastLocalProvider, "ollama", StringComparison.OrdinalIgnoreCase))
                    {
                        StopOllamaModel(_lastLocalModelName);
                    }
                    else if (string.Equals(_lastLocalProvider, "llama.cpp", StringComparison.OrdinalIgnoreCase))
                    {
                        _llamaService?.StopServer();
                    }
                }

                _lastLocalProvider = normalized;
                _lastLocalModelName = modelName;
            }
        }

        private void StopOllamaModel(string? modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName) || _ollamaMonitor == null) return;
            try
            {
                _ollamaMonitor.StopModelAsync(modelName).GetAwaiter().GetResult();
                _logger?.Log("Info", "LangChainKernelFactory", $"Stopped Ollama model '{modelName}' due to provider switch");
            }
            catch (Exception ex)
            {
                _logger?.Log("Warning", "LangChainKernelFactory", $"Failed to stop Ollama model '{modelName}': {ex.Message}");
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
                var modelInfo = _database.ListModels().FirstOrDefault(m => string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase));
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
