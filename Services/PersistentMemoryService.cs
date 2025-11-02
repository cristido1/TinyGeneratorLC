using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace TinyGenerator.Services
{
    // Simple key/value memory store backed by the same SQLite database used by StoriesService.
    // Stores entries per memoryKey and key. ReadContext returns concatenated values for requested keys.
    public sealed class PersistentMemoryService
    {
        private readonly object _lock = new();
        private readonly string _dbPath;
        private readonly string _connectionString;

        public PersistentMemoryService(string dbPath = "data/storage.db")
        {
            _dbPath = dbPath;
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath) ?? ".");
            _connectionString = $"Data Source={_dbPath}";
            InitializeDb();
        }

        private void InitializeDb()
        {
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS memory (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  memory_key TEXT,
  key TEXT,
  value TEXT,
  agent TEXT,
  ts TEXT
);
";
                cmd.ExecuteNonQuery();
            }
        }

        public void Save(string memoryKey, string key, string value, string? agent = null)
        {
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO memory(memory_key, key, value, agent, ts) VALUES($mk,$k,$v,$a,$ts);";
                cmd.Parameters.AddWithValue("$mk", memoryKey ?? string.Empty);
                cmd.Parameters.AddWithValue("$k", key ?? string.Empty);
                cmd.Parameters.AddWithValue("$v", value ?? string.Empty);
                cmd.Parameters.AddWithValue("$a", agent ?? string.Empty);
                cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
        }

        public string ReadContext(string memoryKey, string[] keys)
        {
            if (keys == null || keys.Length == 0) return string.Empty;
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                // build parameterized IN clause
                var parts = new List<string>();
                for (int i = 0; i < keys.Length; i++)
                {
                    parts.Add($"$k{i}");
                    cmd.Parameters.AddWithValue($"$k{i}", keys[i]);
                }
                var inClause = string.Join(",", parts);
                cmd.CommandText = $"SELECT key, value FROM memory WHERE memory_key = $mk AND key IN ({inClause}) ORDER BY id";
                cmd.Parameters.AddWithValue("$mk", memoryKey ?? string.Empty);
                using var r = cmd.ExecuteReader();
                var list = new List<string>();
                while (r.Read())
                {
                    var k = r.IsDBNull(0) ? string.Empty : r.GetString(0);
                    var v = r.IsDBNull(1) ? string.Empty : r.GetString(1);
                    list.Add($"{k}: {v}");
                }
                return string.Join("\n", list);
            }
        }
    }
}
