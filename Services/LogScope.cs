using System;
using System.Collections.Generic;
using System.Threading;

namespace TinyGenerator.Services
{
    /// <summary>
    /// Simple AsyncLocal-based scope helper used to tag log entries with a logical operation.
    /// </summary>
    public static class LogScope
    {
        private static readonly AsyncLocal<Stack<string>> _scopes = new();

        public static IDisposable Push(string scope)
        {
            if (string.IsNullOrWhiteSpace(scope))
            {
                scope = "operation";
            }

            var stack = _scopes.Value;
            if (stack == null)
            {
                stack = new Stack<string>();
                _scopes.Value = stack;
            }
            stack.Push(scope);
            return new ScopeHandle(stack);
        }

        public static string? Current
        {
            get
            {
                var stack = _scopes.Value;
                if (stack == null || stack.Count == 0) return null;
                return stack.Peek();
            }
        }

        private sealed class ScopeHandle : IDisposable
        {
            private readonly Stack<string> _stack;
            private bool _disposed;

            public ScopeHandle(Stack<string> stack)
            {
                _stack = stack;
            }

            public void Dispose()
            {
                if (_disposed) return;
                if (_stack.Count > 0)
                {
                    _stack.Pop();
                }
                _disposed = true;
            }
        }
    }
}
