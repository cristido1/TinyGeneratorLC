using System;
using System.Collections.Generic;
using System.Linq;
using TinyGenerator.Models;

namespace TinyGenerator.Services
{
    /// <summary>
    /// LangChain-based agent service (replaces deprecated AgentService).
    /// 
    /// Retrieves agents from database and provides configured HybridLangChainOrchestrator
    /// instances for each agent, taking into account their skills and model configuration.
    /// 
    /// This service handles:
    /// - Agent retrieval and configuration
    /// - Skill parsing and tool filtering
    /// - Orchestrator caching and lifecycle
    /// - Model resolution from agent config
    /// 
    /// New standard service for agent-based operations (tests, generation, etc.)
    /// </summary>
    public class LangChainAgentService
    {
        private readonly DatabaseService _database;
        private readonly ILangChainKernelFactory _kernelFactory;
        private readonly ICustomLogger? _logger;

        public LangChainAgentService(
            DatabaseService database,
            ILangChainKernelFactory kernelFactory,
            ICustomLogger? logger = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
            _logger = logger;
        }

        /// <summary>
        /// Get all active agents configured in the system.
        /// </summary>
        /// <returns>List of all agents</returns>
        public List<Agent> GetAllAgents()
        {
            try
            {
                var agents = _database.ListAgents();
                _logger?.Log("Info", "LangChainAgentService", $"Retrieved {agents.Count} agents from database");
                return agents;
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LangChainAgentService", $"Failed to get agents: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Get active agents (is_active = true).
        /// </summary>
        public List<Agent> GetActiveAgents()
        {
            try
            {
                var agents = _database.ListAgents().Where(a => a.IsActive).ToList();
                _logger?.Log("Info", "LangChainAgentService", $"Retrieved {agents.Count} active agents");
                return agents;
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LangChainAgentService", $"Failed to get active agents: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Get a specific agent by ID.
        /// </summary>
        public Agent? GetAgent(int agentId)
        {
            try
            {
                var agent = _database.GetAgentById(agentId);
                if (agent != null)
                {
                    _logger?.Log("Info", "LangChainAgentService", $"Retrieved agent {agentId}: {agent.Name}");
                }
                else
                {
                    _logger?.Log("Warning", "LangChainAgentService", $"Agent {agentId} not found");
                }
                return agent;
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LangChainAgentService", $"Failed to get agent {agentId}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Get or create a HybridLangChainOrchestrator for an agent.
        /// Ensures the orchestrator is properly configured with the agent's skills and model.
        /// </summary>
        public HybridLangChainOrchestrator GetOrchestratorForAgent(int agentId)
        {
            try
            {
                _logger?.Log("Info", "LangChainAgentService", $"Getting orchestrator for agent {agentId}");

                // Try to get cached orchestrator
                var cached = _kernelFactory.GetOrchestratorForAgent(agentId);
                if (cached != null)
                {
                    _logger?.Log("Info", "LangChainAgentService", $"Using cached orchestrator for agent {agentId}");
                    return cached;
                }

                // Get agent and ensure orchestrator is created
                var agent = _database.GetAgentById(agentId);
                if (agent == null)
                {
                    throw new InvalidOperationException($"Agent {agentId} not found");
                }

                // Ensure orchestrator exists in factory
                _kernelFactory.EnsureOrchestratorForAgent(agentId, null, ParseAgentSkills(agent));

                // Get the newly created orchestrator
                var orchestrator = _kernelFactory.GetOrchestratorForAgent(agentId);
                if (orchestrator == null)
                {
                    throw new InvalidOperationException($"Failed to create orchestrator for agent {agentId}");
                }

                _logger?.Log("Info", "LangChainAgentService",
                    $"Orchestrator ready for agent {agentId}: {agent.Name}");
                return orchestrator;
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LangChainAgentService",
                    $"Failed to get orchestrator for agent {agentId}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Initialize orchestrators for all active agents.
        /// Typically called at application startup.
        /// </summary>
        public void InitializeActiveAgents()
        {
            try
            {
                var activeAgents = GetActiveAgents();
                _logger?.Log("Info", "LangChainAgentService",
                    $"Initializing orchestrators for {activeAgents.Count} active agents");

                foreach (var agent in activeAgents)
                {
                    try
                    {
                        _kernelFactory.EnsureOrchestratorForAgent(agent.Id, null, ParseAgentSkills(agent));
                        _logger?.Log("Info", "LangChainAgentService",
                            $"Initialized orchestrator for agent {agent.Id}: {agent.Name}");
                    }
                    catch (Exception ex)
                    {
                        _logger?.Log("Error", "LangChainAgentService",
                            $"Failed to initialize orchestrator for agent {agent.Id}: {ex.Message}");
                        // Continue with next agent instead of failing completely
                    }
                }

                _logger?.Log("Info", "LangChainAgentService", "Completed agent initialization");
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LangChainAgentService",
                    $"Failed during agent initialization: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Clear cached orchestrators (useful for testing or reload scenarios).
        /// </summary>
        public void ClearCache()
        {
            try
            {
                _kernelFactory.ClearCache();
                _logger?.Log("Info", "LangChainAgentService", "Cleared all cached orchestrators");
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LangChainAgentService", $"Failed to clear cache: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Get diagnostic information about cached agents.
        /// </summary>
        public int GetCachedAgentCount()
        {
            try
            {
                var count = _kernelFactory.GetCachedCount();
                _logger?.Log("Info", "LangChainAgentService", $"Currently caching {count} orchestrators");
                return count;
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LangChainAgentService", $"Failed to get cache info: {ex.Message}");
                throw;
            }
        }

        // ==================== Helper Methods ====================

        /// <summary>
        /// Parse skills from agent JSON configuration.
        /// Expects agent Skills field to be JSON array: ["text", "memory", "evaluator"]
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

                _logger?.Log("Info", "LangChainAgentService",
                    $"Parsed agent skills: {string.Join(", ", skills)}");
            }
            catch (Exception ex)
            {
                _logger?.Log("Warning", "LangChainAgentService",
                    $"Failed to parse agent skills JSON: {ex.Message}. Using all skills.");
            }

            return skills;
        }
    }
}
