using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace TinyGenerator.Services
{
    public sealed class MemoryEmbeddingBackfillService : IHostedService, IMemoryEmbeddingBackfillScheduler, IDisposable
    {
        private readonly PersistentMemoryService _memoryService;
        private readonly IMemoryEmbeddingGenerator _embeddingGenerator;
        private readonly ICommandDispatcher _dispatcher;
        private readonly IOptionsMonitor<MemoryEmbeddingOptions> _optionsMonitor;
        private readonly ICustomLogger? _logger;
        private readonly object _gate = new();
        private bool _commandRunning;
        private bool _rerunRequested;

        public MemoryEmbeddingBackfillService(
            PersistentMemoryService memoryService,
            IMemoryEmbeddingGenerator embeddingGenerator,
            ICommandDispatcher dispatcher,
            IOptionsMonitor<MemoryEmbeddingOptions> optionsMonitor,
            ICustomLogger? logger = null)
        {
            _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
            _embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _memoryService.MemorySaved += OnMemorySaved;
            RequestBackfill("startup");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _memoryService.MemorySaved -= OnMemorySaved;
            return Task.CompletedTask;
        }

        public void RequestBackfill(string reason = "manual")
        {
            CommandHandle? handle = null;
            lock (_gate)
            {
                if (_commandRunning)
                {
                    _rerunRequested = true;
                    return;
                }
                _commandRunning = true;
            }

            handle = _dispatcher.Enqueue(
                "memory_embedding_worker",
                async ctx => await RunWorkerAsync(ctx.CancellationToken),
                threadScope: "memory/embedding",
                metadata: new Dictionary<string, string>
                {
                    ["reason"] = reason,
                    ["ts"] = DateTime.UtcNow.ToString("o"),
                    ["agentName"] = "memory_backfill_worker",
                    ["modelName"] = "embedding_model"
                });

            _ = handle.CompletionTask.ContinueWith(t => OnCommandCompleted(t), TaskScheduler.Default);
        }

        private async Task<CommandResult> RunWorkerAsync(CancellationToken cancellationToken)
        {
            try
            {
                var updated = await ProcessPendingEmbeddingsAsync(cancellationToken);
                var message = updated == 0 ? "Nessun embedding aggiornato" : $"Embedding aggiornati: {updated}";
                return new CommandResult(true, message);
            }
            catch (OperationCanceledException)
            {
                return new CommandResult(false, "Embedding worker cancellato");
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "MemoryEmbedding", $"Worker error: {ex.Message}", ex.ToString());
                return new CommandResult(false, ex.Message);
            }
        }

        private async Task<int> ProcessPendingEmbeddingsAsync(CancellationToken cancellationToken)
        {
            var updated = 0;
            var attempted = new HashSet<string>();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batchSize = Math.Max(1, _optionsMonitor.CurrentValue.BackfillBatchSize);
                var batch = await _memoryService.GetMemoriesMissingEmbeddingAsync(batchSize);
                if (batch.Count == 0) break;

                var workItems = batch.Where(m => attempted.Add(m.Id)).ToList();
                if (workItems.Count == 0)
                {
                    // Avoid infinite loops if every record in the batch failed during this run.
                    break;
                }

                foreach (var memory in workItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var vector = await _embeddingGenerator.GenerateAsync(memory.TextValue, cancellationToken);
                        if (vector.Length == 0)
                        {
                            _logger?.Log("Warn", "MemoryEmbedding", $"Embedding vuoto per {memory.Id}");
                            continue;
                        }
                        await _memoryService.UpdateEmbeddingAsync(memory.Id, vector, cancellationToken);
                        updated++;
                    }
                    catch (Exception ex)
                    {
                        _logger?.Log("Error", "MemoryEmbedding", $"Errore embedding per {memory.Id}: {ex.Message}", ex.ToString());
                    }
                }
            }
            return updated;
        }

        private void OnMemorySaved(object? sender, MemorySavedEventArgs e)
        {
            RequestBackfill("memory_saved");
        }

        private void OnCommandCompleted(Task<CommandResult> task)
        {
            bool rerun;
            lock (_gate)
            {
                if (_rerunRequested)
                {
                    _rerunRequested = false;
                    rerun = true;
                }
                else
                {
                    _commandRunning = false;
                    rerun = false;
                }
            }

            if (rerun)
            {
                RequestBackfill("pending");
            }
        }

        public void Dispose()
        {
            _memoryService.MemorySaved -= OnMemorySaved;
        }
    }
}
