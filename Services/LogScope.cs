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
        private sealed class ScopeInfo
        {
            public ScopeInfo(string name, long? operationId)
            {
                Name = name;
                OperationId = operationId;
            }

            public string Name { get; }
            public long? OperationId { get; }
        }

        private static long _operationCounter;
        private static readonly AsyncLocal<Stack<ScopeInfo>> _scopes = new();

        public static long GenerateOperationId()
        {
            return Interlocked.Increment(ref _operationCounter);
        }

        public static IDisposable Push(string scope) => Push(scope, null);

        public static IDisposable Push(string scope, long? operationId)
        {
            if (string.IsNullOrWhiteSpace(scope))
            {
                scope = "operation";
            }

            var stack = _scopes.Value;
            if (stack == null)
            {
                stack = new Stack<ScopeInfo>();
                _scopes.Value = stack;
            }
            var inheritedOperation = operationId ?? (stack.Count > 0 ? stack.Peek().OperationId : null);
            stack.Push(new ScopeInfo(scope, inheritedOperation));
            return new ScopeHandle(stack);
        }

        public static string? Current
        {
            get
            {
                var stack = _scopes.Value;
                if (stack == null || stack.Count == 0) return null;
                return stack.Peek().Name;
            }
        }

        public static long? CurrentOperationId
        {
            get
            {
                var stack = _scopes.Value;
                if (stack == null || stack.Count == 0) return null;
                return stack.Peek().OperationId;
            }
        }

        private sealed class ScopeHandle : IDisposable
        {
            private readonly Stack<ScopeInfo> _stack;
            private bool _disposed;

            public ScopeHandle(Stack<ScopeInfo> stack)
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
