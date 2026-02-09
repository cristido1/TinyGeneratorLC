using System;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands;

public sealed class DelegateCommand : ICommand
{
    private readonly Func<CommandContext, Task<CommandResult>> _handler;

    public string CommandName { get; }
    public int Priority { get; }
    public event EventHandler<CommandProgressEventArgs>? Progress { add { } remove { } }

    public DelegateCommand(string commandName, Func<CommandContext, Task<CommandResult>> handler, int priority = 2)
    {
        CommandName = string.IsNullOrWhiteSpace(commandName) ? "command" : commandName.Trim();
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        Priority = Math.Max(1, priority);
    }

    public Task<CommandResult> Start(CommandContext context) => _handler(context);

    public Task End(CommandContext context, CommandResult result) => Task.CompletedTask;
}
