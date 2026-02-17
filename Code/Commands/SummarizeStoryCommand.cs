using TinyGenerator.Models;
using Microsoft.Extensions.DependencyInjection;

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
        private readonly IServiceScopeFactory? _scopeFactory;
        private readonly IAgentCallService? _modelExecution;
        public string? LastError { get; private set; }

        public SummarizeStoryCommand(
            long storyId,
            DatabaseService database,
            ILangChainKernelFactory kernelFactory,
            ICustomLogger logger,
            IAgentResolutionService? agentResolutionService = null,
            IServiceScopeFactory? scopeFactory = null,
            IAgentCallService? modelExecution = null)
        {
            _storyId = storyId;
            _database = database;
            _kernelFactory = kernelFactory;
            _logger = logger;
            _agentResolutionService = agentResolutionService ?? new AgentResolutionService(database);
            _scopeFactory = scopeFactory;
            _modelExecution = modelExecution;
        }

        public Task<bool> ExecuteAsync(CancellationToken ct = default)
            => ExecuteAsync(ct, runId: null);

        public async Task<bool> ExecuteAsync(CancellationToken ct, string? runId)
        {
            LastError = null;
            _logger.Log("Information", "SummarizeStory", $"Starting summarization for story {_storyId}");

            try
            {
                // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
                // 1. Carica la storia dal database
                // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
                var story = _database.GetStoryById(_storyId);
                if (story == null)
                {
                    return Fail(runId, $"Story {_storyId} not found");
                }

                if (string.IsNullOrWhiteSpace(story.StoryRaw))
                {
                    return Fail(runId, $"Story {_storyId} has no content to summarize");
                }

                _logger.Log("Information", "SummarizeStory", $"Loaded story: '{story.Title}' ({story.CharCount} chars)");

                // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
                // 2. Trova l'agente Story Summarizer
                // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
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
                    return Fail(runId, "No active Story Summarizer agent found");
                }

                _logger.Log("Information", "SummarizeStory", $"Using agent: {summarizerAgent.Name} (ID: {summarizerAgent.Id}, Model: {summarizerAgent.ModelName})");

                // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
                // 3. Ottieni orchestrator per l'agente summarizer
                // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
                var orchestrator = _kernelFactory.GetOrchestratorForAgent(summarizerAgent.Id);
                if (orchestrator == null)
                {
                    _logger.Log("Warning", "SummarizeStory", $"No orchestrator found for agent {summarizerAgent.Id}, creating new one");
                    
                    // Crea orchestrator al volo se non esiste
                    _kernelFactory.EnsureOrchestratorForAgent(summarizerAgent.Id);
                    orchestrator = _kernelFactory.GetOrchestratorForAgent(summarizerAgent.Id);
                    
                    if (orchestrator == null)
                    {
                        return Fail(runId, $"Failed to create orchestrator for agent {summarizerAgent.Id}");
                    }
                }

                // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
                // 4. Prepara il prompt per il summarizer
                // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
                var summarizerPrompt = BuildSummarizerPrompt(story);
                _logger.Log("Information", "SummarizeStory", $"Prompt prepared ({summarizerPrompt.Length} chars)");

                // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
                // 5. Invoca l'orchestrator per generare il riassunto
                // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
                _logger.Log("Information", "SummarizeStory", "Invoking summarizer agent...");
                
                var execution = _modelExecution;
                if (execution == null && _scopeFactory != null)
                {
                    using var scope = _scopeFactory.CreateScope();
                    execution = scope.ServiceProvider.GetService<IAgentCallService>();
                }
                if (execution == null)
                {
                    execution = ServiceLocator.Services?.GetService<IAgentCallService>();
                }

                if (execution == null)
                {
                    return Fail(runId, "IAgentCallService non disponibile: chiamata diretta al modello disabilitata");
                }

                var modelResult = await execution.ExecuteAsync(
                    new CommandModelExecutionService.Request
                    {
                        CommandKey = "summarize_story",
                        Agent = summarizerAgent,
                        RoleCode = CommandRoleCodes.Summarizer,
                        Prompt = summarizerPrompt,
                        SystemPrompt = summarizerAgent.Instructions ?? summarizerAgent.Prompt ?? "Sei un summarizer esperto.",
                        MaxAttempts = 2,
                        RetryDelaySeconds = 1,
                        StepTimeoutSec = 60,
                        UseResponseChecker = false,
                        EnableFallback = true,
                        DiagnoseOnFinalFailure = true,
                        ExplainAfterAttempt = 1,
                        RunId = $"summarize_story_{_storyId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}",
                        EnableDeterministicValidation = true,
                        DeterministicValidator = output =>
                        {
                            var text = (output ?? string.Empty).Trim();
                            return string.IsNullOrWhiteSpace(text)
                                ? new CommandModelExecutionService.DeterministicValidationResult(false, "Riassunto vuoto")
                                : new CommandModelExecutionService.DeterministicValidationResult(true, null);
                        }
                    },
                    ct);

                var summary = (modelResult.Text ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(summary))
                {
                    var reason = modelResult.Error ?? "n/a";
                    return Fail(runId, $"Summarizer returned empty result: {reason}");
                }

                _logger.Log("Information", "SummarizeStory", $"Summary generated ({summary.Length} chars)");

                // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
                // 6. Salva il riassunto nella storia
                // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
                bool updated = _database.UpdateStorySummary(_storyId, summary);
                if (updated)
                {
                    _logger.Log("Information", "SummarizeStory", $"âœ“ Summary saved for story {_storyId}");
                    if (!string.IsNullOrWhiteSpace(runId))
                    {
                        _logger.Append(runId, $"Summary saved for story {_storyId} ({summary.Length} chars)");
                    }
                    return true;
                }
                else
                {
                    return Fail(runId, $"Failed to save summary for story {_storyId}");
                }
            }
            catch (Exception ex)
            {
                LastError = $"Exception during summarization: {ex.Message}";
                _logger.Log("Error", "SummarizeStory", LastError);
                _logger.Log("Error", "SummarizeStory", ex.StackTrace ?? "");
                if (!string.IsNullOrWhiteSpace(runId))
                {
                    _logger.Append(runId, LastError, "error");
                }
                return false;
            }
        }

        private bool Fail(string? runId, string message)
        {
            LastError = message;
            _logger.Log("Error", "SummarizeStory", message);
            if (!string.IsNullOrWhiteSpace(runId))
            {
                _logger.Append(runId, message, "error");
            }
            return false;
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

