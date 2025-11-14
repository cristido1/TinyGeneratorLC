using System;
using Microsoft.Extensions.Logging;

namespace TinyGenerator.Services
{
    public class CustomLoggerProvider : ILoggerProvider
    {
        private readonly ICustomLogger _customLogger;
        private readonly NotificationService? _notifications;

        public CustomLoggerProvider(ICustomLogger customLogger, NotificationService? notifications = null)
        {
            _customLogger = customLogger ?? throw new ArgumentNullException(nameof(customLogger));
            _notifications = notifications;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new AdapterLogger(categoryName, _customLogger, _notifications);
        }

        public void Dispose()
        {
            // nothing to dispose here; CustomLogger is owned by DI and will be disposed by container
        }

        private class AdapterLogger : ILogger
        {
            private readonly string _category;
            private readonly ICustomLogger _custom;
            private readonly NotificationService? _notifications;

            public AdapterLogger(string category, ICustomLogger custom, NotificationService? notifications)
            {
                _category = category;
                _custom = custom;
                _notifications = notifications;
            }

            public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                try
                {
                    var message = formatter != null ? formatter(state, exception) : state?.ToString() ?? string.Empty;
                    var level = logLevel.ToString();
                    var stateStr = state?.ToString();
                    _custom.Log(level, _category, message, exception?.ToString(), stateStr);
                    // Broadcast log entry to all clients as a notification (fire-and-forget)
                    // Only forward information/warning/error/critical (ignore Trace/Debug spam)
                    // Skip ProgressService category because it already broadcasts via ProgressHub to avoid duplicate AppNotification
                    if (logLevel >= LogLevel.Information && !_category?.Contains("ProgressService", StringComparison.OrdinalIgnoreCase) == true)
                    try
                    {
                        var toplevel = "info";
                        if (logLevel == LogLevel.Warning) toplevel = "warning";
                        if (logLevel == LogLevel.Error || logLevel == LogLevel.Critical) toplevel = "error";
                        _ = _notifications?.NotifyAllAsync(level + " - " + _category, message, toplevel);
                    }
                    // else ignore Trace/Debug
                    catch { }
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
