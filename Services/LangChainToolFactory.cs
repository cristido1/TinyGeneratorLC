using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using TinyGenerator.Skills;
using TinyGenerator.Services;

namespace TinyGenerator.Services
{
    /// <summary>
    /// Factory for creating and registering LangChain Tools from migrated Skills.
    /// Simplifies setup of HybridLangChainOrchestrator with all available tools.
    /// </summary>
    public class LangChainToolFactory
    {
        private readonly PersistentMemoryService? _memoryService;
        private readonly DatabaseService? _database;
        private readonly ICustomLogger? _logger;

        public LangChainToolFactory(
            PersistentMemoryService? memoryService = null,
            DatabaseService? database = null,
            ICustomLogger? logger = null)
        {
            _memoryService = memoryService;
            _database = database;
            _logger = logger;
        }

        /// <summary>
        /// Create a pre-configured HybridLangChainOrchestrator with all migrated tools.
        /// </summary>
        public HybridLangChainOrchestrator CreateFullOrchestrator(int? agentId = null, int? modelId = null)
        {
            var orchestrator = new HybridLangChainOrchestrator(_logger);

            // Register all migrated LangChain tools
            RegisterTextTool(orchestrator, modelId, agentId);
            RegisterMathTool(orchestrator, modelId, agentId);
            RegisterMemoryTool(orchestrator, modelId, agentId);
            RegisterEvaluatorTool(orchestrator, modelId, agentId);

            _logger?.Log("Info", "ToolFactory", "Created full HybridLangChainOrchestrator with all tools");
            return orchestrator;
        }

        /// <summary>
        /// Create orchestrator with only essential tools (for quick testing).
        /// </summary>
        public HybridLangChainOrchestrator CreateEssentialOrchestrator(int? agentId = null, int? modelId = null)
        {
            var orchestrator = new HybridLangChainOrchestrator(_logger);

            RegisterTextTool(orchestrator, modelId, agentId);
            RegisterMathTool(orchestrator, modelId, agentId);

            _logger?.Log("Info", "ToolFactory", "Created essential HybridLangChainOrchestrator");
            return orchestrator;
        }

        private void RegisterTextTool(HybridLangChainOrchestrator orchestrator, int? modelId, int? agentId)
        {
            try
            {
                var tool = new TextTool(_logger)
                {
                    ModelId = modelId,
                    AgentId = agentId
                };
                orchestrator.RegisterTool(tool);
                _logger?.Log("Info", "ToolFactory", "Registered TextTool");
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "ToolFactory", $"Failed to register TextTool: {ex.Message}");
            }
        }

        private void RegisterMathTool(HybridLangChainOrchestrator orchestrator, int? modelId, int? agentId)
        {
            try
            {
                var tool = new MathTool(_logger)
                {
                    ModelId = modelId,
                    AgentId = agentId
                };
                orchestrator.RegisterTool(tool);
                _logger?.Log("Info", "ToolFactory", "Registered MathTool");
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "ToolFactory", $"Failed to register MathTool: {ex.Message}");
            }
        }

        private void RegisterMemoryTool(HybridLangChainOrchestrator orchestrator, int? modelId, int? agentId)
        {
            try
            {
                if (_memoryService == null)
                {
                    _logger?.Log("Warn", "ToolFactory", "MemoryService not available, skipping MemoryTool");
                    return;
                }

                var tool = new MemoryTool(_memoryService, _logger)
                {
                    ModelId = modelId,
                    AgentId = agentId
                };
                orchestrator.RegisterTool(tool);
                _logger?.Log("Info", "ToolFactory", "Registered MemoryTool");
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "ToolFactory", $"Failed to register MemoryTool: {ex.Message}");
            }
        }

        private void RegisterEvaluatorTool(HybridLangChainOrchestrator orchestrator, int? modelId, int? agentId)
        {
            try
            {
                var tool = new EvaluatorTool(_database, _logger)
                {
                    ModelId = modelId,
                    AgentId = agentId
                };
                orchestrator.RegisterTool(tool);
                _logger?.Log("Info", "ToolFactory", "Registered EvaluatorTool");
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "ToolFactory", $"Failed to register EvaluatorTool: {ex.Message}");
            }
        }

        /// <summary>
        /// Manually register a custom LangChain tool.
        /// </summary>
        public void RegisterCustomTool(HybridLangChainOrchestrator orchestrator, BaseLangChainTool tool)
        {
            try
            {
                orchestrator.RegisterTool(tool);
                _logger?.Log("Info", "ToolFactory", $"Registered custom tool: {tool.Name}");
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "ToolFactory", $"Failed to register custom tool {tool.Name}: {ex.Message}");
            }
        }
    }
}
