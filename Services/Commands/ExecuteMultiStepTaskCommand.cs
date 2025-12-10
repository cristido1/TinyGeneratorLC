using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands
{
    public class ExecuteMultiStepTaskCommand
    {
        private readonly long _executionId;
        private readonly Guid _generationId;
        private readonly MultiStepOrchestrationService _orchestrator;
        private readonly DatabaseService _database;
        private readonly ICustomLogger _logger;
        private readonly ICommandDispatcher? _dispatcher;

        public ExecuteMultiStepTaskCommand(
            long executionId,
            Guid generationId,
            MultiStepOrchestrationService orchestrator,
            DatabaseService database,
            ICustomLogger logger,
            ICommandDispatcher? dispatcher = null)
        {
            _executionId = executionId;
            _generationId = generationId;
            _orchestrator = orchestrator;
            _database = database;
            _logger = logger;
            _dispatcher = dispatcher;
        }

        public async Task ExecuteAsync(CancellationToken ct = default)
        {
            var threadId = _generationId.GetHashCode();

            Console.WriteLine($"[DEBUG] ExecuteMultiStepTaskCommand.ExecuteAsync START - executionId={_executionId}, generationId={_generationId}");
            _logger.Log("Information", "MultiStep", $"[ExecuteMultiStepTaskCommand] Starting execution for task {_executionId}, generationId={_generationId}");
            if (_logger is CustomLogger customLogger)
            {
                await customLogger.FlushAsync();
            }

            try
            {
                Console.WriteLine($"[DEBUG] ExecuteMultiStepTaskCommand - Getting execution from DB, id={_executionId}");
                var execution = _database.GetTaskExecutionById(_executionId);
                if (execution == null)
                {
                    Console.WriteLine($"[DEBUG] ExecuteMultiStepTaskCommand - Execution {_executionId} NOT FOUND in DB");
                    _logger.Log("Error", "MultiStep", $"Task execution {_executionId} not found");
                    return;
                }
                Console.WriteLine($"[DEBUG] ExecuteMultiStepTaskCommand - Execution found: status={execution.Status}, current_step={execution.CurrentStep}, max_step={execution.MaxStep}");

                // Progress callback
                void ReportProgress(int current, int max, string stepDesc)
                {
                    _logger.Log("Information", "MultiStep", $"Step {current}/{max}: {stepDesc}");
                    _ = _logger.BroadcastStepProgress(_generationId, current, max, stepDesc);
                    _dispatcher?.UpdateStep(_generationId.ToString(), current, max);
                }
                
                // Retry callback
                void ReportRetry(int retryCount)
                {
                    _dispatcher?.UpdateRetry(_generationId.ToString(), retryCount);
                }

                // Execute all steps
                using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                executionCts.CancelAfter(TimeSpan.FromHours(2)); // Global timeout

                Console.WriteLine($"[DEBUG] ExecuteMultiStepTaskCommand - About to call ExecuteAllStepsAsync");
                await _orchestrator.ExecuteAllStepsAsync(
                    _executionId,
                    threadId,
                    (desc, current, max, stepDesc) => ReportProgress(current, max, stepDesc),
                    executionCts.Token,
                    retryCount => ReportRetry(retryCount)
                );
                Console.WriteLine($"[DEBUG] ExecuteMultiStepTaskCommand - ExecuteAllStepsAsync completed successfully");

                _logger.Log("Information", "MultiStep", $"Task execution {_executionId} completed successfully");
                    _ = _logger.BroadcastTaskComplete(_generationId, "completed");
            }
            catch (OperationCanceledException ex)
            {
                Console.WriteLine($"[DEBUG] ExecuteMultiStepTaskCommand - OperationCanceledException: {ex.Message}");
                _logger.Log("Warning", "MultiStep", $"Task execution {_executionId} cancelled");
                
                var execution = _database.GetTaskExecutionById(_executionId);
                if (execution != null)
                {
                    execution.Status = "cancelled";
                    execution.UpdatedAt = DateTime.UtcNow.ToString("o");
                    _database.UpdateTaskExecution(execution);
                    // No need to delete story - it's not created yet if execution fails
                }

                _ = _logger.BroadcastTaskComplete(_generationId, "cancelled");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG] ExecuteMultiStepTaskCommand - EXCEPTION CAUGHT: Type={ex.GetType().Name}, Message={ex.Message}");
                Console.WriteLine($"[DEBUG] ExecuteMultiStepTaskCommand - StackTrace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"[DEBUG] ExecuteMultiStepTaskCommand - InnerException: {ex.InnerException.Message}");
                }
                _logger.Log("Error", "MultiStep", $"Task execution {_executionId} error: {ex.Message}", ex.ToString());
                
                var execution = _database.GetTaskExecutionById(_executionId);
                if (execution != null)
                {
                    execution.Status = "failed";
                    execution.UpdatedAt = DateTime.UtcNow.ToString("o");
                    _database.UpdateTaskExecution(execution);
                    // No need to delete story - it's not created yet if execution fails
                }

                _ = _logger.BroadcastTaskComplete(_generationId, "failed");
            }
        }
    }
}
