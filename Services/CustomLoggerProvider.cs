using System;
using Microsoft.Extensions.Logging;

namespace TinyGenerator.Services
{
    public class CustomLoggerProvider : ILoggerProvider
    {
        private readonly ICustomLogger _customLogger;

        public CustomLoggerProvider(ICustomLogger customLogger)
        {
            _customLogger = customLogger ?? throw new ArgumentNullException(nameof(customLogger));
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new AdapterLogger(categoryName, _customLogger);
        }

        public void Dispose()
        {
            // nothing to dispose here; CustomLogger is owned by DI and will be disposed by container
        }

        private class AdapterLogger : ILogger
        {
            private readonly string _category;
            private readonly ICustomLogger _custom;

            public AdapterLogger(string category, ICustomLogger custom)
            {
                _category = category;
                _custom = custom;
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
