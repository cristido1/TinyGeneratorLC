using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
        private readonly IServiceScopeFactory? _scopeFactory;
        private readonly ICallCenter? _callCenter;

        public string? LastError { get; private set; }

        public SummarizeStoryCommand(
            long storyId,
            DatabaseService database,
            ILangChainKernelFactory kernelFactory,
            ICustomLogger logger,
            IAgentResolutionService? agentResolutionService = null,
            IServiceScopeFactory? scopeFactory = null,
            IAgentCallService? modelExecution = null,
            ICallCenter? callCenter = null)
        {
            _storyId = storyId;
            _database = database;
            _kernelFactory = kernelFactory;
            _logger = logger;
            _agentResolutionService = agentResolutionService ?? new AgentResolutionService(database);
            _scopeFactory = scopeFactory;
            _ = modelExecution;
            _callCenter = callCenter;
        }

        public Task<bool> ExecuteAsync(CancellationToken ct = default)
            => ExecuteAsync(ct, runId: null);

        public async Task<bool> ExecuteAsync(CancellationToken ct, string? runId)
        {
            LastError = null;
            _logger.Log("Information", "SummarizeStory", $"Starting summarization for story {_storyId}");

            try
            {
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

                _kernelFactory.EnsureOrchestratorForAgent(summarizerAgent.Id);

                var summarizerPrompt = BuildSummarizerPrompt(story);
                _logger.Log("Information", "SummarizeStory", $"Prompt prepared ({summarizerPrompt.Length} chars)");

                var callCenter = ResolveCallCenter();
                if (callCenter == null)
                {
                    return Fail(runId, "ICallCenter non disponibile: chiamata centralizzata disabilitata");
                }

                var systemPrompt = summarizerAgent.Instructions ?? summarizerAgent.Prompt ?? "Sei un summarizer esperto.";
                var history = new ChatHistory();
                history.AddSystem(systemPrompt);
                history.AddUser(summarizerPrompt);

                var options = new CallOptions
                {
                    Operation = "summarize_story",
                    Timeout = TimeSpan.FromSeconds(60),
                    MaxRetries = 1,
                    UseResponseChecker = false,
                    AllowFallback = true,
                    AskFailExplanation = true,
                    SystemPromptOverride = systemPrompt
                };
                options.DeterministicChecks.Add(new CheckEmpty
                {
                    Options = Options.Create<object>(new Dictionary<string, object>
                    {
                        ["ErrorMessage"] = "Riassunto vuoto"
                    })
                });

                var result = await callCenter.CallAgentAsync(
                    storyId: _storyId,
                    threadId: $"summarize_story_{_storyId}".GetHashCode(StringComparison.Ordinal),
                    agent: summarizerAgent,
                    history: history,
                    options: options,
                    cancellationToken: ct).ConfigureAwait(false);

                var summary = (result.ResponseText ?? string.Empty).Trim();
                if (!result.Success || string.IsNullOrWhiteSpace(summary))
                {
                    var reason = result.FailureReason ?? "n/a";
                    return Fail(runId, $"Summarizer returned empty result: {reason}");
                }

                _logger.Log("Information", "SummarizeStory", $"Summary generated ({summary.Length} chars)");

                var updated = _database.UpdateStorySummary(_storyId, summary);
                if (!updated)
                {
                    return Fail(runId, $"Failed to save summary for story {_storyId}");
                }

                _logger.Log("Information", "SummarizeStory", $"Summary saved for story {_storyId}");
                if (!string.IsNullOrWhiteSpace(runId))
                {
                    _logger.Append(runId, $"Summary saved for story {_storyId} ({summary.Length} chars)");
                }

                return true;
            }
            catch (Exception ex)
            {
                LastError = $"Exception during summarization: {ex.Message}";
                _logger.Log("Error", "SummarizeStory", LastError);
                _logger.Log("Error", "SummarizeStory", ex.StackTrace ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(runId))
                {
                    _logger.Append(runId, LastError, "error");
                }
                return false;
            }
        }

        private ICallCenter? ResolveCallCenter()
        {
            if (_callCenter != null)
            {
                return _callCenter;
            }

            if (_scopeFactory != null)
            {
                using var scope = _scopeFactory.CreateScope();
                var fromScope = scope.ServiceProvider.GetService<ICallCenter>();
                if (fromScope != null)
                {
                    return fromScope;
                }
            }

            var fromRoot = ServiceLocator.Services?.GetService<ICallCenter>();
            if (fromRoot != null)
            {
                return fromRoot;
            }
            
            return null;
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

        private string BuildSummarizerPrompt(StoryRecord story)
        {
            return $"""
                Create a summary of the following story.

                TITLE: {story.Title ?? "Untitled"}

                STORY TEXT:
                {story.StoryRaw}

                ---
                Provide a 3-5 sentence summary that captures the main characters, central conflict, key events, and resolution. Write in the same language as the story.
                """;
        }
    }
}
