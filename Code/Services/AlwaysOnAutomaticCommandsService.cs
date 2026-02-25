using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TinyGenerator.Services;

public sealed class AlwaysOnAutomaticCommandsService : BackgroundService
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly DatabaseService _database;
    private readonly ILangChainKernelFactory _kernelFactory;
    private readonly ICustomLogger _customLogger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryEmbeddingBackfillScheduler _embeddingScheduler;
    private readonly LogAnalysisService _logAnalysisService;
    private readonly ILogger<AlwaysOnAutomaticCommandsService> _logger;
    private readonly ILogger<AnalyzePendingLogErrorsCommand>? _logAnalysisCommandLogger;
    private readonly IOptionsMonitor<AlwaysOnAutomaticCommandsOptions> _optionsMonitor;

    private readonly TimeSpan _tick = TimeSpan.FromSeconds(5);
    private DateTime _startedAtUtc;
    private DateTime? _lastEmbeddingTriggerUtc;
    private DateTime? _lastModelStatsTriggerUtc;
    private DateTime? _lastStorySummariesTriggerUtc;
    private DateTime? _lastLogErrorAnalysisTriggerUtc;

    public AlwaysOnAutomaticCommandsService(
        ICommandDispatcher dispatcher,
        DatabaseService database,
        ILangChainKernelFactory kernelFactory,
        ICustomLogger customLogger,
        IServiceScopeFactory scopeFactory,
        IMemoryEmbeddingBackfillScheduler embeddingScheduler,
        LogAnalysisService logAnalysisService,
        IOptionsMonitor<AlwaysOnAutomaticCommandsOptions> optionsMonitor,
        ILogger<AlwaysOnAutomaticCommandsService> logger,
        ILogger<AnalyzePendingLogErrorsCommand>? logAnalysisCommandLogger = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
        _customLogger = customLogger ?? throw new ArgumentNullException(nameof(customLogger));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _embeddingScheduler = embeddingScheduler ?? throw new ArgumentNullException(nameof(embeddingScheduler));
        _logAnalysisService = logAnalysisService ?? throw new ArgumentNullException(nameof(logAnalysisService));
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logAnalysisCommandLogger = logAnalysisCommandLogger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _startedAtUtc = DateTime.UtcNow;
        _logger.LogInformation("AlwaysOnAutomaticCommandsService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_tick, stoppingToken).ConfigureAwait(false);

                var nowUtc = DateTime.UtcNow;
                var opts = _optionsMonitor.CurrentValue ?? new AlwaysOnAutomaticCommandsOptions();

                TryTriggerEmbeddingBackfill(nowUtc, opts.EmbeddingBackfill);
                TryTriggerModelStatsUpdate(nowUtc, opts.UpdateModelStatsFromLogs);
                TryTriggerStorySummaries(nowUtc, opts.StorySummaries);
                TryTriggerLogErrorAnalysis(nowUtc, opts.LogErrorAnalysis);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Always-on automation loop failed");
            }
        }

        _logger.LogInformation("AlwaysOnAutomaticCommandsService stopped");
    }

    private void TryTriggerEmbeddingBackfill(DateTime nowUtc, EmbeddingBackfillScheduleOptions cfg)
    {
        if (!IsDue(_lastEmbeddingTriggerUtc, _startedAtUtc, cfg.StartupDelaySeconds, cfg.IntervalSeconds, nowUtc))
        {
            return;
        }

        _embeddingScheduler.RequestBackfill("always_on_interval");
        _lastEmbeddingTriggerUtc = nowUtc;
    }

    private void TryTriggerModelStatsUpdate(DateTime nowUtc, UpdateModelStatsFromLogsScheduleOptions cfg)
    {
        if (!IsDue(_lastModelStatsTriggerUtc, _startedAtUtc, cfg.StartupDelaySeconds, cfg.IntervalSeconds, nowUtc))
        {
            return;
        }

        if (!IsOperationActive("update_model_stats_from_logs", "update_model_stats"))
        {
            var cmd = new UpdateModelStatsFromLogsCommand(
                _database,
                batchSize: cfg.BatchSize,
                maxBatchesPerRun: cfg.MaxBatchesPerRun);

            _dispatcher.Enqueue(
                cmd,
                runId: $"always_stats_{DateTime.UtcNow:yyyyMMddHHmmssfff}",
                threadScope: "system/model_stats",
                metadata: new Dictionary<string, string>
                {
                    ["operation"] = "update_model_stats_from_logs",
                    ["trigger"] = "always_on",
                    ["batchSize"] = Math.Max(1, cfg.BatchSize).ToString(),
                    ["maxBatchesPerRun"] = Math.Max(1, cfg.MaxBatchesPerRun).ToString()
                },
                priority: 1);
        }

        _lastModelStatsTriggerUtc = nowUtc;
    }

    private void TryTriggerStorySummaries(DateTime nowUtc, StorySummariesScheduleOptions cfg)
    {
        if (!IsDue(_lastStorySummariesTriggerUtc, _startedAtUtc, cfg.StartupDelaySeconds, cfg.IntervalSeconds, nowUtc))
        {
            return;
        }

        if (!IsOperationActive("always_on_story_summaries", "batch_summarize"))
        {
            var cmd = new AlwaysOnStorySummariesCommand(
                _database,
                _kernelFactory,
                _dispatcher,
                _customLogger,
                _scopeFactory,
                minScore: cfg.MinScore);

            _dispatcher.Enqueue(
                cmd,
                runId: $"always_summary_{DateTime.UtcNow:yyyyMMddHHmmssfff}",
                threadScope: "system/story_summaries",
                metadata: new Dictionary<string, string>
                {
                    ["operation"] = "always_on_story_summaries",
                    ["trigger"] = "always_on",
                    ["minScore"] = cfg.MinScore.ToString()
                },
                priority: 2);
        }

        _lastStorySummariesTriggerUtc = nowUtc;
    }

    private void TryTriggerLogErrorAnalysis(DateTime nowUtc, LogErrorAnalysisScheduleOptions cfg)
    {
        if (!IsDue(_lastLogErrorAnalysisTriggerUtc, _startedAtUtc, cfg.StartupDelaySeconds, cfg.IntervalSeconds, nowUtc))
        {
            return;
        }

        if (!IsOperationActive("analyze_pending_log_errors", "log_analyzer"))
        {
            var cmd = new AnalyzePendingLogErrorsCommand(
                _database,
                _logAnalysisService,
                _logAnalysisCommandLogger,
                maxThreadsPerRun: cfg.MaxThreadsPerRun,
                scanWindowThreads: cfg.ScanWindowThreads);

            _dispatcher.Enqueue(
                cmd,
                runId: $"always_logerr_{DateTime.UtcNow:yyyyMMddHHmmssfff}",
                threadScope: "system/log_error_analysis",
                metadata: new Dictionary<string, string>
                {
                    ["operation"] = "analyze_pending_log_errors",
                    ["trigger"] = "always_on",
                    ["maxThreadsPerRun"] = Math.Max(1, cfg.MaxThreadsPerRun).ToString(),
                    ["scanWindowThreads"] = Math.Max(Math.Max(1, cfg.MaxThreadsPerRun), cfg.ScanWindowThreads).ToString(),
                    ["agentName"] = "log_analyzer"
                },
                priority: 3);
        }

        _lastLogErrorAnalysisTriggerUtc = nowUtc;
    }

    private bool IsOperationActive(params string[] operationNames)
    {
        if (operationNames == null || operationNames.Length == 0) return false;
        var set = new HashSet<string>(
            operationNames.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()),
            StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0) return false;

        try
        {
            foreach (var cmd in _dispatcher.GetActiveCommands())
            {
                if (!IsActiveStatus(cmd.Status))
                {
                    continue;
                }

                if (set.Contains(cmd.OperationName))
                {
                    return true;
                }

                if (cmd.Metadata != null &&
                    cmd.Metadata.TryGetValue("operation", out var op) &&
                    !string.IsNullOrWhiteSpace(op) &&
                    set.Contains(op))
                {
                    return true;
                }
            }
        }
        catch
        {
            // best-effort
        }

        return false;
    }

    private static bool IsActiveStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;
        return status.Equals("queued", StringComparison.OrdinalIgnoreCase)
               || status.Equals("running", StringComparison.OrdinalIgnoreCase)
               || status.Equals("batch_queued", StringComparison.OrdinalIgnoreCase)
               || status.Equals("batch_running", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDue(DateTime? lastRunUtc, DateTime startedAtUtc, int startupDelaySeconds, int intervalSeconds, DateTime nowUtc)
    {
        var safeStartupDelay = TimeSpan.FromSeconds(Math.Max(0, startupDelaySeconds));
        var safeInterval = TimeSpan.FromSeconds(Math.Max(1, intervalSeconds));
        if (!lastRunUtc.HasValue)
        {
            return nowUtc - startedAtUtc >= safeStartupDelay;
        }

        return nowUtc - lastRunUtc.Value >= safeInterval;
    }
}
