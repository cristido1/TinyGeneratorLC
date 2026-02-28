using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed class UpdateModelStatsFromLogsCommand : ICommand
{
    private readonly DatabaseService _database;
    private readonly int _batchSize;
    private readonly int _maxBatchesPerRun;

    public UpdateModelStatsFromLogsCommand(DatabaseService database, int batchSize = 200, int maxBatchesPerRun = 5)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _batchSize = Math.Max(1, batchSize);
        _maxBatchesPerRun = Math.Max(1, maxBatchesPerRun);
    }

    public string CommandName => "update_model_stats_from_logs";
    public int Priority => 1;
    public bool Batch => true;

    public Task<CommandResult> ExecuteAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var processedTotal = 0;
        var loops = 0;
        for (; loops < _maxBatchesPerRun; loops++)
        {
            ct.ThrowIfCancellationRequested();
            var processed = _database.UpdateModelStatsFromUnexaminedLogs(_batchSize);
            if (processed <= 0)
            {
                break;
            }
            processedTotal += processed;
        }

        var message = processedTotal > 0
            ? $"Model stats aggiornate da log: {processedTotal} record (batch={_batchSize}, cicli={Math.Max(1, loops)})"
            : "Nessun log modello da elaborare";

        if (processedTotal <= 0)
        {
            // Pulizia report storici no-op/falliti di questa operazione:
            // se non c'è nulla da elaborare, il report non è informativo.
            _database.DeleteSystemReportsByOperationType(CommandName, deletedBy: "auto_cleanup_no_logs");
        }

        return Task.FromResult(new CommandResult(true, message));
    }
}
