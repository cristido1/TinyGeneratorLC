using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public static class CommandDispatcherEnqueueExtensions
{
    public static CommandHandle Enqueue(
        this ICommandDispatcher dispatcher,
        string operationName,
        Func<CommandContext, Task<CommandResult>> handler,
        string? runId = null,
        string? threadScope = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        int priority = 2)
    {
        if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
        var command = new DelegateCommand(operationName, handler, priority);
        return dispatcher.Enqueue(command, runId, threadScope, metadata, priority);
    }
}
