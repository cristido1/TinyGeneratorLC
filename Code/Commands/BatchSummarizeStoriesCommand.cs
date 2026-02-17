using Microsoft.Extensions.DependencyInjection;
using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands
{
    /// <summary>
    /// Comando batch che accoda riassunti per tutte le storie con valutazione >= 60.
    /// Termina immediatamente dopo aver accodato i comandi, senza attendere il completamento.
    /// </summary>
    public class BatchSummarizeStoriesCommand : ICommand
    {
        private readonly DatabaseService _database;
        private readonly ILangChainKernelFactory _kernelFactory;
        private readonly ICommandEnqueuer _dispatcher;
        private readonly ICustomLogger _logger;
        private readonly IServiceScopeFactory? _scopeFactory;
        private readonly int _minScore;

        public BatchSummarizeStoriesCommand(
            DatabaseService database,
            ILangChainKernelFactory kernelFactory,
            ICommandEnqueuer dispatcher,
            ICustomLogger logger,
            IServiceScopeFactory? scopeFactory = null,
            int minScore = 60)
        {
            _database = database;
            _kernelFactory = kernelFactory;
            _dispatcher = dispatcher;
            _logger = logger;
            _scopeFactory = scopeFactory;
            _minScore = minScore;
        }

        public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _logger.Log("Information", "BatchSummarize", $"Starting batch summarization (min score: {_minScore})");

            try
            {
                // Trova tutte le storie con valutazione >= minScore senza riassunto
                var allStories = _database.GetAllStories();
                var eligibleStories = allStories
                    .Where(s =>
                        s.Score >= _minScore &&
                        string.IsNullOrWhiteSpace(s.Summary) &&
                        !string.IsNullOrWhiteSpace(s.StoryRaw))
                    .ToList();

                _logger.Log("Information", "BatchSummarize", $"Found {eligibleStories.Count} stories eligible for summarization");

                if (eligibleStories.Count == 0)
                {
                    return new CommandResult(true, "No stories to summarize");
                }

                // Accoda un comando SummarizeStory per ogni storia
                int enqueued = 0;
                foreach (var story in eligibleStories)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        IAgentCallService? modelExecution = null;
                        if (_scopeFactory != null)
                        {
                            using var modelScope = _scopeFactory.CreateScope();
                            modelExecution = modelScope.ServiceProvider.GetService<IAgentCallService>();
                        }
                        modelExecution ??= ServiceLocator.Services?.GetService<IAgentCallService>();

                        var runId = Guid.NewGuid().ToString();
                        var cmd = new SummarizeStoryCommand(
                            story.Id,
                            _database,
                            _kernelFactory,
                            _logger,
                            scopeFactory: _scopeFactory,
                            modelExecution: modelExecution);

                        var summarizeCommand = new DelegateCommand(
                            "SummarizeStory",
                            async ctx =>
                            {
                                bool success = await cmd.ExecuteAsync(ctx.CancellationToken, ctx.RunId);
                                var message = success
                                    ? "Summary generated"
                                    : (string.IsNullOrWhiteSpace(cmd.LastError) ? "Failed to generate summary" : cmd.LastError);
                                return new CommandResult(success, message);
                            },
                            priority: 3);

                        _dispatcher.Enqueue(
                            summarizeCommand,
                            runId: runId,
                            metadata: new Dictionary<string, string>
                            {
                                ["storyId"] = story.Id.ToString(),
                                ["storyTitle"] = story.Title ?? "Untitled",
                                ["triggeredBy"] = "batch_summarize",
                                ["batchScore"] = story.Score.ToString("F2")
                            },
                            priority: 3);

                        enqueued++;
                        _logger.Log("Information", "BatchSummarize", $"Enqueued summarization for story {story.Id} (score: {story.Score:F2})");
                    }
                    catch (Exception ex)
                    {
                        _logger.Log("Warning", "BatchSummarize", $"Failed to enqueue story {story.Id}: {ex.Message}");
                    }
                }

                var message = $"Enqueued {enqueued} summarization commands";
                _logger.Log("Information", "BatchSummarize", message);

                // Ritorna immediatamente - i comandi accodati verranno eseguiti in background
                return new CommandResult(true, message);
            }
            catch (Exception ex)
            {
                _logger.Log("Error", "BatchSummarize", $"Batch summarization failed: {ex.Message}");
                return new CommandResult(false, ex.Message);
            }
        }
    }
}
