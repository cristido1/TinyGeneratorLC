using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TinyGenerator.Models;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services
{
    public sealed class AutomaticOperationsService : BackgroundService
    {
        private const int MaxAutoEpisodeNumber = 6;
        private static readonly HashSet<string> IgnoredOperations = new(StringComparer.OrdinalIgnoreCase)
        {
            "memory_embedding_worker",
            "BatchSummarizeStories",
            "SummarizeStory",
            "auto_idle_attempt",
            "update_model_stats_from_logs",
            "update_model_stats",
            "always_on_story_summaries",
            "analyze_pending_log_errors",
            "log_analyzer"
        };

        private readonly ICommandDispatcher _dispatcher;
        private readonly StoriesService _stories;
        private readonly DatabaseService _database;
        private readonly IOptionsMonitor<AutomaticOperationsOptions> _optionsMonitor;
        private readonly ILogger<AutomaticOperationsService> _logger;
        private readonly ILangChainKernelFactory _kernelFactory;
        private readonly ICustomLogger _customLogger;
        private readonly CommandTuningOptions _tuning;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TextValidationService _textValidationService;
        private readonly IOptionsMonitor<NarrativeRuntimeEngineOptions> _nreOptionsMonitor;
        private readonly ICallCenter _callCenter;
        private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(10);
        private DateTime _lastActivityUtc;
        private DateTime _lastAttemptUtc;
        private DateTime _lastAutoSeriesEpisodeAttemptUtc;
        private DateTime _lastAutoNreStoryAttemptUtc;
        private int _lastAutoSeriesId;
        private int _lastTaskIndex = -1;
        private bool _idleAttempted;
        private readonly HashSet<long> _autoCompleteDeferredStoryIds = new();
        private volatile bool _enabled;
        private readonly IDisposable? _optionsSubscription;

        public AutomaticOperationsService(
            ICommandDispatcher dispatcher,
            StoriesService stories,
            DatabaseService database,
            IOptionsMonitor<AutomaticOperationsOptions> optionsMonitor,
            ILangChainKernelFactory kernelFactory,
            ICustomLogger customLogger,
            IOptions<CommandTuningOptions> tuningOptions,
            IServiceScopeFactory scopeFactory,
            TextValidationService textValidationService,
            IOptionsMonitor<NarrativeRuntimeEngineOptions> nreOptionsMonitor,
            ICallCenter callCenter,
            ILogger<AutomaticOperationsService> logger)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _stories = stories ?? throw new ArgumentNullException(nameof(stories));
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
            _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
            _customLogger = customLogger ?? throw new ArgumentNullException(nameof(customLogger));
            _tuning = tuningOptions?.Value ?? new CommandTuningOptions();
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _textValidationService = textValidationService ?? throw new ArgumentNullException(nameof(textValidationService));
            _nreOptionsMonitor = nreOptionsMonitor ?? throw new ArgumentNullException(nameof(nreOptionsMonitor));
            _callCenter = callCenter ?? throw new ArgumentNullException(nameof(callCenter));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _enabled = _optionsMonitor.CurrentValue?.Enabled ?? false;
            _optionsSubscription = _optionsMonitor.OnChange(OnOptionsChanged);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _lastActivityUtc = DateTime.UtcNow;
            _lastAttemptUtc = DateTime.UtcNow;
            _lastAutoSeriesEpisodeAttemptUtc = DateTime.MinValue;
            _lastAutoNreStoryAttemptUtc = DateTime.MinValue;
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

                    DrainAutoCompleteDeferredFailures();
                    TryRunSelectedAutoAdvancement(opts);

                    if (TryRunAutoCompleteBacklogImmediate(opts))
                    {
                        _lastActivityUtc = DateTime.UtcNow;
                        _idleAttempted = false;
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
                        _logger.LogDebug("Idle auto-op: nessun candidato disponibile.");
                        continue;
                    }

                    var chosen = PickNextTask(available);
                    if (chosen == null)
                    {
                        _logger.LogDebug("Idle auto-op: nessun task selezionato.");
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
                        if (IsBenignAlreadyQueuedReason(reason))
                        {
                            continue;
                        }
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
            _lastAutoSeriesEpisodeAttemptUtc = DateTime.MinValue;
            _lastAutoNreStoryAttemptUtc = DateTime.MinValue;
            _idleAttempted = false;
            _autoCompleteDeferredStoryIds.Clear();
            _logger.LogInformation("AutomaticOperationsService {State} via config reload", _enabled ? "enabled" : "disabled");
        }

        private void TryRunSelectedAutoAdvancement(AutomaticOperationsOptions opts)
        {
            var mode = NormalizeAutoAdvancementMode(opts.AutoAdvancementMode);
            if (mode == "nre")
            {
                TryRunAutoNreStoryGeneration(opts, useManualMethod: false);
                return;
            }

            if (mode == "nre_manual")
            {
                TryRunAutoNreStoryGeneration(opts, useManualMethod: true);
                return;
            }

            TryRunAutoStateDrivenSeriesEpisode(opts);
        }

        private void TryRunAutoStateDrivenSeriesEpisode(AutomaticOperationsOptions opts)
        {
            try
            {
                var auto = opts.AutoStateDrivenSeriesEpisode;
                if (auto == null || !auto.Enabled)
                {
                    return;
                }

                var interval = TimeSpan.FromMinutes(Math.Max(1, auto.IntervalMinutes));
                var nowUtc = DateTime.UtcNow;
                if (nowUtc - _lastAutoSeriesEpisodeAttemptUtc < interval)
                {
                    return;
                }

                if (HasPendingStoryAutomationBacklog(opts))
                {
                    return;
                }

                if (IsOperationQueued("StateDrivenEpisodeAuto"))
                {
                    _lastAutoSeriesEpisodeAttemptUtc = nowUtc;
                    return;
                }

                TryEnqueueAutoStateDrivenSeriesEpisode(auto);
                _lastAutoSeriesEpisodeAttemptUtc = nowUtc;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto state-driven episode check failed inside AutomaticOperationsService");
            }
        }

        private void TryRunAutoNreStoryGeneration(AutomaticOperationsOptions opts, bool useManualMethod)
        {
            try
            {
                var auto = opts.AutoNreStoryGeneration;
                if (auto == null || !auto.Enabled)
                {
                    return;
                }

                var interval = TimeSpan.FromMinutes(Math.Max(1, auto.IntervalMinutes));
                var nowUtc = DateTime.UtcNow;
                if (nowUtc - _lastAutoNreStoryAttemptUtc < interval)
                {
                    return;
                }

                if (HasPendingStoryAutomationBacklog(opts))
                {
                    return;
                }

                if (IsOperationQueued("AutoNreStoryGeneration") || IsOperationQueued("run_nre"))
                {
                    _lastAutoNreStoryAttemptUtc = nowUtc;
                    return;
                }

                TryEnqueueAutoNreStoryGeneration(auto, useManualMethod);
                _lastAutoNreStoryAttemptUtc = nowUtc;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto NRE story generation check failed inside AutomaticOperationsService");
            }
        }

        private bool HasPendingStoryAutomationBacklog(AutomaticOperationsOptions opts)
        {
            try
            {
                if (HasActiveNonIgnoredCommands())
                {
                    return true;
                }

                if (opts.ReviseAndEvaluate?.Enabled == true &&
                    CountStoriesByStatus(new[] { "inserted", "inserito" }) > 0)
                {
                    return true;
                }

                if (opts.EvaluateRevised?.Enabled == true &&
                    CountRevisedStoriesNeedingEvaluations(2) > 0)
                {
                    return true;
                }

                if (opts.AutoDeleteLowRated?.Enabled == true)
                {
                    var minEvals = Math.Max(1, opts.AutoDeleteLowRated.MinEvaluations);
                    var minAvg = opts.AutoDeleteLowRated.MinAverageScore;
                    if (CountLowRatedStories(minAvg, minEvals) > 0)
                    {
                        return true;
                    }
                }

                if (opts.AutoCompleteAudioPipeline?.Enabled == true)
                {
                    var minAvg = opts.AutoCompleteAudioPipeline.MinAverageScore;
                    if (CountAutoCompleteCandidates(minAvg) > 0)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Backlog check for auto state-driven episode failed");
                return true; // fail-safe: avoid creating new episodes if unsure
            }
        }

        private void DrainAutoCompleteDeferredFailures()
        {
            try
            {
                var failed = _stories.DrainAutoCompleteDeferredFailures();
                if (failed == null || failed.Count == 0)
                {
                    return;
                }

                foreach (var storyId in failed)
                {
                    if (storyId > 0)
                    {
                        _autoCompleteDeferredStoryIds.Add(storyId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unable to drain auto-complete deferred failures");
            }
        }

        private bool TryRunAutoCompleteBacklogImmediate(AutomaticOperationsOptions opts)
        {
            try
            {
                var auto = opts.AutoCompleteAudioPipeline;
                if (auto == null || !auto.Enabled)
                {
                    return false;
                }

                if (HasActiveNonIgnoredCommands())
                {
                    return false;
                }

                var minAvg = auto.MinAverageScore;
                if (!TryGetTopStoryForAutoComplete(minAvg, out var storyId, out _, _autoCompleteDeferredStoryIds))
                {
                    if (_autoCompleteDeferredStoryIds.Count > 0)
                    {
                        _autoCompleteDeferredStoryIds.Clear();
                        return TryRunAutoCompleteBacklogImmediate(opts);
                    }

                    return false;
                }

                var enqueued = _stories.EnqueueFinalMixPipeline(
                    storyId,
                    trigger: "auto_complete_audio_pipeline",
                    priority: Math.Max(1, auto.Priority));

                return enqueued;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Immediate auto-complete audio pipeline attempt failed");
                return false;
            }
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
                if (!IsQueuedOrRunningStatus(cmd.Status))
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
                        var enqueuedCount = _stories.StoryEvaluationsEnqueuer(storyId, trigger: "idle_auto_revised", priority: Math.Max(1, opts.EvaluateRevised.Priority), maxEvaluators: 2);
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
                        var enqueued = _stories.EnqueueFinalMixPipeline(
                            storyId,
                            trigger: "auto_complete_audio_pipeline",
                            priority: Math.Max(1, opts.AutoCompleteAudioPipeline.Priority));
                        return enqueued
                            ? new IdleTaskResult(true, storyId, null, filter, count, title)
                            : new IdleTaskResult(false, storyId, "enqueue pipeline mix+video fallito", filter, count, title);
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
                    (
                        IsQueuedOrRunningStatus(s.Status) &&
                        string.Equals(s.OperationName, operationName, StringComparison.OrdinalIgnoreCase)
                    ) ||
                    (
                        IsQueuedOrRunningStatus(s.Status) &&
                        s.Metadata != null &&
                        s.Metadata.TryGetValue("operation", out var op) &&
                        string.Equals(op, operationName, StringComparison.OrdinalIgnoreCase)
                    ));
            }
            catch
            {
                return false;
            }
        }

        private static bool IsBenignAlreadyQueuedReason(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;
            var text = reason.Trim();
            return text.Contains("in coda", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("already queued", StringComparison.OrdinalIgnoreCase);
        }

        private bool HasAnyActiveCommands()
        {
            try
            {
                return _dispatcher.GetActiveCommands().Any(s => IsQueuedOrRunningStatus(s.Status));
            }
            catch
            {
                return false;
            }
        }

        private static bool IsQueuedOrRunningStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status)) return false;
            return status.Equals("queued", StringComparison.OrdinalIgnoreCase)
                   || status.Equals("running", StringComparison.OrdinalIgnoreCase)
                   || status.Equals("batch_queued", StringComparison.OrdinalIgnoreCase)
                   || status.Equals("batch_running", StringComparison.OrdinalIgnoreCase);
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

        private bool TryGetTopStoryForAutoComplete(double minAverageScore, out long storyId, out string? reason, ISet<long>? excludedStoryIds = null)
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
                    if (excludedStoryIds != null && excludedStoryIds.Contains(story.Id))
                    {
                        lastReason = "storia rinviata a giro successivo dopo errore";
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
                    if (_autoCompleteDeferredStoryIds.Contains(story.Id)) continue;
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

        private bool TryEnqueueAutoNreStoryGeneration(AutoNreStoryGenerationOptions auto, bool useManualMethod)
        {
            try
            {
                var nreOptions = _nreOptionsMonitor.CurrentValue ?? new NarrativeRuntimeEngineOptions();
                var runId = Guid.NewGuid().ToString();
                var priority = Math.Max(1, auto.Priority);
                var maxSteps = Math.Max(1, auto.MaxSteps <= 0 ? 15 : auto.MaxSteps);

                _customLogger.Start(runId);
                _customLogger.Append(runId, "🤖 Auto NRE: accodato suggerimento titolo+prompt.");

                _dispatcher.Enqueue(
                    "AutoNreStoryGeneration",
                    async ctx =>
                    {
                        var suggestionCommand = new GenerateNrePromptSuggestionCommand(
                            database: _database,
                            callCenter: _callCenter,
                            options: Options.Create(nreOptions),
                            logger: _customLogger,
                            themeHint: null,
                            settingHint: null,
                            genreHint: null,
                            toneHint: null,
                            constraintsHint: null);

                        var suggestionResult = await suggestionCommand.ExecuteAsync(ctx.CancellationToken, $"{ctx.RunId}_suggest").ConfigureAwait(false);
                        if (!suggestionResult.Success)
                        {
                            return new CommandResult(false, $"Auto NRE: suggerimento fallito: {suggestionResult.Message}");
                        }

                        if (!TryParseNreSuggestion(suggestionResult.Message, out var suggestion, out var parseError))
                        {
                            return new CommandResult(false, $"Auto NRE: suggerimento non parseabile: {parseError}");
                        }

                        var (structureMode, costSeverity, combatIntensity) = PickRandomNreParameters();
                        _customLogger.Append(ctx.RunId, $"🎲 Parametri NRE casuali: structure={structureMode}, cost={costSeverity}, combat={combatIntensity}");

                        var request = new EngineRequest
                        {
                            EngineName = nreOptions.EngineName,
                            Method = useManualMethod
                                ? "manual"
                                : (string.IsNullOrWhiteSpace(nreOptions.DefaultMethod) ? "state_driven" : nreOptions.DefaultMethod.Trim()),
                            StructureMode = structureMode,
                            CostSeverity = costSeverity,
                            CombatIntensity = combatIntensity,
                            MaxSteps = maxSteps,
                            SnapshotOnFailure = nreOptions.SnapshotOnFailure,
                            RunId = ctx.RunId,
                            UserPrompt = string.IsNullOrWhiteSpace(suggestion.Prompt) ? "Tema:\nlibero" : suggestion.Prompt.Trim(),
                            ResourceHints = string.IsNullOrWhiteSpace(suggestion.ResourceHints) ? null : suggestion.ResourceHints.Trim()
                        };

                        using var scope = _scopeFactory.CreateScope();
                        var engine = scope.ServiceProvider.GetRequiredService<NreEngine>();
                        var runNre = new RunNreCommand(
                            title: string.IsNullOrWhiteSpace(suggestion.Title) ? "NRE Story" : suggestion.Title.Trim(),
                            request: request,
                            database: _database,
                            engine: engine,
                            options: Options.Create(nreOptions),
                            logger: _customLogger,
                            dispatcher: _dispatcher,
                            storiesService: _stories,
                            callCenter: _callCenter);

                        var runResult = await runNre.ExecuteAsync(ctx.CancellationToken, ctx.RunId).ConfigureAwait(false);
                        if (useManualMethod && runResult.Success)
                        {
                            if (TryParseRunNreResult(runResult.Message, out var storyId, out var reviseQueued) &&
                                storyId > 0 &&
                                !reviseQueued)
                            {
                                var reviseRunId = _stories.EnqueueReviseStoryCommand(
                                    storyId,
                                    trigger: "auto_nre_manual_fallback",
                                    priority: Math.Max(priority + 1, 2),
                                    force: true);
                                if (string.IsNullOrWhiteSpace(reviseRunId))
                                {
                                    return new CommandResult(
                                        false,
                                        $"Auto NRE manual: impossibile accodare revisione per storyId={storyId}; non posso garantire valutazione effettuata.");
                                }
                            }
                        }

                        return runResult;
                    },
                    runId: runId,
                    metadata: new Dictionary<string, string>
                    {
                        ["operation"] = "auto_nre_story_generation",
                        ["mode"] = useManualMethod ? "nre_manual" : "nre",
                        ["engine"] = nreOptions.EngineName,
                        ["method"] = useManualMethod ? "manual" : nreOptions.DefaultMethod,
                        ["maxSteps"] = maxSteps.ToString(),
                        ["stepCurrent"] = "0",
                        ["stepMax"] = maxSteps.ToString(),
                        ["agentName"] = nreOptions.WriterAgentName
                    },
                    priority: priority);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enqueue automatic NRE story generation");
                return false;
            }
        }

        private bool TryEnqueueAutoStateDrivenSeriesEpisode(AutoStateDrivenSeriesEpisodeOptions auto)
        {
            var serie = PickNextSeriesForAutoEpisode();
            if (serie == null) return false;

            var writer = ResolveAutoEpisodeWriterAgent(auto.WriterAgentId);
            if (writer == null) return false;

            var nextEpisodeNumber = GetNextAutoSeriesEpisodeNumber(serie.Id);
            if (nextEpisodeNumber > MaxAutoEpisodeNumber)
            {
                _logger.LogInformation(
                    "Auto episode generation stopped: next episode {EpisodeNumber} exceeds max {MaxEpisode}.",
                    nextEpisodeNumber,
                    MaxAutoEpisodeNumber);
                return false;
            }

            var episode = _database.EnsureSeriesEpisode(serie.Id, nextEpisodeNumber);
            if (episode == null) return false;

            var narrativeProfileId = serie.DefaultNarrativeProfileId.GetValueOrDefault(1);
            if (narrativeProfileId <= 0) narrativeProfileId = 1;

            var plannerMode = string.IsNullOrWhiteSpace(serie.DefaultPlannerMode) ? null : serie.DefaultPlannerMode!.Trim();
            var theme = SeriesEpisodePromptBuilder.BuildStateDrivenEpisodeTheme(serie, episode);
            var title = SeriesEpisodePromptBuilder.BuildStateDrivenEpisodeTitle(serie, episode);

            var genId = Guid.NewGuid();
            _customLogger.Start(genId.ToString());
            _customLogger.Append(genId.ToString(), $"Avvio episodio automatico: serie={serie.Id}, ep={episode.Number}");

            var startCmd = new StartStateDrivenStoryCommand(_database);
            var start = startCmd.ExecuteAsync(
                theme: theme,
                title: title,
                narrativeProfileId: narrativeProfileId,
                serieId: serie.Id,
                serieEpisode: episode.Number,
                plannerMode: plannerMode,
                ct: CancellationToken.None).GetAwaiter().GetResult();

            if (!start.success || start.storyId <= 0)
            {
                _customLogger.Append(genId.ToString(), $"Errore avvio storia: {start.error}", "error");
                _customLogger.MarkCompleted(genId.ToString(), "failed");
                return false;
            }

            var minutes = auto.TargetMinutes <= 0 ? 30 : auto.TargetMinutes;
            var wpm = auto.WordsPerMinute <= 0 ? 150 : auto.WordsPerMinute;
            var priority = Math.Max(1, auto.Priority);

            _dispatcher.Enqueue(
                "StateDrivenEpisodeAuto",
                async ctx =>
                {
                    try
                    {
                        var cmd = new GenerateStateDrivenEpisodeToDurationCommand(
                            storyId: start.storyId,
                            writerAgentId: writer.Id,
                            targetMinutes: minutes,
                            wordsPerMinute: wpm,
                            database: _database,
                            kernelFactory: _kernelFactory,
                            storiesService: _stories,
                            logger: _customLogger,
                            textValidationService: _textValidationService,
                            tuning: _tuning,
                            scopeFactory: _scopeFactory);

                        return await cmd.ExecuteAsync(runIdForProgress: genId.ToString(), ct: ctx.CancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        _customLogger.Append(genId.ToString(), "Operazione annullata.", "warning");
                        await _customLogger.MarkCompletedAsync(genId.ToString(), "cancelled");
                        _ = _customLogger.BroadcastTaskComplete(genId, "cancelled");
                        return new CommandResult(false, "cancelled");
                    }
                    catch (Exception ex)
                    {
                        _customLogger.Append(genId.ToString(), "Errore: " + ex.Message, "error");
                        await _customLogger.MarkCompletedAsync(genId.ToString(), ex.Message);
                        _ = _customLogger.BroadcastTaskComplete(genId, "failed");
                        return new CommandResult(false, ex.Message);
                    }
                },
                runId: genId.ToString(),
                metadata: new Dictionary<string, string>
                {
                    ["operation"] = "state_driven_series_episode_auto",
                    ["storyId"] = start.storyId.ToString(),
                    ["serieId"] = serie.Id.ToString(),
                    ["episodeId"] = episode.Id.ToString(),
                    ["episodeNumber"] = episode.Number.ToString(),
                    ["writerAgentId"] = writer.Id.ToString(),
                    ["writerName"] = writer.Name,
                    ["targetMinutes"] = minutes.ToString(),
                    ["wordsPerMinute"] = wpm.ToString()
                },
                priority: priority);

            return true;
        }

        private Series? PickNextSeriesForAutoEpisode()
        {
            try
            {
                var all = _database.ListAllSeries()
                    .Where(s => s != null && s.Id > 0)
                    .OrderBy(s => s.Id)
                    .ToList();
                if (all.Count == 0) return null;

                var eligible = new List<Series>();
                foreach (var serie in all)
                {
                    var nextEpisodeNumber = GetNextAutoSeriesEpisodeNumber(serie.Id);
                    if (nextEpisodeNumber <= MaxAutoEpisodeNumber)
                    {
                        eligible.Add(serie);
                    }
                }

                if (eligible.Count == 0)
                {
                    return null;
                }

                Series? selected = null;
                if (_lastAutoSeriesId > 0)
                {
                    selected = eligible.FirstOrDefault(s => s.Id > _lastAutoSeriesId);
                }

                selected ??= eligible[0];
                _lastAutoSeriesId = selected.Id;
                return selected;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed selecting series for auto state-driven episode");
                return null;
            }
        }

        private int GetNextAutoSeriesEpisodeNumber(int serieId)
        {
            if (serieId <= 0) return 1;
            try
            {
                var maxStoryEpisode = _database.GetAllStories()
                    .Where(s => s.SerieId == serieId && !s.Deleted && s.SerieEpisode.HasValue && s.SerieEpisode.Value > 0)
                    .Select(s => s.SerieEpisode!.Value)
                    .DefaultIfEmpty(0)
                    .Max();

                var next = maxStoryEpisode + 1;
                return next <= 0 ? 1 : next;
            }
            catch
            {
                return _database.GetNextSeriesEpisodeNumber(serieId);
            }
        }

        private Agent? ResolveAutoEpisodeWriterAgent(int writerAgentId)
        {
            if (writerAgentId > 0)
            {
                var agent = _database.GetAgentById(writerAgentId);
                if (agent != null && agent.IsActive) return agent;
            }

            // Preferred default for auto state-driven episodes: Qwen3 30b writer
            var preferred = _database.ListAgents()
                .FirstOrDefault(a =>
                    a.IsActive &&
                    !string.IsNullOrWhiteSpace(a.Description) &&
                    a.Description.Contains("State-Driven Writer", StringComparison.OrdinalIgnoreCase) &&
                    a.Description.Contains("Qwen3 30b", StringComparison.OrdinalIgnoreCase));
            if (preferred != null)
            {
                return preferred;
            }

            preferred = _database.ListAgents()
                .FirstOrDefault(a =>
                    a.IsActive &&
                    a.ModelId.HasValue &&
                    string.Equals(a.Role, "writer", StringComparison.OrdinalIgnoreCase) &&
                    (_database.GetModelInfoById(a.ModelId.Value)?.Name ?? string.Empty)
                        .Contains("qwen3:30b", StringComparison.OrdinalIgnoreCase));
            if (preferred != null)
            {
                return preferred;
            }

            var writers = _database.ListAgents()
                .Where(a => a.IsActive && a.Role.Contains("writer", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (writers.Count == 0) return null;
            if (writers.Count == 1) return writers[0];

            var weighted = new List<(Agent Agent, double Weight)>();
            double totalWeight = 0;
            foreach (var writer in writers)
            {
                var score = GetAutoEpisodeWriterScore(writer);
                var weight = Math.Max(1.0, score);
                weighted.Add((writer, weight));
                totalWeight += weight;
            }

            if (totalWeight <= 0)
            {
                return writers[Random.Shared.Next(writers.Count)];
            }

            var roll = Random.Shared.NextDouble() * totalWeight;
            foreach (var entry in weighted)
            {
                roll -= entry.Weight;
                if (roll <= 0)
                {
                    return entry.Agent;
                }
            }

            return weighted[weighted.Count - 1].Agent;
        }

        private double GetAutoEpisodeWriterScore(Agent writer)
        {
            if (writer.ModelId.HasValue)
            {
                var model = _database.GetModelInfoById(writer.ModelId.Value);
                if (model != null)
                {
                    if (model.WriterScore > 0) return model.WriterScore;
                    if (model.TotalScore > 0) return model.TotalScore;
                }
            }

            return 1.0;
        }

        private static string NormalizeAutoAdvancementMode(string? mode)
        {
            if (string.Equals(mode, "nre", StringComparison.OrdinalIgnoreCase))
            {
                return "nre";
            }

            if (string.Equals(mode, "nre_manual", StringComparison.OrdinalIgnoreCase))
            {
                return "nre_manual";
            }

            return "series";
        }

        private static bool TryParseRunNreResult(string? message, out long storyId, out bool reviseQueued)
        {
            storyId = 0;
            reviseQueued = false;
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            var text = message.Trim();
            var storyToken = "storyId=";
            var storyIdx = text.IndexOf(storyToken, StringComparison.OrdinalIgnoreCase);
            if (storyIdx < 0)
            {
                return false;
            }

            var storyStart = storyIdx + storyToken.Length;
            var storyEnd = text.IndexOfAny(new[] { ',', ')', ' ' }, storyStart);
            if (storyEnd < 0)
            {
                storyEnd = text.Length;
            }

            if (!long.TryParse(text[storyStart..storyEnd], out storyId) || storyId <= 0)
            {
                return false;
            }

            var reviseToken = "reviseQueued=";
            var reviseIdx = text.IndexOf(reviseToken, StringComparison.OrdinalIgnoreCase);
            if (reviseIdx >= 0)
            {
                var reviseStart = reviseIdx + reviseToken.Length;
                var reviseEnd = text.IndexOfAny(new[] { ',', ')', ' ' }, reviseStart);
                if (reviseEnd < 0)
                {
                    reviseEnd = text.Length;
                }
                _ = bool.TryParse(text[reviseStart..reviseEnd], out reviseQueued);
            }

            return true;
        }

        private static (string StructureMode, string CostSeverity, string CombatIntensity) PickRandomNreParameters()
        {
            var structureModes = new[] { "standard", "military_strict" };
            var costSeverities = new[] { "low", "medium", "high" };
            var combatIntensities = new[] { "low", "normal", "high", "total_war" };

            var structureMode = structureModes[Random.Shared.Next(structureModes.Length)];
            var costSeverity = structureMode == "military_strict"
                ? costSeverities[Random.Shared.Next(costSeverities.Length)]
                : "medium";
            var combatIntensity = structureMode == "military_strict"
                ? combatIntensities[Random.Shared.Next(combatIntensities.Length)]
                : "normal";

            return (structureMode, costSeverity, combatIntensity);
        }

        private static bool TryParseNreSuggestion(string? json, out AutoNrePromptSuggestionDto suggestion, out string? error)
        {
            suggestion = new AutoNrePromptSuggestionDto();
            error = null;

            try
            {
                var parsed = JsonSerializer.Deserialize<AutoNrePromptSuggestionDto>(json ?? string.Empty, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (parsed == null)
                {
                    error = "json vuoto";
                    return false;
                }

                parsed.Title = (parsed.Title ?? string.Empty).Trim();
                parsed.Prompt = (parsed.Prompt ?? string.Empty).Trim();
                parsed.ResourceHints = (parsed.ResourceHints ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(parsed.Title) || string.IsNullOrWhiteSpace(parsed.Prompt))
                {
                    error = "campi obbligatori mancanti (title/prompt)";
                    return false;
                }

                suggestion = parsed;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private sealed class AutoNrePromptSuggestionDto
        {
            public string? Title { get; set; }
            public string? Prompt { get; set; }
            public string? ResourceHints { get; set; }
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



