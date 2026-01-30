using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TinyGenerator.Services
{
    public sealed class AutomaticOperationsService : BackgroundService
    {
        private static readonly HashSet<string> IgnoredOperations = new(StringComparer.OrdinalIgnoreCase)
        {
            "memory_embedding_worker",
            "BatchSummarizeStories",
            "SummarizeStory"
        };

        private readonly ICommandDispatcher _dispatcher;
        private readonly StoriesService _stories;
        private readonly DatabaseService _database;
        private readonly IOptionsMonitor<AutomaticOperationsOptions> _optionsMonitor;
        private readonly ILogger<AutomaticOperationsService> _logger;
        private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(10);
        private DateTime _lastActivityUtc;
        private DateTime _lastAttemptUtc;
        private int _lastTaskIndex = -1;

        public AutomaticOperationsService(
            ICommandDispatcher dispatcher,
            StoriesService stories,
            DatabaseService database,
            IOptionsMonitor<AutomaticOperationsOptions> optionsMonitor,
            ILogger<AutomaticOperationsService> logger)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _stories = stories ?? throw new ArgumentNullException(nameof(stories));
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _lastActivityUtc = DateTime.UtcNow;
            _lastAttemptUtc = DateTime.UtcNow;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_pollInterval, stoppingToken);

                    var opts = _optionsMonitor.CurrentValue ?? new AutomaticOperationsOptions();
                    if (!opts.Enabled)
                    {
                        continue;
                    }

                    if (HasActiveNonIgnoredCommands())
                    {
                        _lastActivityUtc = DateTime.UtcNow;
                        continue;
                    }

                    var idleThreshold = TimeSpan.FromSeconds(Math.Max(5, opts.IdleSeconds));
                    var nowUtc = DateTime.UtcNow;

                    if (nowUtc - _lastActivityUtc < idleThreshold)
                    {
                        continue;
                    }

                    if (nowUtc - _lastAttemptUtc < idleThreshold)
                    {
                        continue;
                    }

                    var tasks = BuildTasks(opts);
                    var available = tasks.Where(t => t.HasCandidate()).ToList();
                    if (available.Count == 0)
                    {
                        _lastAttemptUtc = nowUtc;
                        continue;
                    }

                    var chosen = PickNextTask(available);
                    if (chosen == null)
                    {
                        _lastAttemptUtc = nowUtc;
                        continue;
                    }

                    _lastAttemptUtc = nowUtc;
                    var enqueued = chosen.TryEnqueue();
                    if (enqueued)
                    {
                        _lastActivityUtc = DateTime.UtcNow;
                        _logger.LogInformation("Idle auto-op queued: {TaskName}", chosen.Name);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Idle auto-ops loop failed");
                }
            }
        }

        private bool HasActiveNonIgnoredCommands()
        {
            var commands = _dispatcher.GetActiveCommands();
            foreach (var cmd in commands)
            {
                if (!string.Equals(cmd.Status, "queued", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(cmd.Status, "running", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsIgnoredOperation(cmd))
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        private static bool IsIgnoredOperation(CommandSnapshot cmd)
        {
            if (IgnoredOperations.Contains(cmd.OperationName))
            {
                return true;
            }

            var op = cmd.Metadata != null && cmd.Metadata.TryGetValue("operation", out var metaOp)
                ? metaOp ?? string.Empty
                : string.Empty;

            return IgnoredOperations.Contains(op);
        }

        private List<IdleTask> BuildTasks(AutomaticOperationsOptions opts)
        {
            var list = new List<IdleTask>();

            if (opts.ReviseAndEvaluate.Enabled)
            {
                list.Add(new IdleTask(
                    name: "revise_and_evaluate",
                    priority: Math.Max(1, opts.ReviseAndEvaluate.Priority),
                    hasCandidate: () => (_database.GetFirstStoryIdByStatusCode("inserted") ??
                                        _database.GetFirstStoryIdByStatusCode("inserito")).HasValue,
                    tryEnqueue: () =>
                    {
                        var storyId = _database.GetFirstStoryIdByStatusCode("inserted")
                            ?? _database.GetFirstStoryIdByStatusCode("inserito");
                        if (!storyId.HasValue) return false;
                        var runId = _stories.EnqueueReviseStoryCommand(storyId.Value, trigger: "idle_auto", priority: Math.Max(1, opts.ReviseAndEvaluate.Priority), force: false);
                        return !string.IsNullOrWhiteSpace(runId);
                    }));
            }

            if (opts.EvaluateRevised.Enabled)
            {
                list.Add(new IdleTask(
                    name: "evaluate_revised",
                    priority: Math.Max(1, opts.EvaluateRevised.Priority),
                    hasCandidate: () => _database.GetFirstRevisedStoryIdNeedingEvaluations(minEvaluations: 2).HasValue,
                    tryEnqueue: () =>
                    {
                        var storyId = _database.GetFirstRevisedStoryIdNeedingEvaluations(minEvaluations: 2);
                        if (!storyId.HasValue) return false;
                        var count = _stories.EnqueueStoryEvaluations(storyId.Value, trigger: "idle_auto_revised", priority: Math.Max(1, opts.EvaluateRevised.Priority), maxEvaluators: 2);
                        return count > 0;
                    }));
            }

            if (opts.AutoDeleteLowRated.Enabled)
            {
                list.Add(new IdleTask(
                    name: "auto_delete_low_rated",
                    priority: Math.Max(1, opts.AutoDeleteLowRated.Priority),
                    hasCandidate: () => _database.GetFirstStoryIdBelowEvaluationThreshold(
                        opts.AutoDeleteLowRated.MinAverageScore,
                        Math.Max(1, opts.AutoDeleteLowRated.MinEvaluations)).HasValue,
                    tryEnqueue: () =>
                    {
                        var storyId = _database.GetFirstStoryIdBelowEvaluationThreshold(
                            opts.AutoDeleteLowRated.MinAverageScore,
                            Math.Max(1, opts.AutoDeleteLowRated.MinEvaluations));
                        if (!storyId.HasValue) return false;
                        var runId = _stories.EnqueueDeleteStoryCommand(storyId.Value, trigger: "idle_auto_delete", priority: Math.Max(1, opts.AutoDeleteLowRated.Priority));
                        return !string.IsNullOrWhiteSpace(runId);
                    }));
            }

            if (opts.UpdateModelStats.Enabled)
            {
                list.Add(new IdleTask(
                    name: "update_model_stats",
                    priority: Math.Max(1, opts.UpdateModelStats.Priority),
                    hasCandidate: () => _database.HasPendingModelResponseLogs(),
                    tryEnqueue: () =>
                    {
                        if (IsOperationQueued("update_model_stats")) return false;
                        var runId = $"update_model_stats_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                        _dispatcher.Enqueue(
                            "update_model_stats",
                            ctx =>
                            {
                                var updated = _database.UpdateModelStatsFromUnexaminedLogs();
                                var msg = updated == 0 ? "No model stats updates" : $"Updated model stats: {updated}";
                                return Task.FromResult(new CommandResult(true, msg));
                            },
                            runId: runId,
                            threadScope: "system/model_stats",
                            metadata: new Dictionary<string, string>
                            {
                                ["operation"] = "update_model_stats",
                                ["trigger"] = "idle_auto"
                            },
                            priority: Math.Max(1, opts.UpdateModelStats.Priority));
                        return true;
                    }));
            }

            return list.OrderBy(t => t.Priority).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private IdleTask? PickNextTask(List<IdleTask> available)
        {
            if (available.Count == 0) return null;

            if (_lastTaskIndex < 0 || _lastTaskIndex >= available.Count)
            {
                _lastTaskIndex = 0;
                return available[0];
            }

            var nextIndex = (_lastTaskIndex + 1) % available.Count;
            _lastTaskIndex = nextIndex;
            return available[nextIndex];
        }

        private bool IsOperationQueued(string operationName)
        {
            try
            {
                return _dispatcher.GetActiveCommands().Any(s =>
                    string.Equals(s.OperationName, operationName, StringComparison.OrdinalIgnoreCase) ||
                    (s.Metadata != null &&
                     s.Metadata.TryGetValue("operation", out var op) &&
                     string.Equals(op, operationName, StringComparison.OrdinalIgnoreCase)));
            }
            catch
            {
                return false;
            }
        }

        private sealed class IdleTask
        {
            public string Name { get; }
            public int Priority { get; }
            public Func<bool> HasCandidate { get; }
            public Func<bool> TryEnqueue { get; }

            public IdleTask(string name, int priority, Func<bool> hasCandidate, Func<bool> tryEnqueue)
            {
                Name = name;
                Priority = priority;
                HasCandidate = hasCandidate;
                TryEnqueue = tryEnqueue;
            }
        }
    }
}
