using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services
{
    /// <summary>
    /// After a successful story evaluation command completes, if the story has at least 2 evaluations
    /// and the average score is > 65/100, enqueue the formatter command (story_raw -> story_tagged).
    /// This never blocks the evaluation command.
    /// </summary>
    public sealed class AutoFormatOnHighEvaluationService : IHostedService
    {
        private const double MinAverageScore = 65.0;
        private const int MinEvaluations = 2;

        private readonly ICommandDispatcher _dispatcher;
        private readonly DatabaseService _database;
        private readonly ILangChainKernelFactory _kernelFactory;
        private readonly ICustomLogger? _customLogger;
        private readonly ILogger<AutoFormatOnHighEvaluationService>? _logger;

        public AutoFormatOnHighEvaluationService(
            ICommandDispatcher dispatcher,
            DatabaseService database,
            ILangChainKernelFactory kernelFactory,
            ICustomLogger? customLogger = null,
            ILogger<AutoFormatOnHighEvaluationService>? logger = null)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
            _customLogger = customLogger;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _dispatcher.CommandCompleted += OnCommandCompleted;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _dispatcher.CommandCompleted -= OnCommandCompleted;
            return Task.CompletedTask;
        }

        private void OnCommandCompleted(CommandCompletedEventArgs args)
        {
            if (!args.Success) return;
            if (!IsStoryEvaluationOperation(args.OperationName)) return;

            // Fire-and-forget: never block the dispatcher worker thread.
            _ = Task.Run(() => HandleEvaluationCompletionAsync(args.RunId, args.OperationName));
        }

        private async Task HandleEvaluationCompletionAsync(string runId, string operationName)
        {
            try
            {
                var snapshot = _dispatcher
                    .GetActiveCommands()
                    .FirstOrDefault(s => string.Equals(s.RunId, runId, StringComparison.OrdinalIgnoreCase));

                if (snapshot?.Metadata == null) return;
                if (!snapshot.Metadata.TryGetValue("storyId", out var sidRaw)) return;
                if (!long.TryParse(sidRaw, out var storyId) || storyId <= 0) return;

                // Only enqueue if we don't already have a tagged story.
                var story = _database.GetStoryById(storyId);
                if (story == null) return;
                if (!string.IsNullOrWhiteSpace(story.StoryTagged)) return;

                var (count, average) = _database.GetStoryEvaluationStats(storyId);
                if (count < MinEvaluations) return;
                if (average <= MinAverageScore) return;

                var formatRunId = $"format_story_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";

                _dispatcher.Enqueue(
                    "TransformStoryRawToTagged",
                    async ctx =>
                    {
                        try
                        {
                            var cmd = new TransformStoryRawToTaggedCommand(
                                storyId,
                                _database,
                                _kernelFactory,
                                storiesService: null,
                                logger: _customLogger,
                                commandDispatcher: _dispatcher);

                            return await cmd.ExecuteAsync(ctx.CancellationToken, ctx.RunId);
                        }
                        catch (Exception ex)
                        {
                            return new CommandResult(false, ex.Message);
                        }
                    },
                    runId: formatRunId,
                    threadScope: "story/format",
                    metadata: new Dictionary<string, string>
                    {
                        ["storyId"] = storyId.ToString(),
                        ["operation"] = "format_story_auto",
                        ["trigger"] = "evaluate_story_completed",
                        ["evaluationCount"] = count.ToString(),
                        ["evaluationAvg"] = average.ToString("F2")
                    },
                    priority: 2);

                _logger?.LogInformation(
                    "Auto-enqueued formatter for story {StoryId} after {Operation} (count={Count}, avg={Avg:F2})",
                    storyId,
                    operationName,
                    count,
                    average);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Auto-format follow-up failed for runId={RunId}", runId);
                await Task.CompletedTask;
            }
        }

        private static bool IsStoryEvaluationOperation(string operationName)
        {
            if (string.IsNullOrWhiteSpace(operationName)) return false;

            // UI enqueues "story_evaluate_story". Some internal flows enqueue "evaluate_story".
            return operationName.Contains("evaluate_story", StringComparison.OrdinalIgnoreCase);
        }
    }
}
