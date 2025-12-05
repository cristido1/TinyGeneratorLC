using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TinyGenerator.Services
{
    public sealed record CommandResult(bool Success, string? Message);

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
        IReadOnlyDictionary<string, string>? Metadata);
}
