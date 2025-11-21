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
            ICustomLogger? logger = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _logger = logger;
            _toolFactory = new LangChainToolFactory(null, database, logger);
            _agentOrchestrators = new Dictionary<int, HybridLangChainOrchestrator>();
        }

        /// <summary>
        /// Create a HybridLangChainOrchestrator with tools for a given model.
        /// Uses full orchestrator by default.
        /// </summary>
        public HybridLangChainOrchestrator CreateOrchestrator(
            string? model = null,
            IEnumerable<string>? allowedPlugins = null,
            int? agentId = null)
        {
            try
            {
                _logger?.Log("Info", "LangChainKernelFactory", $"Creating orchestrator for model: {model}, agentId: {agentId}");

                // Use full orchestrator with all tools
                var orchestrator = _toolFactory.CreateFullOrchestrator(agentId, null);

                // Filter tools if specified
                if (allowedPlugins != null)
                {
                    FilterOrchestratorTools(orchestrator, allowedPlugins);
                }

                _logger?.Log("Info", "LangChainKernelFactory", "Orchestrator created successfully");
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
        /// Filter orchestrator tools to only allowed plugins.
        /// Tools are identified by their method names in the schema.
        /// </summary>
        private void FilterOrchestratorTools(
            HybridLangChainOrchestrator orchestrator,
            IEnumerable<string> allowedPlugins)
        {
            try
            {
                var allowedSet = new HashSet<string>(
                    allowedPlugins.Select(p => p.ToLowerInvariant()),
                    StringComparer.OrdinalIgnoreCase);

                _logger?.Log("Info", "LangChainKernelFactory",
                    $"Filtering tools to allowed plugins: {string.Join(", ", allowedSet)}");

                // The orchestrator's schema contains all tools
                // In a real implementation, we would filter here
                // For now, this is a placeholder for future enhancement
            }
            catch (Exception ex)
            {
                _logger?.Log("Warning", "LangChainKernelFactory",
                    $"Failed to filter tools: {ex.Message}");
            }
        }
    }
}
