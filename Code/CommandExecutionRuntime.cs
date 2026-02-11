using System;
using System.Threading;

namespace TinyGenerator.Services;

internal static class CommandExecutionRuntime
{
    private sealed record RuntimeContext(string OperationName, int TimeoutSec);

    private static readonly AsyncLocal<RuntimeContext?> CurrentContext = new();

    public static string? CurrentOperationName => CurrentContext.Value?.OperationName;

    public static int CurrentTimeoutSec => CurrentContext.Value?.TimeoutSec ?? 0;

    public static IDisposable Push(string operationName, int timeoutSec)
    {
        var previous = CurrentContext.Value;
        var normalizedOperation = string.IsNullOrWhiteSpace(operationName) ? "command" : operationName.Trim();
        CurrentContext.Value = new RuntimeContext(normalizedOperation, Math.Max(0, timeoutSec));
        return new ScopeHandle(previous);
    }

    private sealed class ScopeHandle : IDisposable
    {
        private readonly RuntimeContext? _previous;
        private bool _disposed;

        public ScopeHandle(RuntimeContext? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed) return;
            CurrentContext.Value = _previous;
            _disposed = true;
        }
    }
}
