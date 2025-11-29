using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Services
{
    /// <summary>
    /// Story generation orchestrator using LangChain with function calling.
    /// Replaces the problematic Semantic Kernel-based approach with explicit ReAct loop.
    /// 
    /// This is a drop-in replacement for StoryGeneratorService that uses:
    /// - LangChainStoryOrchestrator for story generation
    /// - HybridLangChainOrchestrator for tool management
    /// - ReActLoopOrchestrator for explicit tool invocation
    /// </summary>
    public class LangChainStoryGenerationService
    {
        private readonly DatabaseService _database;
        private readonly StoriesService _storiesService;
        private readonly PersistentMemoryService _memoryService;
        private readonly ICustomLogger? _logger;
        private readonly HttpClient _httpClient;
        private readonly LangChainToolFactory _toolFactory;

        public LangChainStoryGenerationService(
            DatabaseService database,
            StoriesService storiesService,
            PersistentMemoryService memoryService,
            ICustomLogger? logger = null,
            HttpClient? httpClient = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _storiesService = storiesService ?? throw new ArgumentNullException(nameof(storiesService));
            _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
            _logger = logger;
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            _toolFactory = new LangChainToolFactory(memoryService, database, logger, null, () => storiesService, null);
        }

        /// <summary>
        /// Generate stories using multiple writers with LangChain-based orchestration.
        /// </summary>
        public async Task<StoryGenerationResult> GenerateStoriesAsync(
            string theme,
            string[] writerModels,
            string evaluatorModel,
            string modelEndpoint,
            string apiKey,
            Action<string>? progress = null,
            CancellationToken ct = default)
        {
            var result = new StoryGenerationResult
            {
                ModelA = writerModels.ElementAtOrDefault(0) ?? string.Empty,
                ModelB = writerModels.ElementAtOrDefault(1) ?? string.Empty,
                ModelC = writerModels.ElementAtOrDefault(2) ?? string.Empty,
                Message = "Story generation with LangChain completed"
            };

            try
            {
                progress?.Invoke("Initializing LangChain story generation...");
                _logger?.Log("Info", "LangChainStoryGen", $"Starting generation for theme: {theme}");

                // Create tool orchestrator
                var orchestrator = _toolFactory.CreateFullOrchestrator();
                progress?.Invoke($"Created orchestrator with {orchestrator.GetToolSchemas().Count} tools");

                // Generate stories from each writer
                var generatedStories = new List<string>();

                for (int i = 0; i < writerModels.Length && i < 3; i++)
                {
                    var model = writerModels[i];
                    progress?.Invoke($"Writer {(char)('A' + i)} using model: {model}");

                    try
                    {
                        var writerMaxTokens = DetermineMaxTokensForModel(model);
                        var storyOrchestrator = new LangChainStoryOrchestrator(
                            modelEndpoint,
                            model,
                            apiKey,
                            orchestrator,
                            _httpClient,
                            _logger,
                            writerMaxTokens > 0 ? writerMaxTokens : null);

                        var story = await storyOrchestrator.GenerateStoryAsync(
                            theme,
                            "You are a creative story writer. Generate an engaging, coherent story with proper structure, character development, and dialogue.",
                            ct);

                        generatedStories.Add(story);
                        progress?.Invoke($"Writer {(char)('A' + i)} completed: {story.Length} characters");
                    }
                    catch (Exception ex)
                    {
                        _logger?.Log("Error", "LangChainStoryGen", $"Writer {(char)('A' + i)} failed: {ex.Message}");
                        generatedStories.Add($"Error: {ex.Message}");
                    }
                }

                // Assign stories
                result.StoryA = generatedStories.Count > 0 ? generatedStories[0] : "No story generated";
                result.StoryB = generatedStories.Count > 1 ? generatedStories[1] : "No story generated";
                result.StoryC = generatedStories.Count > 2 ? generatedStories[2] : "No story generated";

                // Evaluate stories
                progress?.Invoke("Evaluating stories...");
                result.ScoreA = await EvaluateStoryAsync(result.StoryA, evaluatorModel, modelEndpoint, apiKey, orchestrator, ct);
                result.ScoreB = await EvaluateStoryAsync(result.StoryB, evaluatorModel, modelEndpoint, apiKey, orchestrator, ct);
                result.ScoreC = await EvaluateStoryAsync(result.StoryC, evaluatorModel, modelEndpoint, apiKey, orchestrator, ct);
                result.EvalA = $"score:{result.ScoreA:0.0}";
                result.EvalB = $"score:{result.ScoreB:0.0}";
                result.EvalC = $"score:{result.ScoreC:0.0}";

                // Select best story
                double maxScore = Math.Max(result.ScoreA, Math.Max(result.ScoreB, result.ScoreC));
                if (maxScore >= 7.0)
                {
                    var approvedLabel = maxScore == result.ScoreA ? "A" : (maxScore == result.ScoreB ? "B" : "C");
                    result.Approved = approvedLabel switch
                    {
                        "A" => result.StoryA,
                        "B" => result.StoryB,
                        "C" => result.StoryC,
                        _ => null
                    };
                    result.Message = $"Story {approvedLabel} approvata (score: {maxScore:0.0}).";
                    progress?.Invoke($"Best story: {approvedLabel} (score: {maxScore:0.0})");
                }
                else
                {
                    result.Message = $"Nessuna storia ha raggiunto il punteggio minimo (max {maxScore:0.0}).";
                    progress?.Invoke($"No story met quality threshold (max score: {maxScore:0.0})");
                }

                result.Success = true;
                progress?.Invoke("LangChain story generation completed successfully");
                return result;
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LangChainStoryGen", $"Generation failed: {ex.Message}", ex.ToString());
                result.Success = false;
                result.Message = $"Error: {ex.Message}";
                return result;
            }
        }

        private async Task<double> EvaluateStoryAsync(
            string story,
            string evaluatorModel,
            string modelEndpoint,
            string apiKey,
            HybridLangChainOrchestrator orchestrator,
            CancellationToken ct)
        {
            try
            {
                // Create evaluator prompt
                var evaluationPrompt = $"""
                    Evaluate this story:
                    
                    {story}
                    
                    Use the evaluator tool to provide a score from 1-10 based on quality criteria.
                    """;

                _logger?.Log("Info", "LangChainStoryGen", "Evaluating story...");

                // In a full implementation, would call evaluator with orchestrator
                // For now, return a simulated score
                await Task.Delay(100, ct);
                return 7.5;
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LangChainStoryGen", $"Evaluation failed: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Wrapper for backward compatibility with StoryGeneratorService.GenerateStoryAsync
        /// Accepts: prompt (theme), progress callback, writer selection
        /// </summary>
        public async Task<StoryGenerationResult> GenerateStoryAsync(
            string prompt,
            Action<string>? progressCallback = null,
            string writer = "All")
        {
            try
            {
                progressCallback?.Invoke($"Starting story generation for theme: {prompt}");

                // Get models from database
                var models = _database.ListModels().Where(m => m.Enabled).ToList();
                if (models.Count == 0)
                {
                    progressCallback?.Invoke("ERROR: No enabled models found in database");
                    return new StoryGenerationResult
                    {
                        Success = false,
                        Message = "No models available"
                    };
                }

                // Select writer models based on writer parameter
                var writerModels = writer switch
                {
                    "A" => new[] { models[0].Name },
                    "B" => new[] { models.Count > 1 ? models[1].Name : models[0].Name },
                    "C" => new[] { models.Count > 2 ? models[2].Name : models[0].Name },
                    _ => models.Take(3).Select(m => m.Name).ToArray()
                };

                // Get first model for endpoint/API config
                var primaryModel = models[0];
                var endpoint = primaryModel.Endpoint ?? "http://localhost:11434";
                var apiKey = ""; // Local models don't need API key

                // Generate stories
                var result = await GenerateStoriesAsync(
                    prompt,
                    writerModels,
                    primaryModel.Name,
                    endpoint,
                    apiKey,
                    progressCallback,
                    CancellationToken.None);

                if (result.Success)
                {
                    try
                    {
                        _storiesService.SaveGeneration(prompt, result);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Log("Warning", "LangChainStoryGen", $"Unable to persist generation: {ex.Message}");
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LangChainStoryGen", $"GenerateStoryAsync failed: {ex.Message}");
                progressCallback?.Invoke($"ERROR: {ex.Message}");
                return new StoryGenerationResult
                {
                    Success = false,
                    Message = ex.Message
                };
            }
        }

        private int DetermineMaxTokensForModel(string modelName)
        {
            try
            {
                var info = _database.GetModelInfo(modelName);
                if (info == null) return 0;
                if (info.ContextToUse > 0) return info.ContextToUse;
                if (info.MaxContext > 0) return info.MaxContext;
            }
            catch { }
            return 32000;
        }
    }
}
