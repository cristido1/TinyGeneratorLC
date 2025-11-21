using System;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using System.IO;

namespace TinyGenerator.Services
{
    // Simple SQLite logger provider that writes log entries to a `logs` table in storage.db
    public class SqliteLoggerProvider : ILoggerProvider
    {
        private readonly string _dbPath;

        public SqliteLoggerProvider(string dbPath = "data/storage.db")
        {
            _dbPath = dbPath ?? "data/storage.db";
            try
            {
                var dir = Path.GetDirectoryName(_dbPath) ?? "data";
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                EnsureTable();
            }
            catch
            {
                // swallow errors on construction to avoid breaking app startup
            }
        }

        private void EnsureTable()
        {
            // Log table should be created by migrations/DatabaseService
            // This provider doesn't need to ensure the table exists
        }

        public ILogger CreateLogger(string categoryName) => new SqliteLogger(_dbPath, categoryName);

        public void Dispose() { }

        private class SqliteLogger : ILogger
        {
            private readonly string _dbPath;
            private readonly string _category;

            public SqliteLogger(string dbPath, string category)
            {
                _dbPath = dbPath;
                _category = category;
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                try
                {
                    var message = formatter != null ? formatter(state, exception) : state?.ToString();
                    var stateText = state?.ToString();
                    var exText = exception?.ToString();
                    using var conn = new SqliteConnection($"Data Source={_dbPath}");
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    // Write to Log table (capital L) with all columns from LogEntry model
                    cmd.CommandText = "INSERT INTO Log (Ts, Level, Category, Message, Exception, State, ThreadId, AgentName, Context) VALUES (@ts, @level, @cat, @msg, @ex, @st, @tid, @agent, @ctx)";
                    cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"));
                    cmd.Parameters.AddWithValue("@level", logLevel.ToString());
                    cmd.Parameters.AddWithValue("@cat", _category ?? "");
                    cmd.Parameters.AddWithValue("@msg", message ?? "");
                    cmd.Parameters.AddWithValue("@ex", exText ?? string.Empty);
                    cmd.Parameters.AddWithValue("@st", stateText ?? string.Empty);
                    cmd.Parameters.AddWithValue("@tid", Environment.CurrentManagedThreadId);
                    cmd.Parameters.AddWithValue("@agent", DBNull.Value);
                    cmd.Parameters.AddWithValue("@ctx", DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
                catch
                {
                    // don't throw from logger
                }
            }

            private class NullScope : IDisposable
            {
                public static NullScope Instance { get; } = new NullScope();
                public void Dispose() { }
            }
        }
    }
}
