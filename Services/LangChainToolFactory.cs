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
        private readonly HttpClient? _httpClient;
        private readonly StoriesService? _storiesService;

        public LangChainToolFactory(
            PersistentMemoryService? memoryService = null,
            DatabaseService? database = null,
            ICustomLogger? logger = null,
            HttpClient? httpClient = null,
            StoriesService? storiesService = null)
        {
            _memoryService = memoryService;
            _database = database;
            _logger = logger;
            _httpClient = httpClient;
            _storiesService = storiesService;
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
            RegisterTimeTool(orchestrator, modelId, agentId);
            RegisterFileSystemTool(orchestrator, modelId, agentId);
            RegisterHttpTool(orchestrator, modelId, agentId);
            RegisterTtsApiTool(orchestrator, modelId, agentId);
            RegisterAudioCraftTool(orchestrator, modelId, agentId);
            RegisterAudioEvaluatorTool(orchestrator, modelId, agentId);
            RegisterStoryWriterTool(orchestrator, modelId, agentId);
            RegisterStoryEvaluatorTool(orchestrator, modelId, agentId);

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

        /// <summary>
        /// Create orchestrator with only the specified tools (most efficient).
        /// Only registers tools that are in the allowedTools list.
        /// </summary>
        public HybridLangChainOrchestrator CreateOrchestratorWithTools(
            IEnumerable<string> allowedTools,
            int? agentId = null,
            int? modelId = null)
        {
            var orchestrator = new HybridLangChainOrchestrator(_logger);
            var allowedSet = new HashSet<string>(allowedTools.Select(t => t.ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);

            _logger?.Log("Info", "ToolFactory", $"Creating orchestrator with tools: {string.Join(", ", allowedSet)}");

            // Register only the tools that are allowed
            if (allowedSet.Contains("text"))
                RegisterTextTool(orchestrator, modelId, agentId);

            if (allowedSet.Contains("math"))
                RegisterMathTool(orchestrator, modelId, agentId);

            if (allowedSet.Contains("memory"))
                RegisterMemoryTool(orchestrator, modelId, agentId);

            if (allowedSet.Contains("evaluator"))
                RegisterEvaluatorTool(orchestrator, modelId, agentId);

            if (allowedSet.Contains("time"))
                RegisterTimeTool(orchestrator, modelId, agentId);

            if (allowedSet.Contains("filesystem"))
                RegisterFileSystemTool(orchestrator, modelId, agentId);

            if (allowedSet.Contains("http"))
                RegisterHttpTool(orchestrator, modelId, agentId);

            if (allowedSet.Contains("tts"))
                RegisterTtsApiTool(orchestrator, modelId, agentId);

            if (allowedSet.Contains("audiocraft"))
                RegisterAudioCraftTool(orchestrator, modelId, agentId);

            if (allowedSet.Contains("audioevaluator"))
                RegisterAudioEvaluatorTool(orchestrator, modelId, agentId);

            if (allowedSet.Contains("storywriter"))
                RegisterStoryWriterTool(orchestrator, modelId, agentId);

            if (allowedSet.Contains("storyevaluator"))
                RegisterStoryEvaluatorTool(orchestrator, modelId, agentId);

            _logger?.Log("Info", "ToolFactory", $"Created orchestrator with {orchestrator.GetToolSchemas().Count} tools");
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

        private void RegisterTimeTool(HybridLangChainOrchestrator orchestrator, int? modelId, int? agentId)
        {
            try
            {
                var tool = new TimeTool(_logger)
                {
                    ModelId = modelId,
                    AgentId = agentId
                };
                orchestrator.RegisterTool(tool);
                _logger?.Log("Info", "ToolFactory", "Registered TimeTool");
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "ToolFactory", $"Failed to register TimeTool: {ex.Message}");
            }
        }

        private void RegisterFileSystemTool(HybridLangChainOrchestrator orchestrator, int? modelId, int? agentId)
        {
            try
            {
                var tool = new FileSystemTool(_logger)
                {
                    ModelId = modelId,
                    AgentId = agentId
                };
                orchestrator.RegisterTool(tool);
                _logger?.Log("Info", "ToolFactory", "Registered FileSystemTool");
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "ToolFactory", $"Failed to register FileSystemTool: {ex.Message}");
            }
        }

        private void RegisterHttpTool(HybridLangChainOrchestrator orchestrator, int? modelId, int? agentId)
        {
            try
            {
                var tool = new HttpTool(_logger)
                {
                    ModelId = modelId,
                    AgentId = agentId
                };
                orchestrator.RegisterTool(tool);
                _logger?.Log("Info", "ToolFactory", "Registered HttpTool");
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "ToolFactory", $"Failed to register HttpTool: {ex.Message}");
            }
        }

        private void RegisterTtsApiTool(HybridLangChainOrchestrator orchestrator, int? modelId, int? agentId)
        {
            try
            {
                if (_httpClient == null)
                {
                    _logger?.Log("Warn", "ToolFactory", "HttpClient not available, skipping TtsApiTool");
                    return;
                }

                var tool = new TtsApiTool(_httpClient, _logger)
                {
                    ModelId = modelId,
                    AgentId = agentId
                };
                orchestrator.RegisterTool(tool);
                _logger?.Log("Info", "ToolFactory", "Registered TtsApiTool");
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "ToolFactory", $"Failed to register TtsApiTool: {ex.Message}");
            }
        }

        private void RegisterAudioCraftTool(HybridLangChainOrchestrator orchestrator, int? modelId, int? agentId)
        {
            try
            {
                if (_httpClient == null)
                {
                    _logger?.Log("Warn", "ToolFactory", "HttpClient not available, skipping AudioCraftTool");
                    return;
                }

                var tool = new AudioCraftTool(_httpClient, false, _logger)
                {
                    ModelId = modelId,
                    AgentId = agentId
                };
                orchestrator.RegisterTool(tool);
                _logger?.Log("Info", "ToolFactory", "Registered AudioCraftTool");
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "ToolFactory", $"Failed to register AudioCraftTool: {ex.Message}");
            }
        }

        private void RegisterAudioEvaluatorTool(HybridLangChainOrchestrator orchestrator, int? modelId, int? agentId)
        {
            try
            {
                if (_httpClient == null)
                {
                    _logger?.Log("Warn", "ToolFactory", "HttpClient not available, skipping AudioEvaluatorTool");
                    return;
                }

                var tool = new AudioEvaluatorTool(_httpClient, _logger)
                {
                    ModelId = modelId,
                    AgentId = agentId
                };
                orchestrator.RegisterTool(tool);
                _logger?.Log("Info", "ToolFactory", "Registered AudioEvaluatorTool");
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "ToolFactory", $"Failed to register AudioEvaluatorTool: {ex.Message}");
            }
        }

        private void RegisterStoryWriterTool(HybridLangChainOrchestrator orchestrator, int? modelId, int? agentId)
        {
            try
            {
                if (_storiesService == null || _database == null)
                {
                    _logger?.Log("Warn", "ToolFactory", "StoriesService or DatabaseService not available, skipping StoryWriterTool");
                    return;
                }

                var tool = new StoryWriterTool(_storiesService, _database, _logger)
                {
                    ModelId = modelId,
                    AgentId = agentId
                };
                orchestrator.RegisterTool(tool);
                _logger?.Log("Info", "ToolFactory", "Registered StoryWriterTool");
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "ToolFactory", $"Failed to register StoryWriterTool: {ex.Message}");
            }
        }

        private void RegisterStoryEvaluatorTool(HybridLangChainOrchestrator orchestrator, int? modelId, int? agentId)
        {
            try
            {
                var tool = new StoryEvaluatorTool(_database, _logger)
                {
                    ModelId = modelId,
                    AgentId = agentId
                };
                orchestrator.RegisterTool(tool);
                _logger?.Log("Info", "ToolFactory", "Registered StoryEvaluatorTool");
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "ToolFactory", $"Failed to register StoryEvaluatorTool: {ex.Message}");
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
