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

        public PersistentMemoryService(string dbPath = "data/storage.db")
        {
            _dbPath = dbPath;
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            Initialize();
        }

        private void Initialize()
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            try
            {
                using var fkCmd = connection.CreateCommand();
                fkCmd.CommandText = "PRAGMA foreign_keys = ON;";
                fkCmd.ExecuteNonQuery();
            }
            catch { }

            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Memory (
                Id TEXT PRIMARY KEY,
                Collection TEXT NOT NULL,
                TextValue TEXT NOT NULL,
                Metadata TEXT,
                model_id INTEGER NULL,
                agent_id INTEGER NULL,
                CreatedAt TEXT NOT NULL
            );
            ";
            cmd.ExecuteNonQuery();

            // Ensure required columns exist and perform best-effort migration if legacy 'agent' column exists.
            var cols = new List<string>();
            using (var colCmd = connection.CreateCommand())
            {
                colCmd.CommandText = "PRAGMA table_info(Memory);";
                using var rdr = colCmd.ExecuteReader();
                while (rdr.Read()) cols.Add(rdr.GetString(1));
            }

            // If model_id or agent_id missing, add them
            var colsLower = cols.Select(c => c.ToLowerInvariant()).ToList();
            if (!colsLower.Contains("model_id"))
            {
                try
                {
                    using var addModel = connection.CreateCommand();
                    addModel.CommandText = "ALTER TABLE Memory ADD COLUMN model_id INTEGER NULL;";
                    addModel.ExecuteNonQuery();
                }
                catch { }
            }
            if (!colsLower.Contains("agent_id"))
            {
                try
                {
                    using var addAgentId = connection.CreateCommand();
                    addAgentId.CommandText = "ALTER TABLE Memory ADD COLUMN agent_id INTEGER NULL;";
                    addAgentId.ExecuteNonQuery();
                }
                catch { }
            }

            // If legacy 'agent' column exists, migrate rows into a new table without 'agent' column.
            // Detect legacy 'agent' column case-insensitively and migrate it out
            if (colsLower.Contains("agent"))
            {
                try
                {
                    using var create = connection.CreateCommand();
                    create.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Memory_new (
                        Id TEXT PRIMARY KEY,
                        Collection TEXT NOT NULL,
                        TextValue TEXT NOT NULL,
                        Metadata TEXT,
                        model_id INTEGER NULL,
                        agent_id INTEGER NULL,
                        CreatedAt TEXT NOT NULL,
                        FOREIGN KEY (agent_id) REFERENCES agents(id) ON DELETE SET NULL ON UPDATE CASCADE
                    );
                    ";
                    create.ExecuteNonQuery();

                    // Copy data; attempt to convert agent to integer if possible, otherwise set NULL. (agent is legacy column)
                    using var copy = connection.CreateCommand();
                    copy.CommandText = @"
                    INSERT OR REPLACE INTO Memory_new (Id, Collection, TextValue, Metadata, model_id, agent_id, CreatedAt)
                    SELECT Id, Collection, TextValue, Metadata, model_id, CASE WHEN CAST(agent AS INTEGER) > 0 THEN CAST(agent AS INTEGER) ELSE NULL END, CreatedAt FROM Memory;
                    ";
                    copy.ExecuteNonQuery();

                    using var drop = connection.CreateCommand();
                    drop.CommandText = "DROP TABLE IF EXISTS Memory;";
                    drop.ExecuteNonQuery();

                    using var rename = connection.CreateCommand();
                    rename.CommandText = "ALTER TABLE Memory_new RENAME TO Memory;";
                    rename.ExecuteNonQuery();
                }
                catch { }
            }

            // Ensure the Memory table's foreign keys reference the current `agents` table
            try
            {
                using var fkCheck = connection.CreateCommand();
                fkCheck.CommandText = "PRAGMA foreign_key_list('Memory');";
                using var fkR = fkCheck.ExecuteReader();
                var needsFix = false;
                while (fkR.Read())
                {
                    var refTable = fkR.IsDBNull(2) ? string.Empty : fkR.GetString(2);
                    if (!string.Equals(refTable, "agents", StringComparison.OrdinalIgnoreCase)) { needsFix = true; break; }
                }

                if (needsFix)
                {
                    try
                    {
                        using var off = connection.CreateCommand(); off.CommandText = "PRAGMA foreign_keys = OFF;"; off.ExecuteNonQuery();
                        using var createFix = connection.CreateCommand();
                        createFix.CommandText = @"CREATE TABLE IF NOT EXISTS Memory_new_fix (
                            Id TEXT PRIMARY KEY,
                            Collection TEXT NOT NULL,
                            TextValue TEXT NOT NULL,
                            Metadata TEXT,
                            model_id INTEGER NULL,
                            agent_id INTEGER NULL,
                            CreatedAt TEXT NOT NULL,
                            FOREIGN KEY (agent_id) REFERENCES agents(id) ON DELETE SET NULL ON UPDATE CASCADE
                        );";
                        createFix.ExecuteNonQuery();

                        using var copyFix = connection.CreateCommand();
                        copyFix.CommandText = @"INSERT OR REPLACE INTO Memory_new_fix (Id, Collection, TextValue, Metadata, model_id, agent_id, CreatedAt)
                            SELECT Id, Collection, TextValue, Metadata, model_id, agent_id, CreatedAt FROM Memory;";
                        copyFix.ExecuteNonQuery();

                        using var dropOld = connection.CreateCommand(); dropOld.CommandText = "DROP TABLE IF EXISTS Memory;"; dropOld.ExecuteNonQuery();
                        using var renameFix = connection.CreateCommand(); renameFix.CommandText = "ALTER TABLE Memory_new_fix RENAME TO Memory;"; renameFix.ExecuteNonQuery();
                        using var on = connection.CreateCommand(); on.CommandText = "PRAGMA foreign_keys = ON;"; on.ExecuteNonQuery();
                    }
                    catch { /* best-effort: ignore failures here */ }
                }
            }
            catch { }
        }

        private static string ComputeHash(string text)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(text);
            var hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash);
        }

        // ➕ Salva informazione testuale
        public async Task SaveAsync(string collection, string text, object? metadata = null, long? modelId = null, int? agentId = null)
        {
            var id = ComputeHash(collection + text);
            var json = metadata != null ? JsonSerializer.Serialize(metadata) : null;

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            try { using var fkCmd = connection.CreateCommand(); fkCmd.CommandText = "PRAGMA foreign_keys = ON;"; fkCmd.ExecuteNonQuery(); } catch { }

            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO Memory (Id, Collection, TextValue, Metadata, model_id, agent_id, CreatedAt)
                VALUES ($id, $collection, $text, $metadata, $model_id, $agent_id, datetime('now'))
            ";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$collection", collection);
            cmd.Parameters.AddWithValue("$text", text);
            cmd.Parameters.AddWithValue("$metadata", json ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$model_id", modelId.HasValue ? (object)modelId.Value : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$agent_id", agentId.HasValue ? (object)agentId.Value : (object)DBNull.Value);

            await cmd.ExecuteNonQueryAsync();
        }

        // 🔍 Cerca testo simile (ricerca semplice full-text LIKE)
        public async Task<List<string>> SearchAsync(string collection, string query, int limit = 5, long? modelId = null, int? agentId = null)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            try { using var fkCmd = connection.CreateCommand(); fkCmd.CommandText = "PRAGMA foreign_keys = ON;"; fkCmd.ExecuteNonQuery(); } catch { }

            var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                SELECT TextValue FROM Memory
                WHERE Collection = $collection
                AND TextValue LIKE $query
                " + (modelId.HasValue ? "AND model_id = $model_id " : "") + (agentId.HasValue ? "AND agent_id = $agent_id " : "") + @"
                ORDER BY CreatedAt DESC
                LIMIT $limit;
            ";
            cmd.Parameters.AddWithValue("$collection", collection);
            cmd.Parameters.AddWithValue("$query", $"%{query}%");
            cmd.Parameters.AddWithValue("$limit", limit);
            if (modelId.HasValue) cmd.Parameters.AddWithValue("$model_id", modelId.Value);
            if (agentId.HasValue) cmd.Parameters.AddWithValue("$agent_id", agentId.Value);

            var results = new List<string>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(reader.GetString(0));
            }

            return results;
        }

        // 🧹 Cancella una voce
        public async Task DeleteAsync(string collection, string text, long? modelId = null, int? agentId = null)
        {
            var id = ComputeHash(collection + text);
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            try { using var fkCmd = connection.CreateCommand(); fkCmd.CommandText = "PRAGMA foreign_keys = ON;"; fkCmd.ExecuteNonQuery(); } catch { }

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
            try { using var fkCmd = connection.CreateCommand(); fkCmd.CommandText = "PRAGMA foreign_keys = ON;"; fkCmd.ExecuteNonQuery(); } catch { }

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
