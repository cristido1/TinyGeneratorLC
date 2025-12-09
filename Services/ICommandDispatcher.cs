using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TinyGenerator.Services
{
    public sealed record CommandResult(bool Success, string? Message);
    public sealed record CommandCompletedEventArgs(string RunId, string OperationName, bool Success, string? Message);

    public sealed class CommandContext
    {
        public string RunId { get; }
        public string OperationName { get; }
        public IReadOnlyDictionary<string, string>? Metadata { get; }
        public long OperationNumber { get; }
        public System.Threading.CancellationToken CancellationToken { get; }

        internal CommandContext(
            string runId,
            string operationName,
            IReadOnlyDictionary<string, string>? metadata,
            long operationNumber,
            System.Threading.CancellationToken cancellationToken)
        {
            RunId = runId;
            OperationName = operationName;
            Metadata = metadata;
            OperationNumber = operationNumber;
            CancellationToken = cancellationToken;
        }
    }

    public interface ICommandDispatcher
    {
        CommandHandle Enqueue(
            string operationName,
            Func<CommandContext, Task<CommandResult>> handler,
            string? runId = null,
            string? threadScope = null,
            IReadOnlyDictionary<string, string>? metadata = null);

        IReadOnlyList<CommandSnapshot> GetActiveCommands();
        void UpdateStep(string runId, int current, int max);
        void UpdateRetry(string runId, int retryCount);

        /// <summary>
        /// Attende il completamento di un comando identificato dal runId e restituisce il relativo CommandResult.
        /// </summary>
        Task<CommandResult> WaitForCompletionAsync(string runId, System.Threading.CancellationToken cancellationToken = default);

        /// <summary>
        /// Evento sollevato alla conclusione di ogni comando (successo o errore).
        /// </summary>
        event Action<CommandCompletedEventArgs>? CommandCompleted;
    }

    public sealed class CommandHandle
    {
        public string RunId { get; }
        public string OperationName { get; }
        public Task<CommandResult> CompletionTask { get; }

        internal CommandHandle(string runId, string operationName, Task<CommandResult> completionTask)
        {
            RunId = runId;
            OperationName = operationName;
            CompletionTask = completionTask;
        }
    }

    public sealed record CommandSnapshot(
        string RunId,
        string OperationName,
        string ThreadScope,
        string Status,
        DateTimeOffset EnqueuedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt,
        IReadOnlyDictionary<string, string>? Metadata,
        string? AgentName = null,
        string? ModelName = null,
        int? CurrentStep = null,
        int? MaxStep = null,
        int RetryCount = 0,
        string? ErrorMessage = null);
}
