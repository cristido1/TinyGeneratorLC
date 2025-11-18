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
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS logs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts TEXT NOT NULL,
    level TEXT NOT NULL,
    category TEXT NOT NULL,
    message TEXT,
    exception TEXT,
    state TEXT
);
";
            cmd.ExecuteNonQuery();
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
                    cmd.CommandText = "INSERT INTO logs (ts, level, category, message, exception, state) VALUES (@ts, @level, @cat, @msg, @ex, @st)";
                    cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"));
                    cmd.Parameters.AddWithValue("@level", logLevel.ToString());
                    cmd.Parameters.AddWithValue("@cat", _category ?? "");
                    cmd.Parameters.AddWithValue("@msg", message ?? "");
                    cmd.Parameters.AddWithValue("@ex", exText ?? string.Empty);
                    cmd.Parameters.AddWithValue("@st", stateText ?? string.Empty);
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
