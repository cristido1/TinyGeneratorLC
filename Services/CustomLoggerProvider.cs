using System;
using Microsoft.Extensions.Logging;

namespace TinyGenerator.Services
{
    public class CustomLoggerProvider : ILoggerProvider
    {
        private readonly ICustomLogger? _customLogger;
        private readonly IServiceProvider? _serviceProvider;

        // Existing constructor preserved for backward compatibility (direct injection)
        public CustomLoggerProvider(ICustomLogger customLogger)
        {
            _customLogger = customLogger ?? throw new ArgumentNullException(nameof(customLogger));
            try { System.Console.WriteLine("[Startup] CustomLoggerProvider constructed with direct ICustomLogger"); } catch { }
        }

        // Lazy-constructor: accept IServiceProvider and resolve dependencies when needed (avoids cycles during DI ValidateOnBuild)
        public CustomLoggerProvider(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            try { System.Console.WriteLine("[Startup] CustomLoggerProvider constructed with IServiceProvider (lazy)"); } catch { }
        }

        public ILogger CreateLogger(string categoryName)
        {
            // Resolve dependencies lazily to avoid cycles during startup validation
            ICustomLogger? custom = _customLogger;
            
            // DO NOT attempt to resolve ICustomLogger from _serviceProvider here!
            // This causes infinite loops during DI setup because CustomLogger creation
            // may trigger logger creation, leading to circular dependencies.
            // If custom is null, we simply fall back to console logging in AdapterLogger.

            try { System.Console.WriteLine($"[Startup] CustomLoggerProvider.CreateLogger(category={categoryName}) customPresent={custom != null}"); } catch { }
            return new AdapterLogger(categoryName, custom);
        }

        public void Dispose()
        {
            // nothing to dispose here; CustomLogger is owned by DI and will be disposed by container
        }

        private class AdapterLogger : ILogger
        {
            private readonly string _category;
            private readonly ICustomLogger? _custom;

            public AdapterLogger(string category, ICustomLogger? custom)
            {
                _category = category;
                _custom = custom;
            }

            IDisposable ILogger.BeginScope<TState>(TState state) => NullScope.Instance;
            public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                try
                {
                    var message = formatter != null ? formatter(state, exception) : state?.ToString() ?? string.Empty;
                    var level = logLevel.ToString();
                    var stateStr = state?.ToString();
                    if (_custom == null)
                    {
                        // Best-effort: if a custom DB logger is not available, fall back to console logging
                        System.Console.WriteLine($"{DateTime.UtcNow:o} [{_category}] {level}: {message} {exception?.ToString()}");
                    }
                    else
                    {
                        _custom.Log(level, _category, message, exception?.ToString(), stateStr);
                    }
                    // Broadcast log entry to all clients as a notification (fire-and-forget)
                    // Only forward information/warning/error/critical (ignore Trace/Debug spam)
                    // Skip progress-related categories because the hub already broadcasts updates to avoid duplicate AppNotification
                    if (logLevel >= LogLevel.Information && !_category?.Contains("Progress", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        try
                        {
                            var toplevel = "info";
                            if (logLevel == LogLevel.Warning) toplevel = "warning";
                            if (logLevel == LogLevel.Error || logLevel == LogLevel.Critical) toplevel = "error";
                            _ = _custom?.NotifyAllAsync(level + " - " + _category, message, toplevel);
                        }
                        catch { }
                    }
                }
                catch
                {
                    // Swallow logging failures to avoid recursive errors
                }
            }
        }

        private class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new NullScope();
            public void Dispose() { }
        }
    }
}
