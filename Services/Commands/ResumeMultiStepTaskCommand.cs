using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands
{
    public class ResumeMultiStepTaskCommand
    {
        private readonly long _executionId;
        private readonly Guid _generationId;
        private readonly MultiStepOrchestrationService _orchestrator;
        private readonly DatabaseService _database;
        private readonly ICustomLogger _logger;
        private readonly ICommandDispatcher? _dispatcher;

        public ResumeMultiStepTaskCommand(
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

            _logger.Log("Information", "MultiStep", $"Resuming task execution {_executionId}");

            try
            {
                var execution = _database.GetTaskExecutionById(_executionId);
                if (execution == null)
                {
                    _logger.Log("Error", "MultiStep", $"Task execution {_executionId} not found");
                    return;
                }

                if (execution.Status == "completed")
                {
                    _logger.Log("Warning", "MultiStep", $"Task execution {_executionId} already completed");
                    return;
                }

                // Reset status to in_progress
                execution.Status = "in_progress";
                execution.UpdatedAt = DateTime.UtcNow.ToString("o");
                _database.UpdateTaskExecution(execution);

                _logger.Log("Information", "MultiStep", $"Resuming from step {execution.CurrentStep}/{execution.MaxStep}");

                // Progress callback
                void ReportProgress(int current, int max, string stepDesc)
                {
                    _logger.Log("Information", "MultiStep", $"Step {current}/{max}: {stepDesc}");
                    _ = _logger.BroadcastStepProgress(_generationId, current, max, stepDesc);
                    // Use same runId pattern as Enqueue: {generationId}_exec
                    _dispatcher?.UpdateStep($"{_generationId}_exec", current, max);
                }

                // Resume execution
                using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                executionCts.CancelAfter(TimeSpan.FromHours(2));

                await _orchestrator.ExecuteAllStepsAsync(
                    _executionId,
                    threadId,
                    (desc, current, max, stepDesc) => ReportProgress(current, max, stepDesc),
                    executionCts.Token
                );

                _logger.Log("Information", "MultiStep", $"Task execution {_executionId} resumed and completed");
                    _ = _logger.BroadcastTaskComplete(_generationId, "completed");
            }
            catch (OperationCanceledException)
            {
                _logger.Log("Warning", "MultiStep", $"Task execution {_executionId} resume cancelled");
                
                var execution = _database.GetTaskExecutionById(_executionId);
                if (execution != null)
                {
                    execution.Status = "cancelled";
                    execution.UpdatedAt = DateTime.UtcNow.ToString("o");
                    _database.UpdateTaskExecution(execution);
                }

                _ = _logger.BroadcastTaskComplete(_generationId, "cancelled");
            }
            catch (Exception ex)
            {
                _logger.Log("Error", "MultiStep", $"Task execution {_executionId} resume error: {ex.Message}");
                
                var execution = _database.GetTaskExecutionById(_executionId);
                if (execution != null)
                {
                    execution.Status = "failed";
                    execution.UpdatedAt = DateTime.UtcNow.ToString("o");
                    _database.UpdateTaskExecution(execution);
                }

                _ = _logger.BroadcastTaskComplete(_generationId, "failed");
            }
        }
    }
}
