using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly Func<StoriesService?>? _storiesAccessor;
        private readonly TtsService? _ttsService;
        private readonly IMemoryEmbeddingGenerator? _embeddingGenerator;
        private readonly IMemoryEmbeddingBackfillScheduler? _embeddingScheduler;

        public LangChainToolFactory(
            PersistentMemoryService? memoryService = null,
            DatabaseService? database = null,
            ICustomLogger? logger = null,
            HttpClient? httpClient = null,
            Func<StoriesService?>? storiesAccessor = null,
            TtsService? ttsService = null,
            IMemoryEmbeddingGenerator? embeddingGenerator = null,
            IMemoryEmbeddingBackfillScheduler? embeddingScheduler = null)
        {
            _memoryService = memoryService;
            _database = database;
            _logger = logger;
            _httpClient = httpClient;
            _storiesAccessor = storiesAccessor;
            _ttsService = ttsService;
            _embeddingGenerator = embeddingGenerator;
            _embeddingScheduler = embeddingScheduler;
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
            // StoryEvaluatorTool removed: evaluations handled by EvaluatorTool only

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
            int? modelId = null,
            string? ttsWorkingFolder = null,
            string? ttsStoryText = null)
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

            if (allowedSet.Contains("responsechecker"))
                RegisterResponseCheckerTool(orchestrator, modelId, agentId);

            if (allowedSet.Contains("chunkfacts") || allowedSet.Contains("chunk_facts"))
                RegisterChunkFactsExtractorTool(orchestrator, modelId, agentId);

            if (allowedSet.Contains("coherence") || allowedSet.Contains("coherence_calculator"))
                RegisterCoherenceCalculatorTool(orchestrator, modelId, agentId);

            // 'storyevaluator' tool removed in favor of unified EvaluatorTool

            var wantsVoiceTool = allowedSet.Contains("voicechoser") || allowedSet.Contains("voicechooser");
            var wantsSchemaTool = allowedSet.Contains("ttsschema") || allowedSet.Contains("ttsschematool");
            // Register TTS-specific tools only if explicitly requested
            if (!string.IsNullOrWhiteSpace(ttsWorkingFolder))
            {
                if (wantsSchemaTool)
                {
                    RegisterTtsSchemaTool(orchestrator, modelId, agentId, ttsWorkingFolder, ttsStoryText);
                }
                if (wantsVoiceTool)
                {
                    RegisterVoiceChoserTool(orchestrator, modelId, agentId, ttsWorkingFolder);
                }
            }

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

                var tool = new MemoryTool(_memoryService, _logger, _embeddingGenerator, _embeddingScheduler)
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
                if (_database == null)
                {
                    _logger?.Log("Warn", "ToolFactory", "Database not available, skipping StoryWriterTool");
                    return;
                }

                var storiesService = _storiesAccessor?.Invoke();
                if (storiesService == null)
                {
                    _logger?.Log("Warn", "ToolFactory", "StoriesService or DatabaseService not available, skipping StoryWriterTool");
                    return;
                }

                var tool = new StoryWriterTool(storiesService, _database, _logger)
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

        // StoryEvaluatorTool has been removed; use EvaluatorTool instead.

        private void RegisterTtsSchemaTool(HybridLangChainOrchestrator orchestrator, int? modelId, int? agentId, string workingFolder, string? storyText = null)
        {
            try
            {
                var tool = new TtsSchemaTool(workingFolder, storyText, _logger, _database)
                {
                    ModelId = modelId,
                    AgentId = agentId
                };
                orchestrator.RegisterTool(tool);
                _logger?.Log("Info", "ToolFactory", $"Registered TtsSchemaTool with folder: {workingFolder}");
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "ToolFactory", $"Failed to register TtsSchemaTool: {ex.Message}");
            }
        }

        private void RegisterVoiceChoserTool(HybridLangChainOrchestrator orchestrator, int? modelId, int? agentId, string workingFolder)
        {
            try
            {
                if (_ttsService == null)
                {
                    _logger?.Log("Warn", "ToolFactory", "TtsService not available, skipping VoiceChoserTool");
                    return;
                }

                var tool = new VoiceChoserTool(workingFolder, _database, _ttsService, _logger)
                {
                    ModelId = modelId,
                    AgentId = agentId
                };
                orchestrator.RegisterTool(tool);
                _logger?.Log("Info", "ToolFactory", $"Registered VoiceChoserTool with folder: {workingFolder}");
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "ToolFactory", $"Failed to register VoiceChoserTool: {ex.Message}");
            }
        }

        private void RegisterResponseCheckerTool(HybridLangChainOrchestrator orchestrator, int? modelId, int? agentId)
        {
            try
            {
                // ResponseCheckerService requires HttpClient - skip if not available
                if (_httpClient == null)
                {
                    _logger?.Log("Warn", "ToolFactory", "HttpClient not available, skipping ResponseCheckerTool");
                    return;
                }

                // TODO: Create ResponseCheckerService properly with IHttpClientFactory
                // For now, ResponseCheckerService is registered globally in DI
                // and used directly by MultiStepOrchestrationService
                _logger?.Log("Info", "ToolFactory", "ResponseCheckerTool registration skipped (use DI service)");

                /*
                // Create ResponseCheckerService
                var checkerService = new ResponseCheckerService(
                    null, // ILangChainKernelFactory - not needed for tool invocation
                    _database,
                    _logger,
                    httpClientFactory // Need IHttpClientFactory
                );

                var tool = new ResponseCheckerTool(checkerService);
                // Note: ResponseCheckerTool uses SK attributes, not BaseLangChainTool
                // Register via SK plugin method if orchestrator supports it
                _logger?.Log("Info", "ToolFactory", "Registered ResponseCheckerTool");
                */
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "ToolFactory", $"Failed to register ResponseCheckerTool: {ex.Message}");
            }
        }

        private void RegisterChunkFactsExtractorTool(HybridLangChainOrchestrator orchestrator, int? modelId, int? agentId)
        {
            try
            {
                if (_database == null)
                {
                    _logger?.Log("Warn", "ToolFactory", "DatabaseService not available, skipping ChunkFactsExtractorTool");
                    return;
                }

                var tool = new ChunkFactsExtractorTool(_database, _logger)
                {
                    ModelId = modelId,
                    AgentId = agentId
                };
                orchestrator.RegisterTool(tool);
                _logger?.Log("Info", "ToolFactory", "Registered ChunkFactsExtractorTool");
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "ToolFactory", $"Failed to register ChunkFactsExtractorTool: {ex.Message}");
            }
        }

        private void RegisterCoherenceCalculatorTool(HybridLangChainOrchestrator orchestrator, int? modelId, int? agentId)
        {
            try
            {
                if (_database == null)
                {
                    _logger?.Log("Warn", "ToolFactory", "DatabaseService not available, skipping CoherenceCalculatorTool");
                    return;
                }

                var tool = new CoherenceCalculatorTool(_database, _logger)
                {
                    ModelId = modelId,
                    AgentId = agentId
                };
                orchestrator.RegisterTool(tool);
                _logger?.Log("Info", "ToolFactory", "Registered CoherenceCalculatorTool");
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "ToolFactory", $"Failed to register CoherenceCalculatorTool: {ex.Message}");
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

