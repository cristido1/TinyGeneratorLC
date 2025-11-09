using System.Data;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;

namespace TinyGenerator.Services
{

    public class PersistentMemoryService
    {
        private readonly string _dbPath;

        public PersistentMemoryService(string dbPath = "Data/memory.sqlite")
        {
            _dbPath = dbPath;
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            Initialize();
        }

        private void Initialize()
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Memory (
                Id TEXT PRIMARY KEY,
                Collection TEXT NOT NULL,
                TextValue TEXT NOT NULL,
                Metadata TEXT,
                CreatedAt TEXT NOT NULL
            );
            ";
            cmd.ExecuteNonQuery();
        }

        private static string ComputeHash(string text)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(text);
            var hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash);
        }

        // ➕ Salva informazione testuale
        public async Task SaveAsync(string collection, string text, object? metadata = null)
        {
            var id = ComputeHash(collection + text);
            var json = metadata != null ? JsonSerializer.Serialize(metadata) : null;

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO Memory (Id, Collection, TextValue, Metadata, CreatedAt)
                VALUES ($id, $collection, $text, $metadata, datetime('now'))
            ";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$collection", collection);
            cmd.Parameters.AddWithValue("$text", text);
            cmd.Parameters.AddWithValue("$metadata", json ?? (object)DBNull.Value);

            await cmd.ExecuteNonQueryAsync();
        }

        // 🔍 Cerca testo simile (ricerca semplice full-text LIKE)
        public async Task<List<string>> SearchAsync(string collection, string query, int limit = 5)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT TextValue FROM Memory
                WHERE Collection = $collection
                AND TextValue LIKE $query
                ORDER BY CreatedAt DESC
                LIMIT $limit;
            ";
            cmd.Parameters.AddWithValue("$collection", collection);
            cmd.Parameters.AddWithValue("$query", $"%{query}%");
            cmd.Parameters.AddWithValue("$limit", limit);

            var results = new List<string>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(reader.GetString(0));
            }

            return results;
        }

        // 🧹 Cancella una voce
        public async Task DeleteAsync(string collection, string text)
        {
            var id = ComputeHash(collection + text);
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var cmd = connection.CreateCommand();
            cmd.CommandText = @"DELETE FROM Memory WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", id);

            await cmd.ExecuteNonQueryAsync();
        }

        // 🧾 Elenca tutte le collezioni
        public async Task<List<string>> GetCollectionsAsync()
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var cmd = connection.CreateCommand();
            cmd.CommandText = @"SELECT DISTINCT Collection FROM Memory ORDER BY Collection;";
            var collections = new List<string>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                collections.Add(reader.GetString(0));
            }

            return collections;
        }
    }
}
