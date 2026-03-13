using System;
using Microsoft.Extensions.Logging;

namespace TinyGenerator.Services
{
    public class CustomLoggerProvider : ILoggerProvider
    {
        private readonly ICustomLogger? _customLogger;
        private readonly IServiceProvider? _serviceProvider;
        private ICustomLogger? _resolvedCustomLogger;
        private int _resolveAttempted;
        [ThreadStatic]
        private static bool _resolvingCustomLogger;

        // Existing constructor preserved for backward compatibility (direct injection)
        public CustomLoggerProvider(ICustomLogger customLogger)
        {
            _customLogger = customLogger ?? throw new ArgumentNullException(nameof(customLogger));
        }

        // Lazy-constructor: accept IServiceProvider and resolve dependencies when needed (avoids cycles during DI ValidateOnBuild)
        public CustomLoggerProvider(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        }

        public ILogger CreateLogger(string categoryName)
        {
            // Resolve dependencies lazily to avoid cycles during startup validation
            ICustomLogger? custom = _customLogger ?? TryResolveCustomLogger();
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
                        // Keep fallback lightweight to avoid startup slowdowns from framework log noise.
                        if (ShouldWriteConsoleFallback(_category, logLevel))
                        {
                            System.Console.WriteLine($"{DateTime.UtcNow:o} [{_category}] {level}: {message} {exception?.ToString()}");
                        }
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

            private static bool ShouldWriteConsoleFallback(string? category, LogLevel level)
            {
                if (level < LogLevel.Warning)
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(category) &&
                    category.StartsWith("Microsoft.AspNetCore.DataProtection", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return true;
            }
        }

        private ICustomLogger? TryResolveCustomLogger()
        {
            if (_customLogger != null)
            {
                return _customLogger;
            }

            if (_resolvedCustomLogger != null)
            {
                return _resolvedCustomLogger;
            }

            if (_serviceProvider == null)
            {
                return null;
            }

            if (System.Threading.Interlocked.CompareExchange(ref _resolveAttempted, 1, 0) != 0)
            {
                return _resolvedCustomLogger;
            }

            if (_resolvingCustomLogger)
            {
                return null;
            }

            try
            {
                _resolvingCustomLogger = true;
                _resolvedCustomLogger = _serviceProvider.GetService(typeof(ICustomLogger)) as ICustomLogger;
                return _resolvedCustomLogger;
            }
            catch
            {
                return null;
            }
            finally
            {
                _resolvingCustomLogger = false;
            }
        }

        private class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new NullScope();
            public void Dispose() { }
        }
    }
}
