using System.Collections.Generic;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public interface ICommandEnqueuer
{
    CommandHandle Enqueue(
        ICommand command,
        string? runId = null,
        string? threadScope = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        int? priority = null);
}
