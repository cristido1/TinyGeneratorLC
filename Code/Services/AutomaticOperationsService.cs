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
            "SummarizeStory",
            "auto_idle_attempt"
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
        private bool _idleAttempted;
        private volatile bool _enabled;
        private readonly IDisposable? _optionsSubscription;

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
            _enabled = _optionsMonitor.CurrentValue?.Enabled ?? false;
            _optionsSubscription = _optionsMonitor.OnChange(OnOptionsChanged);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _lastActivityUtc = DateTime.UtcNow;
            _lastAttemptUtc = DateTime.UtcNow;
            _idleAttempted = false;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_pollInterval, stoppingToken);

                    var opts = _optionsMonitor.CurrentValue ?? new AutomaticOperationsOptions();
                    if (!_enabled)
                    {
                        continue;
                    }

                    if (HasAnyActiveCommands())
                    {
                        _lastActivityUtc = DateTime.UtcNow;
                        _idleAttempted = false;
                        continue;
                    }

                    if (HasActiveNonIgnoredCommands())
                    {
                        _lastActivityUtc = DateTime.UtcNow;
                        _idleAttempted = false;
                        continue;
                    }

                    var idleThreshold = TimeSpan.FromSeconds(Math.Max(5, opts.IdleSeconds));
                    var nowUtc = DateTime.UtcNow;

                    if (nowUtc - _lastActivityUtc < idleThreshold)
                    {
                        continue;
                    }

                    if (_idleAttempted)
                    {
                        continue;
                    }

                    var tasks = BuildTasks(opts);
                    var probes = tasks.Select(t => (Task: t, Probe: t.HasCandidate())).ToList();
                    var available = probes.Where(p => p.Probe.Ok).Select(p => p.Task).ToList();
                    if (available.Count == 0)
                    {
                        var reasons = probes
                            .Select(p => $"{p.Task.Name}: {(string.IsNullOrWhiteSpace(p.Probe.Reason) ? "nessun candidato" : p.Probe.Reason)}")
                            .ToList();
                        var message = reasons.Count == 0
                            ? "Nessun candidato disponibile."
                            : $"Nessun candidato disponibile. Verificati: {string.Join(" | ", reasons)}";
                        ReportAutoAttempt("auto_idle_no_candidates", success: false, message: message, failureKind: "no_candidates");
                        continue;
                    }

                    var chosen = PickNextTask(available);
                    if (chosen == null)
                    {
                        ReportAutoAttempt("auto_idle_no_task", success: false, message: "Nessun task automatico selezionato.");
                        continue;
                    }

                    _lastAttemptUtc = nowUtc;
                    _idleAttempted = true;
                    var enqueueResult = chosen.TryEnqueue();
                    if (enqueueResult.Ok)
                    {
                        _lastActivityUtc = DateTime.UtcNow;
                        _idleAttempted = false;
                        ReportAutoAttempt(
                            chosen.Name,
                            success: true,
                            message: "Comando automatico accodato.",
                            storyId: enqueueResult.StoryId,
                            failureKind: null,
                            filter: enqueueResult.Filter,
                            candidateCount: enqueueResult.CandidateCount,
                            storyTitle: enqueueResult.StoryTitle);
                        _logger.LogInformation("Idle auto-op queued: {TaskName}", chosen.Name);
                    }
                    else
                    {
                        var reason = string.IsNullOrWhiteSpace(enqueueResult.Reason) ? "enqueue=false" : enqueueResult.Reason;
                        ReportAutoAttempt(
                            chosen.Name,
                            success: false,
                            message: $"Tentativo automatico fallito: {reason}.",
                            storyId: enqueueResult.StoryId,
                            failureKind: "enqueue_failed",
                            filter: enqueueResult.Filter,
                            candidateCount: enqueueResult.CandidateCount,
                            storyTitle: enqueueResult.StoryTitle);
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

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                _optionsSubscription?.Dispose();
            }
            catch
            {
                // best-effort
            }
            return base.StopAsync(cancellationToken);
        }

        private void OnOptionsChanged(AutomaticOperationsOptions? options)
        {
            _enabled = options?.Enabled ?? false;
            _lastActivityUtc = DateTime.UtcNow;
            _lastAttemptUtc = DateTime.UtcNow;
            _idleAttempted = false;
            _logger.LogInformation("AutomaticOperationsService {State} via config reload", _enabled ? "enabled" : "disabled");
        }

        private void ReportAutoAttempt(
            string taskName,
            bool success,
            string message,
            long? storyId = null,
            string? failureKind = null,
            string? filter = null,
            int? candidateCount = null,
            string? storyTitle = null)
        {
            try
            {
                if (HasAnyActiveCommands())
                {
                    return;
                }
                var runId = $"auto_idle_attempt_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                _dispatcher.Enqueue(
                    "auto_idle_attempt",
                    _ => Task.FromResult(new CommandResult(success, message)),
                    runId: runId,
                    threadScope: "system/auto_idle",
                    metadata: new Dictionary<string, string>
                    {
                        ["operation"] = "auto_idle_attempt",
                        ["taskName"] = taskName,
                        ["trigger"] = "auto_idle",
                        ["failureKind"] = failureKind ?? string.Empty,
                        ["storyId"] = storyId?.ToString() ?? string.Empty,
                        ["detail"] = message ?? string.Empty,
                        ["filter"] = filter ?? string.Empty,
                        ["candidateCount"] = candidateCount?.ToString() ?? string.Empty,
                        ["storyTitle"] = storyTitle ?? string.Empty
                    },
                    priority: 10);
            }
            catch
            {
                // best-effort
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
                    hasCandidate: () =>
                    {
                        const string filter = "status in (inserted)";
                        var ok = TryGetBestStoryByStatus(new[] { "inserted", "inserito" }, out var storyId, out var reason);
                        var count = CountStoriesByStatus(new[] { "inserted", "inserito" });
                        var title = storyId > 0 ? _database.GetStoryById(storyId)?.Title : null;
                        return ok
                            ? new IdleTaskResult(true, storyId, null, filter, count, title)
                            : new IdleTaskResult(false, null, reason ?? "nessuna storia in stato inserted", filter, count);
                    },
                    tryEnqueue: () =>
                    {
                        const string filter = "status in (inserted)";
                        var count = CountStoriesByStatus(new[] { "inserted", "inserito" });
                        if (!TryGetBestStoryByStatus(new[] { "inserted", "inserito" }, out var storyId, out var reason))
                        {
                            return new IdleTaskResult(false, null, reason ?? "nessuna storia in stato inserted", filter, count);
                        }
                        var runId = _stories.EnqueueReviseStoryCommand(storyId, trigger: "idle_auto", priority: Math.Max(1, opts.ReviseAndEvaluate.Priority), force: false);
                        var title = _database.GetStoryById(storyId)?.Title;
                        return !string.IsNullOrWhiteSpace(runId)
                            ? new IdleTaskResult(true, storyId, null, filter, count, title)
                            : new IdleTaskResult(false, storyId, "enqueue fallito", filter, count, title);
                    }));
            }

            if (opts.EvaluateRevised.Enabled)
            {
                list.Add(new IdleTask(
                    name: "evaluate_revised",
                    priority: Math.Max(1, opts.EvaluateRevised.Priority),
                    hasCandidate: () =>
                    {
                        const string filter = "status=revised AND evaluations<2";
                        var ok = TryGetBestRevisedStoryNeedingEvaluations(minEvaluations: 2, out var storyId, out var reason);
                        var count = CountRevisedStoriesNeedingEvaluations(2);
                        var title = storyId > 0 ? _database.GetStoryById(storyId)?.Title : null;
                        return ok
                            ? new IdleTaskResult(true, storyId, null, filter, count, title)
                            : new IdleTaskResult(false, null, reason ?? "nessuna storia revised da valutare", filter, count);
                    },
                    tryEnqueue: () =>
                    {
                        const string filter = "status=revised AND evaluations<2";
                        var candidateCount = CountRevisedStoriesNeedingEvaluations(2);
                        if (!TryGetBestRevisedStoryNeedingEvaluations(minEvaluations: 2, out var storyId, out var reason))
                        {
                            return new IdleTaskResult(false, null, reason ?? "nessuna storia revised da valutare", filter, candidateCount);
                        }
                        var enqueuedCount = _stories.EnqueueStoryEvaluations(storyId, trigger: "idle_auto_revised", priority: Math.Max(1, opts.EvaluateRevised.Priority), maxEvaluators: 2);
                        var title = _database.GetStoryById(storyId)?.Title;
                        return enqueuedCount > 0
                            ? new IdleTaskResult(true, storyId, null, filter, candidateCount, title)
                            : new IdleTaskResult(false, storyId, "enqueue valutazioni fallito", filter, candidateCount, title);
                    }));
            }

            if (opts.AutoDeleteLowRated.Enabled)
            {
                list.Add(new IdleTask(
                    name: "auto_delete_low_rated",
                    priority: Math.Max(1, opts.AutoDeleteLowRated.Priority),
                    hasCandidate: () =>
                    {
                        var minEvals = Math.Max(1, opts.AutoDeleteLowRated.MinEvaluations);
                        var minAvg = opts.AutoDeleteLowRated.MinAverageScore;
                        var filter = $"status=evaluated AND evaluations>={minEvals} AND avg<{minAvg}";
                        var ok = TryGetBestLowRatedStory(minAvg, minEvals, out var storyId, out var reason);
                        var count = CountLowRatedStories(minAvg, minEvals);
                        var title = storyId > 0 ? _database.GetStoryById(storyId)?.Title : null;
                        return ok
                            ? new IdleTaskResult(true, storyId, null, filter, count, title)
                            : new IdleTaskResult(false, null, reason ?? "nessuna storia sotto soglia", filter, count);
                    },
                    tryEnqueue: () =>
                    {
                        var minEvals = Math.Max(1, opts.AutoDeleteLowRated.MinEvaluations);
                        var minAvg = opts.AutoDeleteLowRated.MinAverageScore;
                        var filter = $"status=evaluated AND evaluations>={minEvals} AND avg<{minAvg}";
                        var count = CountLowRatedStories(minAvg, minEvals);
                        if (!TryGetBestLowRatedStory(minAvg, minEvals, out var storyId, out var reason))
                        {
                            return new IdleTaskResult(false, null, reason ?? "nessuna storia sotto soglia", filter, count);
                        }
                        var runId = _stories.EnqueueDeleteStoryCommand(storyId, trigger: "idle_auto_delete", priority: Math.Max(1, opts.AutoDeleteLowRated.Priority));
                        var title = _database.GetStoryById(storyId)?.Title;
                        return !string.IsNullOrWhiteSpace(runId)
                            ? new IdleTaskResult(true, storyId, null, filter, count, title)
                            : new IdleTaskResult(false, storyId, "enqueue delete fallito", filter, count, title);
                    }));
            }

            if (opts.UpdateModelStats.Enabled)
            {
                list.Add(new IdleTask(
                    name: "update_model_stats",
                    priority: Math.Max(1, opts.UpdateModelStats.Priority),
                    hasCandidate: () =>
                    {
                        const string filter = "model logs examined=false AND category in (ModelResponse, ModelCompletion)";
                        var count = _database.GetPendingModelResponseLogsCount();
                        return count > 0
                            ? new IdleTaskResult(true, null, null, filter, count)
                            : new IdleTaskResult(false, null, "nessun log modello da analizzare", filter, count);
                    },
                    tryEnqueue: () =>
                    {
                        const string filter = "model logs examined=false AND category in (ModelResponse, ModelCompletion)";
                        var count = _database.GetPendingModelResponseLogsCount();
                        if (IsOperationQueued("update_model_stats"))
                        {
                            return new IdleTaskResult(false, null, "update_model_stats già in coda", filter, count);
                        }
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
                        return new IdleTaskResult(true, null, null, filter, count);
                    }));
            }

            if (opts.AutoCompleteAudioPipeline.Enabled)
            {
                list.Add(new IdleTask(
                    name: "auto_complete_audio_pipeline",
                    priority: Math.Max(1, opts.AutoCompleteAudioPipeline.Priority),
                    hasCandidate: () =>
                    {
                        var minAvg = opts.AutoCompleteAudioPipeline.MinAverageScore;
                        var filter = $"status>=evaluated AND AutoTtsFailed=false AND avg>={minAvg}";
                        var ok = TryGetTopStoryForAutoComplete(minAvg, out var storyId, out var reason);
                        var count = CountAutoCompleteCandidates(minAvg);
                        var title = storyId > 0 ? _database.GetStoryById(storyId)?.Title : null;
                        return ok
                            ? new IdleTaskResult(true, storyId, null, filter, count, title)
                            : new IdleTaskResult(false, null, reason ?? "nessuna storia candidata", filter, count);
                    },
                    tryEnqueue: () =>
                    {
                        var minAvg = opts.AutoCompleteAudioPipeline.MinAverageScore;
                        var filter = $"status>=evaluated AND AutoTtsFailed=false AND avg>={minAvg}";
                        var count = CountAutoCompleteCandidates(minAvg);
                        if (!TryGetTopStoryForAutoComplete(minAvg, out var storyId, out var reason))
                        {
                            return new IdleTaskResult(false, null, reason ?? "nessuna storia candidata", filter, count);
                        }
                        var title = _database.GetStoryById(storyId)?.Title;
                        var runId = _stories.EnqueueAllNextStatusCommand(
                            storyId,
                            trigger: "auto_complete_audio_pipeline",
                            priority: Math.Max(1, opts.AutoCompleteAudioPipeline.Priority),
                            ignoreActiveChain: true);
                        return !string.IsNullOrWhiteSpace(runId)
                            ? new IdleTaskResult(true, storyId, null, filter, count, title)
                            : new IdleTaskResult(false, storyId, "enqueue catena stati fallito", filter, count, title);
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

        private bool HasAnyActiveCommands()
        {
            try
            {
                return _dispatcher.GetActiveCommands().Any(s =>
                    string.Equals(s.Status, "queued", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.Status, "running", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private bool TryGetBestStoryByStatus(IEnumerable<string> statusCodes, out long storyId, out string? reason)
        {
            storyId = 0;
            reason = null;

            var codes = statusCodes?.Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim().ToLowerInvariant())
                .ToList() ?? new List<string>();
            if (codes.Count == 0)
            {
                reason = "nessun codice stato valido";
                return false;
            }

            var statuses = _database.ListAllStoryStatuses();
            var statusIds = statuses
                .Where(s => s.Code != null && codes.Contains(s.Code.ToLowerInvariant()))
                .Select(s => s.Id)
                .ToHashSet();

            if (statusIds.Count == 0)
            {
                reason = "stato non trovato";
                return false;
            }

            var stories = _database.GetAllStories()
                .Where(s => s.StatusId.HasValue && statusIds.Contains(s.StatusId.Value) && !s.Deleted)
                .ToList();

            if (stories.Count == 0)
            {
                reason = "nessuna storia nello stato richiesto";
                return false;
            }

            var best = stories
                .Select(s => new
                {
                    s.Id,
                    Score = NormalizeEvaluationScore(GetEvaluationAverage(s.Id))
                })
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.Id)
                .First();

            storyId = best.Id;
            return true;
        }

        private double GetEvaluationAverage(long storyId)
        {
            var evals = _database.GetStoryEvaluations(storyId);
            if (evals == null || evals.Count == 0)
            {
                return 0.0;
            }
            return evals.Average(e => e.TotalScore);
        }

        private static double NormalizeEvaluationScore(double avgScore)
        {
            return DatabaseService.NormalizeEvaluationScoreTo100(avgScore);
        }

        private bool TryGetBestRevisedStoryNeedingEvaluations(int minEvaluations, out long storyId, out string? reason)
        {
            storyId = 0;
            reason = null;
            minEvaluations = Math.Max(1, minEvaluations);

            var statuses = _database.ListAllStoryStatuses();
            var revisedStatus = statuses.FirstOrDefault(s => string.Equals(s.Code, "revised", StringComparison.OrdinalIgnoreCase));
            if (revisedStatus == null)
            {
                reason = "stato revised non trovato";
                return false;
            }

            var stories = _database.GetAllStories()
                .Where(s => s.StatusId == revisedStatus.Id && !s.Deleted)
                .ToList();

            if (stories.Count == 0)
            {
                reason = "nessuna storia revised da valutare";
                return false;
            }

            var eligible = new List<(long StoryId, double Score)>();
            foreach (var story in stories)
            {
                var evals = _database.GetStoryEvaluations(story.Id);
                if (evals.Count >= minEvaluations)
                {
                    continue;
                }
                var avg = evals.Count > 0 ? evals.Average(e => e.TotalScore) : 0.0;
                var normalized = DatabaseService.NormalizeEvaluationScoreTo100(avg);
                eligible.Add((story.Id, normalized));
            }

            if (eligible.Count == 0)
            {
                reason = "nessuna storia revised da valutare";
                return false;
            }

            var best = eligible.OrderByDescending(e => e.Score).ThenBy(e => e.StoryId).First();
            storyId = best.StoryId;
            return true;
        }

        private bool TryGetBestLowRatedStory(double minAverageScore, int minEvaluations, out long storyId, out string? reason)
        {
            storyId = 0;
            reason = null;
            minEvaluations = Math.Max(1, minEvaluations);

            var statuses = _database.ListAllStoryStatuses();
            var evaluatedStatus = statuses.FirstOrDefault(s => string.Equals(s.Code, "evaluated", StringComparison.OrdinalIgnoreCase));
            if (evaluatedStatus == null)
            {
                reason = "stato evaluated non trovato";
                return false;
            }

            var stories = _database.GetAllStories()
                .Where(s => s.StatusId == evaluatedStatus.Id && !s.Deleted)
                .ToList();

            if (stories.Count == 0)
            {
                reason = "nessuna storia in stato evaluated";
                return false;
            }

            var eligible = new List<(long StoryId, double Score)>();
            foreach (var story in stories)
            {
                var evals = _database.GetStoryEvaluations(story.Id);
                if (evals.Count < minEvaluations)
                {
                    continue;
                }
                var avg = evals.Average(e => e.TotalScore);
                var normalized = DatabaseService.NormalizeEvaluationScoreTo100(avg);
                if (normalized >= minAverageScore)
                {
                    continue;
                }
                eligible.Add((story.Id, normalized));
            }

            if (eligible.Count == 0)
            {
                reason = "nessuna storia sotto soglia";
                return false;
            }

            var best = eligible.OrderByDescending(e => e.Score).ThenBy(e => e.StoryId).First();
            storyId = best.StoryId;
            return true;
        }

        private bool TryGetTopStoryForAutoComplete(double minAverageScore, out long storyId, out string? reason)
        {
            storyId = 0;
            reason = null;
            try
            {
                var candidates = _database.GetStoriesByEvaluation();
                if (candidates == null || candidates.Count == 0)
                {
                    reason = "nessuna storia con punteggio disponibile";
                    return false;
                }

                var statuses = _database.ListAllStoryStatuses();
                var evaluated = statuses?.FirstOrDefault(s => string.Equals(s.Code, "evaluated", StringComparison.OrdinalIgnoreCase));
                var evaluatedStep = evaluated?.Step ?? -1;
                var eligible = new List<(long StoryId, double Score)>();
                string? lastReason = null;

                foreach (var candidate in candidates)
                {
                    var story = _database.GetStoryById(candidate.Id);
                    if (story == null || story.Deleted)
                    {
                        lastReason = "storia non trovata o eliminata";
                        continue;
                    }
                    if (story.AutoTtsFailed)
                    {
                        lastReason = "storia esclusa: AutoTtsFailed=true";
                        continue;
                    }
                    if (evaluatedStep >= 0)
                    {
                        if (!story.StatusId.HasValue)
                        {
                            lastReason = "storia senza stato";
                            continue;
                        }
                        var status = statuses?.FirstOrDefault(s => s.Id == story.StatusId.Value);
                        if (status == null || status.Step < evaluatedStep)
                        {
                            lastReason = "storia non evaluated o successiva";
                            continue;
                        }
                    }
                    var normalizedScore = DatabaseService.NormalizeEvaluationScoreTo100(candidate.AvgScore);
                    if (normalizedScore < minAverageScore)
                    {
                        lastReason = $"punteggio {normalizedScore:F2} < soglia {minAverageScore:F2}";
                        continue;
                    }
                    var next = _stories.GetNextStatusForStory(story, statuses);
                    if (next == null)
                    {
                        lastReason = "storia gia completa (nessuno stato successivo)";
                        continue;
                    }
                    eligible.Add((candidate.Id, normalizedScore));
                }

                if (eligible.Count == 0)
                {
                    reason = lastReason ?? "nessuna storia valida per auto-complete";
                    return false;
                }

                var best = eligible.OrderByDescending(e => e.Score).First();
                storyId = best.StoryId;
                return true;
            }
            catch
            {
                reason = "errore durante la selezione della storia";
                return false;
            }
        }

        private int CountStoriesByStatus(IEnumerable<string> statusCodes)
        {
            var codes = statusCodes?.Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim().ToLowerInvariant())
                .ToList() ?? new List<string>();
            if (codes.Count == 0) return 0;

            var statuses = _database.ListAllStoryStatuses();
            var statusIds = statuses
                .Where(s => s.Code != null && codes.Contains(s.Code.ToLowerInvariant()))
                .Select(s => s.Id)
                .ToHashSet();
            if (statusIds.Count == 0) return 0;

            return _database.GetAllStories()
                .Count(s => s.StatusId.HasValue && statusIds.Contains(s.StatusId.Value) && !s.Deleted);
        }

        private int CountRevisedStoriesNeedingEvaluations(int minEvaluations)
        {
            minEvaluations = Math.Max(1, minEvaluations);
            var statuses = _database.ListAllStoryStatuses();
            var revisedStatus = statuses.FirstOrDefault(s => string.Equals(s.Code, "revised", StringComparison.OrdinalIgnoreCase));
            if (revisedStatus == null) return 0;

            var stories = _database.GetAllStories()
                .Where(s => s.StatusId == revisedStatus.Id && !s.Deleted)
                .ToList();

            var count = 0;
            foreach (var story in stories)
            {
                var evals = _database.GetStoryEvaluations(story.Id);
                if (evals.Count < minEvaluations)
                {
                    count++;
                }
            }
            return count;
        }

        private int CountLowRatedStories(double minAverageScore, int minEvaluations)
        {
            minEvaluations = Math.Max(1, minEvaluations);
            var statuses = _database.ListAllStoryStatuses();
            var evaluatedStatus = statuses.FirstOrDefault(s => string.Equals(s.Code, "evaluated", StringComparison.OrdinalIgnoreCase));
            if (evaluatedStatus == null) return 0;

            var stories = _database.GetAllStories()
                .Where(s => s.StatusId == evaluatedStatus.Id && !s.Deleted)
                .ToList();

            var count = 0;
            foreach (var story in stories)
            {
                var evals = _database.GetStoryEvaluations(story.Id);
                if (evals.Count < minEvaluations)
                {
                    continue;
                }
                var avg = evals.Average(e => e.TotalScore);
                var normalized = DatabaseService.NormalizeEvaluationScoreTo100(avg);
                if (normalized < minAverageScore)
                {
                    count++;
                }
            }
            return count;
        }

        private int CountAutoCompleteCandidates(double minAverageScore)
        {
            try
            {
                var candidates = _database.GetStoriesByEvaluation();
                if (candidates == null || candidates.Count == 0) return 0;

                var statuses = _database.ListAllStoryStatuses();
                var evaluated = statuses?.FirstOrDefault(s => string.Equals(s.Code, "evaluated", StringComparison.OrdinalIgnoreCase));
                var evaluatedStep = evaluated?.Step ?? -1;
                var count = 0;

                foreach (var candidate in candidates)
                {
                    var story = _database.GetStoryById(candidate.Id);
                    if (story == null || story.Deleted) continue;
                    if (story.AutoTtsFailed) continue;
                    if (evaluatedStep >= 0)
                    {
                        if (!story.StatusId.HasValue) continue;
                        var status = statuses?.FirstOrDefault(s => s.Id == story.StatusId.Value);
                        if (status == null || status.Step < evaluatedStep) continue;
                    }
                    var normalizedScore = DatabaseService.NormalizeEvaluationScoreTo100(candidate.AvgScore);
                    if (normalizedScore < minAverageScore) continue;
                    var next = _stories.GetNextStatusForStory(story, statuses);
                    if (next == null) continue;
                    count++;
                }

                return count;
            }
            catch
            {
                return 0;
            }
        }

        private sealed record IdleTaskResult(
            bool Ok,
            long? StoryId,
            string? Reason,
            string? Filter = null,
            int? CandidateCount = null,
            string? StoryTitle = null);

        private sealed class IdleTask
        {
            public string Name { get; }
            public int Priority { get; }
            public Func<IdleTaskResult> HasCandidate { get; }
            public Func<IdleTaskResult> TryEnqueue { get; }

            public IdleTask(string name, int priority, Func<IdleTaskResult> hasCandidate, Func<IdleTaskResult> tryEnqueue)
            {
                Name = name;
                Priority = priority;
                HasCandidate = hasCandidate;
                TryEnqueue = tryEnqueue;
            }
        }
    }
}
