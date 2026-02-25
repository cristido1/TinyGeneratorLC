using TinyGenerator.Models;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed class AnalyzePendingLogErrorsCommand : ICommand
{
    private readonly DatabaseService _database;
    private readonly LogAnalysisService _analysisService;
    private readonly ILogger<AnalyzePendingLogErrorsCommand>? _logger;
    private readonly int _maxThreadsPerRun;
    private readonly int _scanWindowThreads;

    public AnalyzePendingLogErrorsCommand(
        DatabaseService database,
        LogAnalysisService analysisService,
        ILogger<AnalyzePendingLogErrorsCommand>? logger = null,
        int maxThreadsPerRun = 5,
        int scanWindowThreads = 50)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));
        _logger = logger;
        _maxThreadsPerRun = Math.Max(1, maxThreadsPerRun);
        _scanWindowThreads = Math.Max(_maxThreadsPerRun, scanWindowThreads);
    }

    public string CommandName => "analyze_pending_log_errors";
    public int Priority => 3;
    public bool Batch => false; // usa agente log_analyzer: va accodato

    public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var pendingThreadIds = _database.ListThreadsPendingAnalysis(_scanWindowThreads);
        if (pendingThreadIds.Count == 0)
        {
            return new CommandResult(true, "Nessun thread log in attesa di analisi");
        }

        var analyzed = 0;
        var scanned = 0;
        foreach (var threadId in pendingThreadIds)
        {
            ct.ThrowIfCancellationRequested();
            scanned++;

            var logs = _database.GetLogsByThreadId(threadId);
            if (!ContainsErrorSignals(logs))
            {
                continue;
            }

            var (success, message) = await _analysisService
                .AnalyzeThreadAsync(threadId.ToString(), null, ct)
                .ConfigureAwait(false);

            if (!success)
            {
                _logger?.LogWarning("Analisi log automatica fallita per thread {ThreadId}: {Message}", threadId, message);
                continue;
            }

            analyzed++;
            if (analyzed >= _maxThreadsPerRun)
            {
                break;
            }
        }

        return new CommandResult(true, $"Analisi log errori completata: analizzati={analyzed}, thread_scansionati={scanned}");
    }

    private static bool ContainsErrorSignals(List<LogEntry>? logs)
    {
        if (logs == null || logs.Count == 0) return false;

        foreach (var log in logs)
        {
            if (!string.IsNullOrWhiteSpace(log.Exception))
            {
                return true;
            }

            var level = (log.Level ?? string.Empty).Trim();
            if (level.Equals("error", StringComparison.OrdinalIgnoreCase) ||
                level.Equals("critical", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var category = log.Category ?? string.Empty;
            if (category.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
