using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services
{
    /// <summary>
    /// Servizio background che esegue batch summarization:
    /// - All'avvio dell'applicazione
    /// - Ogni ora
    /// </summary>
    public class AutoSummarizeService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<AutoSummarizeService> _logger;
        private readonly TimeSpan _interval = TimeSpan.FromHours(1);

        public AutoSummarizeService(
            IServiceProvider serviceProvider,
            ILogger<AutoSummarizeService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("AutoSummarizeService started");

            // Attendi 30 secondi dopo l'avvio prima di eseguire la prima volta
            // (per dare tempo al sistema di inizializzarsi completamente)
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Prima esecuzione al startup
            await RunBatchSummarizeAsync("startup", stoppingToken);

            // Poi ogni ora
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_interval, stoppingToken);
                    await RunBatchSummarizeAsync("scheduled", stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("AutoSummarizeService stopping");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in AutoSummarizeService loop");
                    // Continue anyway
                }
            }

            _logger.LogInformation("AutoSummarizeService stopped");
        }

        private async Task RunBatchSummarizeAsync(string trigger, CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation("Running batch summarization (trigger: {Trigger})", trigger);

                using var scope = _serviceProvider.CreateScope();
                var database = scope.ServiceProvider.GetRequiredService<DatabaseService>();
                var kernelFactory = scope.ServiceProvider.GetRequiredService<ILangChainKernelFactory>();
                var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
                var customLogger = scope.ServiceProvider.GetService<ICustomLogger>();

                var runId = Guid.NewGuid().ToString();
                var cmd = new BatchSummarizeStoriesCommand(
                    database,
                    kernelFactory,
                    dispatcher,
                    customLogger!,
                    minScore: 60);

                dispatcher.Enqueue(
                    "BatchSummarizeStories",
                    async ctx => await cmd.ExecuteAsync(ctx.CancellationToken),
                    runId: runId,
                    metadata: new Dictionary<string, string>
                    {
                        ["minScore"] = "60",
                        ["agentName"] = "batch_orchestrator",
                        ["operation"] = "batch_summarize",
                        ["triggeredBy"] = $"auto_{trigger}"
                    },
                    priority: 2);

                _logger.LogInformation("Batch summarization enqueued (runId: {RunId}, trigger: {Trigger})", runId, trigger);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enqueue batch summarization (trigger: {Trigger})", trigger);
            }
        }
    }
}
