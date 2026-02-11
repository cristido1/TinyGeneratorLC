using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands
{
    /// <summary>
    /// Comando per generare un riassunto di una storia esistente usando l'agente Story Summarizer.
    /// </summary>
    public class SummarizeStoryCommand : ICommand
    {
        private readonly long _storyId;
        private readonly DatabaseService _database;
        private readonly ILangChainKernelFactory _kernelFactory;
        private readonly ICustomLogger _logger;
        private readonly IAgentResolutionService _agentResolutionService;

        public SummarizeStoryCommand(
            long storyId,
            DatabaseService database,
            ILangChainKernelFactory kernelFactory,
            ICustomLogger logger,
            IAgentResolutionService? agentResolutionService = null)
        {
            _storyId = storyId;
            _database = database;
            _kernelFactory = kernelFactory;
            _logger = logger;
            _agentResolutionService = agentResolutionService ?? new AgentResolutionService(database);
        }

        public async Task<bool> ExecuteAsync(CancellationToken ct = default)
        {
            _logger.Log("Information", "SummarizeStory", $"Starting summarization for story {_storyId}");

            try
            {
                // ═══════════════════════════════════════════════════════════
                // 1. Carica la storia dal database
                // ═══════════════════════════════════════════════════════════
                var story = _database.GetStoryById(_storyId);
                if (story == null)
                {
                    _logger.Log("Error", "SummarizeStory", $"Story {_storyId} not found");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(story.StoryRaw))
                {
                    _logger.Log("Error", "SummarizeStory", $"Story {_storyId} has no content to summarize");
                    return false;
                }

                _logger.Log("Information", "SummarizeStory", $"Loaded story: '{story.Title}' ({story.CharCount} chars)");

                // ═══════════════════════════════════════════════════════════
                // 2. Trova l'agente Story Summarizer
                // ═══════════════════════════════════════════════════════════
                Agent? summarizerAgent = null;
                try
                {
                    summarizerAgent = _agentResolutionService.Resolve(CommandRoleCodes.Summarizer).Agent;
                }
                catch (Exception ex)
                {
                    _logger.Log("Warning", "SummarizeStory", $"Risoluzione centralizzata summarizer fallita: {ex.Message}");
                }

                if (summarizerAgent == null || !(summarizerAgent.Name?.Contains("Story Summarizer", StringComparison.OrdinalIgnoreCase) == true))
                {
                    var agents = _database.ListAgents();
                    summarizerAgent = agents.FirstOrDefault(a =>
                        string.Equals(a.Role, CommandRoleCodes.Summarizer, StringComparison.OrdinalIgnoreCase) &&
                        a.IsActive &&
                        a.Name?.Contains("Story Summarizer", StringComparison.OrdinalIgnoreCase) == true);
                }

                if (summarizerAgent == null)
                {
                    _logger.Log("Error", "SummarizeStory", "No active Story Summarizer agent found");
                    return false;
                }

                _logger.Log("Information", "SummarizeStory", $"Using agent: {summarizerAgent.Name} (ID: {summarizerAgent.Id}, Model: {summarizerAgent.ModelName})");

                // ═══════════════════════════════════════════════════════════
                // 3. Ottieni orchestrator per l'agente summarizer
                // ═══════════════════════════════════════════════════════════
                var orchestrator = _kernelFactory.GetOrchestratorForAgent(summarizerAgent.Id);
                if (orchestrator == null)
                {
                    _logger.Log("Warning", "SummarizeStory", $"No orchestrator found for agent {summarizerAgent.Id}, creating new one");
                    
                    // Crea orchestrator al volo se non esiste
                    _kernelFactory.EnsureOrchestratorForAgent(summarizerAgent.Id);
                    orchestrator = _kernelFactory.GetOrchestratorForAgent(summarizerAgent.Id);
                    
                    if (orchestrator == null)
                    {
                        _logger.Log("Error", "SummarizeStory", $"Failed to create orchestrator for agent {summarizerAgent.Id}");
                        return false;
                    }
                }

                // ═══════════════════════════════════════════════════════════
                // 4. Prepara il prompt per il summarizer
                // ═══════════════════════════════════════════════════════════
                var summarizerPrompt = BuildSummarizerPrompt(story);
                _logger.Log("Information", "SummarizeStory", $"Prompt prepared ({summarizerPrompt.Length} chars)");

                // ═══════════════════════════════════════════════════════════
                // 5. Invoca l'orchestrator per generare il riassunto
                // ═══════════════════════════════════════════════════════════
                _logger.Log("Information", "SummarizeStory", "Invoking summarizer agent...");
                
                // Per summarizer semplice, non servono tools - usa il bridge diretto
                var bridge = _kernelFactory.CreateChatBridge(
                    summarizerAgent.ModelName ?? "qwen2.5:7b-instruct",
                    summarizerAgent.Temperature,
                    summarizerAgent.TopP,
                    summarizerAgent.RepeatPenalty,
                    summarizerAgent.TopK,
                    summarizerAgent.RepeatLastN,
                    summarizerAgent.NumPredict);

                var messages = new List<ConversationMessage>
                {
                    new ConversationMessage { Role = "user", Content = summarizerPrompt }
                };

                var result = await bridge.CallModelWithToolsAsync(messages, new List<Dictionary<string, object>>(), ct);
                var summary = result?.Trim() ?? "";

                if (string.IsNullOrWhiteSpace(summary))
                {
                    _logger.Log("Error", "SummarizeStory", "Summarizer returned empty result");
                    return false;
                }

                _logger.Log("Information", "SummarizeStory", $"Summary generated ({summary.Length} chars)");

                // ═══════════════════════════════════════════════════════════
                // 6. Salva il riassunto nella storia
                // ═══════════════════════════════════════════════════════════
                bool updated = _database.UpdateStorySummary(_storyId, summary);
                if (updated)
                {
                    _logger.Log("Information", "SummarizeStory", $"✓ Summary saved for story {_storyId}");
                    return true;
                }
                else
                {
                    _logger.Log("Error", "SummarizeStory", $"Failed to save summary for story {_storyId}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.Log("Error", "SummarizeStory", $"Exception during summarization: {ex.Message}");
                _logger.Log("Error", "SummarizeStory", ex.StackTrace ?? "");
                return false;
            }
        }

        /// <summary>
        /// Costruisce il prompt per il summarizer includendo titolo e testo della storia.
        /// </summary>
        private string BuildSummarizerPrompt(StoryRecord story)
        {
            var prompt = $"""
                Create a summary of the following story.

                TITLE: {story.Title ?? "Untitled"}

                STORY TEXT:
                {story.StoryRaw}

                ---
                Provide a 3-5 sentence summary that captures the main characters, central conflict, key events, and resolution. Write in the same language as the story.
                """;

            return prompt;
        }
    }
}
