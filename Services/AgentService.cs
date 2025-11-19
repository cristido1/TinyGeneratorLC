using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
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
        private readonly ILogger<AgentService>? _logger;

        public AgentService(DatabaseService database, IKernelFactory kernelFactory, ILogger<AgentService>? logger = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
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
    }
}
