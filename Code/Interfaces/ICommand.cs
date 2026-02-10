using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands;

public sealed class CommandProgressEventArgs : EventArgs
{
    public int Current { get; }
    public int Max { get; }
    public string? Description { get; }

    public CommandProgressEventArgs(int current, int max, string? description = null)
    {
        Current = current;
        Max = max;
        Description = description;
    }
}

public interface ICommand
{
    string CommandName => ToSnakeCase(GetType().Name);
    int Priority => 2;
    event EventHandler<CommandProgressEventArgs>? Progress { add { } remove { } }

    Task<CommandResult> Execute(CommandContext context) => CommandExecutionFunction.Execute(this, context);
    Task Cancel(CommandContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static string ToSnakeCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "command";
        }

        var name = value.EndsWith("Command", StringComparison.OrdinalIgnoreCase)
            ? value[..^7]
            : value;

        return CommandExecutionFunction.ToSnakeCase(name);
    }
}

internal static class CommandExecutionFunction
{
    public static async Task<CommandResult> Execute(object command, CommandContext context)
    {
        var commandType = command.GetType();

        var preferredSignatures = new (Type[] Args, object?[] Values)[]
        {
            (new[] { typeof(CancellationToken), typeof(string) }, new object?[] { context.CancellationToken, context.RunId }),
            (new[] { typeof(string), typeof(CancellationToken) }, new object?[] { context.RunId, context.CancellationToken }),
            (new[] { typeof(CancellationToken) }, new object?[] { context.CancellationToken }),
            (Type.EmptyTypes, Array.Empty<object?>())
        };

        MethodInfo? executeMethod = null;
        object?[]? executeArgs = null;

        foreach (var (args, values) in preferredSignatures)
        {
            executeMethod = commandType.GetMethod("ExecuteAsync", args);
            if (executeMethod != null)
            {
                executeArgs = values;
                break;
            }
        }

        if (executeMethod == null)
        {
            return new CommandResult(false, $"ExecuteAsync non supportato per {commandType.Name}");
        }

        object? invocationResult;
        try
        {
            invocationResult = executeMethod.Invoke(command, executeArgs);
        }
        catch (TargetInvocationException tie)
        {
            return new CommandResult(false, tie.InnerException?.Message ?? tie.Message);
        }

        return await UnwrapToCommandResult(invocationResult, context.CancellationToken).ConfigureAwait(false);
    }

    private static async Task<CommandResult> UnwrapToCommandResult(object? invocationResult, CancellationToken ct)
    {
        if (invocationResult == null)
        {
            return new CommandResult(true, null);
        }

        if (invocationResult is CommandResult cr)
        {
            return cr;
        }

        if (invocationResult is Task task)
        {
            await task.ConfigureAwait(false);
            var taskType = task.GetType();
            if (!taskType.IsGenericType)
            {
                return new CommandResult(true, null);
            }

            var resultProperty = taskType.GetProperty("Result");
            var resultValue = resultProperty?.GetValue(task);
            return MapResult(resultValue);
        }

        return MapResult(invocationResult);
    }

    private static CommandResult MapResult(object? value)
    {
        if (value == null)
        {
            return new CommandResult(true, null);
        }

        if (value is CommandResult commandResult)
        {
            return commandResult;
        }

        if (value is bool boolResult)
        {
            return new CommandResult(boolResult, null);
        }

        var type = value.GetType();
        if (type.FullName != null && type.FullName.StartsWith("System.ValueTuple`", StringComparison.Ordinal))
        {
            var item1 = type.GetField("Item1")?.GetValue(value);
            if (item1 is bool success)
            {
                var item2 = type.GetField("Item2")?.GetValue(value);
                var item3 = type.GetField("Item3")?.GetValue(value);
                var message = item3 as string ?? item2 as string;
                if (string.IsNullOrWhiteSpace(message) && item2 is long storyId)
                {
                    message = success ? $"storyId={storyId}" : null;
                }
                return new CommandResult(success, message);
            }
        }

        var successProp = type.GetProperty("Success");
        var messageProp = type.GetProperty("Message") ?? type.GetProperty("Error");
        if (successProp?.PropertyType == typeof(bool))
        {
            var success = (bool)(successProp.GetValue(value) ?? false);
            var message = messageProp?.GetValue(value)?.ToString();
            return new CommandResult(success, message);
        }

        return new CommandResult(true, value.ToString());
    }

    internal static string ToSnakeCase(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "command";
        }

        var value = raw.Trim();
        var chars = new System.Text.StringBuilder(value.Length + 8);
        char prev = '\0';
        foreach (var c in value)
        {
            if (char.IsUpper(c))
            {
                if (chars.Length > 0 && prev != '_' && (char.IsLower(prev) || char.IsDigit(prev)))
                {
                    chars.Append('_');
                }
                chars.Append(char.ToLowerInvariant(c));
            }
            else if (char.IsLetterOrDigit(c))
            {
                chars.Append(char.ToLowerInvariant(c));
            }
            else if (chars.Length > 0 && prev != '_')
            {
                chars.Append('_');
            }

            prev = chars.Length > 0 ? chars[chars.Length - 1] : '\0';
        }

        return chars.ToString().Trim('_');
    }
}
